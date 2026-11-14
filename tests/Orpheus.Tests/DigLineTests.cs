using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net.Protocol;
using Orpheus.Net.Session;
using Orpheus.Net.Transport;
using Orpheus.Save;
using Orpheus.WorldGen;
using Xunit;
using Xunit.Abstractions;

namespace Orpheus.Tests
{
    /// <summary>
    /// Приёмка `08` 0.7 (0-028): бот <c>dig_line</c> снимает скалы поперёк шва; наблюдатель держит те же чанки и после каждой пачки
    /// сверяет их с сервером побайтно; затем сервер сохраняется, «перезапускается» (загрузка сохранения в новый такт и новый
    /// сервер), наблюдатель входит заново — все снятые скалы на месте. Замер: чанк по сети против модели `07` 3.6 (236 байт),
    /// трафик пачек правок.
    /// </summary>
    public class DigLineTests
    {
        private const int U = Torus.SubTilesPerTile;
        private readonly ITestOutputHelper _out;

        public DigLineTests(ITestOutputHelper output)
        {
            _out = output;
        }

        /// <summary>CI: 100 скал, связь без задержки.</summary>
        [Fact]
        public void D08_0_7_DigLine_100Rocks_ObserverBytesEqual_RestartLosesNothing() => Run(100, rttMs: 0, WorldGenModule.Stub);

        /// <summary>
        /// 1-017: тот же бот в мире генератора v1 (по умолчанию сервера, <c>--gen v1</c>). Скал в полосе реже, на пути — деревья,
        /// кусты, гранит и залежи (они дольше), поэтому лимит тактов — вдвое больше.
        /// E1-2: зерно 7 — у шва есть полоса без воды и обрывов (бот их не копает и не обходит; у зерна 1 на шве озеро);
        /// <see cref="PickBand"/> такие полосы пропускает.
        /// </summary>
        [Fact]
        public void D17_DigLine_GenV1_100Rocks_ObserverBytesEqual_RestartLosesNothing() => Run(100, rttMs: 0, WorldGenModule.Current, slack: 2, seed: 7);

        /// <summary>
        /// Ночной прогон: 1000 скал, у копающего RTT 100 мс (имитатор связи). Лимит тактов — вдвое: с 1-010 копание идёт
        /// временем (скала — 8 кадров киркой), а на полном прогоне Big-тесты делят ядра и эффективная задержка растёт.
        /// </summary>
        [Fact]
        [Trait("Category", "Big")]
        public void D08_0_7_DigLine_Big_1000Rocks_Rtt100_ObserverBytesEqual_RestartLosesNothing() => Run(1000, rttMs: 100, WorldGenModule.Stub, slack: 2);

        private void Run(int target, int rttMs, IWorldGenerator generator, int slack = 1, ulong seed = 1)
        {
            var wall = Stopwatch.StartNew();
            MapShape shape = MapShape.World(1024);
            WorldModel world = generator.Generate(shape, seed);
            (int half, int y0, int rocks) = PickBand(world, target);
            _out.WriteLine($"полоса: x ∈ [−{half}, {half}] у шва, строки {y0}…{y0 + 127}, скал в полосе {rocks}");
            Assert.True(rocks >= target * 5 / 4, $"скал в лучшей полосе {rocks} < {target * 5 / 4}");

            // Точка входа копающего — у края полосы (расчищена в снимке генерации), наблюдателя — на шве посреди строк.
            var digStart = new TileCoord(shape.Width - half - 4, y0 + 1);
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    var t = Torus.Wrap(shape, new TileCoord(digStart.X + dx, digStart.Y + dy));
                    world.SetBaseTile(t, FloorId.Grass, ObjectId.Empty);
                }
            }

            byte[] baseBytes = WorldSnapshot.WriteBase(world);
            var obsTile = new TileCoord(0, y0 + 64);
            WorldPos Spawn(int n) => n == 0 ? EditTests.TileCenter(obsTile.X, obsTile.Y) : EditTests.TileCenter(digStart.X, digStart.Y);

