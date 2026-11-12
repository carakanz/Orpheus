using System;
using System.Collections.Generic;
using System.Diagnostics;
using Orpheus.Core.Bots;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Rng;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net;
using Orpheus.Net.Protocol;
using Orpheus.Net.Session;
using Orpheus.Net.Transport;
using Orpheus.Server;
using Xunit;
using Xunit.Abstractions;

namespace Orpheus.Tests
{
    /// <summary>
    /// Интерес по окну чанков (карточка 0-022; docs/07 4.4, 3.5, 4.3, 4.11 п. 1; риск 08 4.3 «сеть на торе»).
    /// </summary>
    public class InterestTests
    {
        private const int U = Torus.SubTilesPerTile;

        private readonly ITestOutputHelper _out;

        public InterestTests(ITestOutputHelper output)
        {
            _out = output;
        }

        // ---------- Замер ----------

        /// <summary>
        /// Замер карточки 0-022: время <c>SendSnapshots</c> (сборка обзора + запись снимков) на 8 игроках × 64 сущностях —
        /// все в одной окрестности («плотно») и враги по всей карте («врозь»); плюс врозь 512 — где перебор всех дорог.
        /// Только вывод, без порога.
        /// </summary>
        [Theory]
        [InlineData(true, 64)]
        [InlineData(false, 64)]
        [InlineData(false, 512)]
        public void D07_S4_4_Measure_ViewBuild_8Players64Entities(bool dense, int entities)
        {
            var world = FlatWorldGenerator.Instance.Generate(MapShape.World(1024), 1);
            var sim = new Simulation(world, maxEntities: entities);
            var t = new NetTests.ScriptedTransport();
            var server = new NetServer(t, sim, n => ServerRun.SpawnPoint(world.Shape, n), 8, NetModule.Version);
            for (int peer = 1; peer <= 8; peer++)
            {
                t.Connect(peer);
                t.Push(peer, NetChannel.Facts, NetTests.Encode(b => new LoginMessage { Version = NetModule.Version, Name = "p" + peer }.Write(b))); // имена разные (0-035)
            }

            server.Poll(0);
            Assert.Equal(8, server.PlayersInGame);

            var rng = new DetRandom(7);
            var bots = new SimBot[entities - 8];
            for (int i = 0; i < bots.Length; i++)
            {
                TileCoord tile = dense
                    ? new TileCoord(512 - 48 + rng.NextInt(96), 512 - 32 + rng.NextInt(64))
                    : new TileCoord(rng.NextInt(1024), rng.NextInt(1024));
                int id = sim.AddEntity(new WorldPos(tile.X * U + U / 2, tile.Y * U + U / 2));
                bots[i] = SimBot.Create(ShuttleBot.Name, id, i, world.Seed); // челнок: окрестность не расползается за долгий прогрев
            }

            // Прогрев не короче 0,5 с: многоуровневая JIT переписывает горячие методы (уровень 1) через ~100 мс работы.
            const int Measured = 3000;
            var sw = new Stopwatch();
            var warm = Stopwatch.StartNew();
            int warmup = -1;
            for (int k = 0; warmup < 0 || k < warmup + Measured; k++)
            {
                if (warmup < 0 && warm.ElapsedMilliseconds >= 500)
                {
                    warmup = k;
                }

                for (int i = 0; i < bots.Length; i++)
                {
                    int slot = sim.SlotOf(bots[i].EntityId);
                    sim.SetInputAt(slot, bots[i].Think(sim.Tick + 1, sim.Entities[slot]));
                }

                server.Poll(sim.Tick);
                sim.Step();
                if (warmup >= 0)
                {
                    sw.Start();
                }

                server.SendSnapshots();
                sw.Stop();
            }

            double usPerTick = sw.Elapsed.TotalMilliseconds * 1000 / Measured;
            _out.WriteLine($"{(dense ? "плотно" : "врозь")}, {entities} сущностей: SendSnapshots {usPerTick:0.0} мкс/такт ({usPerTick / 8:0.0} на игрока), " +
                           $"наибольший полный {server.MaxSnapshotBytes} байт, дельта {server.MaxDeltaBytes}, обрезано {server.SnapshotsTruncated}; " +
                           $"окно пересчитано {server.ViewWindowRecalcs}, гистерезисом оставлено {server.ViewKeptByHysteresis}, сортировок {server.ViewsSortedByDistance}");
        }

        // ---------- Сетка по чанкам (ядро) ----------

