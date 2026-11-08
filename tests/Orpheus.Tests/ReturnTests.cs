using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net;
using Orpheus.Net.Hosting;
using Orpheus.Net.Protocol;
using Orpheus.Net.Session;
using Orpheus.Net.Transport;
using Orpheus.Save;
using Orpheus.Server;
using Orpheus.WorldGen;
using Xunit;
using Xunit.Abstractions;

namespace Orpheus.Tests
{
    /// <summary>
    /// Вернувшийся игрок — тот же персонаж на том же месте (карточка 0-035; `BRIEF.md` п. 11b; docs/07 6.2, 7.3–7.5):
    /// хозяин — по токену, остальные — по имени; уход и возвращение, вытеснение упавшей игры, перезапуск сервера между,
    /// «имя занято», занятое место, формат сохранения 3 и чтение настоящего файла формата 2.
    /// </summary>
    public class ReturnTests
    {
        private const long TickMicros = 1_000_000 / Simulation.TickRate;
        private static readonly HostToken Token = new HostToken(0x1111222233334444UL, 0x5555666677778888UL);
        private static readonly HostToken Token2 = new HostToken(0x9999aaaabbbbccccUL, 0xddddeeeeffff0001UL);

        private readonly ITestOutputHelper _out;

        public ReturnTests(ITestOutputHelper output) => _out = output;

        // ---------- Петля в памяти ----------

        [Fact]
        public void D07_S7_4_Loopback_PlayerLeavesAndReturns_SamePlace_SameDodge()
        {
            using var h = new Harness();
            NetClient friend = h.Connect("Эвридика", default);
            h.Run(3, friend, InputFrame.None);
            Assert.Equal(NetClientState.InGame, friend.State);
            int first = friend.EntityId;
            WorldPos spawn = h.PositionOf(first);

            // Идёт на восток и уворачивается в последнем кадре: на миг ухода у неё рывок и откат.
            h.Run(40, friend, new InputFrame(Input8.East));
            h.Run(1, friend, new InputFrame(Input8.South, InputButtons.Dodge));
            Mover atLeave = h.MoverOf(first);
            Assert.NotEqual(spawn, atLeave.Position);
            Assert.True(atLeave.Dodge.DashLeft > 0 || atLeave.Dodge.Cooldown > 0, "уворота нет — тест ничего не проверяет");

            friend.Disconnect();
            h.Run(3);
            Assert.Equal(0, h.Sim.EntityCount);
            Assert.Equal(1, h.Server.Characters.Count);
            CharacterRecord parked = h.Server.Characters[0];
            Assert.Equal(0, parked.EntityId);
            Assert.Equal(atLeave.Position, parked.Mover.Position);
            Assert.Equal(atLeave.Carry, parked.Mover.Carry);
            Assert.Equal(atLeave.Dodge, parked.Mover.Dodge);
            Assert.Equal(atLeave.UnitsPerTickX10, parked.Mover.UnitsPerTickX10);

            // Другой игрок пришёл и занял точку появления — вернувшаяся всё равно встаёт на своё место.
            NetClient other = h.Connect("Орфей", default);
            h.Run(3);
            NetClient again = h.Connect(" эвридика ", default); // то же имя: регистр и пробелы по краям не в счёт
            h.Run(1);
            Assert.Equal(NetClientState.InGame, again.State);
            Assert.NotEqual(first, again.EntityId); // номер сущности новый, персонаж — тот же
            Assert.Equal(atLeave.Position, h.PositionOf(again.EntityId));
            Assert.Equal(2, h.Server.Characters.Count);
            Assert.Equal(1, h.Server.CharactersReturned);
            Assert.Equal(0, h.Server.CharactersMoved);
            Assert.Equal(NetClientState.InGame, other.State);
        }

        [Fact]
        public void D07_S7_4_Loopback_HostReturns_ReplacedOrAfterLeave_SamePlace()
        {
            using var h = new Harness();
            NetClient host = h.Connect("хост", Token);
            NetClient friend = h.Connect("друг", default);
            h.Run(3);
            h.Run(30, host, new InputFrame(Input8.NorthEast));
            WorldPos before = h.PositionOf(host.EntityId);

            // Игра хоста упала, соединение висит до тайм-аута — вернувшаяся игра вытесняет его и встаёт там же.
            NetClient again = h.Connect("хост", Token);
            h.Run(2);
            Assert.Equal(NetClientState.InGame, again.State);
            Assert.Equal(ClosingReason.Replaced, host.Closing);
            Assert.Equal(before, h.PositionOf(again.EntityId));
            Assert.Equal(2, h.Sim.EntityCount);

            // Хозяин ушёл совсем, потом вошёл с другим именем — персонаж хозяина узнаётся по токену, не по имени.
            h.Run(20, again, new InputFrame(Input8.West));
            WorldPos atLeave = h.PositionOf(again.EntityId);
            again.Disconnect();
            h.Run(5);
            Assert.Equal(1, h.Sim.EntityCount);
            NetClient back = h.Connect("Хозяин мира", Token);
            h.Run(1);
            Assert.Equal(NetClientState.InGame, back.State);
            Assert.Equal(atLeave, h.PositionOf(back.EntityId));
            Assert.Equal(2, h.Server.Characters.Count);
            Assert.Equal("Хозяин мира", h.Server.Characters[h.Server.Characters.FindHost()].Name);
            Assert.Equal(NetClientState.InGame, friend.State);
        }

