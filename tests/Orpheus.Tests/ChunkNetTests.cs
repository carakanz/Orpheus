using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Rng;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net;
using Orpheus.Net.Protocol;
using Orpheus.Net.Session;
using Orpheus.Net.Transport;
using Orpheus.WorldGen;
using Xunit;
using Xunit.Abstractions;

namespace Orpheus.Tests
{
    /// <summary>
    /// Чанки по сети (docs/07, 3.4–3.7, 3.13 п. 7 и 9; 0-026): клиент получает мир от сервера, правки — пачками по чанку.
    /// </summary>
    public class ChunkNetTests
    {
        private const int U = 1 << Torus.SubTileBits;
        private const long FrameMicros = 1_000_000 / 60;
        private readonly ITestOutputHelper _out;

        public ChunkNetTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private static WorldPos TileCenter(int x, int y) => new WorldPos(x * U + U / 2, y * U + U / 2);

        private static WorldModel Flat(int size = 1024) => FlatWorldGenerator.Instance.Generate(MapShape.World(size), 1);

        // ---------- Окно при входе, сжатие один раз на ревизию ----------

        [Fact]
        public void D07_S3_5_Login_Window24_SameAsServer_CompressOncePerRevision()
        {
            WorldModel world = Flat();
            using var rig = new ChunkRig(world, n => TileCenter(300 + (n & 1), 200)); // все в одном окне
            var clients = new List<NetClient>();
            for (int i = 0; i < 4; i++)
            {
                clients.Add(rig.Join());
            }

            rig.Run(10);
            foreach (NetClient c in clients)
            {
                Assert.Equal(NetClientState.InGame, c.State);
                Assert.Equal(ChunkWindow.Width * ChunkWindow.Height, c.World!.ChunkCount);
                AssertSameAsServer(rig, c);
            }

            Assert.Equal(4 * 24, rig.Server.ChunksSent);
            Assert.Equal(24, rig.Server.ChunkCompressions); // 4 клиента — одна копия на чанк

            // Правки в чанке окна: пачки всем четверым, копия не пересобирается.
            var tile = new TileCoord(300, 200);
            ChunkCoord chunk = Torus.ChunkOf(world.Shape, tile);
            for (int k = 0; k < 3; k++)
            {
                rig.BeforeStep = () => rig.Sim.RequestEdit(new TileCoord(tile.X + k, tile.Y + 3), FloorId.Stone, ObjectId.Torch);
                rig.Run(1);
            }

            rig.BeforeStep = null;
            rig.Run(3);
            Assert.Equal(12, rig.Server.PatchesSent);
            Assert.Equal(24, rig.Server.ChunkCompressions);
            Assert.Equal(3u, world.FindChunk(chunk)!.Revision);

            // Новый клиент: копия изменённого чанка — заново, один раз; остальные 23 — из кэша.
            NetClient fifth = rig.Join();
            rig.Run(10);
            AssertSameAsServer(rig, fifth);
            Assert.Equal(25, rig.Server.ChunkCompressions);
            Assert.Equal(5 * 24, rig.Server.ChunksSent);
            foreach (NetClient c in clients)
            {
                AssertSameAsServer(rig, c);
                Assert.Equal(3, c.World!.PatchesApplied);
            }
        }

        // ---------- 3.13 п. 7 ----------

        /// <summary>
        /// 3.13 п. 7: клиент подходит к чанку через 0,3 с после правки в нём — тайлы клиента побайтно = сервер;
        /// 200 раз с разной задержкой имитатора (0…150 мс в одну сторону, разброс до 20 мс). Персонаж бежит на восток,
        /// правка — за 9 тактов (0,3 с) до того, как чанк войдёт в окно на сервере; затем ещё правки — пачками.
        /// </summary>
        [Fact]
        public void D07_S3_13_7_ApproachChunk03sAfterEdit_200Runs_BytesEqual()
        {
            WorldModel world = Flat();
            var rng = new DetRandom(2026);
            int sent = 0;
            for (int run = 0; run < 200; run++)
            {
                int delay = rng.NextInt(0, 151);
                var sim = new NetSimSettings { DelayMs = delay, JitterMs = rng.NextInt(0, 21), Seed = (ulong)run + 1 };
                int cy = 2 + (run % 20);
                const int startChunk = 10;
                int startX = startChunk * 32 + 16 - 4 - rng.NextInt(0, 3); // 4–6 тайлов до средней линии чанка 10
                using var rig = new ChunkRig(world, _ => TileCenter(startX, cy * 32 + 16));
                NetClient c = rig.Join(sim);
                rig.Input = InputFrameEast;

                // Центр окна сменится 10 → 11, когда X пересечёт 32·10 + 16 тайлов; войдёт столбец 13.
                int midline = (startChunk * 32 + 16) * U;
                var target = new ChunkCoord(startChunk + 2 + 1, cy);
                bool edited = false;
                int editsAfter = 0;
                rig.BeforeStep = () =>
                {
                    int id = c.EntityId;
                    int slot = id > 0 ? rig.Sim.SlotOf(id) : -1;
                    if (slot < 0)
                    {
                        return;
                    }

                    int x = rig.Sim.Entities[slot].Mover.Position.X;
                    if (!edited && midline - x <= 9 * 51)
                    {
                        Assert.True(midline - x > 0, $"прогон {run}: линию уже пересекли до правки");
                        Assert.Null(c.World?.World.FindChunk(target)); // клиент чанка ещё не видел
                        EditIn(rig.Sim, target, rng, 1 + rng.NextInt(0, 4));
                        edited = true;
                    }
                    else if (edited && editsAfter < 6 && rng.NextInt(0, 3) == 0)
                    {
                        EditIn(rig.Sim, target, rng, 1 + rng.NextInt(0, 3));
                        editsAfter++;
                    }
                };
                rig.Run(90 + delay * 30 / 1000 * 2);
                Assert.True(edited, $"прогон {run}: правка не сделана");
                rig.BeforeStep = null;
                rig.Input = default;
                rig.Run(10 + delay * 30 / 1000 * 2);
                Assert.True(c.World!.SameAs(world, target), $"прогон {run} (задержка {delay} мс): чанк {target} у клиента ≠ сервер");
                AssertSameAsServer(rig, c);
                Assert.Equal(0, c.BadMessages);
                sent += (int)rig.Server.PatchesSent;
            }

            Assert.True(sent > 200, $"пачек за 200 прогонов — {sent}: путь «пачка после копии» не проверен");
        }

        // ---------- 3.13 п. 9 ----------

