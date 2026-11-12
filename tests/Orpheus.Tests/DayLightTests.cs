using System;
using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Save;
using Xunit;

namespace Orpheus.Tests
{
    /// <summary>
    /// Карточка 1-014, фаза A «часы мира» (docs/01, 6 «Сутки»): сутки 1200 с = 36 000 тактов
    /// (день 12 мин, сумерки 1, ночь 6, рассвет 1); фон день 1,0 / ночь 0,3, переходы линейные;
    /// часы идут тактом симуляции, в паузе стоят (Step не вызывается); время мира — в хэше состояния
    /// и в сохранении (поле МИР 10); формула L = clamp(фон × k_биома + Σ источников, 0, 1) в 1/1024,
    /// порог видимости 0,27 → 277.
    /// </summary>
    public class DayLightTests
    {
        private static WorldModel Flat(ulong seed) => FlatWorldGenerator.Instance.Generate(MapShape.World(64), seed);

        // ---------- Таблица фаз и фона (01 6) ----------

        [Fact]
        public void D14_Phase_Table()
        {
            // Границы: день 0–720 с, сумерки 720–780, ночь 780–1140, рассвет 1140–1200 (×30 тактов).
            Assert.Equal(DayPhase.Day, DayClock.PhaseOf(0));
            Assert.Equal(DayPhase.Day, DayClock.PhaseOf(DayClock.DuskStartTick - 1));
            Assert.Equal(DayPhase.Dusk, DayClock.PhaseOf(DayClock.DuskStartTick));
            Assert.Equal(DayPhase.Night, DayClock.PhaseOf(DayClock.NightStartTick));
            Assert.Equal(DayPhase.Night, DayClock.PhaseOf(DayClock.DawnStartTick - 1));
            Assert.Equal(DayPhase.Dawn, DayClock.PhaseOf(DayClock.DawnStartTick));
            Assert.Equal(DayPhase.Dawn, DayClock.PhaseOf(DayClock.TicksPerDay - 1));

            // Длины: 12/1/6/1 мин = 21600/1800/10800/1800 тактов.
            Assert.Equal(21_600, DayClock.DuskStartTick);
            Assert.Equal(1_800, DayClock.NightStartTick - DayClock.DuskStartTick);
            Assert.Equal(10_800, DayClock.DawnStartTick - DayClock.NightStartTick);
            Assert.Equal(1_800, DayClock.TicksPerDay - DayClock.DawnStartTick);
            Assert.Equal(36_000, DayClock.TicksPerDay);
        }

        [Fact]
        public void D14_Bg_Table_And_Linear_Ramps()
        {
            int tol = 51; // ±0,05 в 1/1024.

            // День 1,0; ночь 0,3.
            Assert.Equal(1024, DayClock.BackgroundX1024(0));
            Assert.Equal(1024, DayClock.BackgroundX1024(DayClock.DuskStartTick)); // начало сумерек — ещё 1,0
            Assert.Equal(307, DayClock.BackgroundX1024(DayClock.NightStartTick)); // вход в ночь — 0,3
            Assert.Equal(307, DayClock.BackgroundX1024((DayClock.NightStartTick + DayClock.DawnStartTick) / 2));
            Assert.Equal(307, DayClock.BackgroundX1024(DayClock.DawnStartTick)); // начало рассвета — 0,3

            // Середины переходов — середина между 1024 и 307 (≈ 665).
            int mid = (DayClock.DuskStartTick + DayClock.NightStartTick) / 2;
            Assert.InRange(DayClock.BackgroundX1024(mid), (1024 + 307) / 2 - tol, (1024 + 307) / 2 + tol);
            int midDawn = (DayClock.DawnStartTick + DayClock.TicksPerDay) / 2;
            Assert.InRange(DayClock.BackgroundX1024(midDawn), (1024 + 307) / 2 - tol, (1024 + 307) / 2 + tol);

            // Конец рассвета смыкается с днём (t = 1200 → t = 0).
            Assert.InRange(DayClock.BackgroundX1024(DayClock.TicksPerDay - 1), 1024 - tol, 1024);
            Assert.Equal(DayClock.BackgroundX1024(0), DayClock.BackgroundX1024(DayClock.TicksPerDay));

            // Линейность сумерек: шаг фона на такт в середине перехода ≈ 717/1800, монотонно убывает.
            int prev = DayClock.BackgroundX1024(DayClock.DuskStartTick);
            for (int t = DayClock.DuskStartTick + 1; t <= DayClock.NightStartTick; t++)
            {
                int bg = DayClock.BackgroundX1024(t);
                Assert.True(bg <= prev, $"сумерки: фон растёт на t = {t}");
                Assert.True(prev - bg <= 2, $"сумерки: скачок фона {prev - bg} на t = {t} (ступенька)");
                prev = bg;
            }

            prev = DayClock.BackgroundX1024(DayClock.DawnStartTick);
            for (int t = DayClock.DawnStartTick + 1; t < DayClock.TicksPerDay; t++)
            {
                int bg = DayClock.BackgroundX1024(t);
                Assert.True(bg >= prev, $"рассвет: фон убывает на t = {t}");
                Assert.True(bg - prev <= 2, $"рассвет: скачок фона {bg - prev} на t = {t} (ступенька)");
                prev = bg;
            }
        }

        // ---------- Часы в симуляции ----------

        [Fact]
        public void D14_Clock_Advances_With_Tick_Wraps()
        {
            var sim = new Simulation(Flat(1));
            Assert.Equal(0, sim.WorldTime); // старт: рассвет позади, полный день
            sim.Step();
            Assert.Equal(1, sim.WorldTime);
            for (int i = 0; i < 999; i++)
            {
                sim.Step();
            }

            Assert.Equal(1000, sim.WorldTime);

            // Оборот суток: полный цикл тактами — медленно; проверяем свойство обворота на ускоренных часах.
            sim.DayScale = 5_000;
            for (int i = 0; i < 8; i++)
            {
                sim.Step();
            }

            Assert.Equal((1000 + 8 * 5_000) % DayClock.TicksPerDay, sim.WorldTime);
            Assert.InRange(sim.WorldTime, 0, DayClock.TicksPerDay - 1);
        }

