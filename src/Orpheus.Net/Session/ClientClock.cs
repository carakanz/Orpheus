using System;
using Orpheus.Core.Sim;

namespace Orpheus.Net.Session
{
    /// <summary>
    /// Часы клиента (docs/07, 2.4 «Часы клиента», «Часы после фриза»; 0-025): оценка текущего кадра сервера в тактах (дробно)
    /// по местным мкс. Образцы — такт из заголовка каждого снимка и из ответа на пинг (раз в 1 с) плюс owd = RTT/2;
    /// RTT — наименьший из последних <see cref="RttWindow"/> пингов (ответ сервера ждёт его такта до 33 мс — наименьший
    /// почти без этой добавки).
    /// <para>
    /// Ошибка образца — образец минус оценка в момент приёма. Окно — последние <see cref="Window"/> образцов (0,5 с), мерой
    /// служит наибольшая ошибка окна: опоздавший пакет даёт ошибку «назад», раньше срока пакет не приходит — поэтому самый
    /// быстрый образец ближе всех к правде. Сглаживание — поправкой хода своих часов на 1 % (<see cref="Skew"/>) в сторону
    /// ошибки; ошибка больше 100 мс (<see cref="StepTicks"/>) — перестановка сразу: вперёд — по одному образцу, назад —
    /// когда всё окно отстало (одиночный опоздавший пакет назад не переставляет). «Часы после фриза» сервера (сообщение
    /// «часы: кадр N» по каналу 2 или новая эпоха в заголовке снимка) — перестановка сразу по ним, окно — заново.
    /// </para>
    /// Времени внутри нет: мкс передаёт вызывающий. Без выделений после создания.
    /// </summary>
    public sealed class ClientClock
    {
        /// <summary>Длина такта, мкс.</summary>
        public const double TickUs = 1_000_000.0 / Simulation.TickRate;

        /// <summary>Порог перестановки: 100 мс = 3 такта (2.4).</summary>
        public const double StepTicks = 3.0;

        /// <summary>Поправка хода при сглаживании: 1 % (2.4).</summary>
        public const double Skew = 0.01;

        /// <summary>Мёртвая зона сглаживания, тактов (≈ 1,7 мс): меньше — часы идут ровно.</summary>
        public const double DeadbandTicks = 0.05;

        /// <summary>Окно образцов: 15 снимков ≈ 0,5 с.</summary>
        public const int Window = 15;

        /// <summary>Окно RTT: 8 пингов ≈ 8 с.</summary>
        public const int RttWindow = 8;

        private readonly double[] _sample = new double[Window];
        private readonly long[] _sampleUs = new long[Window];
        private readonly long[] _rtt = new long[RttWindow];
        private int _head;
        private int _count;
        private int _rttHead;
        private int _rttCount;
        private double _baseTick;
        private long _baseUs;
        private double _rate = 1.0;

        /// <summary>Оценка есть (было начало: вход, первый образец).</summary>
        public bool Valid { get; private set; }

        /// <summary>Оценка owd = RTT / 2, мкс.</summary>
        public long OwdUs { get; private set; }

        /// <summary>RTT пингов известен (хоть один ответ).</summary>
        public bool HasRtt => _rttCount > 0;

        /// <summary>Текущий ход часов: 1, 1,01 или 0,99.</summary>
        public double Rate => _rate;

        /// <summary>Образцов принято (снимки и ответы на пинг).</summary>
        public long Samples { get; private set; }

        /// <summary>Перестановок вперёд по образцу (ошибка &gt; 100 мс).</summary>
        public long StepsForward { get; private set; }

        /// <summary>Перестановок назад по образцам (всё окно отстало больше чем на 100 мс).</summary>
        public long StepsBack { get; private set; }

        /// <summary>Перестановок по «часам после фриза» сервера, в которых оценка сдвинулась больше чем на такт.</summary>
        public long ServerResets { get; private set; }

        /// <summary>Наибольшая ошибка окна после последнего образца, тактов (+ — оценка отстаёт).</summary>
        public double WindowErrorTicks { get; private set; }

