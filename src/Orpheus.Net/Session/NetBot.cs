using System;
using Orpheus.Core.Bots;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Sim;
using Orpheus.Net.Protocol;

namespace Orpheus.Net.Session
{
    /// <summary>
    /// Бот по сети (docs/07, 11.3): <see cref="NetClient"/> + сценарий <see cref="SimBot"/> (<c>idle</c>, <c>wander</c>).
    /// Вызывающий зовёт <see cref="Frame"/> примерно 60 раз в секунду: разбор пришедшего, сверка предсказания с новым
    /// снимком, затем столько кадров ввода, сколько велят часы клиента (<see cref="NetClient.FramesDue"/>, 2.4; 0-025):
    /// раз в два кадра — решение сценария, каждый кадр — пакет ввода и шаг предсказания.
    /// <para>
    /// Предсказание (11.3 «тот же протокол, предсказание, сверка», 0-025): <see cref="PredictionFactory"/> даёт
    /// <see cref="LocalPrediction"/> после входа (проходимость — из мира того же зерна); его метрика — расхождение
    /// предсказанной позиции с серверной в тот же кадр на каждой сверке (<see cref="LocalPrediction.Compared"/>).
    /// </para>
    /// <para>
    /// <c>run_seam</c> (0-027): решение — каждый кадр ввода, по подтверждённой позиции снимка (<c>Self</c>) и признаку
    /// «все отправленные кадры с движением сервер шагнул» (AckFrame ≥ номера последнего такого кадра), — см.
    /// <see cref="RunSeamBot"/>. <c>seam_watch</c>: стоит, показывает чужих через <see cref="SeamWatch"/> и меряет скачок.
    /// <c>build_room</c> (1-026): добывает и строит комнату 5 × 5 с дверью, переживает ночь (<see cref="BuildRoomBot"/>);
    /// <c>night_camp</c> (1-026): стоит ночью в поле, считает смерть и убийцу (<see cref="NightCampBot"/>).
    /// </para>
    /// </summary>
    public sealed class NetBot
    {
        private readonly string _script;
        private readonly int _botIndex;
        private SimBot? _bot;
        private DodgeBot? _dodge;
        private DigLineBot? _digLine;
        private BuildRoomBot? _build;
        private NightCampBot? _camp;
        private InputFrame _current;
        private LocalPrediction? _pred;
        private uint _reconciledTick;
        private bool _reconciledAny;
        private SeamWatch? _watch;
        private long _pollUs;
        private uint _lastMoveFrame;
        private bool _moved;
        private uint _checkedTick;
        private bool _checkedAny;

        public NetBot(NetClient client, string script, int botIndex)
        {
            Client = client;
            _script = script;
            _botIndex = botIndex;
            if (script == SeamWatchBot.Name)
            {
                // Снимки — в показ наблюдателя тем же порядком, что у клиента Unity (в Poll, со временем этого опроса).
                Action<Snapshot>? applied = client.SnapshotApplied;
                client.SnapshotApplied = s =>
                {
                    applied?.Invoke(s);
                    _watch?.OnSnapshot(s, _pollUs);
                };
                Action? reset = client.ClockReset;
                client.ClockReset = () =>
                {
                    reset?.Invoke();
                    _watch?.OnClockReset();
                };
            }
        }

        public NetClient Client { get; }

        /// <summary>Сценарий <c>dodge</c>: конец окна нажатия до конца показа замаха, кадров (<see cref="DodgeBot.LatestLead"/>).</summary>
        public int DodgeLatestLead { get; set; } = DodgeBot.LatestLeadFrames;

        /// <summary>Сценарий <c>dodge</c>: опыт «позднее нажатие» (<see cref="DodgeBot.LatePressFrames"/>), 0 — выкл.</summary>
        public int DodgeLatePress { get; set; }

        /// <summary>Сценарий.</summary>
        public string Script => _script;