        /// <summary>
        /// Сетка сущностей по чанкам совпадает с перебором после тактов (бродят через границы чанков и шов), удаления
        /// (места сдвигаются) и загрузки; в чанке места по возрастанию.
        /// </summary>
        [Fact]
        public void D07_S4_4_Grid_MatchesBruteForce_AfterStepsRemoveRestore()
        {
            var shape = MapShape.World(256);
            var world = FlatWorldGenerator.Instance.Generate(shape, 3);
            var sim = new Simulation(world, maxEntities: 40);
            var rng = new DetRandom(11);
            var bots = new List<SimBot>();
            for (int i = 0; i < 40; i++)
            {
                int id = sim.AddEntity(Center(new TileCoord(rng.NextInt(48) - 24 + 256, rng.NextInt(48) - 24 + 256)));
                bots.Add(SimBot.Create(WanderBot.Name, id, i, 3));
            }

            Check(sim);
            for (int k = 0; k < 600; k++)
            {
                foreach (SimBot b in bots)
                {
                    int slot = sim.SlotOf(b.EntityId);
                    if (slot >= 0)
                    {
                        sim.SetInputAt(slot, b.Think(sim.Tick + 1, sim.Entities[slot]));
                    }
                }

                sim.Step();
                if (k % 100 == 50)
                {
                    Assert.True(sim.RemoveEntity(bots[k / 20].EntityId));
                }

                Check(sim);
            }

            _out.WriteLine($"переносов {sim.Grid.Moves}, непустых чанков {sim.Grid.ChunkCount}, сущностей {sim.EntityCount}");
            Assert.True(sim.Grid.Moves > 100, $"{sim.Grid.Moves}");

            var copy = new Simulation(world, maxEntities: 40);
            copy.Restore(sim.Tick, sim.Entities, sim.NextEntityId);
            Check(copy);

            static void Check(Simulation s)
            {
                ReadOnlySpan<SimEntity> es = s.Entities;
                int listed = 0;
                for (int i = 0; i < es.Length; i++)
                {
                    ChunkCoord c = Torus.ChunkOf(s.Shape, es[i].Mover.Position);
                    Assert.Equal(c, s.Grid.ChunkOf(i));
                    int prev = -1;
                    bool found = false;
                    for (int slot = s.Grid.First(c); slot >= 0; slot = s.Grid.Next(slot))
                    {
                        Assert.True(slot > prev, "места в чанке не по возрастанию");
                        prev = slot;
                        found |= slot == i;
                        if (slot == i)
                        {
                            listed++;
                        }
                    }

                    Assert.True(found, $"сущность на месте {i} не в своём чанке {c}");
                }

                Assert.Equal(es.Length, listed);
                int chunks = 0;
                for (int i = 0; i < es.Length; i++)
                {
                    if (s.Grid.First(s.Grid.ChunkOf(i)) == i)
                    {
                        chunks++;
                    }
                }

                Assert.Equal(chunks, s.Grid.ChunkCount); // пустые чанки убраны из словаря
            }
        }

        // ---------- Шов тора: враг за швом в снимке (риск 08 4.3) ----------

        /// <summary>
        /// Враг в 5 тайлах за швом — по X, по Y и на углу (оба шва), игрок по обе стороны шва: 6 случаев, враг в каждом
        /// снимке клиента (полных и дельтах) 90 тактов подряд.
        /// </summary>
        [Fact]
        public void D07_S4_4_SeamEnemy5Tiles_InEverySnapshot_X_Y_Corner_BothSides()
        {
            var shape = MapShape.World(1024);
            int w = shape.Width;
            int h = shape.Height;
            var cases = new (string Name, TileCoord Observer, TileCoord Enemy)[]
            {
                ("X, игрок слева от шва", new TileCoord(w - 3, 200), new TileCoord(2, 200)),
                ("X, игрок справа от шва", new TileCoord(2, 400), new TileCoord(w - 3, 400)),
                ("Y, игрок выше шва", new TileCoord(300, h - 3), new TileCoord(300, 2)),
                ("Y, игрок ниже шва", new TileCoord(600, 2), new TileCoord(600, h - 3)),
                ("угол, игрок у (W, H)", new TileCoord(w - 2, h - 2), new TileCoord(1, 2)),
                ("угол, игрок у (0, 0)", new TileCoord(1, 1), new TileCoord(w - 2, h - 3)),
            };

            var enemyIds = new int[cases.Length];
            using var rig = new Rig(shape, Array.ConvertAll(cases, c => Center(c.Observer)), npcs: sim =>
            {
                for (int i = 0; i < cases.Length; i++)
                {
                    enemyIds[i] = sim.AddEntity(Center(cases[i].Enemy));
                }
            });

            var seen = new int[cases.Length];
            var missed = new int[cases.Length];
            rig.Applied = (client, snap) =>
            {
                if (snap.TryFind(enemyIds[client], out _))
                {
                    seen[client]++;
                }
                else
                {
                    missed[client]++;
                }
            };
            rig.Login();
            rig.Run(90);

            int ok = 0;
            for (int i = 0; i < cases.Length; i++)
            {
                long d2 = Torus.Dist2(shape, Center(cases[i].Observer), Center(cases[i].Enemy));
                _out.WriteLine($"{cases[i].Name}: до врага {Math.Sqrt(d2) / U:0.0} тайла, в снимке {seen[i]}, нет {missed[i]}");
                Assert.Equal(25L * U * U, d2);
                if (missed[i] == 0 && seen[i] >= 90)
                {
                    ok++;
                }
            }

            _out.WriteLine($"враг за швом в снимке: {ok} из {cases.Length}; дельт у клиентов {SumDeltas(rig)}");
            Assert.Equal(cases.Length, ok);
            Assert.True(SumDeltas(rig) > 0, "дельты не проверены");
        }

