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
    /// </summary>
    public sealed class NetBot
    {
        private readonly string _script;
        private readonly int _botIndex;
        private SimBot? _bot;
        private DodgeBot? _dodge;
        private DigLineBot? _digLine;
        private InputFrame _current;
        private LocalPrediction? _pred;
        private uint _reconciledTick;
        private bool _reconciledAny;

        public NetBot(NetClient client, string script, int botIndex)
        {
            Client = client;
            _script = script;
            _botIndex = botIndex;
        }

        public NetClient Client { get; }

        /// <summary>Сценарий.</summary>
        public string Script => _script;

        /// <summary>Создать предсказание после входа; null — пока нет (спрашивается каждый кадр до первого не-null).</summary>
        public Func<NetClient, LocalPrediction?>? PredictionFactory { get; set; }

        /// <summary>Бот-уворачивающийся (сценарий <c>dodge</c>, 0-029) — после входа; иначе null.</summary>
        public DodgeBot? Dodge => _dodge;

        /// <summary>Бот <c>dig_line</c> (0-028) — после входа; иначе null.</summary>
        public DigLineBot? DigLine => _digLine;

        /// <summary>Предсказание своего персонажа (после входа, если есть фабрика).</summary>
        public LocalPrediction? Prediction => _pred;

        /// <summary>Кадр бота: разобрать пришедшее, сверить, решить, отправить ввод по часам.</summary>
        public void Frame(long nowMicros)
        {
            Client.Poll(nowMicros);
            if (Client.State != NetClientState.InGame)
            {
                return;
            }

            if (_script == DodgeBot.Name)
            {
                if (_dodge == null)
                {
                    var dodge = new DodgeBot(Client.Shape, Client.Accept.WorldSeed ^ (ulong)(_botIndex + 1) * 0x9E3779B97F4A7C15UL);
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
            else if (_bot == null)
            {
                _bot = SimBot.Create(_script, Client.EntityId, _botIndex, Client.Accept.WorldSeed);
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
                else if ((next & 1) == 0)
                {
                    var self = new SimEntity { Id = Client.EntityId, Kind = SimEntity.KindCharacter };
                    if (Client.HasSnapshot && Client.Latest.TryFind(Client.EntityId, out SnapshotEntity e))
                    {
                        self.Mover = new Mover(e.Position);
                    }

                    _current = _bot!.Think(next / 2, self);
                }

                Client.SendFrame(_current);
                _pred?.Sent(Client.Frame, _current);
            }
        }
    }
}
