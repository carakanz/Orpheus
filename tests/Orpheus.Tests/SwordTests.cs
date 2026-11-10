using System;
using System.Collections.Generic;
using Orpheus.Core.Combat;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Sim;
using Orpheus.Core.Trig;
using Orpheus.Core.World;
using Xunit;

namespace Orpheus.Tests
{
    /// <summary>
    /// Карточка 1-004 «меч» (docs/02 1.2, 3.1, 3.2, 3.4, 6.6): ядро удара в кадрах 60 Гц —
    /// фазы 9/9/21 (третий отход 31), серия ×1,0/×1,0/×1,3 (урон 10/10/13), буфер нажатия ≤12 кадров,
    /// пауза &gt; 0,8 с — серия сначала, отмена уворотом, бег 70 %, сектор 120° и линия удара
    /// (забор — бить можно, стена — нет), PvE, один удар = одно попадание, отбрасывание 1/1/2 тайла,
    /// удар через шов тора. Фаза A: ядро <see cref="MeleeAction"/> + <see cref="Simulation"/>.
    /// </summary>
    public class SwordTests
    {
        private const int U = Torus.SubTilesPerTile;
        private static readonly MapShape Map = MapShape.World(64);

        private const int Reach = SwordStrike.ReachUnits;
        private const int Arc = SwordStrike.HalfArcSteps;

        private static WorldPos At(int x, int y) => new WorldPos(x * U + U / 2, y * U + U / 2);

        private static WorldModel Flat(ulong seed) => FlatWorldGenerator.Instance.Generate(MapShape.World(64), seed);

        // -------------------------------------------------------- mover: фазы

        private static Mover SwordMover(int tx, int ty) => Mover.At(Map, At(tx, ty));

        /// <summary>Кадры post-step фаз одного удара: 9 замаха, 9 удара, 21 отхода, покой.</summary>
        [Fact]
        public void D02_S3_2_Phases_Timing9921()
        {
            var me = SwordMover(20, 32);
            int windup = 0, strike = 0, recovery = 0, firstStrike = -1, firstRecovery = -1;
            for (int f = 0; f < 40; f++)
            {
                me.StepFrame(Map, Input8.None, false, f == 0, 0, Collision.Empty);
                switch (me.Melee.Phase)
                {
                    case MeleeAction.PhaseWindup:
                        windup++;
                        break;
                    case MeleeAction.PhaseStrike:
                        strike++;
                        if (firstStrike < 0) firstStrike = f;
                        break;
                    case MeleeAction.PhaseRecover:
                        recovery++;
                        if (firstRecovery < 0) firstRecovery = f;
                        break;
                }
            }
            Assert.Equal(9, windup);                     // замах 0,15 с (9 кадров)
            Assert.Equal(9, strike);                     // удар 0,15 с
            Assert.Equal(21, recovery);                  // отход 0,35 с
            Assert.Equal(9, firstStrike);                // удар виден на 9-м кадре после нажатия
            Assert.Equal(18, firstRecovery);
            Assert.Equal(MeleeAction.PhaseIdle, me.Melee.Phase); // кадр 39 — покой, цикл 39 кадров
        }

        /// <summary>Удержание кнопки — серия: индексы 0,1,2 и снова 0; третий отход 31 кадр (docs 02 3.2).</summary>
        [Fact]
        public void D02_S3_2_Combo_Indexes012_ThirdRecover31()
        {
            var me = SwordMover(20, 32);
            var indexes = new List<int>();
            var recoverRuns = new List<int>();
            int seenIndex = -1;
            int run = 0;
            for (int f = 0; f < 140; f++)
            {
                me.StepFrame(Map, Input8.None, false, true, 0, Collision.Empty);
                byte p = me.Melee.Phase;
                if (p == MeleeAction.PhaseWindup && seenIndex != me.Melee.Index)
                {
                    indexes.Add(me.Melee.Index);
                    seenIndex = me.Melee.Index;
                }
                if (p == MeleeAction.PhaseRecover)
                {
                    if (run == 0) run = 1;
                    else run++;
                }
                else if (run > 0)
                {
                    recoverRuns.Add(run);
                    run = 0;
                }
            }
            Assert.Equal(new[] { 0, 1, 2, 0 }, indexes);          // серия зациклена
            Assert.Equal(new[] { 21, 21, 31 }, recoverRuns);      // после третьего удара отход ×1,5
        }