        [Fact]
        public void D07_S7_3_Loopback_SameNameWhileOwnerInGame_NameTaken()
        {
            using var h = new Harness();
            NetClient host = h.Connect("Орфей", Token);
            NetClient owner = h.Connect("Эвридика", default);
            h.Run(3);

            NetClient twin = h.Connect("ЭВРИДИКА", default);
            NetClient hostName = h.Connect("орфей", default); // имя персонажа хозяина — тоже занято
            h.Run(3);
            Assert.Equal(NetClientState.Rejected, twin.State);
            Assert.Equal(LoginRejectReason.NameTaken, twin.Reject!.Value.Reason);
            Assert.Equal("Имя занято: в этом мире так зовут другого героя", twin.RejectText);
            Assert.Equal(LoginRejectReason.NameTaken, hostName.Reject!.Value.Reason);
            Assert.Equal(NetClientState.InGame, owner.State);
            Assert.Equal(2, h.Server.PlayersInGame);

            // Хозяин вышел — его имя всё равно его (персонаж мира), другому не даётся.
            host.Disconnect();
            h.Run(3);
            NetClient hostName2 = h.Connect("Орфей", default);
            h.Run(3);
            Assert.Equal(LoginRejectReason.NameTaken, hostName2.Reject!.Value.Reason);

            // Владелец вышел — вход с его именем и есть его возвращение.
            owner.Disconnect();
            h.Run(3);
            NetClient back = h.Connect("эвридика", default);
            h.Run(2);
            Assert.Equal(NetClientState.InGame, back.State);
            Assert.Equal(2, h.Server.Characters.Count);

            // Пустое имя из пробелов — «имя не подходит» (имя — ключ персонажа).
            NetClient blank = h.Connect("   ", default);
            h.Run(3);
            Assert.Equal(LoginRejectReason.BadName, blank.Reject!.Value.Reason);
        }

        /// <summary>
        /// Хозяин не берёт имя чужого персонажа (0-037, docs/07 7.3): ни игрока в мире, ни ушедшего — «имя занято»; отказ
        /// не вытесняет его прежнее соединение. Своё имя и новое свободное — можно. Новый персонаж — повод сохранить мир,
        /// вернувшийся — нет.
        /// </summary>
        [Fact]
        public void D07_S7_3_Loopback_HostTakesPlayerName_NameTaken_OldHostStays()
        {
            using var h = new Harness();
            var created = new List<string>();
            h.Server.CharacterCreated = name => created.Add(name);
            NetClient host = h.Connect("Орфей", Token);
            NetClient friend = h.Connect("Эвридика", default);
            h.Run(3);
            Assert.Equal(new[] { "Орфей", "Эвридика" }, created);

            NetClient taker = h.Connect("ЭВРИДИКА", Token);
            h.Run(3);
            Assert.Equal(NetClientState.Rejected, taker.State);
            Assert.Equal(LoginRejectReason.NameTaken, taker.Reject!.Value.Reason);
            Assert.Equal(NetClientState.InGame, host.State); // прежнее соединение хозяина не выбито
            Assert.Null(host.Closing);

            // Игрок ушёл — имя всё равно его.
            friend.Disconnect();
            h.Run(3);
            NetClient taker2 = h.Connect("эвридика", Token);
            h.Run(3);
            Assert.Equal(LoginRejectReason.NameTaken, taker2.Reject!.Value.Reason);
            Assert.Equal(NetClientState.InGame, host.State);

            // Своё имя и новое свободное — вход (вытесняет прежнее соединение), персонаж тот же — не повод.
            NetClient renamed = h.Connect("Орфей Фракийский", Token);
            h.Run(2);
            Assert.Equal(NetClientState.InGame, renamed.State);
            Assert.Equal(ClosingReason.Replaced, host.Closing);
            Assert.Equal("Орфей Фракийский", h.Server.Characters[h.Server.Characters.FindHost()].Name);
            NetClient back = h.Connect("Эвридика", default);
            h.Run(2);
            Assert.Equal(NetClientState.InGame, back.State);
            Assert.Equal(2, h.Server.Characters.Count);
            Assert.Equal(new[] { "Орфей", "Эвридика" }, created);
        }

