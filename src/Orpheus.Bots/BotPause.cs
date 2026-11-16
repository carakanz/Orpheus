using Orpheus.Core.Geometry;
using Orpheus.Net.Protocol;
using Orpheus.Net.Session;

namespace Orpheus.Bots
{
    /// <summary>
    /// Пауза ботами (0-036, <c>--pause-at S --pause-for S</c>): бот 0 ставит паузу, через заданное время её снимает последний бот
    /// (снять может любой). Замер по каждому боту: сколько стоял в паузе, двигались ли в паузе подтверждённая позиция (снимок)
    /// и предсказание, сколько кадров ввода ушло в паузе (после того как бот о ней узнал) и сколько правок предсказания после.
    /// </summary>
    internal sealed class BotPause
    {
        private readonly NetBot[] _bots;
        private readonly double _at;
        private readonly double _for;
        private readonly bool[] _was;
        private readonly WorldPos[] _self0;
        private readonly WorldPos[] _pred0;
        private readonly long[] _inputs0;
        private readonly long[] _inputsLast;
        private readonly int[] _frames;
        private readonly long[] _resyncAtResume;
        private readonly bool[] _hadPred;
        private readonly long[] _resyncSeen;
        // P-003 (Pause15s: «сдвигов в паузе 57» в `-All`): база замера держится, пока поток снимков
        // идёт (кадр за кадром снимок новее) — под нагрузкой «в пути» идут секунды до-паузного мира,
        // 0,5 с запаса не хватает. Замер начинается событием: снимок такта паузы пришёл и поток встал.
        private readonly uint[] _snapTick;
        private readonly bool[] _settleSnap;
        private readonly bool[] _resumePending;
        private bool _requested;
        private bool _released;

        public BotPause(NetBot[] bots, double at, double forSeconds)
        {
            _bots = bots;
            _at = at;
            _for = forSeconds;
            int n = bots.Length;
            _was = new bool[n];
            _self0 = new WorldPos[n];
            _pred0 = new WorldPos[n];
            _inputs0 = new long[n];
            _inputsLast = new long[n];
            _frames = new int[n];
            _resyncAtResume = new long[n];
            for (int i = 0; i < n; i++)
            {
                _resyncAtResume[i] = -1;
            }

            _hadPred = new bool[n];
            _resyncSeen = new long[n];
            _snapTick = new uint[n];
            _settleSnap = new bool[n];
            _resumePending = new bool[n];
            Log = new System.Collections.Generic.List<string>();
            MovedFrames = new long[n];
            InputsInPause = new long[n];
            MeasuredFrames = new long[n];
            ResyncWindowFrames = new long[n];
        }

        /// <summary>
        /// Кадров ботов в паузе, где снимок или предсказание сдвинулись от позиции на входе в паузу. База замера
        /// держится, пока применяется новый снимок (поток «допаузного» мира в пути) и пока не пройдены
        /// <see cref="SettleFrames"/> кадров; замер начинается, когда снимок такта паузы применён и поток встал (P-003).
        /// </summary>
        public long[] MovedFrames { get; }

        /// <summary>Кадров ботов (60 Гц) минимального запаса после входа в паузу, пока позиция на входе уточняется: 0,5 с.</summary>
        public const int SettleFrames = 30;

        /// <summary>Кадров ввода, отправленных ботом в паузе (после того как он о ней узнал).</summary>
        public long[] InputsInPause { get; }

        /// <summary>Кадров замера в паузе (база установлена: поток снимков встал): 0 — пауза не «встала», сдвиги не замерялись (P-003).</summary>
        public long[] MeasuredFrames { get; }

        /// <summary>Кадров окна «после паузы» (от первого снимка новее такта паузы до конца прогона): 0 — окно не открылось (P-003).</summary>
        public long[] ResyncWindowFrames { get; }

        /// <summary>Правок предсказания после снятия паузы (до конца прогона); пауза не снималась — −1.</summary>
        public long ResyncedAfter(int i) => _resyncAtResume[i] < 0 ? -1 : (_bots[i].Prediction?.Resynced ?? 0) - _resyncAtResume[i];

        /// <summary>
        /// Журнал (C-003): когда у бота появилось предсказание и каждая правка — время, пауза, снимок, ack, что сервер применил
        /// («своё» снимка), где стояло предсказание, сущности рядом. Не больше <see cref="LogLimit"/> строк.
        /// </summary>
        public System.Collections.Generic.List<string> Log { get; }

        /// <summary>Потолок строк журнала.</summary>
        public const int LogLimit = 64;