        /// <summary>Создать предсказание после входа; null — пока нет (спрашивается каждый кадр до первого не-null).</summary>
        public Func<NetClient, LocalPrediction?>? PredictionFactory { get; set; }

        /// <summary>Бот-уворачивающийся (сценарий <c>dodge</c>, 0-029) — после входа; иначе null.</summary>
        public DodgeBot? Dodge => _dodge;

        /// <summary>Бот <c>dig_line</c> (0-028) — после входа; иначе null.</summary>
        public DigLineBot? DigLine => _digLine;

        /// <summary>Бот <c>build_room</c> (1-026, приёмка `08` 3.1 1.5) — после входа; иначе null.</summary>
        public BuildRoomBot? Builder => _build;

        /// <summary>Бот <c>night_camp</c> (1-026, приёмка `08` 3.1 1.6) — после входа; иначе null.</summary>
        public NightCampBot? Camp => _camp;

        /// <summary>Предсказание своего персонажа (после входа, если есть фабрика).</summary>
        public LocalPrediction? Prediction => _pred;

        /// <summary>Круги <c>run_seam</c>: <see cref="RunSeamBot.LapsAll"/> (по умолчанию) или <see cref="RunSeamBot.LapsAxes"/>.</summary>
        public string RunLaps { get; set; } = RunSeamBot.LapsAll;

        /// <summary>Бег <c>run_seam</c> (после входа); другой сценарий — null.</summary>
        public RunSeamBot? Runner => _bot as RunSeamBot;

        /// <summary>Наблюдатель <c>seam_watch</c> (после входа); другой сценарий — null.</summary>
        public SeamWatch? Watch => _watch;

        /// <summary>Сценарий кончился сам: <c>run_seam</c> прошёл все круги, <c>seam_watch</c> остался один.</summary>
        public bool Finished => Runner?.Done == true || _watch?.Alone == true;