        /// <summary>
        /// 3.13 п. 9: бег 10 минут по прямой (связь в памяти, ускоренно) — у клиента одновременно ≤ 48 чанков, трафик
        /// подгрузки ≤ 0,5 КБ/с. Мир — заглушка генератора (зерно 1), вдоль пути прорезан коридор, чтобы бег не упёрся в скалы.
        /// </summary>
        [Fact]
        public void D07_S3_13_9_Run10Min_AtMost48Chunks_LoadTrafficUnderHalfKBs()
        {
            WorldModel world = new StubGenerator().Generate(MapShape.World(512), 1);
            const int y = 300;
            for (int x = 0; x < 512; x++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    world.SetBaseTile(new TileCoord(x, y + dy), FloorId.Grass, ObjectId.Empty);
                }
            }

            using var rig = new ChunkRig(world, _ => TileCenter(100, y));
            NetClient c = rig.Join();
            rig.Input = InputFrameEast;
            rig.Run(60); // вход и окно входа
            long bytes0 = rig.Server.ChunkBytes + rig.Server.WindowMessagesSent * ChunkWindowMessage.Bytes;
            int start = rig.Sim.GetEntity(c.EntityId).Mover.Position.X;
            int max = 0;
            const int ticks = 10 * 60 * Simulation.TickRate;
            rig.AfterTick = () => max = Math.Max(max, c.World!.ChunkCount);
            rig.Run(ticks);
            long bytes = rig.Server.ChunkBytes + rig.Server.WindowMessagesSent * ChunkWindowMessage.Bytes - bytes0;
            double perSec = bytes / 600.0;
            long ran = Torus.Delta(world.Shape, new WorldPos(start, 0), rig.Sim.GetEntity(c.EntityId).Mover.Position).X;
            _out.WriteLine($"10 мин бега: чанков у клиента не больше {max}; подгрузка {bytes} байт = {perSec:F0} Б/с; " +
                           $"чанков {rig.Server.ChunksSent}, центров окна {rig.Server.WindowMessagesSent}, сжатий {rig.Server.ChunkCompressions}");
            Assert.True(max <= ChunkWindow.MaxChunks, $"чанков одновременно {max}");
            Assert.True(perSec <= 512, $"подгрузка {perSec:F0} Б/с");
            Assert.True(rig.Server.ChunksSent > 100, $"чанков {rig.Server.ChunksSent}: бег не шёл");
            Assert.True(c.World!.ChunksDropped > 100);
            AssertSameAsServer(rig, c);
            Assert.Equal(0, c.BadMessages);
            _ = ran;
        }

        /// <summary>
        /// Бег туда-обратно через среднюю линию чанка (центр окна меняется туда-обратно): гистерезис 8 × 6 — новый столбец
        /// приходит один раз, чанки не гоняются по кругу и не выбрасываются (мутация «выход по 6 × 4» — красная).
        /// </summary>
        [Fact]
        public void D07_S3_5_RunAcrossBoundary_NoChunkChurn()
        {
            WorldModel world = Flat();
            using var rig = new ChunkRig(world, _ => TileCenter(10 * 32 + 14, 200));
            NetClient c = rig.Join();
            rig.Run(10);
            Assert.Equal(24, rig.Server.ChunksSent);
            for (int k = 0; k < 30; k++)
            {
                rig.Input = InputFrameEast;
                rig.Run(20); // 4 тайла: через линию 10·32 + 16
                rig.Input = InputFrameWest;
                rig.Run(20);
            }

            rig.Run(5);
            Assert.True(rig.Server.ViewWindowRecalcs >= 60, $"окно пересчитано {rig.Server.ViewWindowRecalcs} раз: линию не пересекали");
            Assert.Equal(24 + ChunkWindow.Height, rig.Server.ChunksSent);
            Assert.Equal(0, rig.Server.WindowMessagesSent);
            Assert.Equal(0, c.World!.ChunksDropped);
            AssertSameAsServer(rig, c);
        }

        /// <summary>
        /// Б1 (П по 0-026): бег на восток 600 тактов, затем на запад 1200 (связь в памяти) — на каждом такте набор чанков
        /// клиента = набору сервера и весь блок 6 × 4 вокруг серверной позиции у клиента есть: дыр нет ни по пути туда, ни на
        /// обратном пути через выброшенные раньше чанки. Мутация П2 «клиент выбрасывает по центру, сдвинутому на чанк» — красная.
        /// </summary>
        [Fact]
        public void D07_S3_5_RunEastThenWest_ClientSetEqualsServer_EveryTick()
        {
            WorldModel world = Flat();
            using var rig = new ChunkRig(world, _ => TileCenter(300, 200));
            NetClient c = rig.Join();
            rig.Run(10);
            int checks = 0;
            rig.AfterTick = () =>
            {
                c.Poll(rig.NowMicros);
                AssertSameSetAsServer(rig, c, $"такт {checks + 1}: ");
                checks++;
            };
            rig.Input = InputFrameEast;
            rig.Run(600);
            long droppedEast = c.World!.ChunksDropped;
            rig.Input = InputFrameWest;
            rig.Run(1200);
            rig.AfterTick = null;
            Assert.Equal(1800, checks);
            Assert.True(droppedEast > 0 && c.World.ChunksDropped > droppedEast,
                $"выброшено {droppedEast} / {c.World.ChunksDropped}: окно не уезжало в обе стороны");
            AssertSameAsServer(rig, c);
            Assert.Equal(0, c.BadMessages);
        }

        /// <summary>
        /// Б1 через имитатор (RTT 120 ± 20, потери 1 %, 3 зерна): восток — запад — восток с остановками; после каждого
        /// затишья набор клиента = набору сервера, 6 × 4 вокруг серверной позиции без дыр, чанки побайтно = сервер.
        /// </summary>
        [Fact]
        public void D07_S3_5_RunBackAndForth_ThroughSimulator_NoHolesAfterQuiet()
        {
            WorldModel world = Flat();
            for (ulong seed = 1; seed <= 3; seed++)
            {
                using var rig = new ChunkRig(world, _ => TileCenter(300, 200));
                NetClient c = rig.Join(NetSimSettings.FromRtt(120, 20, 1.0, seed));
                rig.Run(20);
                foreach ((InputFrame dir, int ticks) in new[] { (InputFrameEast, 400), (InputFrameWest, 800), (InputFrameEast, 300) })
                {
                    rig.Input = dir;
                    rig.Run(ticks);
                    rig.Input = default;
                    rig.Run(20); // затишье: всё отправленное дошло
                    AssertSameAsServer(rig, c);
                }

                Assert.True(c.World!.ChunksDropped > 0, $"зерно {seed}: окно не уезжало");
                Assert.Equal(0, c.BadMessages);
            }
        }

