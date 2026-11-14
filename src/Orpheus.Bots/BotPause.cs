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
        private readonly long[] _resyncLast;
        private readonly long[] _resyncAtResume;
        private readonly bool[] _hadPred;
        private readonly long[] _resyncSeen;
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
            _resyncLast = new long[n];
            _resyncAtResume = new long[n];
            for (int i = 0; i < n; i++)
            {
                _resyncAtResume[i] = -1;
            }

            _hadPred = new bool[n];
            _resyncSeen = new long[n];
            Log = new System.Collections.Generic.List<string>();
            MovedFrames = new long[n];
            InputsInPause = new long[n];
        }

        /// <summary>
        /// Кадров ботов в паузе, где снимок или предсказание сдвинулись от позиции на входе в паузу. Первые
        /// <see cref="SettleFrames"/> кадров паузы — не в счёт: снимок, бывший в пути, мог прийти после «пауза» (другой канал).
        /// </summary>
        public long[] MovedFrames { get; }

        /// <summary>Кадров ботов (60 Гц) от входа в паузу, пока позиция на входе уточняется: 0,5 с.</summary>
        public const int SettleFrames = 30;

        /// <summary>Кадров ввода, отправленных ботом в паузе (после того как он о ней узнал).</summary>
        public long[] InputsInPause { get; }

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
                        _inputs0[i] = c.InputsSent;
                    }

                    if (_frames[i]++ < SettleFrames)
                    {
                        _self0[i] = self;
                        _pred0[i] = pred;
                    }
                    else if (self != _self0[i] || pred != _pred0[i])
                    {
                        MovedFrames[i]++;
                    }

                    _inputsLast[i] = c.InputsSent;
                    _resyncLast[i] = p?.Resynced ?? 0;
                }
                else if (_was[i])
                {
                    InputsInPause[i] += _inputsLast[i] - _inputs0[i];
                    _resyncAtResume[i] = _resyncLast[i]; // с последнего кадра паузы: правка в кадре снятия — уже «после»
                }

                _was[i] = c.Paused;
            }
        }
    }
}
