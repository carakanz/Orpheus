using System;

namespace Orpheus.Core.World
{
    /// <summary>Фаза суток (<c>01</c> 6 «Сутки»).</summary>
    public enum DayPhase : byte
    {
        /// <summary>День: фон 1,0.</summary>
        Day = 0,

        /// <summary>Сумерки: фон 1,0 → 0,3 линейно.</summary>
        Dusk = 1,

        /// <summary>Ночь: фон 0,3.</summary>
        Night = 2,

        /// <summary>Рассвет: фон 0,3 → 1,0 линейно.</summary>
        Dawn = 3,
    }

    /// <summary>
    /// Часы мира и формула освещённости (<c>01</c> 6 «Сутки», «Освещённость тайла», карточка 1-014).
    /// <para>
    /// Сутки — 1200 с = 36 000 тактов (30 Гц): день 0–720 с (21 600 тактов), сумерки 720–780 с,
    /// ночь 780–1140 с, рассвет 1140–1200 с. Мир стартует с t = 0: рассвет позади, впереди полный день.
    /// Часы целочисленные (<see cref="Simulation"/> двигает тактом), без плавающей точки — детерминизм (11.5).
    /// </para>
    /// <para>
    /// Освещённость — целое в 1/1024: L = clamp(фон × k_биома + Σ источников, 0, 1024).
    /// Порог видимости 0,27 → 277 (<c>01</c> 6: ниже — пол силуэтом, существ не видно).
    /// </para>
    /// </summary>
    public static class DayClock
    {
        /// <summary>Тактов в секунду (частота симуляции).</summary>
        public const int TicksPerSecond = 30;

        /// <summary>Секунд в сутках.</summary>
        public const int SecondsPerDay = 1200;

        /// <summary>Тактов в сутках: 1200 × 30.</summary>
        public const int TicksPerDay = SecondsPerDay * TicksPerSecond; // 36 000

        /// <summary>Начало сумерек: 720 с × 30.</summary>
        public const int DuskStartTick = 21_600;

        /// <summary>Начало ночи: 780 с × 30.</summary>
        public const int NightStartTick = 23_400;

        /// <summary>Начало рассвета: 1140 с × 30.</summary>
        public const int DawnStartTick = 34_200;

        /// <summary>Масштаб освещённости: 1,0 = 1024.</summary>
        public const int LightScale = 1024;

        /// <summary>Фон дня: 1,0 → 1024.</summary>
        public const int DayBgX1024 = 1024;

        /// <summary>Фон ночи: 0,3 → 307 (0,3 × 1024 = 307,2).</summary>
        public const int NightBgX1024 = 307;

        /// <summary>Порог видимости 0,27 (<c>01</c> 6): ниже — силуэт, существ не рисовать. 0,27 × 1024 = 276,48 → 277.</summary>
        public const int VisibilityThresholdX1024 = 277;

        /// <summary>Привести такт (любой, в том числе отрицательный) в сутки [0; <see cref="TicksPerDay"/>).</summary>
        public static int Wrap(int ticks)
        {
            int r = ticks % TicksPerDay;
            return r < 0 ? r + TicksPerDay : r;
        }

        /// <summary>Фаза суток по такту t ∈ [0; 36 000) (значение нормализуется <see cref="Wrap"/>).</summary>
        public static DayPhase PhaseOf(int ticks)
        {
            int t = Wrap(ticks);
            if (t < DuskStartTick)
            {
                return DayPhase.Day;
            }

            if (t < NightStartTick)
            {
                return DayPhase.Dusk;
            }

            return t < DawnStartTick ? DayPhase.Night : DayPhase.Dawn;
        }

        /// <summary>
        /// Фоновая освещённость, 1/1024: день 1024, ночь 307, сумерки и рассвет — линейно
        /// (на границах фаз значения совмещены: 1024 в сумерки начинается, 307 в ночь входит).
        /// </summary>
        public static int BackgroundX1024(int ticks)
        {
            int t = Wrap(ticks);
            switch (PhaseOf(t))
            {
                case DayPhase.Day:
                    return DayBgX1024;

                case DayPhase.Night:
                    return NightBgX1024;

                case DayPhase.Dusk:
                {
                    // 1024 → 307 за 1800 тактов, округление к ближнему.
                    int span = NightStartTick - DuskStartTick;
                    int drop = DayBgX1024 - NightBgX1024;
                    return DayBgX1024 - (drop * (t - DuskStartTick) + span / 2) / span;
                }

                default: // Dawn: 307 → 1024 за 1800 тактов.
                {
                    int span = TicksPerDay - DawnStartTick;
                    int rise = DayBgX1024 - NightBgX1024;
                    return NightBgX1024 + (rise * (t - DawnStartTick) + span / 2) / span;
                }
            }
        }

        /// <summary>
        /// k_биома, 1/1024 (<c>01</c> 6 «Поправки»: лес 0,85 → 870, джунгли 0,7 → 717; остальные биомы 1,0 → 1024).
        /// Номер биома — тот же байт, что <c>Orpheus.WorldGen.Biome</c> (Core намеренно на WorldGen не ссылается).
        /// </summary>
        public static int BiomeKX1024(byte biome)
        {
            const byte forest = 1;
            const byte jungle = 2;
            if (biome == forest)
            {
                return 870; // 0,85 × 1024 = 870,4
            }

            return biome == jungle ? 717 : LightScale; // 0,7 × 1024 = 716,8
        }

        /// <summary>
        /// Освещённость тайла (<c>01</c> 6): L = clamp(фон × k_биома + Σ источников, 0, 1024).
        /// Все части — в 1/1024; SourcesX1024 — сумма источников (см. свет-сетку).
        /// </summary>
        public static int TileLightX1024(int bgX1024, int biomeKX1024, int sourcesX1024)
        {
            long v = (long)bgX1024 * biomeK1024Clamp(biomeKX1024) / LightScale + sourcesX1024;
            if (v < 0)
            {
                return 0;
            }

            return v > LightScale ? LightScale : (int)v;
        }

        /// <summary>Виден ли тайл при своей L (порог 0,27).</summary>
        public static bool IsVisible(int lightX1024) => lightX1024 >= VisibilityThresholdX1024;

        private static int biomeK1024Clamp(int k) => k < 0 ? 0 : k > LightScale ? LightScale : k;
    }
}