        // ---------- Гистерезис ----------

        /// <summary>
        /// Игрок стоит; три сущности ходят туда-сюда по 1 тайлу через край блока 6 × 4 (правый и нижний край окна):
        /// одна начинает снаружи. За 300 тактов у клиента входов и выходов каждой ≤ 1 (4.4: уходит, только выйдя из 8 × 6).
        /// </summary>
        [Fact]
        public void D07_S4_4_EdgeShuttle_1Tile_NoFlicker_AtMostOneTransitionPer300Ticks()
        {
            var shape = MapShape.World(1024);
            WorldPos observer = Center(new TileCoord(515, 515));
            ChunkCoord c = ChunkWindow.BlockCenter(shape, observer);
            int edgeX = (c.X + ChunkWindow.Width / 2) * Torus.ChunkSize;  // первый тайл правее блока 6 × 4
            int edgeY = (c.Y + ChunkWindow.Height / 2) * Torus.ChunkSize; // первый тайл ниже блока
            var ids = new int[3];
            using var rig = new Rig(shape, new[] { observer }, npcs: sim =>
            {
                ids[0] = sim.AddEntity(Center(new TileCoord(edgeX - 1, 500))); // внутри, ходит через правый край
                ids[1] = sim.AddEntity(Center(new TileCoord(edgeX, 530)));     // снаружи, ходит через правый край
                ids[2] = sim.AddEntity(Center(new TileCoord(520, edgeY - 1))); // внутри, ходит через нижний край
            });
            var axisX = new[] { true, true, false };
            var edges = new[] { edgeX, edgeX, edgeY };

            var present = new bool?[3];
            var transitions = new int[3];
            rig.Applied = (cl, snap) =>
            {
                for (int i = 0; i < 3; i++)
                {
                    bool now = snap.TryFind(ids[i], out _);
                    if (present[i].HasValue && present[i] != now)
                    {
                        transitions[i]++;
                    }

                    present[i] = now;
                }
            };
            rig.Login();
            var dir = new[] { 1, -1, 1 };
            int turns = 0;
            for (int t = 0; t < 300; t++)
            {
                rig.Tick(() =>
                {
                    for (int i = 0; i < 3; i++)
                    {
                        int slot = rig.Sim.SlotOf(ids[i]);
                        WorldPos p = rig.Sim.Entities[slot].Mover.Position;
                        int at = axisX[i] ? p.X : p.Y;
                        int edge = edges[i] * U;
                        // Туда-обратно между краем − 0,5 и краем + 0,5 тайла (шаг такта 0,2 тайла).
                        if (dir[i] > 0 && at >= edge + U / 2)
                        {
                            dir[i] = -1;
                            turns++;
                        }
                        else if (dir[i] < 0 && at <= edge - U / 2)
                        {
                            dir[i] = 1;
                            turns++;
                        }

                        Input8 d = axisX[i] ? (dir[i] > 0 ? Input8.East : Input8.West) : (dir[i] > 0 ? Input8.South : Input8.North);
                        rig.Sim.SetInputAt(slot, new InputFrame(d));
                    }
                });
            }

            _out.WriteLine($"край блока x = {edgeX}, y = {edgeY}; разворотов {turns}; входов/выходов: {transitions[0]}, {transitions[1]}, {transitions[2]}; " +
                           $"оставлено гистерезисом {rig.Server.ViewKeptByHysteresis}");
            Assert.True(turns >= 60, $"{turns}");
            for (int i = 0; i < 3; i++)
            {
                Assert.True(transitions[i] <= 1, $"сущность {i}: {transitions[i]} входов/выходов за 300 тактов");
                Assert.True(present[i] == true, $"сущность {i} не в обзоре в конце");
            }
        }