        /// <summary>Нажатие за 12 кадров до конца отхода — второй удар серии сразу (docs 02 1.2, 3.2).</summary>
        [Fact]
        public void D02_S1_2_Buffer_PressAtFrame27_ContinuesCombo()
        {
            var me = SwordMover(20, 32);
            for (int f = 0; f <= 39; f++)
            {
                bool press = f == 0 || f == 27; // кадры отхода 18…38: FramesLeft = 39 − f ≤ 12 с f = 27
                me.StepFrame(Map, Input8.None, false, press, 0, Collision.Empty);
            }
            Assert.Equal(MeleeAction.PhaseWindup, me.Melee.Phase);
            Assert.Equal(1, me.Melee.Index); // это второй удар серии, без паузы
        }

        /// <summary>Нажатие за 18 кадров до конца отхода — удар НЕ начинается (docs 02 1.2: раньше 0,2 с — нет).</summary>
        [Fact]
        public void D02_S1_2_Buffer_PressTooEarly_Stops()
        {
            var me = SwordMover(20, 32);
            for (int f = 0; f < 45; f++)
            {
                bool press = f == 0 || f == 21; // FramesLeft = 18 > 12
                me.StepFrame(Map, Input8.None, false, press, 0, Collision.Empty);
            }
            Assert.Equal(MeleeAction.PhaseIdle, me.Melee.Phase);
        }

        /// <summary>Пауза больше 0,8 с в покое — серия начинается сначала (docs 02 3.2).</summary>
        [Fact]
        public void D02_S3_2_Combo_IdlePauseHalfSecondResets()
        {
            var me = SwordMover(20, 32);
            for (int f = 0; f <= 126; f++)
            {
                bool press = f == 0 || f == 27; // два удара серии: индексы 0, 1; после — индекс 2
                me.StepFrame(Map, Input8.None, false, press, 0, Collision.Empty);
            }
            Assert.Equal(MeleeAction.PhaseIdle, me.Melee.Phase);
            Assert.Equal(2, me.Melee.Index); // серия продолжилась бы третьим
            me.StepFrame(Map, Input8.None, false, true, 0, Collision.Empty); // 49 кадров покоя → сброс
            Assert.Equal(MeleeAction.PhaseWindup, me.Melee.Phase);
            Assert.Equal(0, me.Melee.Index);
        }

        /// <summary>Уворот из замаха: удар пропадает, индекса нет (приёмка 1-004).</summary>
        [Fact]
        public void D02_S3_2_CancelFromWindup_NoStrike()
        {
            var me = SwordMover(20, 32);
            me.StepFrame(Map, Input8.None, false, true, 0, Collision.Empty);
            for (int f = 1; f <= 4; f++)
                me.StepFrame(Map, Input8.East, false, false, 0, Collision.Empty);
            Assert.Equal(MeleeAction.PhaseWindup, me.Melee.Phase);
            me.StepFrame(Map, Input8.East, true, false, 0, Collision.Empty); // уворот
            Assert.False(me.Melee.Active);
            Assert.Equal(0, me.Melee.Index);
            for (int f = 0; f < 60; f++)
            {
                me.StepFrame(Map, Input8.None, false, false, 0, Collision.Empty);
                Assert.NotEqual(MeleeAction.PhaseStrike, me.Melee.Phase); // удара не будет
            }
        }