        /// <summary>Кадр бота: разобрать пришедшее, сверить, решить, отправить ввод по часам.</summary>
        public void Frame(long nowMicros)
        {
            _pollUs = nowMicros;
            Client.Poll(nowMicros);
            if (Client.State != NetClientState.InGame)
            {
                return;
            }

            if (_script == DodgeBot.Name)
            {
                if (_dodge == null)
                {
                    var dodge = new DodgeBot(Client.Shape, Client.Accept.WorldSeed ^ (ulong)(_botIndex + 1) * 0x9E3779B97F4A7C15UL, DodgeLatestLead)
                    {
                        LatePressFrames = DodgeLatePress,
                    };
                    _dodge = dodge;
                    Client.SwingStarted = m => dodge.OnSwing(m);
                    Client.MeleeResolved = m => dodge.OnResult(m);
                }
            }
            else if (_script == DigLineBot.Name)
            {
                if (_digLine == null)
                {
                    var dig = new DigLineBot(Client.Shape);
                    _digLine = dig;
                    Client.EditReplied = dig.OnReply;
                }
            }
            else if (_script == BuildRoomBot.Name)
            {
                if (_build == null)
                {
                    var build = new BuildRoomBot(Client.Shape, Client.Latest.Self.Position);
                    _build = build;
                    Client.EditReplied = build.OnReply;
                }
            }
            else if (_script == NightCampBot.Name)
            {
                if (_camp == null)
                {
                    var camp = new NightCampBot();
                    _camp = camp;
                    Client.DeathSeen = m => camp.OnDeath(m); // жертва/убийца — из «Смерти» (тип 24, 1-001)
                }
            }
            else if (_bot == null)
            {
                _bot = _script == RunSeamBot.Name
                    ? new RunSeamBot(Client.EntityId, Client.Shape, RunLaps)
                    : SimBot.Create(_script, Client.EntityId, _botIndex, Client.Accept.WorldSeed, Client.Shape);
                if (_bot is SeamWatchBot)
                {
                    _watch = new SeamWatch(Client.Shape);
                }
            }

            if (_pred == null && PredictionFactory != null)
            {
                _pred = PredictionFactory(Client); // null — ещё не готово (мир строится), спросим в следующем кадре
            }

            if (_pred != null && Client.HasSnapshot && (!_reconciledAny || Client.Latest.Tick != _reconciledTick))
            {
                _reconciledAny = true;
                _reconciledTick = Client.Latest.Tick;
                _pred.Reconcile(Client.Latest.AckFrame, Client.Latest.Self, nowMicros);
            }

            if (_bot is RunSeamBot check && Client.HasSnapshot && Client.Latest.Self.Present && (!_checkedAny || Client.Latest.Tick != _checkedTick))
            {
                // Сверка кадров (проверка 0-027 п. 2): сдвиг своей позиции между снимками = шагнутые кадры × 25,6 unit.
                _checkedAny = true;
                _checkedTick = Client.Latest.Tick;
                check.CheckConfirmed(Client.Latest.AckFrame, Client.Latest.Self.Position, Client.Latest.Self.Carry);
            }

            int n = Client.FramesDue(nowMicros);
            for (int k = 0; k < n; k++)
            {
                uint next = Client.Frame + 1;
                if (_dodge != null)
                {
                    WorldPos? pos = _pred != null && _pred.Synced ? _pred.Position
                        : Client.HasSnapshot && Client.Latest.TryFind(Client.EntityId, out SnapshotEntity se) ? se.Position : (WorldPos?)null;
                    _current = _dodge.Next(next, pos);
                }
                else if (_digLine != null)
                {
                    WorldPos? pos = _pred != null && _pred.Synced ? _pred.Position
                        : Client.HasSnapshot && Client.Latest.TryFind(Client.EntityId, out SnapshotEntity se) ? se.Position : (WorldPos?)null;
                    _current = pos.HasValue ? _digLine.Next(Client, pos.Value) : InputFrame.None; // каждый кадр: кнопка копания — в кадрах
                }
                else if (_build != null)
                {
                    WorldPos? pos = _pred != null && _pred.Synced ? _pred.Position
                        : Client.HasSnapshot && Client.Latest.TryFind(Client.EntityId, out SnapshotEntity se) ? se.Position : (WorldPos?)null;
                    _current = pos.HasValue ? _build.Next(Client, pos.Value, nowMicros) : InputFrame.None; // каждый кадр: копание, постановка, ходьба
                }
                else if (_camp != null)
                {
                    _current = _camp.Next(Client); // стоит: ввод пустой, счётчик смерти идёт
                }
                else if (_bot is RunSeamBot run)
                {
                    _current = InputFrame.None;
                    if (Client.HasSnapshot && Client.Latest.Self.Present)
                    {
                        bool settled = !_moved || (int)(Client.Latest.AckFrame - _lastMoveFrame) >= 0;
                        _current = run.ThinkFrame(Client.Latest.Self.Position, settled);
                    }
                }
                else if ((next & 1) == 0)
                {
                    var self = new SimEntity { Id = Client.EntityId, Kind = SimEntity.KindCharacter };
                    if (Client.HasSnapshot && Client.Latest.TryFind(Client.EntityId, out SnapshotEntity e))
                    {
                        self.Mover = new Mover(e.Position);
                    }

                    _current = _bot!.Think(next / 2, self);
                }

                long renumbers = Client.FrameRenumbers;
                Client.SendFrame(_current);
                _pred?.Sent(Client.Frame, _current);
                if (_bot is RunSeamBot sent)
                {
                    if (Client.FrameRenumbers != renumbers)
                    {
                        sent.ResetFrameCheck();
                    }

                    sent.SentFrame(Client.Frame, _current.Direction);
                }

                if (_current.Direction.IsMoving)
                {
                    _moved = true;
                    _lastMoveFrame = Client.Frame;
                }
            }

            if (_watch != null && Client.HasSnapshot)
            {
                _watch.Frame(nowMicros, Client.EntityId, Client.Latest.Self.Position);
            }
        }
    }
}