        /// <summary>
        /// Вне блока 8 × 6 в снимке никого: стоящие сразу за блоком (по X и по Y с обеих сторон, на углу) не появляются
        /// ни разу; сущность между окном и блоком 8 × 6, не бывшая в обзоре, — тоже. Бывшая в обзоре уходит из окна и
        /// остаётся, пока не вышла из 8 × 6, затем исчезает у клиента (дельта «исчезнувшая, номером», 4.3).
        /// </summary>
        [Fact]
        public void D07_S4_4_OutsideKeepBlock_NeverInSnapshot_LeaverRemovedAtKeepEdge()
        {
            var shape = MapShape.World(1024);
            WorldPos observer = Center(new TileCoord(515, 515));
            ChunkCoord c = ChunkWindow.BlockCenter(shape, observer);
            int left = (c.X - ChunkWindow.KeepWidth / 2) * Torus.ChunkSize;  // первый тайл блока 8 × 6
            int right = (c.X + ChunkWindow.KeepWidth / 2) * Torus.ChunkSize; // первый тайл правее блока
            int top = (c.Y - ChunkWindow.KeepHeight / 2) * Torus.ChunkSize;
            int bottom = (c.Y + ChunkWindow.KeepHeight / 2) * Torus.ChunkSize;
            int windowRight = (c.X + ChunkWindow.Width / 2) * Torus.ChunkSize;
            var outside = new List<int>();
            int between = 0;
            int leaver = 0;
            using var rig = new Rig(shape, new[] { observer }, npcs: sim =>
            {
                outside.Add(sim.AddEntity(Center(new TileCoord(right, 515))));
                outside.Add(sim.AddEntity(Center(new TileCoord(left - 1, 515))));
                outside.Add(sim.AddEntity(Center(new TileCoord(515, bottom))));
                outside.Add(sim.AddEntity(Center(new TileCoord(515, top - 1))));
                outside.Add(sim.AddEntity(Center(new TileCoord(right, bottom))));
                between = sim.AddEntity(Center(new TileCoord(windowRight + 2, 520)));
                leaver = sim.AddEntity(Center(new TileCoord(windowRight - 4, 510)));
            });

            int outsideSeen = 0;
            int betweenSeen = 0;
            long lastSeenX = -1;
            int leaverMissingInside = 0;
            rig.Applied = (cl, snap) =>
            {
                foreach (int id in outside)
                {
                    if (snap.TryFind(id, out _))
                    {
                        outsideSeen++;
                    }
                }

                if (snap.TryFind(between, out _))
                {
                    betweenSeen++;
                }

                WorldPos truth = rig.History[Rig.Key(leaver, snap.Tick)];
                if (snap.TryFind(leaver, out SnapshotEntity e))
                {
                    lastSeenX = e.Position.X;
                }
                else if (truth.X < right * U)
                {
                    leaverMissingInside++;
                }
            };
            rig.Login();
            rig.Sim.SetInput(leaver, new InputFrame(Input8.East));
            rig.Run(250); // 50 тайлов на восток: из окна через 4 тайла, из 8 × 6 — через 36

            WorldPos end = rig.Sim.GetEntity(leaver).Mover.Position;
            _out.WriteLine($"блок 8 × 6: x ∈ [{left}, {right}), y ∈ [{top}, {bottom}); уходящий виден до x = {lastSeenX / (double)U:0.00}, " +
                           $"сейчас {end.X / (double)U:0.0}; оставлено гистерезисом {rig.Server.ViewKeptByHysteresis}");
            Assert.Equal(0, outsideSeen);
            Assert.Equal(0, betweenSeen);
            Assert.Equal(0, leaverMissingInside);
            Assert.True(end.X > (right + 5) * U);
            Assert.InRange(lastSeenX, (right - 1) * U, right * U - 1); // виден до края 8 × 6, не дальше
            Assert.False(rig.Clients[0].Latest.TryFind(leaver, out _));
        }