        /// <summary>Уворот из фазы удара: фаза досчитывается (docs 02 2.2 «Отмена действий»).</summary>
        [Fact]
        public void D02_S3_2_CancelFromStrike_PhaseCompletes()
        {
            var me = SwordMover(20, 32);
            for (int f = 0; f <= 10; f++)
                me.StepFrame(Map, Input8.East, f == 10, f == 0, 0, Collision.Empty);
            Assert.Equal(MeleeAction.PhaseStrike, me.Melee.Phase); // уворот фазу удара не снимает
            for (int f = 11; f <= 18; f++)
                me.StepFrame(Map, Input8.East, false, false, 0, Collision.Empty);
            Assert.Equal(MeleeAction.PhaseRecover, me.Melee.Phase); // досчиталась в отход
        }

        /// <summary>Уворот из отхода: удар засчитан (уже нанесён), индекс сохраняется (docs 02 2.2).</summary>
        [Fact]
        public void D02_S3_2_CancelFromRecovery_StrikeKept()
        {
            var me = SwordMover(20, 32);
            for (int f = 0; f <= 19; f++)
                me.StepFrame(Map, Input8.East, f == 19, f == 0, 0, Collision.Empty);
            Assert.False(me.Melee.Active);
            Assert.Equal(0, me.Melee.Index); // индекс сохранён, не обнулён как из замаха-в-начало
            for (int f = 0; f < 60; f++)
            {
                me.StepFrame(Map, Input8.None, false, false, 0, Collision.Empty);
                Assert.NotEqual(MeleeAction.PhaseStrike, me.Melee.Phase);
            }
        }

        /// <summary>Бег во время замаха/удара — 70 % скорости (docs 02 3.2).</summary>
        [Fact]
        public void D02_S3_2_Run_DuringSwing_SevenTYPercent()
        {
            var me = SwordMover(20, 32);
            me.StepFrame(Map, Input8.East, false, true, 0, Collision.Empty); // замах, кадр нажатия
            int x0 = me.Position.X;
            for (int f = 1; f <= 9; f++) // кадры 1…9 — замах/граница: скорость 70 %
                me.StepFrame(Map, Input8.East, false, false, 0, Collision.Empty);
            int during = me.Position.X - x0;
            for (int f = 10; f < 45; f++) // добить цикл до покоя
                me.StepFrame(Map, Input8.None, false, false, 0, Collision.Empty);
            for (int f = 0; f < 5; f++) // стабилизировать остаток на покое
                me.StepFrame(Map, Input8.None, false, false, 0, Collision.Empty);
            x0 = me.Position.X;
            for (int f = 0; f < 9; f++) // те же 9 кадров свободным бегом
                me.StepFrame(Map, Input8.East, false, false, 0, Collision.Empty);
            int free = me.Position.X - x0;
            Assert.True(during < free, $"в замахе бег быстрее свободного: {during} ≥ {free}");
            Assert.True(during * 100 >= free * 60 && during * 100 <= free * 80,
                $"70 % ожидается, получено {during * 100.0 / free:F2} %");
        }

        /// <summary>Origin удара фиксируется переходом «замах → удар» и не двигается бегом (docs 02 1.2:22).</summary>
        [Fact]
        public void D02_S1_2_Origin_LockedAtStrikeEntry()
        {
            var me = SwordMover(20, 32);
            for (int f = 0; f <= 9; f++)
                me.StepFrame(Map, Input8.East, false, f == 0, 0, Collision.Empty);
            Assert.Equal(MeleeAction.PhaseStrike, me.Melee.Phase);
            int lockedX = me.Melee.OriginX, lockedY = me.Melee.OriginY;
            Assert.Equal(me.Position.X, lockedX);
            Assert.Equal(me.Position.Y, lockedY);
            for (int f = 10; f <= 17; f++)
            {
                me.StepFrame(Map, Input8.East, false, false, 0, Collision.Empty);
                Assert.Equal(lockedX, me.Melee.OriginX);
                Assert.Equal(lockedY, me.Melee.OriginY);
            }
        }

