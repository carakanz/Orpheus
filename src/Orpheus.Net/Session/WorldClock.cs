using System;
using Orpheus.Core.World;

namespace Orpheus.Net.Session
{
    /// <summary>
    /// Часы мира клиента (карточка 1-014, <c>01</c> 6 «Сеть»): время мира — у сервера, оно приходит в заголовке
    /// каждого снимка (<see cref="Snapshot.WorldTime"/>, такт суток 0…35 999). Клиент ведёт свою оценку целыми
    /// тактами с дробью 1/256 и двигает её местными мкс; расхождение с образцом больше 2 с — рывок к серверному.
    /// <para>
    /// Сглаживание — ходом часов ±1 % (<see cref="SkewPpm"/>) в сторону ошибки образца, как у <see cref="ClientClock"/>:
    /// свет меняется медленно, точность в десятые доли секунды не нужна, а рывок каждые 100 мс — виден. Между снимками
    /// часы идут сами не дольше <see cref="MaxFreeRunUs"/> (сервер замолчал — время встаёт, а не уходит вперёд).
    /// </para>
    /// <para>
    /// Времени внутри нет: мкс передаёт вызывающий. Плавающей точки нет (11.5). В паузе (0-036) сервер не двигает
    /// <see cref="Core.Sim.Simulation.WorldTime"/>, поэтому часы клиента встают (<see cref="Hold"/>) и идут дальше с того
    /// же места (<see cref="Release"/>).
    /// </para>
    /// </summary>
    public sealed class WorldClock
    {
        /// <summary>Дробь такта: оценка хранится в 1/256 такта.</summary>
        public const int Scale = 256;

        /// <summary>Порог рывка к серверному времени: 2 с = 60 тактов (<c>01</c> 6 «Сеть»).</summary>
        public const int JumpTicks = 2 * DayClock.TicksPerSecond;

        /// <summary>Поправка хода: 1 % (<see cref="ClientClock.Skew"/>), в миллионных долях.</summary>
        public const int SkewPpm = 10_000;

        /// <summary>Мёртвая зона сглаживания, 1/256 такта (¼ такта ≈ 83 мс): меньше — часы идут ровно.</summary>
        public const long DeadbandTicksX256 = Scale / 4;

        /// <summary>Наибольший свободный ход без снимка: 10 с. Больше — часы стоят на последней оценке.</summary>
        public const long MaxFreeRunUs = 10_000_000;

        private long _baseX256;
        private long _baseUs;
        private long _lastSampleUs;
        private int _ratePpm = 1_000_000;
        private bool _held;
        private long _heldUs;

        /// <summary>Оценка есть (пришёл хоть один снимок).</summary>
        public bool Valid { get; private set; }

        /// <summary>Образцов принято (заголовок каждого снимка).</summary>
        public long Samples { get; private set; }

        /// <summary>Рывков к серверному времени (расхождение больше 2 с).</summary>
        public long Jumps { get; private set; }

        /// <summary>Ошибка последнего образца, 1/256 такта (+ — сервер впереди оценки).</summary>
        public long ErrorTicksX256 { get; private set; }

        /// <summary>Текущий ход часов: 1 000 000, 1 010 000 или 990 000 долей на миллион.</summary>
        public int RatePpm => _ratePpm;

        /// <summary>Часы стоят: пауза сервера (0-036).</summary>
        public bool Held => _held;

        /// <summary>
        /// Оценка времени мира в 1/256 такта, нормализованная в [0; <see cref="DayClock.TicksPerDay"/> × <see cref="Scale"/>);
        /// до первого снимка — 0. Часы стоят — на миге остановки.
        /// </summary>
        public long EstimateX256(long nowUs)
        {
            if (!Valid)
            {
                return 0;
            }

            long dt = Math.Max(0L, (_held ? _heldUs : nowUs) - _baseUs);
            // Свободный ход отсчитывается от последнего снимка: дольше MaxFreeRunUs после молчания сервера
            // оценка не уходит вперёд (время встаёт, а не убегает).
            long over = Math.Max(0L, (_held ? _heldUs : nowUs) - _lastSampleUs - MaxFreeRunUs);
            dt -= Math.Min(dt, over);

            // Тактов = мкс × 30 / 1 000 000; дробь × 256; ход — в миллионных долях.
            long run = dt * DayClock.TicksPerSecond * Scale * _ratePpm / 1_000_000_000_000L;
            return WrapX256(_baseX256 + run);
        }