        private void Note(int i, double seconds, string what)
        {
            if (Log.Count >= LogLimit)
            {
                return;
            }

            NetClient c = _bots[i].Client;
            LocalPrediction? p = _bots[i].Prediction;
            string near = string.Empty;
            if (c.HasSnapshot)
            {
                for (int k = 0; k < c.Latest.Count; k++)
                {
                    SnapshotEntity e = c.Latest.Entities[k];
                    if (e.Id != c.EntityId)
                    {
                        near += $" [{e}]";
                    }
                }
            }

            Log.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "бот {0}, {1:0.000} с: {2}; пауза {3} (запрошена {4}, снята {5}); снимок {6}, ack {7}, отправлен кадр {8}; сервер: {9}; предсказание: {10}, расхождение {11} u, сверок {12}, установок {13}, правок {14}; чужие:{15}",
                i, seconds, what, c.Paused ? "стоит" : "нет", _requested ? "да" : "нет", _released ? "да" : "нет",
                c.HasSnapshot ? c.Latest.Tick : 0, c.HasSnapshot ? c.Latest.AckFrame : 0, c.Frame,
                c.HasSnapshot ? c.Latest.Self.ToString() : "—", p != null ? p.Position.ToString() : "—", p?.LastDevUnits ?? 0,
                p?.Compared ?? 0, p?.Initial ?? 0, p?.Resynced ?? 0, near.Length == 0 ? " нет" : near));
        }

        /// <summary>Кадр ботов (после их <see cref="NetBot.Frame"/>): запросы паузы по расписанию и замер.</summary>
        public void Frame(double seconds)
        {
            NetClient first = _bots[0].Client;
            if (!_requested && seconds >= _at && first.State == NetClientState.InGame)
            {
                _requested = true;
                first.RequestPause(true);
            }

            NetClient last = _bots[_bots.Length - 1].Client;
            if (_requested && !_released && seconds >= _at + _for && last.Paused)
            {
                _released = true;
                last.RequestPause(false);
            }

            for (int i = 0; i < _bots.Length; i++)
            {
                NetClient c = _bots[i].Client;
                LocalPrediction? p = _bots[i].Prediction;
                WorldPos self = c.HasSnapshot ? c.Latest.Self.Position : default;
                WorldPos pred = p?.Position ?? default;
                bool snapAdvanced = c.HasSnapshot && (int)(c.Latest.Tick - _snapTick[i]) > 0;
                if (c.HasSnapshot)
                {
                    _snapTick[i] = c.Latest.Tick;
                }

                if (p != null && !_hadPred[i])
                {
                    _hadPred[i] = true;
                    Note(i, seconds, "предсказание создано (мир бота построен)");
                }

                if (p != null && p.Resynced != _resyncSeen[i])
                {
                    _resyncSeen[i] = p.Resynced;
                    Note(i, seconds, "ПРАВКА предсказания");
                }

                if (c.Paused)
                {
                    if (!_was[i])
                    {
                        _frames[i] = 0;
                        _settleSnap[i] = false;
                        _inputs0[i] = c.InputsSent;
                    }

                    if (!_settleSnap[i] && c.HasSnapshot && (int)(c.Latest.Tick - c.PauseTick) >= 0)
                    {
                        _settleSnap[i] = true; // снимок такта паузы применён: дальше сервер не шагает
                    }

                    if (!_settleSnap[i] || snapAdvanced || _frames[i] < SettleFrames)
                    {
                        _frames[i]++;
                        _self0[i] = self;
                        _pred0[i] = pred;
                    }
                    else
                    {
                        _frames[i]++;
                        MeasuredFrames[i]++;
                        if (self != _self0[i] || pred != _pred0[i])
                        {
                            MovedFrames[i]++;
                        }
                    }

                    _inputsLast[i] = c.InputsSent;
                }
                else if (_was[i])
                {
                    InputsInPause[i] += _inputsLast[i] - _inputs0[i];
                    _resumePending[i] = true; // P-003: окно «после» открывается чистым снимком после паузы
                }
                else if (_resumePending[i] && snapAdvanced && (int)(c.Latest.Tick - c.PauseTick) > 0)
                {
                    // P-003: правки сверки с первым снимком, догнавшим паузу, — не «правки игры после паузы»;
                    // окно считается от применения этого снимка.
                    _resumePending[i] = false;
                    _resyncAtResume[i] = p?.Resynced ?? 0;
                }

                if (!_resumePending[i] && _resyncAtResume[i] >= 0 && !c.Paused)
                {
                    ResyncWindowFrames[i]++;
                }

                _was[i] = c.Paused;
            }
        }
    }
}