        // ------------------------------------------------------- сектор (чистая геометрия)

        [Fact]
        public void D02_S3_1_Sector_Arc120Reach()
        {
            var east = Angle16.FromRaw(0);
            var o = At(20, 32);
            var s = new MeleeSector(o, east, Reach, Arc);
            Assert.True(s.Overlaps(Map, At(21, 32), Collision.RadiusUnits), "восток в 1 тайле — попадает");
            Assert.True(s.Overlaps(Map, At(21, 33), Collision.RadiusUnits), "45° — попадает (дуга 120°)");
            Assert.False(s.Overlaps(Map, At(20, 33), Collision.RadiusUnits), "90° — вне дуги");
            Assert.False(s.Overlaps(Map, At(22, 32), Collision.RadiusUnits), "2 тайла — не дотянуться (1,4 т)");
        }

        // ------------------------------------------------------------ симуляция: боёвка

        private sealed class SwordSim
        {
            public readonly Simulation Sim;
            public readonly int Char;
            public readonly int Enemy;
            public readonly int Slot;

            /// <param name="clearLine">Снимать природные объекты на линии (20…22,32). Тесты про
            /// стену/забор ставят свой объект на (21,32) — вторая правка того же тайла в пачке Busy (3.7).</param>
            public SwordSim(ulong seed = 7, bool clearLine = true)
            {
                Sim = new Simulation(Flat(seed));
                Clear(20, 32);
                if (clearLine)
                {
                    Clear(21, 32); // линия удара: без природных объектов (дерево/камень) между бойцами
                }

                Clear(22, 32);
                Char = Sim.AddEntity(At(20, 32));
                var inv = Inventory.StartingKit();
                inv.Selected = 3; // WoodenSword (1-002)
                Assert.True(Sim.SetInventory(Char, inv));
                Enemy = Sim.AddEnemy(new WorldPos(22 * U, 32 * U + U / 2)); // 1,5 тайла на восток
                Slot = Sim.SlotOf(Char);
            }

            /// <summary>Тайл без постройки (правка применится первым тактом).</summary>
            public void Clear(int tx, int ty) =>
                Assert.True(Sim.RequestEdit(new TileCoord(tx, ty), FloorId.Grass, ObjectId.Empty));

            /// <summary>
            /// Такт (2 кадра 60 Гц) с удержанием удара: игрок догоняет цель, пока та дальше 430 unit
            /// (после отбрасывания), иначе стоит; меч в кадрах — ячейка <see cref="SwordSlot"/>.
            /// Возвращает попадания (урон) по врагу.
            /// </summary>
            public void Tick(out List<int> hits)
            {
                hits = new List<int>();
                var me = Sim.GetEntity(Char).Mover.Position;
                var en = Sim.GetEntity(Enemy).Mover.Position;
                Input8 dir = Torus.Delta(Map, me, en).X > 430 ? Input8.East : Input8.None;
                Span<InputFrame> frames = stackalloc InputFrame[]
                {
                    new InputFrame(dir, InputButtons.Attack, 0, SwordSlot),
                    new InputFrame(dir, InputButtons.Attack, 0, SwordSlot),
                };
                Sim.SetFramesAt(Slot, frames);
                Sim.Step();
                foreach (SimEvent ev in Sim.Events)
                    if (ev.Kind == SimEventKind.Damage && ev.Victim == Enemy)
                        hits.Add(ev.Damage);
            }

            /// <summary>Ячейка быстрой панели с мечом — кадр ввода держит её, иначе ApplySlot даст кирку (1-002).</summary>
            public const byte SwordSlot = 3;
        }