        /// <summary>
        /// З. 4 проверки 0-037: отказ хозяину по имени (пустое из пробелов) не вытесняет его прежнее соединение — игра
        /// хоста остаётся в мире. «Сервер полон» хозяину не грозит, пока в мире его прежнее соединение: оно и освободит место.
        /// </summary>
        [Fact]
        public void D07_S7_7_Loopback_HostBadName_Rejected_OldHostStays_FullServerStillReplaces()
        {
            using var h = new Harness();
            NetClient host = h.Connect("Орфей", Token);
            h.Run(3);
            Assert.Equal(NetClientState.InGame, host.State);

            foreach (string bad in new[] { "   ", "\t" }) // пустое из пробелов; длинное клиент и не отправит
            {
                NetClient again = h.Connect(bad, Token);
                h.Run(3);
                Assert.Equal(NetClientState.Rejected, again.State);
                Assert.Equal(LoginRejectReason.BadName, again.Reject!.Value.Reason);
                Assert.Equal(NetClientState.InGame, host.State); // прежнее соединение хозяина не выбито
                Assert.Null(host.Closing);
            }

            // Мир полон (хозяин + 7): вход хозяина заново — вытесняет прежнее соединение, а не «сервер полон».
            var others = new List<NetClient>();
            for (int i = 0; i < 7; i++)
            {
                others.Add(h.Connect("игрок" + i, default));
            }

            h.Run(3);
            Assert.All(others, c => Assert.Equal(NetClientState.InGame, c.State));
            Assert.Equal(8, h.Server.PlayersInGame);
            NetClient extra = h.Connect("лишний", default);
            h.Run(3);
            Assert.Equal(LoginRejectReason.ServerFull, extra.Reject!.Value.Reason);
            NetClient back = h.Connect("Орфей", Token);
            h.Run(3);
            Assert.Equal(NetClientState.InGame, back.State);
            Assert.Equal(ClosingReason.Replaced, host.Closing);
            Assert.Equal(8, h.Server.PlayersInGame);
        }

        [Fact]
        public void D07_S7_4_Loopback_PlaceBlocked_NearestFreeTile_ThenSpawn()
        {
            using var h = new Harness();
            NetClient p = h.Connect("строитель", default);
            h.Run(3);
            h.Run(100, p, new InputFrame(Input8.North)); // 20 тайлов от точек появления: застройка их не накроет
            WorldPos atLeave = h.PositionOf(p.EntityId);
            p.Disconnect();
            h.Run(3);

            // Пока его нет, место выхода застроили: тайл под ним и все соседи — стена.
            MapShape shape = h.Sim.Shape;
            TileCoord t0 = Torus.TileOf(shape, atLeave);
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    h.Sim.World.SetBaseTile(Torus.Wrap(shape, new TileCoord(t0.X + dx, t0.Y + dy)), FloorId.Grass, ObjectId.StoneWall);
                }
            }

            Assert.True(Collision.TouchesSolid(shape, atLeave, h.Sim.Solid));
            NetClient back = h.Connect("строитель", default);
            h.Run(1);
            WorldPos placed = h.PositionOf(back.EntityId);
            Assert.False(Collision.TouchesSolid(shape, placed, h.Sim.Solid), "поставлен в стену");
            TileCoord tp = Torus.TileOf(shape, placed);
            Int2 d = Torus.Delta(shape, t0, tp);
            Assert.Equal(2, Math.Max(Math.Abs(d.X), Math.Abs(d.Y))); // ближайшее кольцо, где свободно
            Assert.Equal(1, h.Server.CharactersMoved);

            // Вокруг всё занято на 8 тайлов — к точке появления.
            back.Disconnect();
            h.Run(3);
            for (int dy = -NetServer.PlaceSearchTiles - 2; dy <= NetServer.PlaceSearchTiles + 2; dy++)
            {
                for (int dx = -NetServer.PlaceSearchTiles - 2; dx <= NetServer.PlaceSearchTiles + 2; dx++)
                {
                    h.Sim.World.SetBaseTile(Torus.Wrap(shape, new TileCoord(tp.X + dx, tp.Y + dy)), FloorId.Grass, ObjectId.StoneWall);
                }
            }