            var tap = new CountingTransport();
            using var rig = new EditTests.EditRig(new Simulation(world, maxEntities: 32), Spawn, t => tap.Wrap(t));
            NetClient observer = rig.Join();
            NetClient digger = rig.Join(rttMs > 0 ? new NetSimSettings { DelayMs = rttMs / 2, JitterMs = 5, Seed = 11 } : (NetSimSettings?)null);
            var bot = new DigLineBot(shape, half, target);
            digger.EditReplied = bot.OnReply;
            rig.InputOf = c => c == digger && digger.HasSnapshot && digger.Latest.TryFind(digger.EntityId, out SnapshotEntity se)
                ? bot.Next(digger, se.Position)
                : InputFrame.None;

            // После каждого такта: наблюдатель разбирает пришедшее, каждый изменённый в такте чанк у него = сервер побайтно.
            long patchChecks = 0;
            long mismatches = 0;
            var revised = new HashSet<ChunkCoord>();
            rig.AfterTick = () =>
            {
                observer.Poll(rig.NowMicros);
                foreach (SimEvent e in rig.Sim.Events)
                {
                    if (e.Kind != SimEventKind.ChunkRevised)
                    {
                        continue;
                    }

                    revised.Add(e.Chunk);
                    if (observer.World!.World.FindChunk(e.Chunk) == null)
                    {
                        mismatches++; // правка вне окна наблюдателя — он её не видит
                        continue;
                    }

                    patchChecks++;
                    if (!observer.World.SameAs(rig.Sim.World, e.Chunk))
                    {
                        mismatches++;
                    }
                }
            };

            rig.Run(10);
            long startTicks = rig.Ticks;
            int limitTicks = (target * 60 + 3000) * slack;
            while (!bot.Done && rig.Ticks - startTicks < limitTicks)
            {
                rig.Run(30);
            }

            rig.Run(20); // ответы и пачки догоняют
            long digTicks = rig.Ticks - startTicks;
            _out.WriteLine($"снято скал {bot.RocksDug} из {target}, прочего {bot.OtherDug}, отказов {bot.Refused} ({bot.RefusalsText}), строк {bot.Rows}, " +
                           $"тактов {digTicks} ({digTicks / 30.0:0.0} с игры)");
            Assert.True(bot.Done, $"бот снял {bot.RocksDug} из {target} за {digTicks} тактов");

            // Приёмка 1-010: в сумке копающего — сумма детерминированных бросков добычи по всем снятым «каменным» тайлам; откатов нет.
            int stoneWanted = bot.StoneTiles.Sum(e => DigLoot.DropFor(e.Raw, e.Tile.X, e.Tile.Y, world.Seed, out _));
            Assert.True(stoneWanted >= bot.RocksDug, $"броски {stoneWanted} < снятых скал {bot.RocksDug} (скала даёт 1…2)");
            Assert.Equal(stoneWanted, digger.Inv.CountAt(Material.Stone)); // своя сумка (предсказание + тип 26)
            Assert.Equal(stoneWanted, rig.Sim.GetEntity(digger.EntityId).Inv.CountAt(Material.Stone)); // сумка сервера
            Assert.Equal(0, digger.World!.PredictionsRolledBack); // предсказания добычи не откатывались

            // У наблюдателя — все снятые скалы, чанки = сервер побайтно.
            int seen = bot.RockTiles.Count(t => observer.World!.World.FindChunk(Torus.ChunkOf(shape, t)) != null &&
                                                observer.World.World.GetObject(t) == ObjectId.Empty);
            foreach (ChunkCoord c in revised)
            {
                Assert.True(observer.World!.SameAs(rig.Sim.World, c), $"чанк ({c.X}, {c.Y}) у наблюдателя ≠ сервер");
            }

