using Orpheus.Core.Geometry;
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