        // ---------- Пересчёт окна ----------

        /// <summary>
        /// Бег на восток зигзагом через границу чанков (смена чанка персонажа каждые ~6 тактов) и через середину чанков
        /// (там меняется центр блока окна, 3.5). Пересчётов окна ≤ смен центра блока окна; вдоль границы — ещё и ≤ смен
        /// чанка персонажа (4.4: пересчёт при переходе границы чанка, а не каждый такт).
        /// </summary>
        [Theory]
        [InlineData(0)]
        [InlineData(16)]
        public void D07_S4_4_RunAlongChunkBorder_WindowRecalcs_LeqChunkChanges(int offsetInChunk)
        {
            var shape = MapShape.World(1024);
            int y = 16 * Torus.ChunkSize + offsetInChunk;
            using var rig = new Rig(shape, new[] { new WorldPos(500 * U + U / 2, y * U - U / 4) });
            rig.Login();
            int id = rig.Clients[0].EntityId;
            WorldPos p0 = rig.Sim.GetEntity(id).Mover.Position;
            ChunkCoord prevChunk = Torus.ChunkOf(shape, p0);
            ChunkCoord prevCenter = ChunkWindow.BlockCenter(shape, p0);
            int chunkChanges = 0;
            int centerChanges = 0;
            long recalcs0 = rig.Server.ViewWindowRecalcs;
            for (int t = 0; t < 600; t++)
            {
                rig.Inputs[0] = new InputFrame((t / 5) % 2 == 0 ? Input8.SouthEast : Input8.NorthEast);
                rig.Tick();
                WorldPos p = rig.Sim.GetEntity(id).Mover.Position;
                ChunkCoord ch = Torus.ChunkOf(shape, p);
                ChunkCoord ce = ChunkWindow.BlockCenter(shape, p);
                chunkChanges += ch != prevChunk ? 1 : 0;
                centerChanges += ce != prevCenter ? 1 : 0;
                prevChunk = ch;
                prevCenter = ce;
            }

            long recalcs = rig.Server.ViewWindowRecalcs - recalcs0;
            WorldPos end = rig.Sim.GetEntity(id).Mover.Position;
            _out.WriteLine($"вдоль y = {y}: пробежал {Torus.Delta(shape, p0, end).X / (double)U:0} тайлов; смен чанка {chunkChanges}, " +
                           $"смен центра окна {centerChanges}, пересчётов окна {recalcs}");
            Assert.True(Torus.Delta(shape, p0, end).X > 60 * U);
            Assert.True(recalcs <= centerChanges, $"{recalcs} > {centerChanges}");
            if (offsetInChunk == 0)
            {
                Assert.True(chunkChanges >= 50, $"{chunkChanges}");
                Assert.True(recalcs <= chunkChanges, $"{recalcs} > {chunkChanges}");
            }
            else
            {
                Assert.True(centerChanges >= 50, $"{centerChanges}");
            }
        }

        // ---------- Обрезка: ближние первыми ----------

        /// <summary>
        /// Обзор больше 1200 байт: игрок у шва, 70 дальних сущностей (номера меньше) на его стороне и 90 ближних за швом.
        /// Полный снимок несёт ближайшие: самая дальняя в снимке не дальше самой ближней невлезшей.
        /// </summary>
        [Fact]
        public void D07_S4_4_Truncation_FullSnapshot_NearestFirst_AcrossSeam()
        {
            var shape = MapShape.World(1024);
            WorldPos observer = Center(new TileCoord(1022, 300));
            var all = new List<int>();
            using var rig = new Rig(shape, new[] { observer }, capacity: 200, npcs: sim =>
            {
                for (int i = 0; i < 70; i++)
                {
                    all.Add(sim.AddEntity(Center(new TileCoord(1022 - 40 - i % 20, 290 + i / 20 * 5))));
                }

                for (int i = 0; i < 90; i++)
                {
                    all.Add(sim.AddEntity(Center(new TileCoord(2 + i % 15, 290 + i / 15 * 3))));
                }
            });
            rig.Server.KeyframePeriod = 1; // одни полные снимки
            int checkedSnaps = 0;
            rig.Applied = (cl, snap) =>
            {
                long maxIn = 0;
                long minOut = long.MaxValue;
                foreach (int id in all)
                {
                    long d2 = Torus.Dist2(shape, observer, rig.History[Rig.Key(id, snap.Tick)]);
                    if (snap.TryFind(id, out _))
                    {
                        maxIn = Math.Max(maxIn, d2);
                    }
                    else
                    {
                        minOut = Math.Min(minOut, d2);
                    }
                }

                Assert.True(maxIn <= minOut, $"такт {snap.Tick}: в снимке есть на {Math.Sqrt(maxIn) / U:0.0} тайла, а не влезла на {Math.Sqrt(minOut) / U:0.0}");
                Assert.True(snap.Count < all.Count + 1, "обрезки нет — тест ничего не проверяет");
                checkedSnaps++;
            };
            rig.Login();
            rig.Run(10);
            _out.WriteLine($"снимков проверено {checkedSnaps}; в снимке {rig.Clients[0].Latest.Count} из {all.Count + 1}; " +
                           $"наибольший {rig.Server.MaxSnapshotBytes} байт; обрезано {rig.Server.SnapshotsTruncated}");
            Assert.True(checkedSnaps >= 10);
            Assert.True(rig.Server.MaxSnapshotBytes <= NetSettings.SnapshotBudget);
        }