            int seam = bot.RockTiles.Count(t => t.X < half + 2) ;
            _out.WriteLine($"у наблюдателя {seen} из {bot.RocksDug}; сверок пачек {patchChecks}, расхождений {mismatches}; " +
                           $"скал восточнее шва {seam}, западнее {bot.RocksDug - seam}");
            Assert.Equal(bot.RocksDug, seen);
            Assert.Equal(0, mismatches);
            Assert.True(seam > 0 && seam < bot.RocksDug, "скалы по обе стороны шва");
            Assert.Equal(0, digger.World!.PendingPredictions);
            Assert.All(digger.World.World.GetChunkCoords().ToArray(), c => Assert.True(digger.World.SameAs(rig.Sim.World, c)));

            // Замер (`08` 0.7, `07` 3.6): чанк по сети — сжатая копия; сообщение — плюс 10 байт заголовка.
            double secs = rig.Ticks / 30.0;
            double avgCopy = tap.ChunkCount > 0 ? (double)(tap.ChunkBytes - tap.ChunkCount * ChunkMessage.HeaderBytes) / tap.ChunkCount : 0;
            _out.WriteLine($"чанков по сети {tap.ChunkCount}: сжатая копия в среднем {avgCopy:0} байт, наибольшая {tap.MaxChunkBytes - ChunkMessage.HeaderBytes} " +
                           $"(модель `07` 3.6: 236 / 425); сообщение в среднем {(double)tap.ChunkBytes / Math.Max(1, tap.ChunkCount):0} байт");
            _out.WriteLine($"пачек правок {tap.PatchCount} ({tap.PatchBytes} байт, в среднем {(double)tap.PatchBytes / Math.Max(1, tap.PatchCount):0.0}); " +
                           $"наблюдателю {tap.PatchBytesTo(0)} байт за {secs:0} с = {tap.PatchBytesTo(0) / secs:0.0} Б/с; ответов на правки {tap.ReplyCount} ({tap.ReplyBytes} байт)");
            _out.WriteLine($"сервер: запросов {rig.Server.EditRequests}, принято {rig.Server.EditsAccepted}, отказов {rig.Server.EditsRefused}; " +
                           $"предсказаний у копающего {digger.World.Predicted}, подтверждено {digger.World.PredictionsConfirmed}, " +
                           $"откатов {digger.World.PredictionsRolledBack}, улажено пачкой раньше ответа {digger.World.PredictionsSettledByPatch}");

            // Перезапуск: сохранение → загрузка в новый такт и сервер → наблюдатель входит заново.
            byte[] save = WorldSave.Build(WorldSave.Capture(rig.Sim, WorldSnapshot.StoredHash(baseBytes), "dig_line"), 1, 0);
            Simulation loaded = SaveFixtures.Load(baseBytes, save);
            int lostOnServer = bot.RockTiles.Count(t => loaded.World.GetObject(t) != ObjectId.Empty);
            foreach (ChunkCoord c in revised)
            {
                Assert.True(loaded.World.FindChunk(c)!.ContentEquals(rig.Sim.World.FindChunk(c)!), $"чанк ({c.X}, {c.Y}) после загрузки ≠ до");
            }

            using var rig2 = new EditTests.EditRig(loaded, n => EditTests.TileCenter(obsTile.X, obsTile.Y), null);
            NetClient again = rig2.Join();
            rig2.Run(10);
            int lost = bot.RockTiles.Count(t => again.World!.World.GetObject(t) != ObjectId.Empty);
            foreach (ChunkCoord c in revised)
            {
                Assert.True(again.World!.SameAs(rig.Sim.World, c), $"после перезапуска чанк ({c.X}, {c.Y}) у наблюдателя ≠ сервер до перезапуска");
            }