        /// <summary>Оценка кадра сервера (такт, дробно) в момент <paramref name="nowUs"/>; до начала — 0.</summary>
        public double Estimate(long nowUs) => Valid ? _baseTick + (nowUs - _baseUs) * _rate / TickUs : 0.0;

        /// <summary>Номер кадра ввода 60 Гц по оценке (L = 0, 2.7): два кадра на такт.</summary>
        public uint FrameAt(long nowUs) => (uint)(long)Math.Floor(Estimate(nowUs) * 2.0);

        /// <summary>RTT по транспорту (до первого пинга): owd = половина.</summary>
        public void SeedRtt(long rttUs)
        {
            if (_rttCount == 0 && rttUs >= 0)
            {
                OwdUs = rttUs / 2;
            }
        }

        /// <summary>
        /// Начало (вход): такт сервера «сейчас» на момент отправки — плюс owd. До первого RTT пинга owd — по транспорту.
        /// </summary>
        public void Start(uint serverTickNow, long nowUs)
        {
            Set(serverTickNow + OwdUs / TickUs, nowUs);
        }

        /// <summary>
        /// Ответ на пинг: RTT в окно; первый RTT — оценка переставляется по этому ответу (owd до него был грубым).
        /// </summary>
        public void OnPong(uint serverTickNow, long rttUs, long nowUs)
        {
            bool first = _rttCount == 0;
            _rtt[_rttHead] = Math.Max(0, rttUs);
            _rttHead = (_rttHead + 1) % RttWindow;
            _rttCount = Math.Min(_rttCount + 1, RttWindow);
            long min = long.MaxValue;
            for (int i = 0; i < _rttCount; i++)
            {
                min = Math.Min(min, _rtt[i]);
            }

            OwdUs = min / 2;
            if (first)
            {
                Set(serverTickNow + OwdUs / TickUs, nowUs);
                return;
            }

            OnSample(serverTickNow, nowUs);
        }

        /// <summary>
        /// Образец: такт сервера в момент отправки (заголовок снимка; ответ на пинг) — пришёл в <paramref name="nowUs"/>.
        /// </summary>
        public void OnSample(uint serverTickAtSend, long nowUs)
        {
            double s = serverTickAtSend + OwdUs / TickUs;
            Samples++;
            if (!Valid)
            {
                Set(s, nowUs);
                return;
            }

            _sample[_head] = s;
            _sampleUs[_head] = nowUs;
            _head = (_head + 1) % Window;
            _count = Math.Min(_count + 1, Window);

            // Ошибки окна — против нынешней линии часов (её ход мог смениться после образца).
            double max = double.MinValue;
            for (int i = 0; i < _count; i++)
            {
                max = Math.Max(max, _sample[i] - Estimate(_sampleUs[i]));
            }

            WindowErrorTicks = max;
            if (max > StepTicks)
            {
                StepsForward++;
                Shift(max, nowUs);
                return;
            }

            if (_count == Window && max < -StepTicks)
            {
                StepsBack++;
                Shift(max, nowUs);
                return;
            }

            double rate = max > DeadbandTicks ? 1.0 + Skew : max < -DeadbandTicks ? 1.0 - Skew : 1.0;
            if (rate != _rate)
            {
                _baseTick = Estimate(nowUs);
                _baseUs = nowUs;
                _rate = rate;
            }
        }

        /// <summary>
        /// «Часы после фриза» (2.4): сервер не догоняет такты и сообщил свой кадр — перестановка сразу, окно образцов заново.
        /// </summary>
        public void OnServerReset(uint serverTickAtSend, long nowUs)
        {
            double s = serverTickAtSend + OwdUs / TickUs;
            if (Valid && Math.Abs(s - Estimate(nowUs)) > 1.0)
            {
                ServerResets++;
            }

            Set(s, nowUs);
        }

        private void Shift(double ticks, long nowUs) => Set(Estimate(nowUs) + ticks, nowUs);

        private void Set(double tick, long nowUs)
        {
            _baseTick = tick;
            _baseUs = nowUs;
            _rate = 1.0;
            _count = 0;
            _head = 0;
            WindowErrorTicks = 0;
            Valid = true;
        }
    }
}