        /// <summary>Такт суток (целый, 0…35 999) в момент <paramref name="nowUs"/>; до первого снимка — 0.</summary>
        public int TicksAt(long nowUs) => (int)(EstimateX256(nowUs) / Scale);

        /// <summary>Фаза суток на месте клиента (её читает рендер, 1-021).</summary>
        public DayPhase PhaseAt(long nowUs) => DayClock.PhaseOf(TicksAt(nowUs));

        /// <summary>Фоновая освещённость, 1/1024, на месте клиента (<c>01</c> 6 «Освещённость тайла»).</summary>
        public int BackgroundX1024(long nowUs) => DayClock.BackgroundX1024(TicksAt(nowUs));

        /// <summary>
        /// Образец: время мира в такте снимка (момент отправки) — прибыло в <paramref name="nowUs"/>,
        /// плюс запоздание доставки <paramref name="owdUs"/> (образец относится к будущему на owd).
        /// Расхождение больше 2 с — рывок; меньше — ход часов ±1 %.
        /// </summary>
        public void OnSample(int serverTicksAtSend, long owdUs, long nowUs)
        {
            long sample = WrapX256(((long)DayClock.Wrap(serverTicksAtSend) + (owdUs * DayClock.TicksPerSecond) / 1_000_000L) * Scale);
            Samples++;
            _lastSampleUs = nowUs;
            if (!Valid)
            {
                Set(sample, nowUs);
                return;
            }

            long error = Shortest(sample - EstimateX256(nowUs));
            ErrorTicksX256 = error;
            if (Math.Abs(error) > JumpTicks * Scale)
            {
                Jumps++;
                Set(sample, nowUs);
                return;
            }

            int rate = error > DeadbandTicksX256 ? 1_000_000 + SkewPpm : error < -DeadbandTicksX256 ? 1_000_000 - SkewPpm : 1_000_000;
            if (rate != _ratePpm)
            {
                _baseX256 = EstimateX256(nowUs);
                _baseUs = nowUs;
                _ratePpm = rate;
            }
        }

        /// <summary>Пауза сервера (0-036): часы встают на нынешней оценке; образцы в паузе не трогают ход.</summary>
        public void Hold(long nowUs)
        {
            if (_held)
            {
                return;
            }

            _held = true;
            _heldUs = nowUs;
        }

        /// <summary>Пауза снята: часы идут с того же места — время паузы вынуто из линии часов. Возвращает, сколько стояли, мкс.</summary>
        public long Release(long nowUs)
        {
            if (!_held)
            {
                return 0;
            }

            long d = Math.Max(0L, nowUs - _heldUs);
            _held = false;
            _baseUs += d;
            return d;
        }

        private void Set(long ticksX256, long nowUs)
        {
            _baseX256 = WrapX256(ticksX256);
            _baseUs = nowUs;
            _ratePpm = 1_000_000;
            if (_held)
            {
                _heldUs = nowUs; // стоят — на новой оценке
            }

            Valid = true;
        }

        private static long WrapX256(long v)
        {
            long span = (long)DayClock.TicksPerDay * Scale;
            long r = v % span;
            return r < 0 ? r + span : r;
        }

        /// <summary>Кратчайшее расхождение по кругу суток, 1/256 такта (±18 000 тактов).</summary>
        private static long Shortest(long d)
        {
            long span = (long)DayClock.TicksPerDay * Scale;
            long r = d % span;
            if (r > span / 2)
            {
                r -= span;
            }
            else if (r < -span / 2)
            {
                r += span;
            }

            return r;
        }
    }
}
