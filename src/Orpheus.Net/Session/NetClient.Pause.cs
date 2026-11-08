using System;
using Orpheus.Net.Protocol;
using Orpheus.Net.Transport;

namespace Orpheus.Net.Session
{
    /// <summary>
    /// Пауза у клиента (0-036; см. <see cref="NetServer.Paused"/>). «Пауза» от сервера — часы клиента встают
    /// (<see cref="ClientClock.Hold"/>): номер кадра ввода не растёт, кадры не уходят, предсказание не шагает, опережение L
    /// не подстраивается. «Продолжить» — часы идут дальше с того же места (<see cref="ClientClock.Release"/>). Снимок новее
    /// такта паузы — тоже «продолжить»: сообщение по каналу 2 могла задержать переотправка, а сервер уже шагает. «Пауза»,
    /// пришедшая после снимка новее её такта, — опоздавшая (пауза уже снята): часы всё равно встают и стоят до «продолжить»
    /// (оно следом, канал упорядочен) — столько же, сколько стоял сервер.
    /// </summary>
    public sealed partial class NetClient
    {
        private bool _paused;
        private bool _pauseLate;
        private long _pausedAtUs;

        /// <summary>Пауза стоит (по последнему сообщению сервера).</summary>
        public bool Paused => _paused;

        /// <summary>Такт паузы (последний шагнутый сервером перед ней).</summary>
        public uint PauseTick { get; private set; }

        /// <summary>Кто поставил паузу (пауза стоит) или кто снял последнюю (пусто — сервер сам).</summary>
        public string PausedBy { get; private set; } = string.Empty;

        /// <summary>Сущность поставившего / снявшего (0 — сервер сам).</summary>
        public int PausedByEntity { get; private set; }

        /// <summary>Сообщений «пауза / продолжить» принято.</summary>
        public long PauseMessages { get; private set; }

        /// <summary>«Пауза», пришедшая после снимка новее её такта (опоздала): часы встали до «продолжить», не до снимка.</summary>
        public long PausesStale { get; private set; }

        /// <summary>Пауз, снятых по снимку новее такта паузы раньше сообщения «продолжить».</summary>
        public long PausesReleasedBySnapshot { get; private set; }

        /// <summary>Пауз всего (встали).</summary>
        public long Pauses { get; private set; }

        /// <summary>Сколько всего стояли в паузе, мкс.</summary>
        public long PausedTotalUs { get; private set; }

        /// <summary>Пауза поставлена (true) или снята (false) — для показа (время показа чужих, надпись).</summary>
        public Action<bool>? PauseChanged { get; set; }

        /// <summary>Попросить сервер поставить (true) или снять (false) паузу. Не в игре — ничего.</summary>
        public void RequestPause(bool paused)
        {
            if (State != NetClientState.InGame)
            {
                return;
            }

            int len = new PauseRequestMessage { Paused = paused }.Write(_buf);
            _transport.Send(_serverPeer, NetChannel.Facts, new ReadOnlySpan<byte>(_buf, 0, len));
            _transport.Flush();
        }

        private void OnPauseState(in PauseStateMessage m)
        {
            PauseMessages++;
            if (m.Paused)
            {
                // Снимок новее такта паузы уже пришёл — пауза снята раньше, чем дошло это сообщение (переотправка). Часы всё равно
                // встают: «продолжить» идёт следом (канал 2 упорядочен), и стоят они столько же, сколько стоял сервер, — номера
                // кадров, ушедшие вперёд на эту паузу, снова совпадут со шкалой сервера. Снять такую паузу может только
                // «продолжить», не снимок.
                _pauseLate = _hasSnapshot && (int)(_latest.Tick - m.ServerTick) > 0;
                if (_pauseLate && !_paused)
                {
                    PausesStale++;
                }

                PausedBy = m.ByName ?? string.Empty;
                PausedByEntity = m.ByEntity;
                PauseTick = m.ServerTick;
                if (!_paused)
                {
                    _paused = true;
                    _pausedAtUs = _nowUs;
                    Pauses++;
                    _clock.Hold(_nowUs);
                    PauseChanged?.Invoke(true);
                }

                return;
            }

            if (_paused)
            {
                PausedBy = m.ByName ?? string.Empty;
                PausedByEntity = m.ByEntity;
                Resume();
            }
        }

        /// <summary>Снимок такта <paramref name="tick"/> пришёл в паузе: новее такта паузы — сервер уже шагает.</summary>
        private void ResumeIfSnapshotNewer(uint tick)
        {
            if (_paused && !_pauseLate && (int)(tick - PauseTick) > 0)
            {
                PausesReleasedBySnapshot++;
                Resume();
            }
        }

        private void Resume()
        {
            long d = _clock.Release(_nowUs);
            long stood = Math.Max(d, _nowUs - _pausedAtUs);
            PausedTotalUs += stood;
            _paused = false;

            // Таймеры опережения L — в той же вынутой шкале: пауза не считается ни выдержкой «запас с избытком 2 с», ни шагом L.
            _leadStepUs += stood;
            _leadHoldUntilUs += stood;
            _leadChangedUs += stood;
            PauseChanged?.Invoke(false);
        }
    }
}