        /// <summary>
        /// У шва тора (персонаж в чанке (0, 0)): окно заворачивается — у клиента ровно 24 чанка блока 6 × 4 со свёрнутыми
        /// координатами, каждый = серверу (мутация «окно без заворота» — красная).
        /// </summary>
        [Fact]
        public void D07_S3_5_AtSeam_WindowWraps_SameAsServer()
        {
            WorldModel world = Flat();
            MapShape shape = world.Shape;
            for (int cy = 0; cy < shape.ChunksY; cy++)
            {
                for (int cx = 0; cx < shape.ChunksX; cx++)
                {
                    world.SetBaseTile(new TileCoord(cx * 32, cy * 32), (byte)(1 + (cx + 3 * cy) % 5), ObjectId.Empty);
                }
            }

            using var rig = new ChunkRig(world, _ => TileCenter(3, 4));
            NetClient c = rig.Join();
            rig.Run(10);
            var expected = new HashSet<ChunkCoord>();
            for (int dy = -2; dy < 2; dy++)
            {
                for (int dx = -3; dx < 3; dx++)
                {
                    expected.Add(Torus.Wrap(shape, new ChunkCoord(dx, dy)));
                }
            }

            Assert.Equal(0, c.BadMessages);
            Assert.True(expected.SetEquals(c.World!.World.GetChunkCoords().ToArray()),
                "окно у шва: " + string.Join(" ", c.World.World.GetChunkCoords().ToArray().Select(k => $"({k.X},{k.Y})")));
            AssertSameAsServer(rig, c);
        }

        // ---------- Порядок, разрыв, вход заново ----------

        /// <summary>
        /// 3.7 «Порядок»: пачка потерялась (тап выбрасывает первую) — следующая не к той ревизии: клиент её не применяет,
        /// просит чанк целиком, получает, совпадает с сервером; потом пачки снова применяются.
        /// </summary>
        [Fact]
        public void D07_S3_7_PatchGap_RequestsChunk_ThenSame()
        {
            WorldModel world = Flat();
            int dropped = 0;
            using var rig = new ChunkRig(world, _ => TileCenter(300, 200));
            NetClient c = rig.Join(wrap: t => new DropTap(t, data => data[0] == (byte)MessageType.ChunkPatch && dropped++ == 0));
            rig.Run(10);
            ChunkCoord chunk = Torus.ChunkOf(world.Shape, new TileCoord(300, 200));
            rig.BeforeStep = () => rig.Sim.RequestEdit(new TileCoord(300, 205), FloorId.Stone, ObjectId.Torch);
            rig.Run(1);
            rig.BeforeStep = () => rig.Sim.RequestEdit(new TileCoord(301, 206), FloorId.Sand, ObjectId.Carpet, new BuildRecord(7, 100, 0, FloorId.Grass, 0));
            rig.Run(1);
            rig.BeforeStep = null;
            rig.Run(5);
            Assert.True(dropped >= 2, $"пачек через тап {dropped}");
            Assert.Equal(1, c.World!.PatchGaps);
            Assert.Equal(1, rig.Server.ChunkRequests);
            Assert.True(c.World.SameAs(world, chunk));

            rig.BeforeStep = () => rig.Sim.RequestEdit(new TileCoord(302, 207), FloorId.Dirt, ObjectId.Empty);
            rig.Run(1);
            rig.BeforeStep = null;
            rig.Run(3);
            Assert.Equal(1, c.World.PatchesApplied);
            Assert.True(c.World.SameAs(world, chunk));
            AssertSameAsServer(rig, c);
        }

        /// <summary>Потеря связи и вход заново: окно приходит заново целиком, с правками, сделанными без клиента.</summary>
        [Fact]
        public void D07_S3_5_Reconnect_WindowComesAgain()
        {
            WorldModel world = Flat();
            using var rig = new ChunkRig(world, _ => TileCenter(300, 200));
            NetClient a = rig.Join();
            rig.Run(10);
            Assert.Equal(24, a.World!.ChunkCount);
            a.Disconnect();
            rig.Run(5);
            rig.BeforeStep = () => rig.Sim.RequestEdit(new TileCoord(299, 199), FloorId.Stone, ObjectId.Torch);
            rig.Run(1);
            rig.BeforeStep = null;
            NetClient b = rig.Join();
            rig.Run(10);
            Assert.Equal(24, b.World!.ChunkCount);
            Assert.Equal(48, rig.Server.ChunksSent);
            AssertSameAsServer(rig, b);
        }

        /// <summary>
        /// Правки бота в чанках окна наблюдателя (бот рядом копает и строит каждый такт): у наблюдателя тайлы побайтно =
        /// сервер после каждого такта (связь в памяти без задержки).
        /// </summary>
        [Fact]
        public void D07_S3_7_BotEdits_ObserverSameEveryTick()
        {
            WorldModel world = Flat();
            using var rig = new ChunkRig(world, n => TileCenter(300 + 3 * n, 200));
            NetClient observer = rig.Join();
            NetClient bot = rig.Join();
            rig.Run(10);
            var rng = new DetRandom(5);
            int checks = 0;
            rig.Input = InputFrameEast;
            rig.BeforeStep = () =>
            {
                WorldPos p = rig.Sim.GetEntity(bot.EntityId).Mover.Position;
                int tx = (p.X >> Torus.SubTileBits) + rng.NextInt(-4, 5);
                int ty = (p.Y >> Torus.SubTileBits) + rng.NextInt(-4, 5);
                if (rng.NextInt(0, 2) == 0)
                {
                    rig.Sim.RequestEdit(new TileCoord(tx, ty), FloorId.WoodFloor, ObjectId.Carpet, new BuildRecord(1, 50, 0, FloorId.Grass, 0));
                }
                else
                {
                    rig.Sim.RequestEdit(new TileCoord(tx, ty), FloorId.Dirt, ObjectId.Empty);
                }
            };
            rig.AfterTick = () =>
            {
                observer.Poll(rig.NowMicros);
                AssertSameAsServer(rig, observer);
                checks++;
            };
            rig.Run(300);
            Assert.Equal(300, checks);
            Assert.True(observer.World!.PatchesApplied > 200, $"пачек у наблюдателя {observer.World.PatchesApplied}");
            Assert.Equal(0, observer.World.PatchGaps);
        }