            NetClient third = h.Connect("строитель", default);
            h.Run(1);
            Assert.Equal(NetClientState.InGame, third.State);
            Assert.Equal(ServerRun.SpawnPoint(shape, 1), h.PositionOf(third.EntityId)); // № 0 занят первым входом
            Assert.Equal(2, h.Server.CharactersMoved);
        }

        // ---------- Формат сохранения 3 ----------

        [Fact]
        public void D07_S6_2_Save_Format3_Characters_RoundTrip_InWorldParkedOnLoad()
        {
            SaveFixtures.LiveWorld live = SaveFixtures.Create(1024, 77, bots: 2);
            live.Run(ticks: 30, editsPerTick: 1);
            Simulation sim = live.Sim;
            var roster = new CharacterRoster();
            int inWorld = sim.AddEntity(ServerRun.SpawnPoint(sim.Shape, 5));
            roster.AddInWorld(false, "живой", inWorld);
            int hostId = sim.AddEntity(ServerRun.SpawnPoint(sim.Shape, 6));
            roster.AddInWorld(true, "хост", hostId);
            for (int i = 0; i < 7; i++)
            {
                sim.SetInputAt(sim.SlotOf(hostId), new InputFrame(Input8.SouthWest));
                sim.Step();
                sim.RefillPools();
            }

            Mover hostMover = sim.Entities[sim.SlotOf(hostId)].Mover;
            Assert.True(roster.Park(1, sim)); // хозяин вышел до сохранения
            Mover live1 = sim.Entities[sim.SlotOf(inWorld)].Mover;
            ulong hashAtSave = StateHash.Compute(sim).Total;

            byte[] bytes = WorldSave.Build(WorldSave.Capture(sim, live.BaseSha, "мир", roster.Capture()), 9, 0);
            SaveFile f = SaveContainer.Read(bytes);
            Assert.Equal(3u, f.Header.FormatVersion);
            Assert.True(f.HasSection(SaveSections.Characters));

            WorldModel world = WorldSnapshot.LoadBase(live.BaseBytes);
            LoadedWorldState state = WorldSave.Load(f, world, live.BaseSha);
            Assert.Equal(3u, state.FormatVersion);
            Assert.Equal(2, state.Characters.Length);
            Assert.Equal("живой", state.Characters[0].Name);
            Assert.Equal(inWorld, state.Characters[0].EntityId);
            Assert.True(state.Characters[1].IsHost);
            Assert.Equal(0, state.Characters[1].EntityId);
            Assert.Equal(hostMover.Position, state.Characters[1].Mover.Position);
            Assert.Equal(hostMover.Carry, state.Characters[1].Mover.Carry);
            Assert.Equal(hostMover.Dodge, state.Characters[1].Mover.Dodge);

            var loaded = new Simulation(world, maxEntities: 64);
            state.RestoreInto(loaded);
            Assert.Equal(hashAtSave, StateHash.Compute(loaded).Total); // такт сохранения — тот же
            int entities = loaded.EntityCount;

            // Сервер после загрузки: бывший в мире — теперь вне мира, его движение — за ключом, сущности нет.
            CharacterRoster restored = CharacterRoster.Restore(state.Characters, loaded);
            Assert.Equal(entities - 1, loaded.EntityCount);
            Assert.Equal(-1, loaded.SlotOf(inWorld));
            Assert.Equal(0, restored.InWorld);
            Assert.Equal(live1.Position, restored[0].Mover.Position);
            Assert.Equal(live1.Carry, restored[0].Mover.Carry);
            Assert.Equal(0, restored.FindPlayer("ЖИВОЙ"));
            Assert.Equal(1, restored.FindHost());
        }

        [Fact]
        public void D07_S6_7_Save_Format3_BrokenCharacters_Refused()
        {
            SaveFixtures.LiveWorld live = SaveFixtures.Create(1024, 78, bots: 1);
            Simulation sim = live.Sim;
            int id = sim.Entities[0].Id;

            void Refused(string why, params CharacterRecord[] chars)
            {
                byte[] bytes = WorldSave.Build(WorldSave.Capture(sim, live.BaseSha, "мир", chars), 2, 0);
                var e = Assert.Throws<InvalidDataException>(() => WorldSave.Load(SaveContainer.Read(bytes), WorldSnapshot.LoadBase(live.BaseBytes), live.BaseSha));
                _out.WriteLine($"{why}: {e.Message}");
            }

            var at = new Mover(ServerRun.SpawnPoint(sim.Shape, 3));
            Refused("два хозяина", new CharacterRecord { IsHost = true, Name = "а", Mover = at }, new CharacterRecord { IsHost = true, Name = "б", Mover = at });
            Refused("одно имя", new CharacterRecord { Name = "Аид", Mover = at }, new CharacterRecord { Name = "аид", Mover = at });
            Refused("нет сущности", new CharacterRecord { Name = "а", EntityId = id + 100 });
            Refused("одна сущность", new CharacterRecord { Name = "а", EntityId = id }, new CharacterRecord { Name = "б", EntityId = id });
            Refused("вне карты", new CharacterRecord { Name = "а", Mover = new Mover(new WorldPos(sim.Shape.WidthUnits, 0)) });
        }

        /// <summary>
        /// Настоящий файл формата 2 (записан сборкой <c>25e7c2b</c> = main до 0-035: сервер 1024², зерно 5, 2 бота и 3 игрока
        /// по сети в мире на миг остановки) — читается: персонажей нет (в формате 2 они не записаны), сущности — как были.
        /// Пересохранение даёт формат 3 с тем же хэшем такта.
        /// </summary>
        [Fact]
        public void D07_S6_7_Save_Format2_RealFile_Loads_ResavedAsFormat3()
        {
            byte[] v2 = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Golden", "format2_players.save"));
            WorldModel generated = WorldGenModule.Current.Generate(MapShape.World(1024), 5);
            byte[] baseBytes = WorldSnapshot.WriteBase(generated);
            SaveFile f = SaveContainer.Read(v2);
            Assert.True(f.IsOk, f.Error);
            Assert.Equal(2u, f.Header.FormatVersion);
            Assert.False(f.HasSection(SaveSections.Characters));

            WorldModel world = WorldSnapshot.LoadBase(baseBytes);
            LoadedWorldState state = WorldSave.Load(f, world, WorldSnapshot.StoredHash(baseBytes));
            Assert.Equal(2u, state.FormatVersion);
            Assert.Empty(state.Characters);
            Assert.Equal(5, state.Entities.Length);
            Assert.Equal(450, state.Tick);
            var sim = new Simulation(world, maxEntities: 64);
            state.RestoreInto(sim);
            ulong hash = StateHash.Compute(sim).Total;
            _out.WriteLine($"формат 2: такт {state.Tick}, сущностей {state.Entities.Length}, хэш {hash:x16}");

            // Сервер на таком мире: персонажей нет — ничего не убирается, боты берут сущности по порядку, как раньше.
            CharacterRoster roster = CharacterRoster.Restore(state.Characters, sim);
            Assert.Equal(0, roster.Count);
            Assert.Equal(5, sim.EntityCount);

            byte[] v3 = WorldSave.Build(WorldSave.Capture(sim, WorldSnapshot.StoredHash(baseBytes), state.Name, roster.Capture()), 2, 0);
            Assert.Equal(3u, SaveContainer.Read(v3).Header.FormatVersion);
            Assert.Equal(hash, StateHash.Compute(SaveFixtures.Load(baseBytes, v3)).Total);
        }

        // ---------- Сервер целиком: перезапуск между уходом и возвращением ----------

        /// <summary>
        /// Слово Автора (п. 11b): хост, вернувшийся после падения, — тем же персонажем на том же месте. Игра хоста ушла,
        /// сервер подождал (1 с вместо 60), сохранил мир и вышел; игра запустила сервер заново — с новым токеном —
        /// и хозяин встал там же. Друг, вышедший раньше, — тоже там же; друг, бывший в мире на миг остановки, — тоже.
        /// </summary>
        [Fact]
        public void D07_S7_4_ServerRun_HostCrashed_ServerRestarted_HostAndFriendsSamePlace()
        {
            string dir = Path.Combine(Path.GetTempPath(), "orpheus-return-" + Guid.NewGuid().ToString("N"));
            var clients = new List<NetClient>();
            try
            {
                WorldPos hostAt, leftAt, stayedAt;
                using (var srv = new InProcServer(dir, Token, create: true, "--host-grace", "1"))
                {
                    int port = srv.WaitReady(20_000);
                    NetClient host = Udp(port, "хост", Token, clients);
                    NetClient left = Udp(port, "ушедший", default, clients);
                    NetClient stayed = Udp(port, "оставшийся", default, clients);
                    Assert.True(Pump(() => host.State == NetClientState.InGame && left.State == NetClientState.InGame && stayed.State == NetClientState.InGame, 10_000, clients.ToArray()),
                        $"не вошли: {host.State} {left.State} {stayed.State}");

                    Walk(host, new InputFrame(Input8.East), 700, clients);
                    Walk(left, new InputFrame(Input8.North), 500, clients);
                    Walk(stayed, new InputFrame(Input8.SouthWest), 600, clients);
                    leftAt = Settled(left, clients);
                    left.Disconnect();
                    Assert.True(SpinWait.SpinUntil(() => SavedLine(srv.Text, "игрок вышел"), 5_000), "уход игрока не сохранён");

                    hostAt = Settled(host, clients);
                    stayedAt = Settled(stayed, clients);

                    // Игра хоста ушла (падение замечается по тайм-ауту транспорта 10 с — тот же путь, только позже; здесь
                    // отключение, чтобы тест шёл секунды): сервер ждёт 1 с, сохраняет и выходит.
                    host.Disconnect();
                    var keep = new List<NetClient> { stayed };
                    Assert.True(Pump(() => srv.Exited, 30_000, keep.ToArray()), "сервер не вышел без хозяина");
                    ServerResult r = srv.Stop(5_000);
                    Assert.True(r.HostLeft);
                    Assert.True(r.SavesWritten >= 2);
                }

                foreach (NetClient c in clients)
                {
                    c.Transport.Dispose();
                }

                clients.Clear();
                _out.WriteLine($"хост {hostAt}, ушедший {leftAt}, оставшийся {stayedAt}");

                // Игра хоста перезапускает сервер: тот же мир, новый токен.
                using (var srv = new InProcServer(dir, Token2, create: false))
                {
                    int port = srv.WaitReady(20_000);
                    Assert.Contains("\"msg\":\"персонажи мира\"", srv.Text);
                    NetClient host = Udp(port, "хост снова", Token2, clients);
                    NetClient left = Udp(port, "Ушедший", default, clients);
                    NetClient stayed = Udp(port, "оставшийся", default, clients);
                    NetClient fresh = Udp(port, "новенький", default, clients);
                    Assert.True(Pump(() => host.HasSnapshot && left.HasSnapshot && stayed.HasSnapshot && fresh.HasSnapshot && host.Latest.Self.Present, 10_000, clients.ToArray()),
                        $"не вошли: {host.State} {left.State} {stayed.State} {fresh.State}");
                    Assert.Equal(hostAt, Settled(host, clients));
                    Assert.Equal(leftAt, Settled(left, clients));
                    Assert.Equal(stayedAt, Settled(stayed, clients));
                    Assert.Equal(ServerRun.SpawnPoint(MapShape.World(1024), 0), Settled(fresh, clients)); // новый — на точку появления
                    ServerResult r = srv.Stop(30_000);
                    Assert.Equal(0, r.ExitCode);
                }
            }
            finally
            {
                foreach (NetClient c in clients)
                {
                    c.Transport.Dispose();
                }

                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                }
            }
        }

        // ---------- Сервер в потоке (0-031): тот же персонаж там же ----------

        /// <summary>
        /// Режим «сервер в потоке» (0-031 + 0-035): <see cref="ServerHost"/> в своём потоке, клиент по локальной связи,
        /// сохранения — через хранилище платформы (в памяти). Вышел — уход сохранён («игрок вышел»); вошёл снова — то же
        /// место; сервер остановлен и запущен заново из сохранения — персонажи мира восстановлены, снова то же место.
        /// </summary>
        [Fact]
        public void D07_S7_4_ServerThread_PlayerLeavesAndReturns_RestartBetween_SamePlace()
        {
            var storage = new MemoryStorage();
            string world = Path.Combine(Path.GetTempPath(), "orpheus-return-thread-" + Guid.NewGuid().ToString("N")); // в памяти, на диске нет
            WorldPos at;
            using (var srv = new ThreadServerRun(storage, world, create: true))
            {
                var clients = new List<NetClient>();
                NetClient first = srv.Connect("одиночка", clients);
                Assert.True(Pump(() => first.HasSnapshot && first.Latest.Self.Present, 10_000, clients.ToArray()), $"не вошёл: {first.State}");
                Walk(first, new InputFrame(Input8.NorthEast), 600, clients);
                at = Settled(first, clients);
                Assert.NotEqual(ServerRun.SpawnPoint(MapShape.World(1024), 0), at);
                first.Disconnect();
                Pump(() => false, 100, clients.ToArray());
                Assert.True(SpinWait.SpinUntil(() => srv.Log.Saved("игрок вышел"), 5_000), "уход игрока не сохранён");

                clients.Clear();
                NetClient again = srv.Connect("Одиночка", clients); // имя без учёта регистра — тот же персонаж
                Assert.True(Pump(() => again.HasSnapshot && again.Latest.Self.Present, 10_000, clients.ToArray()), $"не вошёл снова: {again.State}");
                Assert.Equal(at, Settled(again, clients));
                again.Disconnect();
                Pump(() => false, 100, clients.ToArray());
                ServerResult r = srv.Stop();
                Assert.Equal(0, r.ExitCode);
                Assert.True(r.SavesWritten >= 2, $"сохранений {r.SavesWritten}");
            }

            using (var srv = new ThreadServerRun(storage, world, create: false))
            {
                Assert.True(srv.Log.Has("персонажи мира"), "персонажи мира не восстановлены после загрузки");
                var clients = new List<NetClient>();
                NetClient back = srv.Connect("одиночка", clients);
                NetClient fresh = srv.Connect("новенький", clients);
                Assert.True(Pump(() => back.HasSnapshot && back.Latest.Self.Present && fresh.HasSnapshot && fresh.Latest.Self.Present, 10_000, clients.ToArray()),
                    $"не вошли: {back.State} {fresh.State}");
                Assert.Equal(at, Settled(back, clients));
                Assert.Equal(ServerRun.SpawnPoint(MapShape.World(1024), 0), Settled(fresh, clients));
                Assert.Equal(0, srv.Stop().ExitCode);
            }

            Assert.False(Directory.Exists(world), "сохранение мимо хранилища: папка мира появилась на диске");
        }

        // ---------- Помощники ----------

        private static NetClient Udp(int port, string name, HostToken token, List<NetClient> all)
        {
            LiteNetTransport t = LiteNetTransport.Connect("127.0.0.1", port);
            var c = new NetClient(t, t.ServerPeer, name, NetModule.Version) { HostToken = token };
            all.Add(c);
            return c;
        }

        /// <summary>Идти кадрами <paramref name="frame"/> <paramref name="ms"/> мс, остальным — пустой ввод.</summary>
        private static void Walk(NetClient who, InputFrame frame, int ms, List<NetClient> all)
        {
            var w = System.Diagnostics.Stopwatch.StartNew();
            while (w.ElapsedMilliseconds < ms)
            {
                foreach (NetClient c in all)
                {
                    c.Poll(Environment.TickCount64 * 1000);
                    if (c.State == NetClientState.InGame)
                    {
                        c.SendFrame(c == who ? frame : InputFrame.None);
                    }
                }

                Thread.Sleep(15);
            }
        }

        /// <summary>Своя позиция из снимков, когда она перестала меняться (ввод пустой).</summary>
        private static WorldPos Settled(NetClient who, List<NetClient> all)
        {
            WorldPos last = who.Latest.Self.Position;
            int same = 0;
            Assert.True(Pump(() =>
            {
                WorldPos now = who.Latest.Self.Position;
                same = who.Latest.Self.Present && now == last ? same + 1 : 0;
                last = now;
                return same >= 20;
            }, 10_000, all.ToArray()), "позиция не устоялась");
            return last;
        }

        private static bool Pump(Func<bool> done, int timeoutMs, params NetClient[] clients)
        {
            var w = System.Diagnostics.Stopwatch.StartNew();
            while (w.ElapsedMilliseconds < timeoutMs)
            {
                foreach (NetClient c in clients)
                {
                    c.Poll(Environment.TickCount64 * 1000);
                    if (c.State == NetClientState.InGame)
                    {
                        c.SendFrame(InputFrame.None);
                    }
                }

                if (done())
                {
                    return true;
                }

                Thread.Sleep(15);
            }

            return done();
        }

        private static bool SavedLine(string text, string reason)
        {
            foreach (string line in text.Split('\n'))
            {
                if (line.Contains("\"msg\":\"сохранено\"") && line.Contains($"\"reason\":\"{reason}\""))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Сервер и клиенты на петле в памяти; такт — по вызову.</summary>
        private sealed class Harness : IDisposable
        {
            private readonly List<NetClient> _clients = new List<NetClient>();
            private readonly LoopbackTransport _server;

            public Harness()
            {
                WorldModel world = FlatWorldGenerator.Instance.Generate(MapShape.World(1024), 7);
                Sim = new Simulation(world, maxEntities: 64);
                _server = LoopbackTransport.CreateServer(16);
                Server = new NetServer(_server, Sim, n => ServerRun.SpawnPoint(world.Shape, n), 8, NetModule.Version) { HostToken = Token };
            }

            public Simulation Sim { get; }
            public NetServer Server { get; }
            public long Now { get; private set; }

            public NetClient Connect(string name, HostToken token)
            {
                var c = new NetClient(LoopbackTransport.ConnectTo(_server), LoopbackTransport.ServerPeer, name, NetModule.Version) { HostToken = token };
                _clients.Add(c);
                return c;
            }

            public WorldPos PositionOf(int entityId) => MoverOf(entityId).Position;

            public Mover MoverOf(int entityId)
            {
                int slot = Sim.SlotOf(entityId);
                Assert.True(slot >= 0, $"сущности #{entityId} нет");
                return Sim.Entities[slot].Mover;
            }

            public void Run(int ticks) => Run(ticks, null, InputFrame.None);

            /// <summary><paramref name="ticks"/> тактов: <paramref name="who"/> шлёт <paramref name="frame"/> (2 кадра на такт), остальные — пусто.</summary>
            public void Run(int ticks, NetClient? who, InputFrame frame)
            {
                for (int t = 0; t < ticks; t++)
                {
                    for (int half = 0; half < MovementTick.FramesPerTick; half++)
                    {
                        Now += TickMicros / MovementTick.FramesPerTick;
                        foreach (NetClient c in _clients)
                        {
                            c.Poll(Now);
                            if (c.State == NetClientState.InGame)
                            {
                                c.SendFrame(c == who ? frame : InputFrame.None);
                            }
                        }
                    }

                    Server.Poll(Now);
                    Sim.Step();
                    Server.SendSnapshots();
                }

                foreach (NetClient c in _clients)
                {
                    c.Poll(Now);
                }
            }

            public void Dispose()
            {
                foreach (NetClient c in _clients)
                {
                    c.Transport.Dispose();
                }

                _server.Dispose();
            }
        }

        /// <summary>Сервер в потоке (0-031) на локальной связи и хранилище в памяти; клиенты — по той же связи.</summary>
        private sealed class ThreadServerRun : IDisposable
        {
            private readonly LoopbackTransport _link = LoopbackTransport.CreateServer(4);
            private readonly ServerThread _host;
            private readonly List<LoopbackTransport> _clients = new List<LoopbackTransport>();

            public ThreadServerRun(MemoryStorage storage, string world, bool create)
            {
                var args = new List<string> { "--world", world, "--metrics", "0" };
                if (create)
                {
                    args.AddRange(new[] { "--create", "--seed", "11", "--size", "1024" });
                }

                var services = new ServerHostServices { Log = Log, Storage = storage, LocalTransport = _link };
                _host = ServerThread.Start(ServerOptions.Parse(args.ToArray()), services, "server-0-035-thread");
                Assert.True(SpinWait.SpinUntil(() => _host.IsReady || _host.HasExited, 20_000), "сервер в потоке не готов");
                Assert.True(_host.IsReady, $"сервер в потоке вышел до готовности: {_host.Error}");
            }

            public RecordLog Log { get; } = new RecordLog();

            public NetClient Connect(string name, List<NetClient> all)
            {
                LoopbackTransport t = LoopbackTransport.ConnectTo(_link);
                _clients.Add(t);
                var c = new NetClient(t, LoopbackTransport.ServerPeer, name, NetModule.Version);
                all.Add(c);
                return c;
            }

            public ServerResult Stop()
            {
                Assert.True(_host.Stop(30_000), "сервер в потоке не остановился");
                return _host.Result ?? throw new Xunit.Sdk.XunitException("нет итога: " + _host.Error);
            }

            public void Dispose()
            {
                _host.Dispose();
                foreach (LoopbackTransport t in _clients)
                {
                    t.Dispose();
                }
            }
        }

        /// <summary>Лог сервера в потоке: сообщения и причины сохранений.</summary>
        private sealed class RecordLog : IServerLog
        {
            private readonly List<string> _lines = new List<string>();

            public bool Enabled(string cat, LogLevel level) => level >= LogLevel.Info;

            public void Write(LogLevel level, long tick, string cat, string msg, (string Key, object? Value)[] data)
            {
                string reason = string.Empty;
                foreach ((string key, object? value) in data)
                {
                    reason = key == "reason" ? value?.ToString() ?? string.Empty : reason;
                }

                lock (_lines)
                {
                    _lines.Add(msg + "|" + reason);
                }
            }

            public bool Has(string msg)
            {
                lock (_lines)
                {
                    return _lines.Exists(l => l.StartsWith(msg + "|", StringComparison.Ordinal));
                }
            }

            public bool Saved(string reason)
            {
                lock (_lines)
                {
                    return _lines.Contains("сохранено|" + reason);
                }
            }
        }

        /// <summary>Сервер игры (хозяин) целиком в процессе теста, в своём потоке; UDP только на петле.</summary>
        private sealed class InProcServer : IDisposable
        {
            private readonly CancellationTokenSource _stop = new CancellationTokenSource();
            private readonly Thread _thread;
            private readonly System.Text.StringBuilder _text = new System.Text.StringBuilder();
            private ServerResult? _result;
            private Exception? _error;

            public InProcServer(string dir, HostToken token, bool create, params string[] extra)
            {
                var args = new List<string> { "--world", dir, "--port", "0", "--mode", "host", "--host-token", token.ToString(), "--open-address", "127.0.0.2", "--open-port", "0" };
                if (create)
                {
                    args.AddRange(new[] { "--create", "--seed", "11", "--size", "1024" });
                }

                args.AddRange(extra);
                ServerOptions o = ServerOptions.Parse(args.ToArray());
                var console = new Writer(_text);
                _thread = new Thread(() =>
                {
                    try
                    {
                        _result = ServerRun.Run(o, console, _stop.Token);
                    }
                    catch (Exception e)
                    {
                        _error = e;
                    }
                })
                { IsBackground = true, Name = "server-0-035" };
                _thread.Start();
            }

            public string Text
            {
                get
                {
                    lock (_text)
                    {
                        return _text.ToString();
                    }
                }
            }

            public bool Exited => !_thread.IsAlive;

            public int WaitReady(int timeoutMs)
            {
                string marker = ServerRun.ReadyMarker + " port=";
                int port = -1;
                Assert.True(SpinWait.SpinUntil(() =>
                {
                    string t = Text;
                    int at = t.IndexOf(marker, StringComparison.Ordinal);
                    if (at < 0)
                    {
                        return Exited;
                    }

                    int end = t.IndexOfAny(new[] { '\r', '\n' }, at);
                    if (end < 0)
                    {
                        return false;
                    }

                    port = int.Parse(t.Substring(at + marker.Length, end - at - marker.Length), System.Globalization.CultureInfo.InvariantCulture);
                    return true;
                }, timeoutMs), "сервер не готов");
                Assert.True(port > 0, $"сервер вышел до готовности: {_error}\n{Text}");
                return port;
            }

            public ServerResult Stop(int timeoutMs)
            {
                _stop.Cancel();
                Assert.True(_thread.Join(timeoutMs), "сервер не остановился");
                Assert.Null(_error);
                return _result!;
            }

            public void Dispose()
            {
                _stop.Cancel();
                _thread.Join(30_000);
                _stop.Dispose();
            }

            private sealed class Writer : TextWriter
            {
                private readonly System.Text.StringBuilder _sb;

                public Writer(System.Text.StringBuilder sb) => _sb = sb;

                public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

                public override void Write(char value)
                {
                    lock (_sb)
                    {
                        _sb.Append(value);
                    }
                }

                public override void Write(string? value)
                {
                    lock (_sb)
                    {
                        _sb.Append(value);
                    }
                }
            }
        }
    }
}