        /// <summary>
        /// Дельта больше 1200 байт: 240 сущностей бегут кольцом радиусом 22 тайла вокруг игрока у шва
        /// (половина дуги — за швом), каждая меняет позицию каждый такт. Граница обрезки попадает в плотную
        /// толпу, где северники и южники меняются местами каждый такт. В каждой дельте обновлены ближние:
        /// самая дальняя свежая (позиция = позиции на сервере в такт снимка) не дальше самой ближней устаревшей.
        /// </summary>
        [Fact]
        public void D07_S4_4_Truncation_Delta_NearestUpdatedFirst()
        {
            var shape = MapShape.World(1024);
            WorldPos observer = Center(new TileCoord(1022, 300));
            var all = new List<int>();
            using var rig = new Rig(shape, new[] { observer }, capacity: 250, npcs: sim =>
            {
                for (int i = 0; i < 240; i++)
                {
                    double a = Math.PI * 2 * i / 240;
                    int x = Wrap(observer.X / U + (int)Math.Round(22 * Math.Cos(a)));
                    int y = Wrap(observer.Y / U + (int)Math.Round(22 * Math.Sin(a)));
                    all.Add(sim.AddEntity(Center(new TileCoord(x, y))));
                }
            });
            rig.Server.KeyframePeriod = 100000;
            int deltaSnaps = 0;
            int withStale = 0;
            long prevDeltas = 0;
            rig.Applied = (cl, snap) =>
            {
                long deltas = rig.Clients[0].DeltasApplied;
                bool isDelta = deltas != prevDeltas;
                prevDeltas = deltas;
                if (!isDelta)
                {
                    return;
                }

                long maxFresh = 0;
                long minStale = long.MaxValue;
                for (int i = 1; i < snap.Count; i++)
                {
                    SnapshotEntity e = snap.Entities[i];
                    WorldPos truth = rig.History[Rig.Key(e.Id, snap.Tick)];
                    long d2 = Torus.Dist2(shape, observer, truth);
                    if (e.Position == SnapshotLayout.Quantize(truth))
                    {
                        maxFresh = Math.Max(maxFresh, d2);
                    }
                    else
                    {
                        minStale = Math.Min(minStale, d2);
                    }
                }

                deltaSnaps++;
                if (minStale != long.MaxValue)
                {
                    withStale++;
                    Assert.True(maxFresh <= minStale, $"такт {snap.Tick}: свежая на {Math.Sqrt(maxFresh) / U:0.0} тайла, устаревшая на {Math.Sqrt(minStale) / U:0.0}");
                }
            };
            rig.Login();
            foreach (int id in all)
            {
                rig.Sim.SetInput(id, new InputFrame(id % 2 == 0 ? Input8.North : Input8.South));
            }

            rig.Run(40);
            _out.WriteLine($"дельт {deltaSnaps}, из них с устаревшими {withStale}; наибольшая дельта {rig.Server.MaxDeltaBytes} байт; обрезано {rig.Server.SnapshotsTruncated}");
            Assert.True(withStale >= 10, $"{withStale}");
            Assert.True(rig.Server.MaxDeltaBytes <= NetSettings.SnapshotBudget);
        }

        // ---------- Наблюдатель у шва (приёмка 0.2, тестовая часть) ----------