        /// <summary>
        /// Чанк обогнал «вход принят» (канал 1 против канала 2): копится и разбирается сразу после него. Испорченное —
        /// плохое сообщение, а не падение: чанк вне карты, неразворачиваемый блок, мир не по каналу 1, пачка к чужому чанку.
        /// </summary>
        [Fact]
        public void D07_S3_6_Client_WorldBeforeAccept_Buffered_BadOnesRejected()
        {
            MapShape shape = MapShape.World(1024);
            var t = new PushTransport();
            var c = new NetClient(t, LoopbackTransport.ServerPeer, "c", NetModule.Version);
            var chunk = new Chunk();
            chunk.SetBaseTile(5, FloorId.Stone, ObjectId.Torch);
            byte[] z = chunk.Compress();
            var at = new ChunkCoord(3, 4);
            t.Push(h => h.OnConnected(LoopbackTransport.ServerPeer));
            t.Push(NetChannel.World, NetTests.Encode(b => ChunkMessage.Write(b, 0, at, 7, z)));
            t.Push(NetChannel.Facts, NetTests.Encode(b => new LoginAcceptMessage
            {
                EntityId = 5, ServerTick = 8, MapWidth = shape.Width, MapHeight = shape.Height, Wraps = true, TickRate = 30,
            }.Write(b)));
            c.Poll(0);
            Assert.Equal(NetClientState.InGame, c.State);
            Assert.Equal(1, c.World!.ChunksReceived);
            Chunk got = c.World.World.FindChunk(at)!;
            Assert.Equal(7u, got.Revision);
            Assert.Equal(FloorId.Stone, got.GetFloor(5));
            Assert.Equal(ObjectId.Torch, got.GetObject(5));
            Assert.Equal(0, c.BadMessages);

            t.Push(NetChannel.World, NetTests.Encode(b => ChunkMessage.Write(b, 0, new ChunkCoord(shape.ChunksX, 0), 1, z)));
            t.Push(NetChannel.World, NetTests.Encode(b => ChunkMessage.Write(b, 0, at, 8, new byte[] { 1, 2, 3, 4 })));
            t.Push(NetChannel.Facts, NetTests.Encode(b => ChunkMessage.Write(b, 0, at, 9, z)));
            t.Push(NetChannel.World, NetTests.Encode(b => ChunkMessage.Write(b, 1, at, 9, z)));
            c.Poll(1);
            Assert.Equal(4, c.BadMessages);
            Assert.Equal(7u, c.World.World.FindChunk(at)!.Revision);

            // Пачка к чанку, которого нет, — запрос чанка целиком (Н1); к своему — ровно следующая ревизия.
            t.Push(NetChannel.World, NetTests.Encode(b => ChunkPatchMessage.Write(b,
                new TilePatch(0, new ChunkCoord(9, 9), 1, new[] { new TileEdit(1, FloorId.Dirt, ObjectId.Empty) }))));
            t.Push(NetChannel.World, NetTests.Encode(b => ChunkPatchMessage.Write(b,
                new TilePatch(0, at, 8, new[] { new TileEdit(5, FloorId.Dirt, ObjectId.Empty) }))));
            long sentBefore = t.Sent;
            c.Poll(2);
            Assert.Equal(4, c.BadMessages);
            Assert.Equal(0, c.World.PatchesSkipped);
            Assert.Equal(1, c.World.PatchesToMissing);
            Assert.Equal(1, c.World.Requests);
            Assert.True(t.Sent > sentBefore, "запрос чанка не ушёл");
            Assert.Equal(1, c.World.PatchesApplied);
            Assert.Equal(FloorId.Dirt, c.World.World.FindChunk(at)!.GetFloor(5));
        }

        /// <summary>
        /// Н1 (П по 0-026): сообщение «чанк» потеряно у клиента (как отвергнутое испорченным) — сервер считает чанк
        /// доставленным и шлёт к нему пачку. Клиент не пропускает её молча, а просит чанк целиком; получает — набор и тайлы
        /// = сервер. Мутация «пачка к отсутствующему чанку — пропуск без запроса» — красная.
        /// </summary>
        [Fact]
        public void D07_S3_7_PatchToMissingChunk_RequestsChunk_ThenSame()
        {
            WorldModel world = Flat();
            var tile = new TileCoord(300, 200);
            ChunkCoord lost = Torus.ChunkOf(world.Shape, tile);
            int lostCount = 0;
            using var rig = new ChunkRig(world, _ => TileCenter(300, 200));
            NetClient c = rig.Join(wrap: t => new DropTap(t, data => data[0] == (byte)MessageType.Chunk &&
                ChunkMessage.Read(data, world.Shape).Chunk == lost && lostCount++ == 0));
            rig.Run(10);
            Assert.Equal(1, lostCount);
            Assert.Null(c.World!.World.FindChunk(lost));
            Assert.Equal(ChunkWindow.Width * ChunkWindow.Height - 1, c.World.ChunkCount);

            rig.BeforeStep = () => rig.Sim.RequestEdit(tile, FloorId.Stone, ObjectId.Torch);
            rig.Run(1);
            rig.BeforeStep = null;
            rig.Run(5);
            Assert.Equal(1, c.World.PatchesToMissing);
            Assert.Equal(1, rig.Server.ChunkRequests);
            Assert.True(c.World.SameAs(world, lost), "потерянный чанк не пришёл заново");
            AssertSameAsServer(rig, c);
            Assert.Equal(0, c.BadMessages);
        }

        /// <summary>
        /// Н4 (П по 0-026): у клиента не больше 48 чанков — 49-й новый чанк отвергается (плохое сообщение и счётчик) и не
        /// доходит до <see cref="ClientWorld.ChunkChanged"/> (экран Unity бросил бы исключение в кадре). Копия уже
        /// бывшего чанка при 48 принимается.
        /// </summary>
        [Fact]
        public void D07_S3_5_Client_At48Chunks_ExtraChunkRejected()
        {
            MapShape shape = MapShape.World(1024);
            var t = new PushTransport();
            var c = new NetClient(t, LoopbackTransport.ServerPeer, "c", NetModule.Version);
            byte[] z = new Chunk().Compress();
            t.Push(h => h.OnConnected(LoopbackTransport.ServerPeer));
            t.Push(NetChannel.Facts, NetTests.Encode(b => new LoginAcceptMessage
            {
                EntityId = 5, ServerTick = 8, MapWidth = shape.Width, MapHeight = shape.Height, Wraps = true, TickRate = 30,
            }.Write(b)));
            c.Poll(0);
            int shown = 0;
            c.World!.ChunkChanged = _ => shown++;
            for (int i = 0; i < ChunkWindow.MaxChunks + 1; i++)
            {
                var at = new ChunkCoord(i % 10, i / 10);
                t.Push(NetChannel.World, NetTests.Encode(b => ChunkMessage.Write(b, 0, at, 1, z)));
            }

            c.Poll(1);
            Assert.Equal(ChunkWindow.MaxChunks, c.World.ChunkCount);
            Assert.Equal(ChunkWindow.MaxChunks, shown);
            Assert.Equal(1, c.World.ChunksRejected);
            Assert.Equal(1, c.BadMessages);
            Assert.Null(c.World.World.FindChunk(new ChunkCoord(8, 4)));

            t.Push(NetChannel.World, NetTests.Encode(b => ChunkMessage.Write(b, 0, new ChunkCoord(9, 4), 1, z)));
            t.Push(NetChannel.World, NetTests.Encode(b => ChunkMessage.Write(b, 0, new ChunkCoord(0, 0), 2, z)));
            c.Poll(2);
            Assert.Equal(2, c.World.ChunksRejected);
            Assert.Equal(2, c.BadMessages);
            Assert.Equal(ChunkWindow.MaxChunks + 1, shown); // только копия бывшего чанка (0, 0)
            Assert.Equal(2u, c.World.World.FindChunk(new ChunkCoord(0, 0))!.Revision);
            Assert.Equal(ChunkWindow.MaxChunks, c.World.ChunkCount);
        }