        /// <summary>Приёмка 1-004: разбойник 35 HP умирает за 4 удара (10/10/13/10) в пределах 3 секунд.</summary>
        [Fact]
        public void Accept_Rogue35hp_DiesFromFourStrikes_Within3Seconds()
        {
            var s = new SwordSim();
            var hits = new List<int>();
            int deathTick = -1;
            for (int t = 1; t <= 90 && deathTick < 0; t++)
            {
                s.Tick(out var now);
                hits.AddRange(now);
                var en = s.Sim.GetEntity(s.Enemy);
                if (en.Health <= 0) deathTick = t;
            }
            Assert.True(deathTick > 0, "разбойник умер от ударов мечом");
            Assert.True(deathTick <= 90, $"смерть на такте {deathTick} — 3 с (90 тактов) не хватило");
            Assert.Equal(new[] { 10, 10, 13, 10 }, hits); // серия: 10/10/13 + первый удар второй серии 10
            Assert.Equal(Simulation.MaxHealthCharacter, s.Sim.GetEntity(s.Char).Health); // PvE: игрок не задет
        }

        /// <summary>Один удар = одно попадание (docs 02 3.4): 4 фазы удара — ровно 4 события урона.</summary>
        [Fact]
        public void D02_S3_4_StrikeHitsOnce_PerStrike()
        {
            var s = new SwordSim();
            int hits = 0;
            for (int t = 1; t <= 80; t++)
            {
                s.Tick(out var now);
                hits += now.Count;
                if (s.Sim.GetEntity(s.Enemy).Health <= 0) break; // труп не бьётся
            }
            Assert.Equal(4, hits); // 10+10+13+10 ≥ 35 → смерть на 4-м; больше попаданий за фазу удара нет
        }

        /// <summary>Забор — не преграда удара (docs 02 3.1:157, приёмка 1-004).</summary>
        [Fact]
        public void D02_S3_1_Fence_DoesNotBlockStrike()
        {
            var s = new SwordSim(clearLine: false);
            Assert.True(s.Sim.RequestEdit(new TileCoord(21, 32), FloorId.Grass, ObjectId.WoodFence));
            int hits = 0;
            for (int t = 1; t <= 40 && hits == 0; t++)
            {
                s.Tick(out var now);
                hits += now.Count;
            }
            Assert.True(hits > 0, "забор между бойцами — удар проходит");
        }

        /// <summary>Стена между бойцами глушит удар (docs 02 3.4, приёмка 1-004).</summary>
        [Fact]
        public void D02_S3_4_Wall_BlocksStrikeLine()
        {
            var s = new SwordSim(clearLine: false);
            Assert.True(s.Sim.RequestEdit(new TileCoord(21, 32), FloorId.Grass, ObjectId.StoneWall));
            int hits = 0;
            for (int t = 1; t <= 60; t++)
            {
                s.Tick(out var now);
                hits += now.Count;
            }
            Assert.Equal(0, hits);
        }

        /// <summary>Сектор 120° в симуляции: цель за 90° от прицела не задета (docs 02 3.1).</summary>
        [Fact]
        public void D02_S3_1_Sector90deg_AimEast_Misses()
        {
            var s = new SwordSim();
            var northEnemy = s.Sim.AddEnemy(At(20, 33)); // ровно север, 1 тайл — вне дуги
            _ = northEnemy;
            int northHits = 0;
            for (int t = 1; t <= 40; t++)
            {
                s.Tick(out var now);
                foreach (SimEvent ev in s.Sim.Events)
                    if (ev.Kind == SimEventKind.Damage && ev.Victim == northEnemy)
                        northHits++;
            }
            Assert.Equal(0, northHits);
        }

        /// <summary>PvE (docs 02 3.1:155): меч персонажа персонажа не задевает.</summary>
        [Fact]
        public void D02_S3_1_PvE_PlayerNotHit()
        {
            var s = new SwordSim();
            int second = s.Sim.AddEntity(At(21, 32)); // на линии удара, в секторе и в досягаемости
            int pvpHits = 0;
            for (int t = 1; t <= 40; t++)
            {
                s.Tick(out var now);
                foreach (SimEvent ev in s.Sim.Events)
                    if (ev.Kind == SimEventKind.Damage && ev.Victim == second)
                        pvpHits++;
            }
            Assert.Equal(0, pvpHits);
            Assert.Equal(Simulation.MaxHealthCharacter, s.Sim.GetEntity(second).Health);
        }

