using System;
using Orpheus.Core.Sim;
using Orpheus.Net.Protocol;
using Orpheus.Net.Transport;

namespace Orpheus.Net.Session
{
    /// <summary>
    /// Пауза (0-036; BRIEF п. 11b: «у любого игрока есть возможность поставить паузу»). Пауза — штатный фриз: такт мира стоит
    /// (время мира, ИИ, сутки, кадры ввода — <see cref="ServerFrame"/> не растёт), сеть живёт (разбор пакетов, пинги, вход,
    /// сохранение — цикл сервера крутится с прежним шагом 33 мс). В отличие от фриза 0-025 часы не перескакивают: сервер не
    /// отстаёт — он стоит, и клиенты, получив «пауза», останавливают свои часы на том же месте (<see cref="ClientClock.Hold"/>),
    /// а после «продолжить» идут дальше с него же. Ни перенумерации кадров (0-019), ни правок предсказания, ни смены опережения
    /// (0-033) после паузы нет: время обеих сторон просто вынуто.
    /// <para>
    /// Допущения <c>[док]</c> (карточка 0-036, «Решения»): поставить и снять может любой игрок в игре, в любом из трёх способов
    /// запуска (`07` 2.3 разрешал паузу только одиночной); ушёл поставивший — пауза остаётся (другие видят, кто поставил, и
    /// могут снять); в игре никого не осталось — пауза снимается сама (пустой мир — правило 7.6, а не чужая пауза).
    /// </para>
    /// </summary>
    public sealed partial class NetServer
    {
        /// <summary>Пауза стоит: такт мира не шагается (<c>ServerRun</c> и стенды тестов спрашивают перед тактом).</summary>
        public bool Paused { get; private set; }

        /// <summary>Такт, на котором стоит пауза (последний шагнутый).</summary>
        public uint PauseTick { get; private set; }

        /// <summary>Кто поставил паузу (имя); пауза снята — кто снял; пусто — сервер сам.</summary>
        public string PausedBy { get; private set; } = string.Empty;

        /// <summary>Сущность поставившего / снявшего; 0 — сервер сам.</summary>
        public int PausedByEntity { get; private set; }

        /// <summary>Пауз поставлено.</summary>
        public long PausesSet { get; private set; }

        /// <summary>Пауз снято (игроком или сервером).</summary>
        public long PausesCleared { get; private set; }

        /// <summary>Пауз, снятых сервером, когда в игре никого не осталось.</summary>
        public long PausesClearedEmpty { get; private set; }

        /// <summary>Запросов паузы, ничего не менявших (уже стоит / уже снята).</summary>
        public long PauseRequestsIgnored { get; private set; }

        /// <summary>Снимков, отправленных в паузе (только вошедшим в паузу — каждому один снимок такта паузы).</summary>
        public long PausedSnapshotsSent { get; private set; }

        /// <summary>
        /// Поставить / снять паузу с сервера (тесты, будущая консоль сервера): как запрос игрока, но «кто» — сервер.
        /// </summary>
        public void SetPaused(bool paused) => ChangePause(paused, null);

        private void OnPauseRequest(Player p, PauseRequestMessage m)
        {
            if (m.Paused == Paused)
            {
                PauseRequestsIgnored++;
                return;
            }

            ChangePause(m.Paused, p);
        }

        private void ChangePause(bool paused, Player? by)
        {
            if (paused == Paused)
            {
                return;
            }

            Paused = paused;
            PauseTick = (uint)_sim.Tick;
            PausedBy = by?.Name ?? string.Empty;
            PausedByEntity = by?.EntityId ?? 0;
            if (paused)
            {
                PausesSet++;
            }
            else
            {
                PausesCleared++;
                KeepLagAfterPause();
            }

            Log?.Invoke(NetLogLevel.Info, by != null
                ? $"{(paused ? "пауза" : "продолжить")}: «{by.Name}» (#{by.EntityId}), такт {PauseTick}"
                : $"{(paused ? "пауза" : "продолжить")}: сервер, такт {PauseTick}");
            int len = StateMessage().Write(_buf);
            for (int i = 0; i < _players.Count; i++)
            {
                if (_players[i].State == PlayerState.InGame)
                {
                    _transport.Send(_players[i].Peer, NetChannel.Facts, new ReadOnlySpan<byte>(_buf, 0, len));
                }
            }

            _transport.Flush();
        }

        /// <summary>
        /// Снятие паузы: отставание времени каждого персонажа от кадра сервера — то же, что до паузы (<see cref="Player.ResumeLag"/>).
        /// Кадры, бывшие в пути, пока клиент не узнал о паузе, лежат в очереди; без этого сервер шагнул бы их залпом (запас
        /// кадров полон) и через RTT остался бы без кадров.
        /// </summary>
        private void KeepLagAfterPause()
        {
            for (int i = 0; i < _players.Count; i++)
            {
                Player p = _players[i];
                p.ResumeLag = p.State == PlayerState.InGame && p.HasNext && !p.ClockStale
                    ? Math.Max(0, Math.Min(FrameRing, unchecked((int)(ServerFrame - p.NextFrame))))
                    : 0;
            }
        }

        private PauseStateMessage StateMessage() => new PauseStateMessage
        {
            Paused = Paused,
            ServerTick = PauseTick,
            ByEntity = PausedByEntity,
            ByName = PausedBy,
        };

        /// <summary>Вошедшему в паузу — состояние паузы сразу после «вход принят» (тот же канал 2: придёт после него).</summary>
        private void SendPauseTo(Player p)
        {
            if (!Paused)
            {
                return;
            }

            int len = StateMessage().Write(_buf);
            _transport.Send(p.Peer, NetChannel.Facts, new ReadOnlySpan<byte>(_buf, 0, len));
        }

        /// <summary>Пауза стоит, а в игре никого — снять (сервер сам): вошедший потом не застанет чужую паузу.</summary>
        private void ClearPauseIfEmpty()
        {
            if (Paused && PlayersInGame == 0)
            {
                PausesClearedEmpty++;
                ChangePause(false, null);
            }
        }

        /// <summary>
        /// Вместо <see cref="SendSnapshots"/> в паузе (раз за цикл сервера): снимок такта паузы — только тем, кто его ещё не
        /// получал (вошёл в паузу): иначе вошедший не увидит ни себя, ни мира до снятия. Каждый игрок получает не больше одного
        /// снимка каждого такта — кольцо «что клиент имеет» и подтверждения по номеру такта не путаются. Правки, ответы на
        /// правки и замахи в паузе не рассылаются: такта не было.
        /// </summary>
        public void SendPausedSnapshots()
        {
            ReadOnlySpan<SimEntity> entities = _sim.Entities;
            uint tick = (uint)_sim.Tick;
            bool sent = false;
            for (int p = 0; p < _players.Count; p++)
            {
                Player pl = _players[p];
                if (pl.State == PlayerState.InGame && !(pl.HasSentTick && pl.SentTick == tick) && SendSnapshotTo(pl, entities, tick))
                {
                    PausedSnapshotsSent++;
                    sent = true;
                }
            }

            if (sent)
            {
                _transport.Flush();
            }
        }
    }
}
