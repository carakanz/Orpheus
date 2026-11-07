using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Orpheus.Bots;
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
    /// Сеть (карточка 0-008; docs/07 2.10, 4.3, 11.3, 2.13 п. 1): кодек, бюджет снимка, имитатор связи,
    /// вход и снимки на связи в памяти и по UDP.
    /// </summary>
    public class NetTests
    {
        private const int U = Torus.SubTilesPerTile;
        private const long FrameMicros = 1_000_000 / 60;
        private const long TickMicros = 1_000_000 / Simulation.TickRate;

        private readonly ITestOutputHelper _out;

        public NetTests(ITestOutputHelper output)
        {
            _out = output;
        }

        // ---------- Кодек: туда-обратно побайтно ----------

        [Fact]
        public void D00_Net_Udp_LoopbackOnly_ListensOnLoopbackOnly()
        {
            // Сервер по умолчанию и подключение к 127.0.0.1: сокеты только на петле — брандмауэр Windows не спрашивает разрешения.
            using LiteNetTransport server = LiteNetTransport.Listen(0, 1, loopbackOnly: true);
            using LiteNetTransport client = LiteNetTransport.Connect("127.0.0.1", server.LocalPort);
            IPEndPoint[] listeners = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners();
            foreach (int port in new[] { server.LocalPort, client.LocalPort })
            {
                var mine = Array.FindAll(listeners, e => e.Port == port);
                Assert.NotEmpty(mine);
                Assert.All(mine, e => Assert.True(IPAddress.IsLoopback(e.Address), $"порт {port} слушает {e.Address}"));
            }

            Assert.True(LiteNetTransport.IsLoopbackHost("localhost"));
            Assert.True(LiteNetTransport.IsLoopbackHost("127.0.0.1"));
            Assert.True(LiteNetTransport.IsLoopbackHost("::1"));
            Assert.False(LiteNetTransport.IsLoopbackHost("192.168.1.10"));
            Assert.False(LiteNetTransport.IsLoopbackHost("example.org"));
            Assert.True(ServerOptions.Parse(new[] { "--create", "--port", "0" }).LoopbackOnly);
            Assert.False(ServerOptions.Parse(new[] { "--create", "--port", "0", "--public" }).LoopbackOnly);
        }

        [Fact]
        public void D00_Net_Codec_Login_RoundTripBytes()
        {
            var m = new LoginMessage { Version = new NetVersion(0, 4, 2), Name = "Орфей" };
            byte[] a = Encode(b => m.Write(b));
            LoginMessage back = LoginMessage.Read(a);
            Assert.Equal(m.Version, back.Version);
            Assert.Equal(m.Name, back.Name);
            Assert.Equal(a, Encode(b => back.Write(b)));
            Assert.Equal(1 + 6 + 1 + 10, a.Length); // «Орфей» — 10 байт UTF-8
        }

        [Fact]
        public void D00_Net_Codec_LoginAccept_RoundTripBytes()
        {
            var m = new LoginAcceptMessage
            {
                EntityId = 123456, ServerTick = 4_000_000_000, MapWidth = 4096, MapHeight = 4096, Wraps = true,
                WorldSeed = 0xDEADBEEF_CAFEBABE, TickRate = 30, GenerationHash = 0x0123_4567_89AB_CDEF,
            };
            byte[] a = Encode(b => m.Write(b));
            Assert.Equal(LoginAcceptMessage.Bytes, a.Length);
            LoginAcceptMessage back = LoginAcceptMessage.Read(a);
            Assert.Equal(m.EntityId, back.EntityId);
            Assert.Equal(m.ServerTick, back.ServerTick);
            Assert.Equal(m.MapWidth, back.MapWidth);
            Assert.Equal(m.MapHeight, back.MapHeight);
            Assert.Equal(m.Wraps, back.Wraps);
            Assert.Equal(m.WorldSeed, back.WorldSeed);
            Assert.Equal(m.TickRate, back.TickRate);
            Assert.Equal(m.GenerationHash, back.GenerationHash);
            Assert.Equal(a, Encode(b => back.Write(b)));
        }

        [Fact]
        public void D00_Net_Codec_LoginReject_RoundTripBytes()
        {
            var m = new LoginRejectMessage { Reason = LoginRejectReason.VersionMismatch, ServerVersion = new NetVersion(0, 4, 2) };
            byte[] a = Encode(b => m.Write(b));
            Assert.Equal(LoginRejectMessage.Bytes, a.Length);
            LoginRejectMessage back = LoginRejectMessage.Read(a);
            Assert.Equal(m.Reason, back.Reason);
            Assert.Equal(m.ServerVersion, back.ServerVersion);
            Assert.Equal(a, Encode(b => back.Write(b)));
            Assert.Equal("Версии не совпадают: у сервера 0.4.2, у тебя 0.4.1", back.Describe(new NetVersion(0, 4, 1)));
        }

        [Fact]
        public void D00_Net_Codec_Input_Is38Bytes_RoundTripBytes()
        {
            var m = new InputMessage
            {
                FirstFrame = 0xFFFF_FFF0,
                Frame0 = new WireFrame { Direction = 1, Power = 255, Aim = 0x1234, Buttons = 0x8001 },
                Frame1 = new WireFrame { Direction = 0, Power = 0, Aim = 65535, Buttons = 0 },
                Frame2 = new WireFrame { Direction = 8, Power = 17, Aim = 1, Buttons = 0xFFFF },
                Frame3 = new WireFrame { Direction = 5, Power = 200, Aim = 40000, Buttons = 0x0102 },
                PictureTime = 77, LastSnapshot = 0x7FFF_FFFF, Lead = -5,
            };
            byte[] a = Encode(b => m.Write(b));
            Assert.Equal(38, a.Length);
            Assert.Equal(InputMessage.Bytes, a.Length);
            InputMessage back = InputMessage.Read(a);
            for (int i = 0; i < InputMessage.FrameCount; i++)
            {
                Assert.Equal(m[i], back[i]);
            }

            Assert.Equal(m.FirstFrame, back.FirstFrame);
            Assert.Equal(m.FirstFrame + 3, back.LastFrame);
            Assert.Equal(m.PictureTime, back.PictureTime);
            Assert.Equal(m.LastSnapshot, back.LastSnapshot);
            Assert.Equal(m.Lead, back.Lead);
            Assert.Equal(a, Encode(b => back.Write(b)));
            Assert.Equal(new InputFrame(Input8.West, 0x0102), back.Frame3.ToInputFrame()); // код 5 = направление 4 (запад)
        }

        [Fact]
        public void D00_Net_Codec_Clock_RoundTripBytes()
        {
            var m = new ClockMessage { ServerTick = 123_456_789 };
            byte[] a = Encode(b => m.Write(b));
            Assert.Equal(ClockMessage.Bytes, a.Length);
            Assert.Equal(m.ServerTick, ClockMessage.Read(a).ServerTick);
            Assert.Equal(a, Encode(b => ClockMessage.Read(a).Write(b)));
        }

        [Theory]
        [InlineData(1024)]
        [InlineData(4096)]
        public void D00_Net_Codec_Snapshot_RoundTripBytes(int size)
        {
            var layout = new SnapshotLayout(MapShape.World(size));
            var rng = new DetRandom(7);
            var snap = new Snapshot
            {
                Tick = 99_999, AckFrame = 200_003, Count = 40,
                Self = new SelfState(new WorldPos(size * U - 1, rng.NextInt(size * U)), new Int2(-9, 7)), // без кванта (0-019)
            };
            for (int i = 0; i < snap.Count; i++)
            {
                snap.Entities[i] = new SnapshotEntity
                {
                    Id = i == 0 ? int.MaxValue : i * 37,
                    Kind = (byte)(i % 3),
                    Position = SnapshotLayout.Quantize(new WorldPos(rng.NextInt(size * U), rng.NextInt(size * U))),
                    Direction = (byte)(i % 9),
                };
            }

            byte[] a = Encode(b => snap.Write(b, layout));
            Assert.Equal(layout.SizeFor(snap.Count), a.Length);
            var back = new Snapshot();
            back.Read(a, layout);
            Assert.Equal(snap.Tick, back.Tick);
            Assert.Equal(snap.AckFrame, back.AckFrame);
            Assert.Equal(snap.Count, back.Count);
            Assert.Equal(snap.Self, back.Self);
            for (int i = 0; i < snap.Count; i++)
            {
                Assert.Equal(snap.Entities[i].Id, back.Entities[i].Id);
                Assert.Equal(snap.Entities[i].Kind, back.Entities[i].Kind);
                Assert.Equal(snap.Entities[i].Position, back.Entities[i].Position);
                Assert.Equal(snap.Entities[i].Direction, back.Entities[i].Direction);
            }

            Assert.Equal(a, Encode(b => back.Write(b, layout)));

            // Своё состояние (0-019): 1024² — 47 бит, 4096² — 51; остаток скорости вне −9…9 — испорченный снимок.
            Assert.Equal(size == 1024 ? 47 : 51, layout.SelfBits);
            snap.Self = new SelfState(new WorldPos(5, 5), new Int2(12, 0));
            byte[] bad = Encode(b => snap.Write(b, layout));
            Assert.Throws<ProtocolException>(() => back.Read(bad, layout));
            snap.Self = default;
            back.Read(Encode(b => snap.Write(b, layout)), layout);
            Assert.False(back.Self.Present);
        }

        // ---------- Дельта снимка: кодек (карточка 0-014, docs/07 4.3) ----------

        [Theory]
        [InlineData(1024)]
        [InlineData(4096)]
        public void D00_Net_Codec_SnapshotDelta_RoundTrip_ApplyTo(int size)
        {
            var layout = new SnapshotLayout(MapShape.World(size));
            var rng = new DetRandom(5);
            WorldPos RandomPos() => SnapshotLayout.Quantize(new WorldPos(rng.NextInt(size * U - 1), rng.NextInt(size * U - 1)));

            var baseEnts = new SnapshotEntity[Snapshot.MaxEntities];
            const int baseCount = 20;
            for (int i = 0; i < baseCount; i++)
            {
                baseEnts[i] = new SnapshotEntity { Id = 100 + i, Kind = (byte)(i % 3), Position = RandomPos(), Direction = (byte)(i % 9) };
            }

            WorldPos p0 = RandomPos();
            WorldPos p3 = RandomPos();
            var added = new[]
            {
                new SnapshotEntity { Id = 70_000, Kind = 2, Position = RandomPos(), Direction = 4 },
                new SnapshotEntity { Id = 7, Kind = 1, Position = RandomPos(), Direction = 0 },
            };

            var buf = new byte[NetSettings.MaxUnreliablePayload];
            var self = new SelfState(new WorldPos(3, size * U - 2), new Int2(4, -3));
            var w = new SnapshotDeltaWriter(buf, layout, 123_456, 246_912, 123_430, self);
            Assert.True(w.TryAddRemoved(1));
            Assert.True(w.TryAddRemoved(5));
            Assert.True(w.TryAddUpdated(0, DeltaFields.Position | DeltaFields.Direction, p0, 8));
            Assert.True(w.TryAddUpdated(3, DeltaFields.Position, p3, 0));
            Assert.True(w.TryAddUpdated(7, DeltaFields.Direction, default, 2));
            foreach (SnapshotEntity e in added)
            {
                Assert.True(w.TryAddAdded(e.Id, e.Kind, e.Position, e.Direction));
            }

            int len = w.Finish();
            int bits = 2 * SnapshotDeltaMessage.IndexBits +
                       SnapshotDeltaMessage.UpdatedBits(DeltaFields.Position | DeltaFields.Direction, layout) +
                       SnapshotDeltaMessage.UpdatedBits(DeltaFields.Position, layout) +
                       SnapshotDeltaMessage.UpdatedBits(DeltaFields.Direction, layout) + 2 * layout.EntityBits;
            Assert.Equal(SnapshotDeltaMessage.HeaderBytes + (layout.SelfBits + bits + 7) / 8, len);
            Assert.Equal((byte)MessageType.SnapshotDelta, buf[0]);
            Assert.Equal(123_456u, Snapshot.PeekTick(new ReadOnlySpan<byte>(buf, 0, len)));

            var d = new SnapshotDeltaMessage();
            d.Read(new ReadOnlySpan<byte>(buf, 0, len), layout);
            Assert.Equal(123_456u, d.Tick);
            Assert.Equal(246_912u, d.AckFrame);
            Assert.Equal(123_430u, d.BaselineTick);
            Assert.Equal(self, d.Self);
            Assert.Equal((2, 3, 2), (d.RemovedCount, d.UpdatedCount, d.AddedCount));

            var into = new SnapshotEntity[Snapshot.MaxEntities];
            int n = d.ApplyTo(baseEnts, baseCount, into);
            Assert.Equal(baseCount - 2 + 2, n);

            // Порядок: база без №1 и №5, изменения на месте, новые — в конце.
            var expect = new List<SnapshotEntity>();
            for (int i = 0; i < baseCount; i++)
            {
                if (i == 1 || i == 5)
                {
                    continue;
                }

                SnapshotEntity e = baseEnts[i];
                if (i == 0)
                {
                    e.Position = p0;
                    e.Direction = 8;
                }
                else if (i == 3)
                {
                    e.Position = p3;
                }
                else if (i == 7)
                {
                    e.Direction = 2;
                }

                expect.Add(e);
            }

            expect.AddRange(added);
            Assert.Equal(expect, into.Take(n));
        }

        [Fact]
        public void D00_Net_Codec_SnapshotDelta_Budget_StopsAtLimit()
        {
            var layout = new SnapshotLayout(MapShape.World(1024));
            var buf = new byte[NetSettings.MaxUnreliablePayload];
            const int limit = 60;

            var w = new SnapshotDeltaWriter(buf, layout, 10, 2, 9, limit);
            int n = 0;
            while (w.TryAddUpdated(n, DeltaFields.Position, new WorldPos(4, 4), 0))
            {
                n++;
            }

            // Позиция 1024² — 30 бит: изменение 40 бит = 5 байт; в 60 − 13 байт без своего состояния (47 бит) влезает 8.
            Assert.Equal(((limit - SnapshotDeltaMessage.HeaderBytes) * 8 - layout.SelfBits) / SnapshotDeltaMessage.UpdatedBits(DeltaFields.Position, layout), n);
            Assert.False(w.TryAddAdded(1, 1, new WorldPos(0, 0), 0));
            Assert.InRange(w.Finish(), SnapshotDeltaMessage.HeaderBytes, limit);
        }

        [Fact]
        public void D00_Net_Codec_SnapshotDelta_Bad_Throws()
        {
            var layout = new SnapshotLayout(MapShape.World(1024));
            var buf = new byte[64];
            var d = new SnapshotDeltaMessage();
            var baseEnts = new SnapshotEntity[Snapshot.MaxEntities];
            var into = new SnapshotEntity[Snapshot.MaxEntities];

            // Код направления > 8.
            var w = new SnapshotDeltaWriter(buf, layout, 5, 2, 4);
            Assert.True(w.TryAddAdded(5, 1, new WorldPos(0, 0), 9));
            int len = w.Finish();
            Assert.Throws<ProtocolException>(() => d.Read(new ReadOnlySpan<byte>(buf, 0, len), layout));

            // Номер вне базы и повтор номера — при склейке.
            w = new SnapshotDeltaWriter(buf, layout, 5, 2, 4);
            Assert.True(w.TryAddUpdated(3, DeltaFields.Direction, default, 1));
            len = w.Finish();
            d.Read(new ReadOnlySpan<byte>(buf, 0, len), layout);
            Assert.Throws<ProtocolException>(() => d.ApplyTo(baseEnts, 3, into));
            Assert.Equal(4, d.ApplyTo(baseEnts, 4, into));

            w = new SnapshotDeltaWriter(buf, layout, 5, 2, 4);
            Assert.True(w.TryAddRemoved(2));
            Assert.True(w.TryAddUpdated(2, DeltaFields.Direction, default, 1));
            len = w.Finish();
            d.Read(new ReadOnlySpan<byte>(buf, 0, len), layout);
            Assert.Throws<ProtocolException>(() => d.ApplyTo(baseEnts, 4, into));

            // Отставание базы 0 и больше 255 — не пишется; разделы — только по порядку.
            Assert.Throws<ArgumentOutOfRangeException>(() => new SnapshotDeltaWriter(buf, layout, 5, 2, 5));
            Assert.Throws<ArgumentOutOfRangeException>(() => new SnapshotDeltaWriter(buf, layout, 300, 2, 44));
            bool threw = false;
            try
            {
                var w2 = new SnapshotDeltaWriter(buf, layout, 5, 2, 4);
                w2.TryAddAdded(1, 1, default, 0);
                w2.TryAddRemoved(0);
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }

            Assert.True(threw, "исчезнувшая после новой должна быть ошибкой");

            // Испорченный заголовок: отставание 0.
            w = new SnapshotDeltaWriter(buf, layout, 5, 2, 4);
            len = w.Finish();
            buf[9] = 0;
            Assert.Throws<ProtocolException>(() => d.Read(new ReadOnlySpan<byte>(buf, 0, len), layout));
        }

        [Fact]
        public void D00_Net_Codec_BitWriter_RandomFields_RoundTrip()
        {
            var rng = new DetRandom(11);
            var widths = new int[2000];
            var values = new uint[2000];
            byte[] buf = new byte[2000 * 4];
            var w = new BitWriter(buf);
            for (int i = 0; i < widths.Length; i++)
            {
                widths[i] = rng.NextInt(33);
                values[i] = widths[i] == 0 ? 0 : (uint)rng.Next() & (widths[i] == 32 ? uint.MaxValue : (1u << widths[i]) - 1);
                w.WriteBits(values[i], widths[i]);
            }

            int bytes = w.BytesWritten;
            var r = new BitReader(new ReadOnlySpan<byte>(buf, 0, bytes));
            for (int i = 0; i < widths.Length; i++)
            {
                Assert.Equal(values[i], r.ReadBits(widths[i]));
            }

            r.ExpectEnd();
        }

        [Fact]
        public void D00_Net_Codec_BrokenMessages_ThrowProtocolException()
        {
            byte[] input = Encode(b => new InputMessage { FirstFrame = 5 }.Write(b));
            Assert.Throws<ProtocolException>(() => InputMessage.Read(input.AsSpan(0, 37).ToArray()));
            Assert.Throws<ProtocolException>(() => InputMessage.Read(Append(input, 0)));
            Assert.Throws<ProtocolException>(() => LoginMessage.Read(input));
            Assert.Throws<ProtocolException>(() => Codec.TypeOf(new byte[] { 0 }));
            Assert.Throws<ProtocolException>(() => Codec.TypeOf(new byte[] { 200 }));
            Assert.Throws<ProtocolException>(() => Codec.TypeOf(Array.Empty<byte>()));
            byte[] badDir = Encode(b => new InputMessage { Frame3 = new WireFrame { Direction = 9 } }.Write(b));
            Assert.Throws<ProtocolException>(() => InputMessage.Read(badDir).Frame3.ToInputFrame());
            Assert.Throws<ArgumentException>(() => Encode(b => new LoginMessage { Name = new string('я', 17) }.Write(b))); // 34 байта
        }

        // ---------- Бюджет снимка ----------

        [Theory]
        [InlineData(1024)]
        [InlineData(4096)]
        public void D00_Net_Snapshot_EightPlayers_Within1200Bytes(int size)
        {
            var world = FlatWorldGenerator.Instance.Generate(MapShape.World(Math.Min(size, 1024)), 1);
            var layout = new SnapshotLayout(MapShape.World(size));
            int bytes = layout.SizeFor(8);
            _out.WriteLine($"{size}²: сущность {layout.EntityBits} бит, снимок 8 игроков — {bytes} байт, в бюджет 1200 влезает {layout.MaxEntitiesInBudget}");
            Assert.True(bytes <= NetSettings.SnapshotBudget, $"{bytes} байт");

            // Через сервер: 8 клиентов на связи в памяти, все рядом — каждый снимок ≤ 1200 и несёт всех восьмерых.
            using var h = new LoopbackHarness(world, 8, maxPlayers: 8);
            h.Run(30);
            Assert.Equal(8, h.Server.PlayersInGame);
            Assert.True(h.Server.MaxSnapshotBytes <= NetSettings.SnapshotBudget);
            foreach (NetBot b in h.Bots)
            {
                Assert.Equal(8, b.Client.Latest.Count);
            }

            _out.WriteLine($"через сервер: наибольший снимок {h.Server.MaxSnapshotBytes} байт");
            Assert.Equal(0, h.Server.SnapshotsTruncated);
        }

        [Fact]
        public void D00_Net_Snapshot_OverBudget_TruncatedTo1200()
        {
            var layout = new SnapshotLayout(MapShape.World(4096));
            byte[] buf = new byte[NetSettings.MaxUnreliablePayload];
            var w = new SnapshotWriter(buf, layout, 1, 1);
            int added = 0;
            while (w.TryAdd(added + 1, 1, new WorldPos(added * 100, added * 50), 0))
            {
                added++;
            }

            int len = w.Finish();
            Assert.Equal(layout.MaxEntitiesInBudget, added);
            Assert.True(len <= NetSettings.SnapshotBudget, $"{len}");
            var back = new Snapshot();
            back.Read(new ReadOnlySpan<byte>(buf, 0, len), layout);
            Assert.Equal(added, back.Count);
        }

        // ---------- Имитатор связи ----------

        [Fact]
        public void D00_Net_Sim_LossAndDelay_10000Packets_WithinTolerance()
        {
            var s = new NetSimSettings { DelayMs = 50, JitterMs = 10, LossPpm = 50_000, Seed = 42 };
            List<(int Seq, long At)> got = SimRun(s, 10_000, out NetSimStats stats, out double meanDelayMs, out double sigmaMs);
            double lossRate = 1 - got.Count / 10_000.0;
            _out.WriteLine($"потери {lossRate:P2} (задано 5 %), задержка средняя {meanDelayMs:F2} мс (50), σ {sigmaMs:F2} мс (10)");

            // Биномиальное σ при n = 10 000, p = 0,05 — 0,22 %: допуск ±1 % (4,5σ).
            Assert.InRange(lossRate, 0.04, 0.06);
            Assert.InRange(meanDelayMs, 49.0, 51.0);
            Assert.InRange(sigmaMs, 9.0, 11.0);
            Assert.Equal(stats.Packets - stats.Lost, got.Count);
        }

        [Fact]
        public void D00_Net_Sim_SameSeed_SameDelivery_OtherSeed_Differs()
        {
            var s = new NetSimSettings { DelayMs = 30, JitterMs = 15, LossPpm = 20_000, DuplicatePpm = 10_000, ReorderPpm = 10_000, Seed = 1 };
            List<(int, long)> a = SimRun(s, 10_000, out _, out _, out _);
            List<(int, long)> b = SimRun(s, 10_000, out _, out _, out _);
            Assert.Equal(a, b);
            s.Seed = 2;
            List<(int, long)> c = SimRun(s, 10_000, out _, out _, out _);
            Assert.NotEqual(a, c);
        }

        [Fact]
        public void D00_Net_Sim_DuplicatesAndReorder()
        {
            var s = new NetSimSettings { DelayMs = 20, DuplicatePpm = 30_000, ReorderPpm = 50_000, ReorderMs = 40, Seed = 5 };
            List<(int Seq, long At)> got = SimRun(s, 10_000, out NetSimStats stats, out _, out _);
            int dups = got.Count - 10_000;
            int inversions = 0;
            for (int i = 1; i < got.Count; i++)
            {
                if (got[i].Seq < got[i - 1].Seq)
                {
                    inversions++;
                }
            }

            _out.WriteLine($"дублей {dups} (≈300), перестановок {inversions}, переставлено имитатором {stats.Reordered} (≈500)");
            Assert.InRange(dups, 220, 380);
            Assert.InRange(stats.Reordered, 400, 600);
            Assert.True(inversions > 300, $"{inversions}");
        }

        [Fact]
        public void D00_Net_Sim_ReliableChannel_NoLossInOrder()
        {
            var s = new NetSimSettings { DelayMs = 50, JitterMs = 20, LossPpm = 100_000, DuplicatePpm = 100_000, ReorderPpm = 100_000, Seed = 9 };
            List<(int Seq, long At)> got = SimRun(s, 2_000, out NetSimStats stats, out _, out _, NetChannel.Facts);
            Assert.Equal(2_000, got.Count);
            for (int i = 0; i < got.Count; i++)
            {
                Assert.Equal(i, got[i].Seq);
            }

            Assert.InRange(stats.Retransmits, 150, 300); // ≈ 10 % + повторные
        }

        [Fact]
        public void D00_Net_Sim_BurstJitter_SharedPerWindow()
        {
            var s = new NetSimSettings { DelayMs = 100, JitterMs = 20, BurstJitter = true, BurstMs = 250, Seed = 3 };
            List<(int Seq, long At)> got = SimRun(s, 10_000, out _, out _, out double sigma);
            _out.WriteLine($"σ пачками {sigma:F1} мс (задано 20, 40 пачек)");
            Assert.Equal(10_000, got.Count);
            Assert.InRange(sigma, 12, 28);

            // Внутри пачки (250 мс = 250 пакетов по 1 мс) добавка одна: задержка пакетов пачки одинакова
            // с точностью до шага опроса (1 мс); между пачками — разная.
            var minDelay = new long[40];
            var maxDelay = new long[40];
            Array.Fill(minDelay, long.MaxValue);
            foreach ((int seq, long at) in got)
            {
                int window = seq / 250;
                long delay = at - seq * 1000L;
                minDelay[window] = Math.Min(minDelay[window], delay);
                maxDelay[window] = Math.Max(maxDelay[window], delay);
            }

            var distinct = new HashSet<long>();
            for (int w = 0; w < 40; w++)
            {
                Assert.True(maxDelay[w] - minDelay[w] <= 1000, $"пачка {w}: {minDelay[w]}…{maxDelay[w]} мкс");
                distinct.Add(minDelay[w] / 1000);
            }

            Assert.True(distinct.Count > 10, $"{distinct.Count}");
        }

        /// <summary>
        /// Сервер шлёт <paramref name="n"/> пакетов по одному в миллисекунду виртуального времени клиенту, обёрнутому
        /// имитатором; возвращает (номер, время прихода) в порядке прихода.
        /// </summary>
        private static List<(int Seq, long At)> SimRun(
            NetSimSettings s, int n, out NetSimStats stats, out double meanDelayMs, out double sigmaMs, NetChannel channel = NetChannel.Unreliable)
        {
            using LoopbackTransport server = LoopbackTransport.CreateServer(4);
            using var client = new NetSimTransport(LoopbackTransport.ConnectTo(server), s);
            var serverSide = new Recorder();
            var clientSide = new Recorder();
            server.Poll(0, serverSide);
            client.Poll(0, clientSide);
            int peer = serverSide.Connected[0];
            byte[] buf = new byte[12];
            long now = 0;
            for (int i = 0; i < n; i++, now += 1000)
            {
                BitConverter.TryWriteBytes(buf, i);
                BitConverter.TryWriteBytes(buf.AsSpan(4), now);
                server.Send(peer, channel, buf);
                clientSide.Now = now;
                client.Poll(now, clientSide);
            }

            for (long end = now + 10_000_000; now < end; now += 1000)
            {
                clientSide.Now = now;
                client.Poll(now, clientSide);
            }

            stats = client.ReceiveStats;
            double sum = 0;
            double sum2 = 0;
            foreach ((int _, long at, long sent) in clientSide.Received)
            {
                double d = (at - sent) / 1000.0;
                sum += d;
                sum2 += d * d;
            }

            int c = clientSide.Received.Count;
            meanDelayMs = c > 0 ? sum / c : 0;
            sigmaMs = c > 0 ? Math.Sqrt(Math.Max(0, sum2 / c - meanDelayMs * meanDelayMs)) : 0;
            var result = new List<(int, long)>(c);
            foreach ((int seq, long at, long _) in clientSide.Received)
            {
                result.Add((seq, at));
            }

            return result;
        }

        private sealed class Recorder : ITransportHandler
        {
            public long Now;
            public readonly List<int> Connected = new List<int>();
            public readonly List<(int Seq, long At, long Sent)> Received = new List<(int, long, long)>();

            public void OnConnected(int peer) => Connected.Add(peer);

            public void OnDisconnected(int peer, NetDisconnectReason reason, ReadOnlySpan<byte> payload)
            {
            }

            public void OnReceive(int peer, NetChannel channel, ReadOnlySpan<byte> data) =>
                Received.Add((BitConverter.ToInt32(data), Now, BitConverter.ToInt64(data.Slice(4))));
        }

        // ---------- Вход и снимки на связи в памяти ----------

        [Fact]
        public void D00_Net_Loopback_TwoBots_LoginMove_SeeEachOther()
        {
            var world = FlatWorldGenerator.Instance.Generate(MapShape.World(1024), 42);
            using var h = new LoopbackHarness(world, 2);
            h.Run(300); // 10 с
            Assert.Equal(2, h.Server.PlayersInGame);
            foreach (NetBot b in h.Bots)
            {
                NetClient c = b.Client;
                Assert.Equal(NetClientState.InGame, c.State);
                Assert.InRange(c.SnapshotsApplied, 295, 300);
                Assert.Equal(0, c.SnapshotsMissed);
                Assert.Equal(2, c.Latest.Count);
                Assert.Equal(c.EntityId, c.Latest.Entities[0].Id); // своя — первой
                int other = h.Bots[0] == b ? h.Bots[1].Client.EntityId : h.Bots[0].Client.EntityId;
                Assert.True(c.Latest.TryFind(other, out _), "не видит второго бота");
            }

            // Двигались: позиция ушла от точки появления.
            for (int i = 0; i < 2; i++)
            {
                h.Bots[i].Client.Latest.TryFind(h.Bots[i].Client.EntityId, out SnapshotEntity e);
                Assert.NotEqual(SnapshotLayout.Quantize(ServerRun.SpawnPoint(world.Shape, i)), e.Position);
            }

            // Сверка по такту: позиция в снимке = позиция на сервере в том же такте (квантование 1/32).
            Assert.True(h.CheckedByTick > 500, $"{h.CheckedByTick}");
            Assert.True(h.MaxErrorUnits <= 4, $"{h.MaxErrorUnits}");
        }

        [Fact]
        public void D00_Net_Loopback_Rtt100Loss1_DeterministicBySeed()
        {
            var s = NetSimSettings.FromRtt(100, 10, 1.0, 77);
            ulong a = RunWithSim(s, out LoopbackHarness h1);
            ulong b = RunWithSim(s, out _);
            Assert.Equal(a, b);
            foreach (NetBot bot in h1.Bots)
            {
                Assert.Equal(NetClientState.InGame, bot.Client.State);
                Assert.True(bot.Client.SnapshotsApplied > 550, $"{bot.Client.SnapshotsApplied}");
            }

            Assert.True(h1.MaxErrorUnits <= 4, $"{h1.MaxErrorUnits}");
            _out.WriteLine($"хэш {a:x16}; снимков у бота 0: {h1.Bots[0].Client.SnapshotsApplied}, пропущено {h1.Bots[0].Client.SnapshotsMissed}, " +
                           $"отставание снимка ≤ {h1.MaxLagTicks} тактов");
        }

        private static ulong RunWithSim(NetSimSettings s, out LoopbackHarness h)
        {
            var world = FlatWorldGenerator.Instance.Generate(MapShape.World(1024), 42);
            h = new LoopbackHarness(world, 2, sim: s);
            h.Run(600);
            ulong hash = StateHash.Compute(h.Sim).Total;
            h.Dispose();
            return hash;
        }

        [Fact]
        public void D00_Net_Loopback_VersionMismatch_Rejected()
        {
            var world = FlatWorldGenerator.Instance.Generate(MapShape.World(1024), 1);
            using var h = new LoopbackHarness(world, 1, clientVersion: new NetVersion(9, 9, 9));
            h.Run(10);
            NetClient c = h.Bots[0].Client;
            Assert.Equal(NetClientState.Rejected, c.State);
            Assert.Equal(LoginRejectReason.VersionMismatch, c.Reject!.Value.Reason);
            Assert.Equal($"Версии не совпадают: у сервера {NetModule.Version}, у тебя 9.9.9", c.RejectText);
            Assert.Equal(0, h.Server.PlayersInGame);
            Assert.Equal(0, h.Sim.EntityCount);
        }

        [Fact]
        public void D00_Net_Loopback_Leave_RemovesEntity_ServerFull_Rejected()
        {
            var world = FlatWorldGenerator.Instance.Generate(MapShape.World(1024), 1);
            using var h = new LoopbackHarness(world, 3, maxPlayers: 2);
            h.Run(10);
            Assert.Equal(2, h.Server.PlayersInGame);
            Assert.Equal(NetClientState.Rejected, h.Bots[2].Client.State);
            Assert.Equal(LoginRejectReason.ServerFull, h.Bots[2].Client.Reject!.Value.Reason);

            h.Bots[0].Client.Disconnect();
            h.Run(2);
            Assert.Equal(1, h.Server.PlayersInGame);
            Assert.Equal(1, h.Sim.EntityCount);
            Assert.Equal(h.Bots[1].Client.EntityId, h.Sim.Entities[0].Id);
            Assert.Equal(1, h.Bots[1].Client.Latest.Count);
        }

        // ---------- Дельты снимков на связи в памяти (карточка 0-014, docs/07 4.3) ----------

        [Fact]
        public void D00_Net_Loopback_Deltas_SameWorldAsFullFrames_FewerBytes()
        {
            // 6 ботов: двое ходят (меняются каждый такт), четверо стоят — дельта должна быть сильно меньше полного.
            var scripts = new[] { "wander", "wander", "idle", "idle", "idle", "idle" };
            var d = RunWithKeyframes(scripts, keyframePeriod: 30, ticks: 300);
            var f = RunWithKeyframes(scripts, keyframePeriod: 1, ticks: 300);

            Assert.Equal(f.Hash, d.Hash);         // мир на сервере — тот же
            Assert.True(f.Pos.SequenceEqual(d.Pos), string.Join("\n", Diagnostics)); // и что видят клиенты — то же
            // Счётчики сервера — по всем шести игрокам: 29 тактов из 30 — дельты, контрольный кадр раз в 30 тактов.
            Assert.InRange(d.Deltas, d.Sent * 8 / 10, d.Sent * 29 / 30 + scripts.Length);
            Assert.True(d.Full >= scripts.Length * 300 / 30, $"{d.Full}");
            Assert.Equal(0, d.Unmerged);           // без потерь каждая дельта находит базу
            Assert.Equal(0, f.Deltas);
            Assert.True(d.MaxErr <= 4, $"{d.MaxErr}"); // каждая склейка = позиция сервера в такт снимка
            Assert.True(d.Bytes * 3 < f.Bytes * 2, $"байт: с дельтами {d.Bytes}, полными {f.Bytes}");
            _out.WriteLine($"байт/снимок: дельты {d.DeltaBytes / (double)d.Deltas:0.#}, " +
                           $"полный {f.Bytes / (double)f.Sent:0.#}, всего с дельтами {d.Bytes / (double)d.Sent:0.#} " +
                           $"(контрольных кадров {d.Full}, максимальная дельта {d.MaxDelta} байт)");
        }

        private static (ulong Hash, long[] Pos, long Bytes, long Sent, long Deltas, long DeltaBytes, long Full,
                        long Unmerged, int MaxErr, int MaxDelta) RunWithKeyframes(string[] scripts, uint keyframePeriod, int ticks)
        {
            var world = FlatWorldGenerator.Instance.Generate(MapShape.World(1024), 42);
            var h = new LoopbackHarness(world, scripts.Length, scripts: scripts);
            h.Server.KeyframePeriod = keyframePeriod;
            h.Run(ticks);
            Assert.True(h.CheckedDirections > 0, "направления не сверялись");
            Assert.True(h.DirectionErrors == 0, $"период {keyframePeriod}: направление в снимке не совпало {h.DirectionErrors} раз из {h.CheckedDirections}");

            var pos = new long[scripts.Length];
            for (int i = 0; i < scripts.Length; i++)
            {
                NetClient c = h.Bots[i].Client;
                Assert.Equal(NetClientState.InGame, c.State);
                Assert.True(c.Latest.TryFind(c.EntityId, out SnapshotEntity e), "нет своей сущности в снимке");
                pos[i] = ((long)e.Position.X << 32) | (uint)e.Position.Y;
                Diagnostics.Add($"  период {keyframePeriod} бот{i} #{c.EntityId}: t{c.Latest.Tick} n{c.Latest.Count} " +
                                $"[{Ids(c.Latest)}] примен {c.SnapshotsApplied} дельт {c.DeltasApplied} несклеено {c.DeltasUnmerged} " +
                                $"устар {c.SnapshotsStale} плохих {c.BadMessages} [{c.LastBadMessage}]");
            }

            Diagnostics.Add($"  период {keyframePeriod}: полных {h.Server.FullSent} дельт {h.Server.DeltasSent} " +
                            $"обрезано {h.Server.SnapshotsTruncated} такт {h.Sim.Tick}");

            var r = (StateHash.Compute(h.Sim).Total, pos, h.Server.SnapshotBytes, h.Server.SnapshotsSent,
                     h.Server.DeltasSent, h.Server.DeltaBytes, h.Server.FullSent, SumUnmerged(h.Bots),
                     h.MaxErrorUnits, h.Server.MaxDeltaBytes);
            h.Dispose();
            return r;
        }

        private static long SumUnmerged(List<NetBot> bots)
        {
            long n = 0;
            foreach (NetBot b in bots)
            {
                n += b.Client.DeltasUnmerged;
            }

            return n;
        }

        /// <summary>Диагностика склейки (0-014): строки наружу, когда тест падает.</summary>
        private static readonly List<string> Diagnostics = new List<string>();

        private static string Ids(Snapshot s)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < s.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                sb.Append(s.Entities[i].Id).Append(':').Append(s.Entities[i].Position.X).Append(',').Append(s.Entities[i].Position.Y);
            }

            return sb.ToString();
        }

        [Fact]
        public void D00_Net_Loopback_Deltas_Loss1pct_DeltaFromAckedBase()
        {
            var sim = NetSimSettings.FromRtt(100, 10, 1.0, 77);
            var world = FlatWorldGenerator.Instance.Generate(MapShape.World(1024), 42);
            // Один ходит, другой стоит: у обоих в снимке меняется одна сущность из двух — дельта меньше полного.
            using var h = new LoopbackHarness(world, 2, sim: sim, scripts: new[] { "wander", "idle" });
            h.Run(600);

            foreach (NetBot b in h.Bots)
            {
                NetClient c = b.Client;
                Assert.Equal(NetClientState.InGame, c.State);
                Assert.True(c.SnapshotsApplied > 550, $"{c.SnapshotsApplied}");
                Assert.True(c.SnapshotsMissed > 0, "потерянных снимков нет — тест не про потери");
                Assert.True(c.DeltasApplied > 300, $"{c.DeltasApplied}");
                // База — подтверждённый клиентом снимок (4.3), а не последний отправленный: потеря снимка не рвёт цепочку.
                Assert.Equal(0, c.DeltasUnmerged);
                Assert.True(c.Latest.Count == 2, $"t{c.Latest.Tick} n{c.Latest.Count} [{Ids(c.Latest)}] примен {c.SnapshotsApplied} " +
                                                 $"дельт {c.DeltasApplied} несклеено {c.DeltasUnmerged} устар {c.SnapshotsStale} " +
                                                 $"плохих {c.BadMessages} [{c.LastBadMessage}]; полных {h.Server.FullSent} дельт {h.Server.DeltasSent}");
            }

            // Каждое состояние после склейки — точная позиция сервера в такт снимка; отставание — меньше контрольного кадра.
            Assert.True(h.MaxErrorUnits <= 4, $"{h.MaxErrorUnits}");
            Assert.True(h.CheckedOthers > 500 && h.MaxOthersErrorUnits <= 4, $"чужие: {h.CheckedOthers}, ошибка {h.MaxOthersErrorUnits}");
            Assert.Equal(0, h.SelfNotFirst);
            Assert.True(h.CheckedDirections > 1000, $"{h.CheckedDirections}");
            Assert.Equal(0, h.DirectionErrors); // склейка несёт и направление, не только позицию (M2 проверки 0-014)
            Assert.True(h.MaxLagTicks <= 20, $"{h.MaxLagTicks}");
            Assert.True(h.Server.FullSent >= 20, $"{h.Server.FullSent}");
            Assert.Equal(0, h.Server.SnapshotsTruncated);
            _out.WriteLine($"потерь {h.Bots[0].Client.SnapshotsMissed}, дельт {h.Bots[0].Client.DeltasApplied}, " +
                           $"не склеено {h.Bots[0].Client.DeltasUnmerged}, полных кадров {h.Server.FullSent}, " +
                           $"отставание ≤ {h.MaxLagTicks} тактов");
        }

        [Fact]
        public void D00_Net_Loopback_Deltas_Leave_RemovalReachesOthers()
        {
            var world = FlatWorldGenerator.Instance.Generate(MapShape.World(1024), 3);
            using var h = new LoopbackHarness(world, 3, scripts: new[] { "wander", "idle", "idle" });
            h.Run(60);
            int gone = h.Bots[0].Client.EntityId;
            h.Bots[0].Client.Disconnect();
            h.Run(20);

            for (int i = 1; i < 3; i++)
            {
                NetClient c = h.Bots[i].Client;
                Assert.Equal(2, c.Latest.Count);
                Assert.False(c.Latest.TryFind(gone, out _), "удалённая сущность осталась после склейки");
                Assert.True(c.DeltasApplied > 10, $"{c.DeltasApplied}");
            }
        }

        [Fact]
        public void D00_Net_Loopback_Deltas_Blackout_FullFrameWithin1s()
        {
            // Клиенту 0 полторы секунды не доходит канал 0: база (его подтверждение) выпадает из кольца сервера (32),
            // сервер шлёт полный — после включения связи клиент снова видит мир не позже чем через 1 с (4.3).
            var world = FlatWorldGenerator.Instance.Generate(MapShape.World(1024), 42);
            var gates = new List<GateTransport>();
            using var h = new LoopbackHarness(world, 2, scripts: new[] { "wander", "idle" }, wrap: (i, t) =>
            {
                var g = new GateTransport(t);
                gates.Add(g);
                return g;
            });
            h.Run(60);
            NetClient c = h.Bots[0].Client;
            long fullBefore = h.Server.FullSent;
            gates[0].DropUnreliable = true;
            h.Run(45);
            gates[0].DropUnreliable = false;
            uint resumed = (uint)h.Sim.Tick;

            int waited = 0;
            while (c.Latest.Tick <= resumed && waited < 60)
            {
                h.Run(1);
                waited++;
            }

            Assert.True(waited <= 30, $"восстановление {waited} тактов");
            h.Run(60);
            Assert.Equal(2, c.Latest.Count);
            Assert.True((uint)h.Sim.Tick - c.Latest.Tick <= 2, $"t{c.Latest.Tick} при такте {h.Sim.Tick}");
            Assert.True(h.Server.FullSent - fullBefore > 45 / 30 * 2 + 5, "пока база потеряна, сервер должен слать полные");
            Assert.True(h.MaxErrorUnits <= 4 && h.MaxOthersErrorUnits <= 4, $"{h.MaxErrorUnits} {h.MaxOthersErrorUnits}");
            Assert.Equal(0, c.DeltasUnmerged);
            _out.WriteLine($"обрыв 45 тактов: восстановление за {waited} такт(ов), полных за время обрыва {h.Server.FullSent - fullBefore}");
        }

        [Fact]
        public void D00_Net_Loopback_Deltas_AllMoving_NeverBiggerThanFull()
        {
            // Восемь бегут: все сущности меняются каждый такт. Изменение — номер в базе и поля (5 байт против 9 у полной
            // записи), поэтому дельта всё равно меньше; а где не меньше (вход, все новые) — сервер шлёт полный.
            var world = FlatWorldGenerator.Instance.Generate(MapShape.World(1024), 42);
            using var h = new LoopbackHarness(world, 8);
            h.Run(200);
            var full = new LoopbackHarness(world, 8);
            full.Server.KeyframePeriod = 1;
            full.Run(200);
            Assert.True(h.Server.SnapshotBytes <= full.Server.SnapshotBytes, $"{h.Server.SnapshotBytes} > {full.Server.SnapshotBytes}");
            Assert.True(h.Server.DeltasSent > h.Server.FullSent, $"дельт {h.Server.DeltasSent}, полных {h.Server.FullSent}");
            _out.WriteLine($"8 бегут: байт/снимок {h.Server.SnapshotBytes / (double)h.Server.SnapshotsSent:0.#} " +
                           $"против {full.Server.SnapshotBytes / (double)full.Server.SnapshotsSent:0.#} на одних полных");
            Assert.Equal(0, h.SelfNotFirst);
            Assert.True(h.MaxOthersErrorUnits <= 4, $"{h.MaxOthersErrorUnits}");
            Assert.Equal(0, h.DirectionErrors);
            Assert.Equal(0, full.DirectionErrors);
            full.Dispose();
        }

        /// <summary>Обёртка транспорта для тестов: может «глушить» входящий канал 0 (обрыв связи у клиента).</summary>
        private sealed class GateTransport : ITransport, ITransportHandler
        {
            private readonly ITransport _inner;
            private ITransportHandler? _handler;

            public GateTransport(ITransport inner) => _inner = inner;

            public bool DropUnreliable { get; set; }

            /// <summary>Глушить исходящий канал 0 (клиент перестал слать ввод — обрыв в сторону сервера).</summary>
            public bool DropOutgoingUnreliable { get; set; }

            public void Poll(long nowMicros, ITransportHandler handler)
            {
                _handler = handler;
                _inner.Poll(nowMicros, this);
            }

            public void Send(int peer, NetChannel channel, ReadOnlySpan<byte> data)
            {
                if (!(DropOutgoingUnreliable && channel == NetChannel.Unreliable))
                {
                    _inner.Send(peer, channel, data);
                }
            }

            public void Disconnect(int peer, ReadOnlySpan<byte> payload) => _inner.Disconnect(peer, payload);

            public void Flush() => _inner.Flush();

            public int RoundTripMs(int peer) => _inner.RoundTripMs(peer);

            public void Dispose() => _inner.Dispose();

            void ITransportHandler.OnConnected(int peer) => _handler!.OnConnected(peer);

            void ITransportHandler.OnDisconnected(int peer, NetDisconnectReason reason, ReadOnlySpan<byte> payload) =>
                _handler!.OnDisconnected(peer, reason, payload);

            void ITransportHandler.OnReceive(int peer, NetChannel channel, ReadOnlySpan<byte> data)
            {
                if (!(DropUnreliable && channel == NetChannel.Unreliable))
                {
                    _handler!.OnReceive(peer, channel, data);
                }
            }
        }

        [Fact]
        public void D00_Net_Loopback_Deltas_EightPlayers_WithinBudget()
        {
            var world = FlatWorldGenerator.Instance.Generate(MapShape.World(1024), 42);
            // Четверо ходят, четверо стоят: дельта несёт 4 изменившихся из 8 — меньше полного.
            using var h = new LoopbackHarness(world, 8, scripts: new[] { "wander", "idle" });
            h.Run(200);

            Assert.Equal(0, h.Server.SnapshotsTruncated);
            Assert.InRange(h.Server.MaxSnapshotBytes, 1, NetSettings.SnapshotBudget);
            Assert.InRange(h.Server.MaxDeltaBytes, 1, NetSettings.SnapshotBudget);
            Assert.True(h.Server.DeltaBytes * 10 < (h.Server.SnapshotBytes - h.Server.DeltaBytes) * 10 / h.Server.FullSent * h.Server.DeltasSent * 8,
                        "дельта у восьмерых должна быть хотя бы на 20 % меньше полного");
            foreach (NetBot b in h.Bots)
            {
                NetClient c = b.Client;
                Assert.Equal(8, c.Latest.Count);
                Assert.Equal(c.EntityId, c.Latest.Entities[0].Id); // своя сущность — первой и после склейки
                Assert.True(c.DeltasApplied > 150, $"{c.DeltasApplied}");
            }

            Assert.Equal(0, h.SelfNotFirst);
            Assert.True(h.MaxOthersErrorUnits <= 4, $"{h.MaxOthersErrorUnits}");
            Assert.Equal(0, h.DirectionErrors);
            _out.WriteLine($"8 игроков: байт/снимок — дельта {h.Server.DeltaBytes / (double)h.Server.DeltasSent:0.#}, " +
                           $"полный {(h.Server.SnapshotBytes - h.Server.DeltaBytes) / (double)h.Server.FullSent:0.#}; " +
                           $"максимальная дельта {h.Server.MaxDeltaBytes} байт, наибольший снимок {h.Server.MaxSnapshotBytes}");
        }

        // ---------- Старые пакеты, стоп при обрыве, база дельты (карточка 0-015) ----------

        /// <summary>
        /// Замечание 1 проверки 0-008: перестановка и дубли в обе стороны (имитатор). Сервер отбрасывает старый и
        /// повторный ввод, клиент — старые снимки; применённый кадр ввода на сервере (после приёма каждого такта) и такт
        /// принятого снимка у клиента (каждый принятый) только растут. Период 1 — одни полные снимки (фильтр
        /// <c>OnSnapshot</c>), 30 — в основном дельты (фильтр <c>OnDelta</c>).
        /// </summary>
        [Theory]
        [InlineData(1u)]
        [InlineData(30u)]
        public void D00_Net_Loopback_ReorderDuplicates_StaleDropped_FrameAndTickOnlyGrow(uint keyframePeriod)
        {
            var sim = new NetSimSettings
            {
                DelayMs = 40, JitterMs = 10, LossPpm = 10_000, DuplicatePpm = 50_000, ReorderPpm = 150_000, ReorderMs = 60, Seed = 15,
            };
            var world = FlatWorldGenerator.Instance.Generate(MapShape.World(1024), 42);
            using var h = new LoopbackHarness(world, 3, sim: sim, scripts: new[] { "wander", "wander", "idle" });
            h.Server.KeyframePeriod = keyframePeriod;
            h.Run(600);

            long stale = 0;
            foreach (NetBot b in h.Bots)
            {
                NetClient c = b.Client;
                Assert.Equal(NetClientState.InGame, c.State);
                Assert.True(c.SnapshotsApplied > 500, $"{c.SnapshotsApplied}");
                stale += c.SnapshotsStale;
            }

            _out.WriteLine($"период {keyframePeriod}: ввод отброшен {h.Server.InputsDropped} из {h.Server.InputsDropped + h.Server.InputsAccepted}, " +
                           $"старых снимков {stale}, назад: кадр {h.ServerFrameWentBack}, такт {h.SnapshotTickWentBack}; " +
                           $"дельт {h.Server.DeltasSent}, полных {h.Server.FullSent}");
            Assert.True(h.Server.InputsDropped > 100, $"отброшено ввода {h.Server.InputsDropped}");
            Assert.True(stale > 50, $"старых снимков {stale}");
            Assert.Equal(0, h.ServerFrameWentBack);
            Assert.Equal(0, h.SnapshotTickWentBack);
            Assert.True(h.MaxErrorUnits <= 4 && h.MaxOthersErrorUnits <= 4, $"{h.MaxErrorUnits} {h.MaxOthersErrorUnits}");
            Assert.Equal(0, h.DirectionErrors);
            Assert.Equal(0, h.Server.InputStops); // перестановка — не обрыв
        }

        /// <summary>
        /// 2.13 п. 6 (замечание 3 проверки 0-008): связь от клиента к серверу оборвана — персонаж стоит не позже чем через
        /// 0,2 с (6 тактов) после последнего пришедшего кадра; соединение держится. Связь вернулась — снова идёт.
        /// </summary>
        [Fact]
        public void D07_S2_13_6_LinkCut_CharacterStopsWithin6Ticks_ResumesAfter()
        {
            var world = FlatWorldGenerator.Instance.Generate(MapShape.World(1024), 42);
            var gates = new List<GateTransport>();
            using var h = new LoopbackHarness(world, 2, scripts: new[] { "wander", "wander" }, wrap: (i, t) =>
            {
                var g = new GateTransport(t);
                gates.Add(g);
                return g;
            });
            WorldPos PosOf(int entity) => h.Sim.Entities[h.Sim.SlotOf(entity)].Mover.Position;
            bool Moving(int entity) => h.Sim.Entities[h.Sim.SlotOf(entity)].Input.Direction.IsMoving;

            h.Run(30);
            int id = h.Bots[0].Client.EntityId;
            int waited = 0;
            while (!Moving(id) && waited++ < 300)
            {
                h.Run(1);
            }

            Assert.True(Moving(id), "бот не пошёл");

            // Два обрыва подряд: после возврата связи стоп снова срабатывает (признак «остановлен» снимается новым кадром).
            var report = new List<string>();
            for (int round = 0; round < 2; round++)
            {
                long stopsBefore = h.Server.InputStops;
                gates[0].DropOutgoingUnreliable = true;
                long cut = h.Sim.Tick; // последний кадр принят на приёме такта cut − 1 (перед шагом к cut)
                var pos = new List<WorldPos>();
                for (int k = 0; k < 60; k++)
                {
                    h.Run(1);
                    pos.Add(PosOf(id)); // позиция после такта cut + k + 1
                }

                // Первые такты ввод ещё держится (бежит), не позже 6 тактов после последнего кадра — стоит.
                Assert.NotEqual(pos[0], pos[3]);
                int stoppedFrom = pos.Count - 1;
                while (stoppedFrom > 0 && pos[stoppedFrom - 1] == pos[pos.Count - 1])
                {
                    stoppedFrom--;
                }

                // Конечную позицию персонаж занял на такте cut + stoppedFrom + 1 — ровно через 6 тактов (0,2 с, 2.13 п. 6)
                // после последнего кадра: не позже (норма документа) и не раньше (ранний стоп — ложные стопы при живой
                // связи). Порог — число документа, не константа сервера.
                long lastFrameTick = cut - 1;
                long stoppedTick = cut + stoppedFrom + 1;
                Assert.True(stoppedTick - lastFrameTick == 6, $"обрыв {round}: стоит с такта {stoppedTick}, последний кадр на такте {lastFrameTick}");
                Assert.Equal(stopsBefore + 1, h.Server.InputStops);
                Assert.False(Moving(id));
                Assert.Equal(2, h.Server.PlayersInGame);
                Assert.Equal(NetClientState.InGame, h.Bots[0].Client.State);

                // Связь вернулась: ввод снова применяется — бот идёт.
                gates[0].DropOutgoingUnreliable = false;
                WorldPos stoppedAt = PosOf(id);
                waited = 0;
                while (!(PosOf(id) != stoppedAt && Moving(id)) && waited++ < 300)
                {
                    h.Run(1);
                }

                Assert.NotEqual(stoppedAt, PosOf(id));
                Assert.True(Moving(id), $"обрыв {round}: после возврата связи бот не идёт");
                report.Add($"обрыв {round}: последний кадр на такте {lastFrameTick}, стоит с такта {stoppedTick} " +
                           $"({stoppedTick - lastFrameTick} такт(ов)); после возврата пошёл через {waited} такт(ов)");
            }

            _out.WriteLine(string.Join("\n", report));
        }

        /// <summary>
        /// 2.13 п. 6 на заглушке транспорта (кадр за кадром): стоп ровно через 6 тактов после последнего нового кадра,
        /// стоп — это и движение 0, и кнопки 0 (самоход 0-019 — уже без кнопок, 2.7); повтор того же кадра — не новый кадр (отброшен, стоп не отодвигает);
        /// до первого кадра после входа стопа нет; новый кадр снимает стоп.
        /// </summary>
        [Fact]
        public void D07_S2_13_6_Scripted_StopExactly6Ticks_ClearsButtons_RepeatIsNotNewFrame()
        {
            var world = FlatWorldGenerator.Instance.Generate(MapShape.World(1024), 1);
            var sim = new Simulation(world, maxEntities: 8);
            var t = new ScriptedTransport();
            var server = new NetServer(t, sim, n => ServerRun.SpawnPoint(world.Shape, n), 4, NetModule.Version);
            t.Connect(1);
            t.Push(1, NetChannel.Facts, Encode(b => new LoginMessage { Version = NetModule.Version, Name = "p" }.Write(b)));

            void Tick()
            {
                server.Poll(sim.Tick);
                sim.Step();
                server.SendSnapshots();
            }

            // До первого кадра — стопа нет (ввод и так пустой), счётчик не врёт.
            for (int k = 0; k < 20; k++)
            {
                Tick();
            }

            int id = server.EntityOf(1);
            Assert.True(id > 0);
            Assert.Equal(0, server.InputStops);
            InputFrame Input() => sim.Entities[sim.SlotOf(id)].Input;
            WorldPos Pos() => sim.Entities[sim.SlotOf(id)].Mover.Position;

            var held = new InputFrame(Input8.FromIndex(2), 0x0005); // идёт и держит кнопки
            uint frame = 0;
            byte[] Packet(uint f) => Encode(b => new InputMessage { FirstFrame = f, Frame3 = WireFrame.From(held) }.Write(b));
            for (int k = 0; k < 10; k++)
            {
                t.Push(1, NetChannel.Unreliable, Packet(++frame));
                Tick();
            }

            Assert.Equal(held, Input());
            long lastFrameTick = sim.Tick - 1; // последний новый кадр принят на приёме такта lastFrameTick
            byte[] repeat = Packet(frame);
            long accepted = server.InputsAccepted;
            long dropped = server.InputsDropped;

            // Дальше приходит только повтор последнего пакета (дубль) — новых кадров нет.
            var pos = new Dictionary<long, WorldPos> { [sim.Tick] = Pos() };
            for (int k = 0; k < 12; k++)
            {
                t.Push(1, NetChannel.Unreliable, repeat);
                Tick();
                pos[sim.Tick] = Pos();
                if (sim.Tick - lastFrameTick <= NetServer.SelfStepAfterTicks)
                {
                    Assert.True(Input() == held, $"такт {sim.Tick}: ввод сменился раньше самохода ({Input()})");
                }
                else if (sim.Tick - lastFrameTick <= 6)
                {
                    // 0-019: самоход 2.7 «Нет ввода» — последнее направление, кнопки отпущены.
                    Assert.True(Input() == new InputFrame(held.Direction), $"такт {sim.Tick}: самоход не тем вводом ({Input()})");
                }
                else
                {
                    Assert.True(Input() == InputFrame.None, $"такт {sim.Tick}: через {sim.Tick - lastFrameTick} тактов ввод {Input()}, а не пустой");
                }
            }

            Assert.Equal(0, Input().Buttons);
            Assert.NotEqual(pos[lastFrameTick + 5], pos[lastFrameTick + 6]); // идёт до 6-го такта
            Assert.Equal(pos[lastFrameTick + 6], pos[sim.Tick]);              // и стоит с него
            Assert.Equal(accepted, server.InputsAccepted);                   // повтор того же кадра не принят
            Assert.Equal(dropped + 12, server.InputsDropped);
            Assert.Equal(1, server.InputStops);

            // Новый кадр снимает стоп.
            t.Push(1, NetChannel.Unreliable, Packet(++frame));
            Tick();
            Assert.Equal(held, Input());
            Assert.Equal(1, server.InputStops);
        }

        /// <summary>
        /// M4 проверки 0-014: дельта к такту, которого нет в кольце баз клиента, — отброшена (<c>DeltasUnmerged</c>),
        /// <c>Latest</c> не меняется; клиент продолжает подтверждать последний применённый снимок. Два случая: ячейка
        /// кольца занята другим тактом (тот же такт mod 32) и ячейка пуста.
        /// </summary>
        [Fact]
        public void D00_Net_Client_DeltaWithoutBase_Unmerged_LatestKept()
        {
            var shape = MapShape.World(1024);
            var layout = new SnapshotLayout(shape);
            var t = new ScriptedTransport();
            var c = new NetClient(t, LoopbackTransport.ServerPeer, "c", NetModule.Version);
            var accept = new LoginAcceptMessage
            {
                EntityId = 5, ServerTick = 8, MapWidth = shape.Width, MapHeight = shape.Height, Wraps = shape.Wraps, WorldSeed = 1,
                TickRate = Simulation.TickRate,
            };
            t.Connect(LoopbackTransport.ServerPeer);
            t.Push(LoopbackTransport.ServerPeer, NetChannel.Facts, Encode(b => accept.Write(b)));
            WorldPos p10 = SnapshotLayout.Quantize(new WorldPos(100 * U, 200 * U));
            t.Push(LoopbackTransport.ServerPeer, NetChannel.Unreliable, Encode(b =>
            {
                var w = new SnapshotWriter(b, layout, 10, 20);
                Assert.True(w.TryAdd(5, SimEntity.KindCharacter, p10, 3));
                return w.Finish();
            }));
            c.Poll(0);
            Assert.Equal(NetClientState.InGame, c.State);
            Assert.Equal(10u, c.Latest.Tick);
            c.SendFrame(default);
            Assert.Equal(1, t.Flushes - c.PingsSent); // кадр ввода уходит сразу — один сброс отправки на кадр (0-015); пинг часов (0-025) — свой

            WorldPos moved = SnapshotLayout.Quantize(new WorldPos(101 * U, 200 * U));
            t.Push(LoopbackTransport.ServerPeer, NetChannel.Unreliable, Delta(layout, 50, 42, moved)); // ячейка 10 — такт 10, не 42
            t.Push(LoopbackTransport.ServerPeer, NetChannel.Unreliable, Delta(layout, 51, 20, moved)); // ячейка 20 — пуста
            c.Poll(1);
            Assert.Equal(2, c.DeltasUnmerged);
            Assert.Equal(0, c.DeltasApplied);
            Assert.Equal(10u, c.Latest.Tick);
            Assert.Equal(1, c.Latest.Count);
            Assert.Equal(p10, c.Latest.Entities[0].Position);
            Assert.Equal(3, c.Latest.Entities[0].Direction);

            // Дельта к настоящей базе склеивается.
            t.Push(LoopbackTransport.ServerPeer, NetChannel.Unreliable, Delta(layout, 52, 10, moved));
            c.Poll(2);
            Assert.Equal(1, c.DeltasApplied);
            Assert.Equal(52u, c.Latest.Tick);
            Assert.Equal(moved, c.Latest.Entities[0].Position);
            Assert.Equal(7, c.Latest.Entities[0].Direction);
        }

        private static byte[] Delta(SnapshotLayout layout, uint tick, uint baseline, WorldPos p) => Encode(b =>
        {
            var w = new SnapshotDeltaWriter(b, layout, tick, tick * 2, baseline);
            Assert.True(w.TryAddUpdated(0, DeltaFields.Position | DeltaFields.Direction, p, 7));
            return w.Finish();
        });

        /// <summary>
        /// Замечание 10 проверки 0-008: пакеты, застрявшие в очереди имитатора, после «закрыто» не уходят новому
        /// соединению с тем же номером (LiteNetLib номера переиспользует) — ни входящие, ни исходящие.
        /// </summary>
        [Fact]
        public void D00_Net_Sim_Disconnect_DropsQueuedPackets_ReusedPeerGetsNothingOld()
        {
            var inner = new ManualTransport();
            using var sim = new NetSimTransport(inner, new NetSimSettings { DelayMs = 50, Seed = 1 });
            var rec = new Recorder();
            static byte[] Packet(int seq)
            {
                var b = new byte[12];
                BitConverter.TryWriteBytes(b, seq);
                return b;
            }

            inner.Events.Add(h => h.OnConnected(1));
            sim.Poll(0, rec);
            inner.Events.Add(h => h.OnReceive(1, NetChannel.Unreliable, Packet(1)));
            inner.Events.Add(h => h.OnReceive(1, NetChannel.Facts, Packet(2)));
            sim.Poll(1_000, rec);
            sim.Send(1, NetChannel.Unreliable, Packet(3));
            sim.Send(1, NetChannel.Facts, Packet(4));
            sim.Send(2, NetChannel.Unreliable, Packet(5)); // чужое соединение — не трогается
            Assert.Equal(5, sim.PendingCount);

            // Старое соединение 1 закрылось, новое получило тот же номер — раньше срока доставки старых пакетов.
            inner.Events.Add(h => h.OnDisconnected(1, NetDisconnectReason.Timeout, ReadOnlySpan<byte>.Empty));
            inner.Events.Add(h => h.OnConnected(1));
            sim.Poll(10_000, rec);
            Assert.Equal(1, sim.PendingCount);
            for (long now = 20_000; now <= 200_000; now += 10_000)
            {
                sim.Poll(now, rec);
            }

            Assert.Empty(rec.Received);
            Assert.Equal(new[] { (2, NetChannel.Unreliable) }, inner.Sent);
            Assert.Equal(2, sim.ReceiveStats.Discarded);
            Assert.Equal(2, sim.SendStats.Discarded);

            // Своё «закрыть» тоже чистит очередь.
            sim.Send(1, NetChannel.Unreliable, Packet(6));
            sim.Disconnect(1, ReadOnlySpan<byte>.Empty);
            Assert.Equal(0, sim.PendingCount);
        }

        /// <summary>Транспорт-заглушка с ручными событиями: <see cref="Events"/> раздаются в ближайший <see cref="Poll"/>.</summary>
        private sealed class ManualTransport : ITransport
        {
            public delegate void Event(ITransportHandler handler);

            public readonly List<Event> Events = new List<Event>();
            public readonly List<(int Peer, NetChannel Channel)> Sent = new List<(int, NetChannel)>();
            public long Flushes;

            public void Poll(long nowMicros, ITransportHandler handler)
            {
                foreach (Event e in Events)
                {
                    e(handler);
                }

                Events.Clear();
            }

            public void Send(int peer, NetChannel channel, ReadOnlySpan<byte> data) => Sent.Add((peer, channel));

            public void Disconnect(int peer, ReadOnlySpan<byte> payload)
            {
            }

            public void Flush() => Flushes++;

            public int RoundTripMs(int peer) => 0;

            public void Dispose()
            {
            }
        }

        /// <summary>
        /// Сервер и боты на связи в памяти, виртуальное время: кадр 60 Гц у ботов, такт 30 Гц у сервера.
        /// Сверяет каждый принятый снимок со своей позицией на сервере в том же такте.
        /// </summary>
        private sealed class LoopbackHarness : IDisposable
        {
            private readonly LoopbackTransport _serverTransport;
            private readonly Dictionary<long, WorldPos> _history = new Dictionary<long, WorldPos>();
            private readonly Dictionary<long, byte> _dirHistory = new Dictionary<long, byte>();
            private readonly Dictionary<NetClient, uint> _lastApplied = new Dictionary<NetClient, uint>();
            private readonly Dictionary<int, uint> _lastFrame = new Dictionary<int, uint>();
            private long _frame;

            public LoopbackHarness(WorldModel world, int bots, int maxPlayers = 16, NetVersion? clientVersion = null, NetSimSettings? sim = null, string[]? scripts = null,
                                   Func<int, ITransport, ITransport>? wrap = null)
            {
                Sim = new Simulation(world, maxEntities: 64);
                _serverTransport = LoopbackTransport.CreateServer(maxPlayers + 8);
                Server = new NetServer(_serverTransport, Sim, n => ServerRun.SpawnPoint(world.Shape, n), maxPlayers, NetModule.Version);
                for (int i = 0; i < bots; i++)
                {
                    ITransport t = LoopbackTransport.ConnectTo(_serverTransport);
                    if (sim.HasValue)
                    {
                        NetSimSettings s = sim.Value;
                        s.Seed += (ulong)i;
                        t = new NetSimTransport(t, s);
                    }

                    if (wrap != null)
                    {
                        t = wrap(i, t);
                    }

                    var c = new NetClient(t, LoopbackTransport.ServerPeer, $"bot{i}", clientVersion ?? NetModule.Version);
                    c.SnapshotApplied = snap => Check(c, snap);
                    string script = scripts != null && scripts.Length > 0 ? scripts[i % scripts.Length] : "wander";
                    Bots.Add(new NetBot(c, script, i));
                }
            }

            public Simulation Sim { get; }
            public NetServer Server { get; }
            public List<NetBot> Bots { get; } = new List<NetBot>();
            public int CheckedByTick { get; private set; }
            public int MaxErrorUnits { get; private set; }

            /// <summary>
            /// Чужие сущности в снимке (0-014): наибольшая ошибка позиции против сервера в такт снимка — проверка, что
            /// склейка дельты даёт тот же полный снимок, что и полный кадр.
            /// </summary>
            public int MaxOthersErrorUnits { get; private set; }
            public long CheckedOthers { get; private set; }
            public long SelfNotFirst { get; private set; }
            public long MaxLagTicks { get; private set; }

            /// <summary>
            /// Направление в склеенном снимке ≠ направлению сущности на сервере в такт снимка (своя и чужие; 0-015 по
            /// проверке 0-014, M2). Сверок — <see cref="CheckedDirections"/>.
            /// </summary>
            public long DirectionErrors { get; private set; }
            public long CheckedDirections { get; private set; }

            /// <summary>Сколько раз такт принятого снимка у клиента не вырос (старый снимок стал <c>Latest</c>; 0-015).</summary>
            public long SnapshotTickWentBack { get; private set; }

            /// <summary>Сколько раз после приёма такта применённый кадр ввода на сервере уменьшился (0-015).</summary>
            public long ServerFrameWentBack { get; private set; }

            /// <summary><paramref name="ticks"/> тактов сервера (по два кадра ботов на такт).</summary>
            public void Run(int ticks)
            {
                for (int t = 0; t < ticks; t++)
                {
                    for (int f = 0; f < 2; f++, _frame++)
                    {
                        long now = _frame * FrameMicros;
                        foreach (NetBot b in Bots)
                        {
                            b.Frame(now);
                        }
                    }

                    Server.Poll(_frame * FrameMicros);
                    foreach (NetBot b in Bots)
                    {
                        if (Server.TryGetLastFrame(b.Client.EntityId, out uint applied))
                        {
                            if (_lastFrame.TryGetValue(b.Client.EntityId, out uint prev) && (int)(applied - prev) < 0)
                            {
                                ServerFrameWentBack++;
                            }

                            _lastFrame[b.Client.EntityId] = applied;
                        }
                    }

                    Sim.Step();
                    Server.SendSnapshots();
                    foreach (SimEntity e in Sim.Entities)
                    {
                        _history[Key(e.Id, Sim.Tick)] = e.Mover.Position;
                        _dirHistory[Key(e.Id, Sim.Tick)] = e.Input.DirectionCode;
                    }
                }
            }

            private void Check(NetClient c, Snapshot snap)
            {
                MaxLagTicks = Math.Max(MaxLagTicks, Sim.Tick - snap.Tick);
                if (_lastApplied.TryGetValue(c, out uint prevTick) && (int)(snap.Tick - prevTick) <= 0)
                {
                    SnapshotTickWentBack++;
                }

                _lastApplied[c] = snap.Tick;
                if (snap.Count == 0 || snap.Entities[0].Id != c.EntityId)
                {
                    SelfNotFirst++;
                }

                for (int i = 0; i < snap.Count; i++)
                {
                    if (_dirHistory.TryGetValue(Key(snap.Entities[i].Id, snap.Tick), out byte dir))
                    {
                        CheckedDirections++;
                        if (dir != snap.Entities[i].Direction)
                        {
                            DirectionErrors++;
                        }
                    }
                }

                for (int i = 1; i < snap.Count; i++)
                {
                    SnapshotEntity e = snap.Entities[i];
                    if (_history.TryGetValue(Key(e.Id, snap.Tick), out WorldPos p))
                    {
                        Int2 d = Torus.Delta(Sim.Shape, p, e.Position);
                        MaxOthersErrorUnits = Math.Max(MaxOthersErrorUnits, Math.Max(Math.Abs(d.X), Math.Abs(d.Y)));
                        CheckedOthers++;
                    }
                }
                if (snap.TryFind(c.EntityId, out SnapshotEntity mine) && _history.TryGetValue(Key(c.EntityId, snap.Tick), out WorldPos truth))
                {
                    Int2 d = Torus.Delta(Sim.Shape, truth, mine.Position);
                    MaxErrorUnits = Math.Max(MaxErrorUnits, Math.Max(Math.Abs(d.X), Math.Abs(d.Y)));
                    CheckedByTick++;
                }
            }

            private static long Key(int id, long tick) => ((long)id << 32) | (uint)tick;

            public void Dispose()
            {
                foreach (NetBot b in Bots)
                {
                    b.Client.Transport.Dispose();
                }

                _serverTransport.Dispose();
            }
        }

        // D00_Net_Server_SteadyState_NoAllocations (11.6) — в AllocationTests: непараллельная коллекция (0-020).

        /// <summary>Транспорт-заглушка: <see cref="Poll"/> раздаёт заранее положенные события, отправки считает и выбрасывает.</summary>
        internal sealed class ScriptedTransport : ITransport
        {
            private readonly List<(int Peer, NetChannel Channel, byte[]? Data)> _queue = new List<(int, NetChannel, byte[]?)>(64);

            public long Sent;

            public long Flushes;

            public void Connect(int peer) => _queue.Add((peer, NetChannel.Unreliable, null));

            public void Push(int peer, NetChannel channel, byte[] data) => _queue.Add((peer, channel, data));

            public void Poll(long nowMicros, ITransportHandler handler)
            {
                for (int i = 0; i < _queue.Count; i++)
                {
                    (int peer, NetChannel ch, byte[]? data) = _queue[i];
                    if (data == null)
                    {
                        handler.OnConnected(peer);
                    }
                    else
                    {
                        handler.OnReceive(peer, ch, data);
                    }
                }

                _queue.Clear();
            }

            public void Send(int peer, NetChannel channel, ReadOnlySpan<byte> data) => Sent++;

            public void Disconnect(int peer, ReadOnlySpan<byte> payload)
            {
            }

            public void Flush() => Flushes++;

            public int RoundTripMs(int peer) => 0;

            public void Dispose()
            {
            }
        }

        // ---------- По UDP (127.0.0.1) ----------

        [Fact]
        public void D00_Net_Udp_BotsLogIn_Snapshots30Hz_VersionMismatchRejected()
        {
            UdpRun run = RunUdp(bots: 2, seconds: 3, rttMs: 0, lossPercent: 0, extra: o2 => { });
            BotReport r = run.Report;
            Assert.Equal(2, r.InGame);
            Assert.Equal(0, r.Disconnects);
            foreach (BotStats b in r.Bots)
            {
                _out.WriteLine($"бот {b.Index}: #{b.EntityId}, {b.SnapshotsPerSecond:F1} снимков/с, пропущено {b.SnapshotsMissed}, RTT {b.RttMs} мс");
                Assert.InRange(b.SnapshotsPerSecond, 27, 33);
            }

            Assert.True(run.MaxErrorUnits <= 4, $"{run.MaxErrorUnits}");

            // Отказ входа при несовпадении версии — по UDP.
            UdpRun bad = RunUdp(bots: 1, seconds: 2, rttMs: 0, lossPercent: 0, extra: o2 => o2.Version = new NetVersion(9, 9, 9));
            Assert.Equal(1, bad.Report.Rejected);
            Assert.Equal($"Версии не совпадают: у сервера {NetModule.Version}, у тебя 9.9.9", bad.Report.Bots[0].RejectText);
            Assert.Equal(1, bad.Server.LoginsRejected);
        }

        /// <summary>
        /// Карточка 0-008, приёмка: 2 бота × 60 с, RTT 100 мс, потеря 1 % по UDP 127.0.0.1 — 0 отключений;
        /// позиция в снимке против сервера в том же такте ≤ 0,5 тайла. Ночной прогон: <c>tools\ci.cmd -All -TimeLimitSec 0</c>.
        /// </summary>
        [Fact]
        [Trait("Category", "Big")]
        public void D00_Net_Udp_Big_TwoBots60s_Rtt100_Loss1_NoDisconnects()
        {
            UdpRun run = RunUdp(bots: 2, seconds: 60, rttMs: 100, lossPercent: 1, extra: o2 => { });
            BotReport r = run.Report;
            foreach (BotStats b in r.Bots)
            {
                _out.WriteLine($"бот {b.Index}: #{b.EntityId}, {b.SecondsInGame:F1} с в игре, {b.SnapshotsPerSecond:F1} снимков/с, пропущено {b.SnapshotsMissed}, " +
                               $"старых {b.SnapshotsStale}, RTT {b.RttMs} мс, имитатор: потеряно {b.SimSendLost}/{b.SimSent} и {b.SimReceiveLost}/{b.SimReceived}, задержка {b.SimMeanDelayMs:F1} мс");
            }

            _out.WriteLine($"сверка по такту: {run.Checked} снимков, ошибка ≤ {run.MaxErrorUnits} unit; отставание снимка от такта сервера при приёме: " +
                           $"≤ {run.MaxLagTicks} тактов ({run.MaxLagTicks * 0.2:F1} тайла при 6 т/с), среднее {run.MeanLagTicks:F2}; " +
                           $"больше 2 тактов — {run.LagOver2} из {run.Snapshots}");
            Assert.Equal(2, r.InGame);
            Assert.Equal(0, r.Disconnects);
            Assert.True(run.Checked > 2 * 60 * 25, $"{run.Checked}");
            Assert.True(run.MaxErrorUnits <= U / 2, $"{run.MaxErrorUnits}");

            // 2.13 п. 1 (замечание 2 проверки 0-008): отставание снимка от такта сервера при приёме ≤ 2 тактов (0,4 тайла
            // при 6 т/с) у всех снимков. Задержка 50 мс в одну сторону — 1,5 такта; отправка сразу после такта
            // (ITransport.Flush, 0-015) — без неё LiteNetLib держал снимок до UpdateTime (15 мс) и 3 такта были у 1–5 %.
            // Метрики «предсказание клиента против сервера в тот же момент» (порог 0,5 тайла) в ядре пока нет — предсказание
            // 0-013 живёт в Unity; см. work/backlog.md, после 0-019.
            Assert.True(run.LagOver2 == 0 && run.MaxLagTicks <= 2,
                        $"отставание > 2 тактов у {run.LagOver2} из {run.Snapshots} снимков, максимум {run.MaxLagTicks}");
        }

        private sealed class UdpRun
        {
            public BotReport Report = null!;
            public ServerResult Server = null!;
            public int Checked;
            public int MaxErrorUnits;
            public long MaxLagTicks;
            public double MeanLagTicks;

            /// <summary>Принятых снимков всего / с отставанием больше 2 тактов (0-015, 2.13 п. 1).</summary>
            public long Snapshots;
            public long LagOver2;
        }

        private UdpRun RunUdp(int bots, double seconds, int rttMs, double lossPercent, Action<BotOptions> extra)
        {
            int port = FreeUdpPort();
            ServerOptions so = ServerOptions.Parse(new[] { "--create", "--seed", "42", "--size", "1024", "--port", port.ToString() });
            var history = new ConcurrentDictionary<long, WorldPos>();
            long serverTick = 0;
            so.AfterTick = sim =>
            {
                foreach (SimEntity e in sim.Entities)
                {
                    history[((long)e.Id << 32) | (uint)sim.Tick] = e.Mover.Position;
                }

                Volatile.Write(ref serverTick, sim.Tick);
            };

            using var stopServer = new CancellationTokenSource();
            Task<ServerResult> server = Task.Run(() => ServerRun.Run(so, null, stopServer.Token));

            var run = new UdpRun();
            long lagSum = 0;
            var shape = MapShape.World(1024);
            var bo = BotOptions.Parse(new[]
            {
                "--connect", $"127.0.0.1:{port}", "--count", bots.ToString(), "--script", "wander",
                "--rtt", rttMs.ToString(), "--loss", lossPercent.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--seconds", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), "--seed", "7",
            });
            bo.OnSnapshot = (index, snap, entityId) =>
            {
                long lag = Volatile.Read(ref serverTick) - snap.Tick;
                lagSum += lag;
                run.Snapshots++;
                if (lag > 2)
                {
                    run.LagOver2++;
                }

                run.MaxLagTicks = Math.Max(run.MaxLagTicks, lag);
                if (snap.TryFind(entityId, out SnapshotEntity mine) && history.TryGetValue(((long)entityId << 32) | snap.Tick, out WorldPos truth))
                {
                    Int2 d = Torus.Delta(shape, truth, mine.Position);
                    run.MaxErrorUnits = Math.Max(run.MaxErrorUnits, Math.Max(Math.Abs(d.X), Math.Abs(d.Y)));
                    run.Checked++;
                }
            };
            extra(bo);
            try
            {
                run.Report = BotRun.Run(bo, null);
            }
            finally
            {
                Thread.Sleep(300); // отключения ботов доходят до сервера
                stopServer.Cancel();
                run.Server = server.GetAwaiter().GetResult();
            }

            run.MeanLagTicks = run.Checked > 0 ? lagSum / (double)run.Checked : 0;
            Assert.Equal(0, run.Server.ExitCode);
            return run;
        }

        private static int FreeUdpPort()
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            return ((IPEndPoint)s.LocalEndPoint!).Port;
        }

        // ---------- Вспомогательное ----------

        internal delegate int Writer(Span<byte> dest);

        internal static byte[] Encode(Writer write)
        {
            byte[] buf = new byte[NetSettings.MaxUnreliablePayload];
            int n = write(buf);
            return buf.AsSpan(0, n).ToArray();
        }

        private static byte[] Append(byte[] a, byte b)
        {
            byte[] r = new byte[a.Length + 1];
            a.CopyTo(r, 0);
            r[a.Length] = b;
            return r;
        }
    }
}