        /// <summary>Без ближнего оружия в руках удар не начинается (гейт 1-004: кирка в руках).</summary>
        [Fact]
        public void D02_S3_2_Gate_NoMeleeWeapon_NoSwing()
        {
            var s = new SwordSim();
            var inv = Inventory.StartingKit();
            inv.Selected = 0; // кирка
            Assert.True(s.Sim.SetInventory(s.Char, inv));
            int hits = 0;
            Span<InputFrame> frames = stackalloc InputFrame[]
            {
                new InputFrame(Input8.None, InputButtons.Attack, 0, 0), // кадр держит ячейку кирки (1-002)
                new InputFrame(Input8.None, InputButtons.Attack, 0, 0),
            };
            for (int t = 1; t <= 60; t++)
            {
                s.Sim.SetFramesAt(s.Slot, frames);
                s.Sim.Step();
                foreach (SimEvent ev in s.Sim.Events)
                    if (ev.Kind == SimEventKind.Damage && ev.Victim == s.Enemy)
                        hits++;
            }
            Assert.Equal(0, hits);
            Assert.False(s.Sim.GetEntity(s.Char).Mover.Melee.Active);
        }

        /// <summary>Отбрасывание: 1-й удар — база ÷ масса, 3-й — вдвое (docs 02 3.2: 1/1/2 тайла).</summary>
        [Fact]
        public void D02_S3_2_Knock_ThirdStrike_Double()
        {
            var s = new SwordSim();
            int first = 0, third = 0, hits = 0;
            for (int t = 1; t <= 80 && hits < 3; t++)
            {
                s.Tick(out var now);
                if (now.Count > 0)
                {
                    hits++;
                    int k = s.Sim.GetEntity(s.Enemy).Mover.KnockSpeedX10;
                    if (hits == 1) first = k;
                    if (hits == 3) third = k;
                }
            }
            Assert.Equal(3, hits);
            Assert.Equal(Mover.KnockSpeedFor(1), first); // 1 тайл за 9 кадров при массе 1
            Assert.Equal(first * 2, third);              // третий удар ×2 (2 тайла)
        }

        /// <summary>Удар работает через шов тора: цель за западной границей, атакующий у восточной (приёмка 1-004).</summary>
        [Fact]
        public void Accept_Strike_AcrossShardSeam()
        {
            var s = new SwordSim();
            // персонаж у восточного края (тайл 63), враг за швом (тайл 0): по тору в полтайла на востоке.
            s.Clear(63, 32);
            s.Clear(0, 32);
            int char2 = s.Sim.AddEntity(new WorldPos(63 * U - 40, 32 * U + U / 2));
            var inv = Inventory.StartingKit();
            inv.Selected = 3;
            Assert.True(s.Sim.SetInventory(char2, inv));
            int seamEnemy = s.Sim.AddEnemy(new WorldPos(56, 32 * U + U / 2));
            int slot = s.Sim.SlotOf(char2);
            int hits = 0;
            Span<InputFrame> frames = stackalloc InputFrame[]
            {
                new InputFrame(Input8.East, InputButtons.Attack, 0, SwordSim.SwordSlot),
                new InputFrame(Input8.East, InputButtons.Attack, 0, SwordSim.SwordSlot),
            };
            for (int t = 1; t <= 40 && hits == 0; t++)
            {
                s.Sim.SetFramesAt(slot, frames);
                s.Sim.Step();
                foreach (SimEvent ev in s.Sim.Events)
                    if (ev.Kind == SimEventKind.Damage && ev.Victim == seamEnemy)
                        hits++;
            }
            Assert.True(hits > 0, "удар через шов шардов засчитан");
        }
    }
}