        /// <summary>
        /// Наблюдатель стоит у шва; сущность бежит на восток через шов базовым бегом. Клиент кладёт снимки в
        /// <see cref="RemoteInterpolation"/>, кадр показа — раз в 1/30 с: смещение бегущего за кадр ≤ 6 × 1/30 × 1,1 = 0,22 тайла
        /// во всех кадрах; бегущий есть в каждом кадре и пересекает шов.
        /// </summary>
        [Fact]
        public void D07_S4_4_SeamObserver_RunnerThroughSeam_StepPer30th_Leq022Tile()
        {
            var shape = MapShape.World(1024);
            int runner = 0;
            using var rig = new Rig(shape, new[] { Center(new TileCoord(1023, 300)) }, npcs: sim =>
            {
                runner = sim.AddEntity(Center(new TileCoord(1023 - 40, 302)));
            });
            var interp = new RemoteInterpolation(shape);
            rig.Applied = (cl, snap) => interp.OnSnapshot(snap, rig.NowUs);
            rig.Login();
            rig.Sim.SetInput(runner, new InputFrame(Input8.East));

            double maxStep = 0;
            int frames = 0;
            int seamFrames = 0;
            bool has = false;
            double prevX = 0;
            double prevY = 0;
            double limit = 6.0 / 30 * 1.1 * U;
            double wu = shape.WidthUnits;
            for (int t = 0; t < 400; t++)
            {
                rig.Tick();
                if (!interp.BeginFrame(rig.NowUs + 10_000))
                {
                    continue;
                }

                Assert.True(interp.TryGet(runner, out WorldPos from, out float dx, out float dy), $"такт {t}: бегущего нет в показе");
                double x = from.X + (double)dx;
                double y = from.Y + (double)dy;
                if (has)
                {
                    double sx = x - prevX;
                    double sy = y - prevY;
                    if (Math.Abs(sx) > wu / 2)
                    {
                        seamFrames++;
                        sx -= Math.Sign(sx) * wu;
                    }

                    double step = Math.Sqrt(sx * sx + sy * sy);
                    maxStep = Math.Max(maxStep, step);
                    Assert.True(step <= limit, $"такт {t}: смещение {step / U:0.000} тайла > 0,22");
                }

                has = true;
                prevX = x;
                prevY = y;
                frames++;
            }

            WorldPos end = rig.Sim.GetEntity(runner).Mover.Position;
            _out.WriteLine($"кадров {frames}, через шов {seamFrames}; наибольшее смещение за 1/30 с {maxStep / U:0.000} тайла (предел 0,22); " +
                           $"бегущий в конце x = {end.X / (double)U:0.0}");
            Assert.True(frames >= 380, $"{frames}");
            Assert.True(seamFrames >= 1, "бегущий не пересёк шов в показе");
        }

        // ---------- 4.11 п. 1: снимок 8 ботов ----------

        /// <summary>
        /// 8 ботов (wander) в одной окрестности, 900 тактов: каждый снимок (полный и дельта) ≤ 1200 байт, обрезок нет —
        /// 100 % тактов (4.11 п. 1; врагов в вехе 0 нет).
        /// </summary>
        [Fact]
        public void D07_S4_11_1_EightBots_OneNeighbourhood_EverySnapshot_Within1200()
        {
            var shape = MapShape.World(1024);
            var spawn = new WorldPos[8];
            for (int i = 0; i < 8; i++)
            {
                spawn[i] = ServerRun.SpawnPoint(shape, i);
            }

            using var rig = new Rig(shape, spawn);
            rig.Login();
            var bots = new SimBot[8];
            for (int i = 0; i < 8; i++)
            {
                bots[i] = SimBot.Create(WanderBot.Name, rig.Clients[i].EntityId, i, 1);
            }

            int maxCount = 0;
            rig.Applied = (cl, snap) => maxCount = Math.Max(maxCount, snap.Count);
            for (int t = 0; t < 900; t++)
            {
                for (int i = 0; i < 8; i++)
                {
                    rig.Inputs[i] = bots[i].Think(rig.Sim.Tick + 1, rig.Sim.GetEntity(rig.Clients[i].EntityId));
                }

                rig.Tick();
            }

            NetServer s = rig.Server;
            _out.WriteLine($"снимков {s.SnapshotsSent} (дельт {s.DeltasSent}, полных {s.FullSent}); наибольший полный {s.MaxSnapshotBytes} байт, " +
                           $"наибольшая дельта {s.MaxDeltaBytes}; обрезано {s.SnapshotsTruncated}; сущностей в снимке до {maxCount}");
            Assert.Equal(0, s.SnapshotsTruncated);
            Assert.InRange(s.MaxSnapshotBytes, 1, NetSettings.SnapshotBudget);
            Assert.InRange(s.MaxDeltaBytes, 1, NetSettings.SnapshotBudget);
            Assert.Equal(8, maxCount);
        }