        // ---------- «Золотые» байты ----------

        /// <summary>
        /// «Золотые» байты (backlog [сеть], П по 0-008 з. 7): сообщения мира и существующие — побайтно как записано здесь.
        /// Изменение формата краснит тест — значит, нужна новая версия протокола (2.10). Deflate-поток чанка в золото не
        /// входит (.NET и Mono сжимают по-разному), только рамка; разворачивание — отдельной проверкой ниже.
        /// </summary>
        [Fact]
        public void D07_S2_10_GoldenBytes_AllMessages()
        {
            MapShape shape = MapShape.World(1024);
            var layout = new SnapshotLayout(shape);
            _golden.Clear();
            byte[] gChunk = Golden("10 00 03 00 04 00 2a 00 00 00 aa bb cc",
                b => ChunkMessage.Write(b, 0, new ChunkCoord(3, 4), 42, new byte[] { 0xAA, 0xBB, 0xCC }));
            var patch = new TilePatch(0, new ChunkCoord(31, 1), 0x01020304, new[]
            {
                new TileEdit(5, FloorId.Stone, ObjectId.Torch),
                new TileEdit(1023, FloorId.WoodFloor, ObjectId.Carpet, new BuildRecord(0x0102, 0x0304, 5, FloorId.Grass, 0x0607)),
            });
            byte[] gPatch = Golden("11 00 1f 00 01 00 04 03 02 01 02 00 05 00 03 01 00 ff 83 08 02 00 02 01 04 03 05 01 07 06",
                b => ChunkPatchMessage.Write(b, patch));
            var window = new ChunkWindowMessage { Center = new ChunkCoord(31, 0) };
            byte[] gWindow = Golden("12 00 1f 00 00 00", b => window.Write(b));
            var request = new ChunkRequestMessage { Chunk = new ChunkCoord(1, 2) };
            byte[] gRequest = Golden("13 00 01 00 02 00", b => request.Write(b));

            var login = new LoginMessage { Version = new NetVersion(0, 4, 2), Name = "Орфей" };
            byte[] gLogin = Golden("01 00 00 04 00 02 00 0a d0 9e d1 80 d1 84 d0 b5 d0 b9 00", b => login.Write(b));
            var accept = new LoginAcceptMessage
            {
                EntityId = 123456, ServerTick = 4_000_000_000, MapWidth = 4096, MapHeight = 4096, Wraps = true,
                WorldSeed = 0xDEADBEEF_CAFEBABE, TickRate = 30, GenerationHash = 0x0123_4567_89AB_CDEF,
            };
            byte[] gAccept = Golden("02 40 e2 01 00 00 28 6b ee 00 10 00 00 00 10 00 00 01 be ba fe ca ef be ad de 1e ef cd ab 89 67 45 23 01",
                b => accept.Write(b));
            var input = new InputMessage
            {
                FirstFrame = 0xFFFF_FFF0,
                Frame0 = new WireFrame { Direction = 1, Power = 255, Slot = 3, Aim = 0x1234, Buttons = 0x8001 },
                Frame1 = new WireFrame { Direction = 0, Power = 0, Slot = 0, Aim = 65535, Buttons = 0 },
                Frame2 = new WireFrame { Direction = 8, Power = 17, Slot = 7, Aim = 1, Buttons = 0xFFFF },
                Frame3 = new WireFrame { Direction = 5, Power = 200, Slot = 2, Aim = 40000, Buttons = 0x0102 },
                PictureTime = 77, LastSnapshot = 0x7FFF_FFFF, Lead = 24,
            };
            byte[] gInput = Golden("04 f0 ff ff ff 01 ff 03 34 12 01 80 00 00 00 ff ff 00 00 08 11 07 01 00 ff ff 05 c8 02 40 9c 02 01 4d 00 00 00 ff ff ff 7f 18",
                b => input.Write(b)); // кадр 7 байт: направление, сила, ячейка, прицел, кнопки (1-002, протокол 12)
            byte[] gClock = Golden("06 04 03 02 01", b => new ClockMessage { ServerTick = 0x01020304 }.Write(b));
            byte[] gPing = Golden("0b 04 03 02 01", b => new PingMessage { ClientUs = 0x01020304 }.Write(b));
            var pong = new PongMessage { ClientUs = 0x01020304, ServerTick = 0x0A0B0C0D };
            byte[] gPong = Golden("0c 04 03 02 01 0d 0c 0b 0a", b => pong.Write(b));
            var swing = new SwingStartMessage
            {
                AttackerId = 0x7FFF0001, StartFrame = 0xFFFF_FFFC, WindupFrames = 24, Origin = new WorldPos(262_143, 5),
                Axis = 32768, ReachUnits = 384, HalfArcSteps = 8192, DelayFrames = 13,
            };
            byte[] gSwing = Golden("0d 01 00 ff 7f fc ff ff ff 18 ff ff 03 00 05 00 00 00 00 80 80 01 00 20 0d", b => swing.Write(b));
            var melee = new MeleeResultMessage { AttackerId = 7, StartFrame = 1234, Hit = true, ByLastKnown = true, WaitedFrames = 6, MissingFrames = 3, DecidedAtFrame = 1264, Damage = 10 };
            byte[] gMelee = Golden("0e 07 00 00 00 d2 04 00 00 03 06 03 f0 04 00 00 0a", b => melee.Write(b)); // + урон 1 байт (1-001, протокол 11)
            var reject = new LoginRejectMessage { Reason = LoginRejectReason.NameTaken, ServerVersion = new NetVersion(0, 4, 2) };
            byte[] gReject = Golden("03 06 00 00 04 00 02 00", b => reject.Write(b)); // причина 6 «имя занято» (0-035, П з. 5)

            // Разбор тех же «золотых» байт (П по 0-026, Н8): чтение даёт ровно записанные поля, а не только круг «запись → чтение».
            ChunkMessage rChunk = ChunkMessage.Read(gChunk, shape);
            Assert.Equal((byte)0, rChunk.Map);
            Assert.Equal(new ChunkCoord(3, 4), rChunk.Chunk);
            Assert.Equal(42u, rChunk.Revision);
            Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC }, rChunk.Compressed.ToArray());
            TilePatch rPatch = ChunkPatchMessage.Read(gPatch, shape);
            Assert.Equal(patch.MapId, rPatch.MapId);
            Assert.Equal(patch.Chunk, rPatch.Chunk);
            Assert.Equal(patch.Revision, rPatch.Revision);
            Assert.Equal(patch.Edits, rPatch.Edits);
            Assert.Equal(window, ChunkWindowMessage.Read(gWindow, shape));
            Assert.Equal(request, ChunkRequestMessage.Read(gRequest, shape));
            LoginMessage rLogin = LoginMessage.Read(gLogin);
            Assert.Equal(login.Version, rLogin.Version);
            Assert.Equal(login.Name, rLogin.Name);
            Assert.Equal(accept, LoginAcceptMessage.Read(gAccept));
            Assert.Equal(input, InputMessage.Read(gInput));
            Assert.Equal(0x01020304u, ClockMessage.Read(gClock).ServerTick);
            Assert.Equal(0x01020304u, PingMessage.Read(gPing).ClientUs);
            Assert.Equal(pong, PongMessage.Read(gPong));
            SwingStartMessage rSwing = SwingStartMessage.Read(gSwing);
            Assert.Equal(swing.AttackerId, rSwing.AttackerId);
            Assert.Equal(swing.StartFrame, rSwing.StartFrame);
            Assert.Equal(swing.WindupFrames, rSwing.WindupFrames);
            Assert.Equal(swing.Origin, rSwing.Origin);
            Assert.Equal(swing.Axis, rSwing.Axis);
            Assert.Equal(swing.ReachUnits, rSwing.ReachUnits);
            Assert.Equal(swing.HalfArcSteps, rSwing.HalfArcSteps);
            Assert.Equal(swing.DelayFrames, rSwing.DelayFrames);
            MeleeResultMessage rMelee = MeleeResultMessage.Read(gMelee);
            Assert.Equal(melee.AttackerId, rMelee.AttackerId);
            Assert.Equal(melee.StartFrame, rMelee.StartFrame);
            Assert.Equal(melee.Hit, rMelee.Hit);
            Assert.Equal(melee.ByLastKnown, rMelee.ByLastKnown);
            Assert.Equal(melee.WaitedFrames, rMelee.WaitedFrames);
            Assert.Equal(melee.MissingFrames, rMelee.MissingFrames);
            Assert.Equal(melee.DecidedAtFrame, rMelee.DecidedAtFrame);
            Assert.Equal(melee.Damage, rMelee.Damage); // 1-001
            LoginRejectMessage rReject = LoginRejectMessage.Read(gReject);
            Assert.Equal(LoginRejectReason.NameTaken, rReject.Reason);
            Assert.Equal(new NetVersion(0, 4, 2), rReject.ServerVersion);
            Golden("08", b => new OpenForFriendsMessage().Write(b));
            Golden("09 01 61 1e", b => new OpenResultMessage { Opened = true, Port = 0x1E61 }.Write(b));
            Golden("0a 03", b => new ClosingMessage { Reason = ClosingReason.Replaced }.Write(b));
            byte[] gPauseReq = Golden("16 01", b => new PauseRequestMessage { Paused = true }.Write(b)); // 0-036, протокол 10
            var pauseState = new PauseStateMessage { Paused = true, ServerTick = 0x01020304, ByEntity = 7, ByName = "Эвр" };
            byte[] gPauseState = Golden("17 01 04 03 02 01 07 00 00 00 06 d0 ad d0 b2 d1 80", b => pauseState.Write(b));
            byte[] gSnap = Golden("05 9f 86 01 00 43 0d 03 00 02 a5 d1 07 80 3e e0 dc d1 8f 46 06 a0 37 0b 00 00 00 00 fa 00 fa 80 01 34 01 00 00 08 b8 ff 01 00 08 00", b => // бой в своём состоянии и сущностях (1-001, протокол 11)
            {
                var snap = new Snapshot { Tick = 99_999, AckFrame = 200_003, ClockEpoch = 0xA5, Count = 2, Self = new SelfState(new WorldPos(1000, 2000), new Int2(-9, 7), new Dodge { Elapsed = 0xA3, DashLeft = 15, Cooldown = 17, Direction = 6 }) { LeadMargin = -3, DelayFrames = 19 } };
                snap.Entities[0] = new SnapshotEntity { Id = 5, Kind = 0, Position = SnapshotLayout.Quantize(new WorldPos(1000, 2000)), Direction = 3 };
                snap.Entities[1] = new SnapshotEntity { Id = 77, Kind = 2, Position = SnapshotLayout.Quantize(new WorldPos(262_000, 5)), Direction = 8 };
                return snap.Write(b, layout);
            });
            Golden("07 34 00 00 00 68 00 00 00 2a 01 01 01 a5 00 00 00 00 00 42 00 00 00 00 00 08 02 00 46 1f a0 0f dc 18 00 00 40 00 fa 40 00 20 64", b => // бой: свои биты + доля здоровья новой сущности (1-001)
            {
                var w = new SnapshotDeltaWriter(b, layout, 52, 104, 10);
                w.SetClockEpoch(0xA5);
                Assert.True(w.TryAddRemoved(1));
                Assert.True(w.TryAddUpdated(0, DeltaFields.Position | DeltaFields.Direction, new WorldPos(4000, 4000), 7));
                Assert.True(w.TryAddAdded(99, 1, new WorldPos(8000, 16), 2));
                return w.Finish();
            });
            Assert.True(_golden.Count == 0, string.Join("\n", _golden));
            var rSnap = new Snapshot(); // своё состояние с увороном (0-029, протокол 6) и отчётом (0-033, протокол 7) — из золотых байт
            rSnap.Read(gSnap, layout);
            Assert.Equal(new SelfState(new WorldPos(1000, 2000), new Int2(-9, 7), new Dodge { Elapsed = 0xA3, DashLeft = 15, Cooldown = 17, Direction = 6 }), rSnap.Self);
            Assert.Equal(-3, rSnap.Self.LeadMargin);
            Assert.Equal(19, rSnap.Self.DelayFrames);
            Assert.Equal(2, rSnap.Count);
            Assert.True(PauseRequestMessage.Read(gPauseReq).Paused);
            Assert.Equal(pauseState, PauseStateMessage.Read(gPauseState));
            Assert.Equal(string.Empty, PauseStateMessage.Read(Hex("17 00 00 00 00 00 00 00 00 00 00")).ByName); // снял сервер сам
            Assert.Throws<ProtocolException>(() => PauseRequestMessage.Read(Hex("16 02")));
            Assert.Throws<ProtocolException>(() => PauseStateMessage.Read(Hex("17 01 00 00 00 00 00 00 00 00 21")));
        }

        /// <summary>
        /// Разворачивание «золотого» Deflate-потока чанка (сжат .NET 9 один раз и записан сюда): любой рантайм — Mono и
        /// IL2CPP Unity, .NET сервера — разворачивает его в тот же блок; и чанк, сжатый здесь, разворачивается обратно.
        /// </summary>
        [Fact]
        public void D07_S3_6_GoldenDeflate_Decompresses_RoundTrip()
        {
            var chunk = new Chunk();
            for (int i = 0; i < Chunk.TileCount; i++)
            {
                chunk.SetBaseTile(i, (byte)(i % 7 == 0 ? FloorId.Stone : FloorId.Grass), i % 97 == 0 ? ObjectId.Torch : ObjectId.Empty);
            }

            chunk.SetBaseBuild(17, new BuildRecord(1, 2, 3, FloorId.Grass, 4));
            byte[] blob = new byte[chunk.BlobBytes];
            chunk.WriteBlob(blob);
            byte[] golden = Hex(
                "ed cd b1 0d 00 20 0c c4 40 3f 61 01 f6 5f 16 29 3b d0 04 5f e3 d2 95 56 c6 98 0f c3 63 0e 1c 38 98 30 90 c4 2c " +
                "e1 10 16 95 cd 05");
            Assert.Equal(blob, Chunk.Decompress(golden));
            Assert.Equal(blob, Chunk.Decompress(chunk.Compress()));
        }

        // ---------- Замер (08, 7.7; приёмка 0.7) ----------

        /// <summary>
        /// Замер: сжатый чанк заглушки генератора на 600 случайных чанках (1024², зерно 1) — среднее, 95-й процентиль,
        /// наибольший против модели 3.6 (236 / 341 / 425; модель с макрокартой и лесами, у заглушки их нет); время сжатия.
        /// Числа — в «Замер» карточки; проверка — грубая (не больше модели вдвое).
        /// </summary>
        [Fact]
        public void D07_S3_6_Measure_CompressedChunk_600Chunks()
        {
            WorldModel world = new StubGenerator().Generate(MapShape.World(1024), 1);
            var rng = new DetRandom(1);
            var sizes = new List<int>();
            var w = new Stopwatch();
            for (int i = 0; i < 600; i++)
            {
                var c = new ChunkCoord(rng.NextInt(0, world.Shape.ChunksX), rng.NextInt(0, world.Shape.ChunksY));
                Chunk chunk = world.FindChunk(c)!;
                w.Start();
                byte[] z = chunk.Compress();
                w.Stop();
                sizes.Add(z.Length);
            }

            sizes.Sort();
            double mean = sizes.Average();
            int p95 = sizes[(int)(sizes.Count * 0.95)];
            int max = sizes[sizes.Count - 1];
            double us = w.Elapsed.TotalMilliseconds * 1000 / 600;
            _out.WriteLine($"сжатый чанк (600, зерно 1): среднее {mean:F0}, 95-й {p95}, наибольший {max}, наименьший {sizes[0]} байт; " +
                           $"сжатие {us:F0} мкс на чанк (Debug)");

            // Трафик при входе: окно 6 × 4 вокруг точки появления.
            using var rig = new ChunkRig(world, n => ServerRunSpawn(world.Shape, n));
            NetClient cl = rig.Join();
            rig.Run(10);
            _out.WriteLine($"вход в мир: {rig.Server.ChunksSent} чанков, {rig.Server.ChunkBytes} байт сообщений «чанк»");
            AssertSameAsServer(rig, cl);
            Assert.True(mean <= 2 * 236 && max <= 2 * 425, $"среднее {mean:F0}, наибольший {max}");
        }

        private static WorldPos ServerRunSpawn(MapShape shape, int n) => Orpheus.Server.ServerRun.SpawnPoint(shape, n);

        // ---------- Помощники ----------

        private static readonly InputFrame InputFrameEast = new InputFrame(Orpheus.Core.Movement.Input8.East);
        private static readonly InputFrame InputFrameWest = new InputFrame(Orpheus.Core.Movement.Input8.West);

        private static void EditIn(Simulation sim, ChunkCoord chunk, DetRandom rng, int count)
        {
            for (int i = 0; i < count; i++)
            {
                var t = new TileCoord(chunk.X * 32 + rng.NextInt(0, 32), chunk.Y * 32 + rng.NextInt(0, 32));
                if (rng.NextInt(0, 2) == 0)
                {
                    sim.RequestEdit(t, FloorId.Stone, ObjectId.Torch);
                }
                else
                {
                    sim.RequestEdit(t, FloorId.WoodFloor, ObjectId.Carpet, new BuildRecord((ushort)rng.NextInt(1, 100), 77, 0, FloorId.Grass, 0));
                }
            }
        }

        /// <summary>
        /// Клиент разбирает пришедшее (<c>Poll</c>), затем: набор чанков = набору сервера (<see cref="AssertSameSetAsServer"/>),
        /// каждый чанк клиента = серверу побайтно.
        /// </summary>
        private static void AssertSameAsServer(ChunkRig rig, NetClient c)
        {
            c.Poll(rig.NowMicros);
            AssertSameSetAsServer(rig, c);
            ClientWorld w = c.World!;
            foreach (ChunkCoord k in w.World.GetChunkCoords().ToArray())
            {
                Assert.True(w.SameAs(rig.Sim.World, k), $"чанк ({k.X}, {k.Y}) у клиента #{c.EntityId} ≠ сервер");
            }
        }

        /// <summary>
        /// Главное правило окна (3.5; П по 0-026, Б1): набор чанков клиента = набору, который сервер считает доставленным
        /// (<see cref="NetServer.CopyHeldChunks"/>), — нет ни выброшенных клиентом «лишний раз» (дыра навсегда: сервер их
        /// больше не пришлёт), ни оставленных сверх окна; весь блок 6 × 4 вокруг <b>серверной</b> позиции персонажа у клиента
        /// есть; чанков не больше 48. Отправленное сервером должно быть уже разобрано клиентом.
        /// </summary>
        private static void AssertSameSetAsServer(ChunkRig rig, NetClient c, string where = "")
        {
            ClientWorld w = c.World!;
            var mine = new HashSet<ChunkCoord>(w.World.GetChunkCoords().ToArray());
            var buf = new ChunkCoord[ChunkWindow.MaxChunks];
            int n = rig.Server.CopyHeldChunks(c.EntityId, buf);
            Assert.True(n >= 0, $"{where}игрока #{c.EntityId} нет на сервере");
            var held = new HashSet<ChunkCoord>(buf.Take(n));
            Assert.True(mine.SetEquals(held),
                $"{where}набор клиента ≠ набор сервера: нет у клиента [{Coords(held.Except(mine))}], лишние у клиента [{Coords(mine.Except(held))}]");

            MapShape shape = rig.Sim.World.Shape;
            ChunkCoord center = ChunkWindow.BlockCenter(shape, rig.Sim.GetEntity(c.EntityId).Mover.Position);
            var holes = new List<ChunkCoord>();
            for (int dy = -ChunkWindow.Height / 2; dy < ChunkWindow.Height / 2; dy++)
            {
                for (int dx = -ChunkWindow.Width / 2; dx < ChunkWindow.Width / 2; dx++)
                {
                    ChunkCoord k = Torus.Wrap(shape, new ChunkCoord(center.X + dx, center.Y + dy));
                    if (!mine.Contains(k))
                    {
                        holes.Add(k);
                    }
                }
            }

            Assert.True(holes.Count == 0, $"{where}дыры в 6 × 4 вокруг серверной позиции (центр ({center.X}, {center.Y})): [{Coords(holes)}]");
            Assert.True(w.ChunkCount <= ChunkWindow.MaxChunks, $"{where}чанков у клиента {w.ChunkCount}");
        }

        private static string Coords(IEnumerable<ChunkCoord> cs) => string.Join(" ", cs.Select(k => $"({k.X},{k.Y})"));

        private readonly List<string> _golden = new List<string>();

        /// <summary>Сверить запись с «золотыми» байтами (расхождение копится в <see cref="_golden"/>); вернуть сами золотые байты — для разбора.</summary>
        private byte[] Golden(string hex, NetTests.Writer write)
        {
            byte[] actual = NetTests.Encode(write);
            string got = string.Join(" ", actual.Select(b => b.ToString("x2")));
            if (hex != got)
            {
                _golden.Add($"золотые байты № {_golden.Count + 1}: ждали «{hex}», есть «{got}»");
            }

            return Hex(hex);
        }

        private static byte[] Hex(string hex) =>
            hex.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(h => Convert.ToByte(h, 16)).ToArray();

        /// <summary>Транспорт клиента под рукой теста: события — по очереди в <c>Poll</c>, отправки — счётом.</summary>
        private sealed class PushTransport : ITransport
        {
            private readonly List<Action<ITransportHandler>> _queue = new List<Action<ITransportHandler>>();
            public long Sent;

            public void Push(Action<ITransportHandler> e) => _queue.Add(e);
            public void Push(NetChannel channel, byte[] data) => _queue.Add(h => h.OnReceive(LoopbackTransport.ServerPeer, channel, data));

            public void Poll(long nowMicros, ITransportHandler handler)
            {
                var q = _queue.ToArray();
                _queue.Clear();
                foreach (Action<ITransportHandler> e in q)
                {
                    e(handler);
                }
            }

            public void Send(int peer, NetChannel channel, ReadOnlySpan<byte> data) => Sent++;
            public void Flush() { }
            public void Disconnect(int peer, ReadOnlySpan<byte> payload) { }
            public int RoundTripMs(int peer) => 0;
            public void Dispose() { }
        }

        /// <summary>Тап на транспорте клиента: входящие сообщения мира по условию выбрасываются (потеря пачки).</summary>
        private sealed class DropTap : ITransport, ITransportHandler
        {
            private readonly ITransport _inner;
            private readonly Func<byte[], bool> _drop;
            private ITransportHandler? _handler;

            public DropTap(ITransport inner, Func<byte[], bool> drop)
            {
                _inner = inner;
                _drop = drop;
            }

            public void Poll(long nowMicros, ITransportHandler handler)
            {
                _handler = handler;
                _inner.Poll(nowMicros, this);
            }

            public void Send(int peer, NetChannel channel, ReadOnlySpan<byte> data) => _inner.Send(peer, channel, data);
            public void Flush() => _inner.Flush();
            public void Disconnect(int peer, ReadOnlySpan<byte> payload) => _inner.Disconnect(peer, payload);
            public int RoundTripMs(int peer) => _inner.RoundTripMs(peer);
            public void Dispose() => _inner.Dispose();
            public void OnConnected(int peer) => _handler!.OnConnected(peer);
            public void OnDisconnected(int peer, NetDisconnectReason reason, ReadOnlySpan<byte> payload) => _handler!.OnDisconnected(peer, reason, payload);

            public void OnReceive(int peer, NetChannel channel, ReadOnlySpan<byte> data)
            {
                if (channel == NetChannel.World && _drop(data.ToArray()))
                {
                    return;
                }

                _handler!.OnReceive(peer, channel, data);
            }
        }

        /// <summary>
        /// Сервер и клиенты в памяти (по желанию — через имитатор связи): такт — два кадра клиентов (60 Гц), затем
        /// <c>Poll</c> сервера, <see cref="BeforeStep"/> (правки такта), такт, рассылка, <see cref="AfterTick"/>.
        /// </summary>
        private sealed class ChunkRig : IDisposable
        {
            private readonly LoopbackTransport _st;
            private long _frame;

            public ChunkRig(WorldModel world, Func<int, WorldPos> spawn, int maxPlayers = 8)
            {
                Sim = new Simulation(world, maxEntities: 32);
                _st = LoopbackTransport.CreateServer(maxPlayers + 8);
                Server = new NetServer(_st, Sim, spawn, maxPlayers, NetModule.Version);
            }

            public Simulation Sim { get; }
            public NetServer Server { get; }
            public List<NetClient> Clients { get; } = new List<NetClient>();
            public InputFrame Input { get; set; }
            public Action? BeforeStep { get; set; }
            public Action? AfterTick { get; set; }
            public long NowMicros => _frame * FrameMicros;

            public NetClient Join(NetSimSettings? sim = null, Func<ITransport, ITransport>? wrap = null)
            {
                ITransport t = LoopbackTransport.ConnectTo(_st);
                if (sim.HasValue)
                {
                    t = new NetSimTransport(t, sim.Value);
                }

                if (wrap != null)
                {
                    t = wrap(t);
                }

                var c = new NetClient(t, LoopbackTransport.ServerPeer, "c" + Clients.Count, NetModule.Version);
                Clients.Add(c);
                return c;
            }

            public void Run(int ticks)
            {
                for (int i = 0; i < ticks; i++)
                {
                    for (int f = 0; f < 2; f++, _frame++)
                    {
                        foreach (NetClient c in Clients)
                        {
                            c.Poll(NowMicros);
                            c.SendFrame(Input);
                        }
                    }

                    Server.Poll(NowMicros);
                    BeforeStep?.Invoke();
                    Sim.Step();
                    Server.SendSnapshots();
                    AfterTick?.Invoke();
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