            _out.WriteLine($"сохранение {save.Length} байт; после перезапуска потеряно на сервере {lostOnServer}, у наблюдателя {lost} " +
                           $"из {bot.RocksDug}; время теста {wall.Elapsed.TotalSeconds:0.0} с");
            Assert.Equal(0, lostOnServer);
            Assert.Equal(0, lost);
        }

        /// <summary>
        /// Полоса у шва с наибольшим числом скал: полуширина 48…96 тайлов (окно наблюдателя на шве — ±96), 128 строк от границы чанков
        /// (4 чанка окна по высоте). Возвращает полуширину, первую строку и число скал (строки y0 … y0 + 126, как пройдёт бот).
        /// </summary>
        internal static (int half, int y0, int rocks) PickBand(WorldModel world, int target)
        {
            MapShape shape = world.Shape;
            (int, int, int) best = (0, 0, 0);
            foreach (int half in new[] { 48, 64, 80, 92 })
            {
                for (int y0 = 0; y0 < shape.Height; y0 += Torus.ChunkSize)
                {
                    int n = 0;
                    bool barrier = false; // E1-2: полоса с водой или обрывом боту не годится — их не выкопать и не пройти
                    for (int y = y0; y < y0 + 126; y++)
                    {
                        for (int x = -half - 6; x <= half + 6; x++)
                        {
                            TileCoord t = Torus.Wrap(shape, new TileCoord(x, y));
                            ushort id = ObjectId.IdOf(world.GetObject(t));
                            barrier |= id == ObjectId.Cliff || world.GetFloor(t) == FloorId.Water;
                            if (id == ObjectId.Rock && x >= -half && x <= half)
                            {
                                n++;
                            }
                        }
                    }

                    if (!barrier && n > best.Item3)
                    {
                        best = (half, y0, n);
                    }
                }

                if (best.Item3 >= target * 3 / 2)
                {
                    break;
                }
            }

            return best;
        }

        /// <summary>Обёртка транспорта сервера: счёт отправленных сообщений мира и ответов по типам.</summary>
        private sealed class CountingTransport : ITransport
        {
            private ITransport _inner = null!;
            private readonly Dictionary<int, long> _patchTo = new Dictionary<int, long>();

            public long ChunkCount, ChunkBytes, PatchCount, PatchBytes, ReplyCount, ReplyBytes;
            public int MaxChunkBytes;
            private int _firstPeer = -1;

            public ITransport Wrap(ITransport inner)
            {
                _inner = inner;
                return this;
            }

            /// <summary>Байт пачек правок клиенту по счёту входа (0 — первый вошедший).</summary>
            public long PatchBytesTo(int n) => _patchTo.TryGetValue(_firstPeer + n, out long v) ? v : 0;

            public void Poll(long nowMicros, ITransportHandler handler) => _inner.Poll(nowMicros, handler);

            public void Send(int peer, NetChannel channel, ReadOnlySpan<byte> data)
            {
                if (_firstPeer < 0 || peer < _firstPeer)
                {
                    _firstPeer = peer;
                }

                switch ((MessageType)data[0])
                {
                    case MessageType.Chunk:
                        ChunkCount++;
                        ChunkBytes += data.Length;
                        MaxChunkBytes = Math.Max(MaxChunkBytes, data.Length);
                        break;
                    case MessageType.ChunkPatch:
                        PatchCount++;
                        PatchBytes += data.Length;
                        _patchTo[peer] = (_patchTo.TryGetValue(peer, out long v) ? v : 0) + data.Length;
                        break;
                    case MessageType.TileReply:
                        ReplyCount++;
                        ReplyBytes += data.Length;
                        break;
                }

                _inner.Send(peer, channel, data);
            }

            public void Flush() => _inner.Flush();
            public void Disconnect(int peer, ReadOnlySpan<byte> payload) => _inner.Disconnect(peer, payload);
            public int RoundTripMs(int peer) => _inner.RoundTripMs(peer);
            public void Dispose() => _inner.Dispose();
        }
    }
}