        // ---------- Помощники ----------

        private static WorldPos Center(TileCoord t) => new WorldPos(t.X * U + U / 2, t.Y * U + U / 2);

        /// <summary>Номер тайла по модулю карты 1024 (кольцо вокруг наблюдателя у шва залезает за край).</summary>
        private static int Wrap(int tile) => ((tile % 1024) + 1024) % 1024;

        private static long SumDeltas(Rig rig)
        {
            long n = 0;
            foreach (NetClient c in rig.Clients)
            {
                n += c.DeltasApplied;
            }

            return n;
        }

        /// <summary>
        /// Сервер и клиенты на связи в памяти, виртуальное время: два кадра ввода клиентов на такт, снимок такта клиенты
        /// принимают сразу после него. Игрок № i появляется в <c>players[i]</c>; чужие сущности — <c>npcs</c> до входа
        /// игроков. Позиции всех сущностей по тактам — <see cref="History"/>.
        /// </summary>
        private sealed class Rig : IDisposable
        {
            private const long FrameUs = 1_000_000 / 60;
            private readonly LoopbackTransport _st;
            private long _frame;

            public Rig(MapShape shape, WorldPos[] players, int capacity = 64, Action<Simulation>? npcs = null)
            {
                Sim = new Simulation(FlatWorldGenerator.Instance.Generate(shape, 1), capacity);
                npcs?.Invoke(Sim);
                _st = LoopbackTransport.CreateServer(players.Length + 4);
                Server = new NetServer(_st, Sim, n => players[n], players.Length, NetModule.Version);
                Clients = new NetClient[players.Length];
                Inputs = new InputFrame[players.Length];
                Spawns = players;
                for (int i = 0; i < players.Length; i++)
                {
                    var c = new NetClient(LoopbackTransport.ConnectTo(_st), LoopbackTransport.ServerPeer, $"p{i}", NetModule.Version);
                    int k = i;
                    c.SnapshotApplied = snap => Applied?.Invoke(k, snap);
                    Clients[i] = c;
                }
            }

            public Simulation Sim { get; }
            public NetServer Server { get; }
            public NetClient[] Clients { get; }
            public InputFrame[] Inputs { get; }
            public WorldPos[] Spawns { get; }
            public Dictionary<long, WorldPos> History { get; } = new Dictionary<long, WorldPos>();
            public Action<int, Snapshot>? Applied { get; set; }
            public long NowUs => _frame * FrameUs;

            public static long Key(int id, long tick) => ((long)id << 32) | (uint)tick;

            /// <summary>Такты до входа всех и первого снимка; игрок № i — там, где задан.</summary>
            public void Login()
            {
                for (int t = 0; t < 30; t++)
                {
                    Tick();
                    if (Array.TrueForAll(Clients, c => c.State == NetClientState.InGame && c.HasSnapshot))
                    {
                        for (int i = 0; i < Clients.Length; i++)
                        {
                            Assert.Equal(Spawns[i], Sim.GetEntity(Clients[i].EntityId).Mover.Position);
                        }

                        return;
                    }
                }

                throw new InvalidOperationException("клиенты не вошли за 30 тактов");
            }

            public void Run(int ticks)
            {
                for (int t = 0; t < ticks; t++)
                {
                    Tick();
                }
            }

            public void Tick(Action? beforeStep = null)
            {
                for (int f = 0; f < 2; f++, _frame++)
                {
                    for (int i = 0; i < Clients.Length; i++)
                    {
                        Clients[i].Poll(NowUs);
                        if (Clients[i].State == NetClientState.InGame)
                        {
                            Clients[i].SendFrame(Inputs[i]);
                        }
                    }
                }

                Server.Poll(NowUs);
                beforeStep?.Invoke();
                Sim.Step();
                Server.SendSnapshots();
                foreach (SimEntity e in Sim.Entities)
                {
                    History[Key(e.Id, Sim.Tick)] = e.Mover.Position;
                }

                foreach (NetClient c in Clients)
                {
                    c.Poll(NowUs);
                }
            }

            public void Dispose()
            {
                foreach (NetClient c in Clients)
                {
                    c.Transport.Dispose();
                }

                _st.Dispose();
            }
        }
    }
}