        [Fact]
        public void D14_Clock_Stands_When_No_Step()
        {
            // Пауза (0-036) — sim.Step() не вызывается: часы стоят сами, спец-логики нет.
            var sim = new Simulation(Flat(1));
            sim.Step();
            int t = sim.WorldTime;
            for (int i = 0; i < 100; i++)
            {
                _ = sim.World;
            }

            Assert.Equal(t, sim.WorldTime);
        }

        [Fact]
        public void D14_DayScale_Zero_Stands()
        {
            var sim = new Simulation(Flat(1)) { DayScale = 0 };
            sim.Step();
            sim.Step();
            Assert.Equal(0, sim.WorldTime);
        }

        // ---------- Хэш состояния ----------

        [Fact]
        public void D14_StateHash_Includes_WorldTime()
        {
            WorldModel world = Flat(2);
            var a = new Simulation(world);
            var b = new Simulation(Flat(2)); // та же форма/зерно, другой мир-объект
            Assert.Equal(StateHash.Compute(a).Total, StateHash.Compute(b).Total); // время одинаковое — хэш равный

            b.SetWorldTime(23_400); // ночь
            Assert.NotEqual(StateHash.Compute(a).Total, StateHash.Compute(b).Total);

            // Формула «мир + такт + сущности» без времени (перегрузка = 0) совпадает с t = 0.
            Assert.Equal(StateHash.Compute(a).Total, StateHash.Compute(world, 0, a.Entities).Total);
        }

        // ---------- Сохранение ----------

        [Fact]
        public void D14_Save_Roundtrip_WorldTime()
        {
            SaveFixtures.LiveWorld live = SaveFixtures.Create(1024, 0x5114, bots: 2);
            for (int i = 0; i < 900; i++)
            {
                live.Sim.Step();
            }

            Assert.Equal(900, live.Sim.WorldTime);

            StateHashResult before = StateHash.Compute(live.Sim);
            byte[] save = WorldSave.Build(WorldSave.Capture(live.Sim, live.BaseSha, "тест"), 11, 1_762_400_000_000);
            Simulation loaded = SaveFixtures.Load(live.BaseBytes, save);
            Assert.Equal(live.Sim.WorldTime, loaded.WorldTime);
            Assert.Equal(before.Total, StateHash.Compute(loaded).Total);

            // Побайтно: сохранение загруженного — тот же файл.
            byte[] again = WorldSave.Build(WorldSave.Capture(loaded, live.BaseSha, "тест"), 11, 1_762_400_000_000);
            Assert.Equal(save, again);
        }

        [Fact]
        public void D14_Save_Zero_Time_Old_File_And_Old_Save_Loads_Zero()
        {
            // t = 0 — поле МИР 10 не пишется: файл как до 1-014; загрузка даёт 0.
            SaveFixtures.LiveWorld live = SaveFixtures.Create(256, 0x5114, bots: 0);
            byte[] save = WorldSave.Build(WorldSave.Capture(live.Sim, live.BaseSha, "нулевой"), 1, 0);
            Simulation loaded = SaveFixtures.Load(live.BaseBytes, save);
            Assert.Equal(0, loaded.WorldTime);
            Assert.Equal(StateHash.Compute(live.Sim).Total, StateHash.Compute(loaded).Total);
        }

        // ---------- Формула L и порог (01 6) ----------

        [Fact]
        public void D14_Light_Formula()
        {
            // День, луг (k = 1,0): фон 1,0 → 1024.
            Assert.Equal(1024, DayClock.TileLightX1024(1024, 1024, 0));

            // Clamp сверху: день + источник.
            Assert.Equal(1024, DayClock.TileLightX1024(1024, 1024, 1024));

            // Clamp снизу: фон − 500 (источник «отрицательный» не уходит в минус).
            Assert.Equal(0, DayClock.TileLightX1024(307, 1024, -500));

            // Сумма: ночь 307 + источники 300 → 607.
            Assert.Equal(607, DayClock.TileLightX1024(307, 1024, 300));

            // k_биома: лес 0,85 → ночь 307 × 870 / 1024 = 260.
            Assert.Equal(870, DayClock.BiomeKX1024(1)); // лес
            Assert.Equal(717, DayClock.BiomeKX1024(2)); // джунгли
            Assert.Equal(1024, DayClock.BiomeKX1024(0)); // луг
            Assert.Equal(307 * 870 / 1024, DayClock.TileLightX1024(307, DayClock.BiomeKX1024(1), 0));
        }

        [Fact]
        public void D14_Visibility_Threshold()
        {
            Assert.Equal(277, DayClock.VisibilityThresholdX1024); // 0,27
            Assert.False(DayClock.IsVisible(276));
            Assert.True(DayClock.IsVisible(277));

            // Ночь на лугу (0,3 → 307) — видно; ночь в лесу (307 × 0,85 = 260) — уже нет (<c>01</c> 6).
            Assert.True(DayClock.IsVisible(DayClock.TileLightX1024(307, 1024, 0)));
            Assert.False(DayClock.IsVisible(DayClock.TileLightX1024(307, DayClock.BiomeKX1024(1), 0)));

            // Факел посреди ночного леса поднимает свет выше порога.
            Assert.True(DayClock.IsVisible(DayClock.TileLightX1024(307, DayClock.BiomeKX1024(1), 300)));
        }
    }
}
