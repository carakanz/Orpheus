using System;
using System.Collections.Generic;
using System.Linq;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net;
using Orpheus.Net.Protocol;
using Orpheus.Net.Session;
using Orpheus.Net.Transport;
using Xunit;
using Xunit.Abstractions;

namespace Orpheus.Tests
{
    /// <summary>
    /// Правки тайлов (0-028; docs/07, 3.7, 3.13 п. 8; `05` 1.3–1.5): правила вехи 0, проверки сервера, конфликт в такте,
    /// предсказание и откат у клиента, существа и враги.
    /// </summary>
    public class EditTests
    {
        private const int U = 1 << Torus.SubTileBits;
        private const long FrameMicros = 1_000_000 / 60;
        private readonly ITestOutputHelper _out;

        public EditTests(ITestOutputHelper output)
        {
            _out = output;
        }

        internal static WorldPos TileCenter(int x, int y) => new WorldPos(x * U + U / 2, y * U + U / 2);

        private static WorldModel Flat(int size = 1024) => FlatWorldGenerator.Instance.Generate(MapShape.World(size), 1);

        private static readonly InputFrame DigHold = new InputFrame(Input8.None, InputButtons.Dig);

        // ---------- Правила ----------

        /// <summary>
        /// Досягаемость копания «персонаж + инструмент» (0-034): 0,4 + 0,75 = 1,15 т до ближайшей точки тайла — из центра тайла
        /// диагональ да, через тайл нет; у края своего тайла — через тайл да. Радиус 6 по оси максимума через шов, линия ≤ 32.
        /// </summary>
        [Fact]
        public void D07_S3_7_Rules_Reach_Radius_Line()
        {
            MapShape map = MapShape.World(1024);
            WorldPos c = TileCenter(0, 0);
            Assert.Equal(294, EditRules.DigReachUnits); // 102 + 192
            Assert.True(EditRules.InDigReach(map, c, new TileCoord(1, 1)));
            Assert.True(EditRules.InDigReach(map, c, new TileCoord(1023, 1023))); // диагональ через шов
            Assert.False(EditRules.InDigReach(map, c, new TileCoord(2, 0))); // 1,5 т до края > 1,15
            Assert.True(EditRules.InDigReach(map, new WorldPos(c.X - U * 3 / 8, c.Y), new TileCoord(1, 1))); // смещён: 0,875 × 0,5 — да
            Assert.True(EditRules.InDigReach(map, new WorldPos(c.X + 90, c.Y), new TileCoord(2, 0))); // у края: 384 − 90 = 294 — ровно
            Assert.False(EditRules.InDigReach(map, new WorldPos(c.X + 89, c.Y), new TileCoord(2, 0))); // на 1 unit дальше
            Assert.False(EditRules.InDigReach(map, new WorldPos(c.X + 90, c.Y), new TileCoord(2, 1))); // через тайл по диагонали — нет

            Assert.True(EditRules.InPlaceRadius(map, c, new TileCoord(1018, 6))); // 6 через шов, угол квадрата 13 × 13
            Assert.False(EditRules.InPlaceRadius(map, c, new TileCoord(1017, 0)));
            Assert.False(EditRules.InPlaceRadius(map, c, new TileCoord(0, 7)));

            var line = new TileCoord[EditRules.MaxLineTiles];
            Assert.Equal(32, EditRules.Line(map, new TileCoord(1010, 5), new TileCoord(17, 5), line)); // через шов: 1010 … 1023, 0 … 17
            Assert.Equal(new TileCoord(1023, 5), line[13]);
            Assert.Equal(new TileCoord(0, 5), line[14]);
            Assert.Equal(-1, EditRules.Line(map, new TileCoord(1010, 5), new TileCoord(18, 5), line));
            Assert.Equal(5, EditRules.Line(map, new TileCoord(3, 3), new TileCoord(7, 5), line));
            Assert.Equal(new TileCoord(7, 5), line[4]);

            WorldModel w = Flat();
            w.SetBaseTile(new TileCoord(1, 0), FloorId.Stone, ObjectId.Rock);
            w.SetBaseTile(new TileCoord(2, 0), FloorId.Water, ObjectId.Empty);
            w.SetBaseTile(new TileCoord(3, 0), FloorId.Grass, ObjectId.StoneWall);
            var times = new DigTimes();
            Assert.Equal(EditReply.Accepted, EditRules.CheckDig(w, c, new TileCoord(1, 0), times));
            Assert.Equal(EditReply.Empty, EditRules.CheckDig(w, c, new TileCoord(0, 1), times));
            Assert.Equal(EditReply.NotAllowed, EditRules.CheckDig(w, TileCenter(3, 1), new TileCoord(3, 0), times)); // постройку не копают (снос — потом)
            times.SetModifier(ObjectId.Rock, 0);
            Assert.Equal(EditReply.NotAllowed, EditRules.CheckDig(w, c, new TileCoord(1, 0), times)); // время 0 — неразрушима
            Assert.Equal(EditReply.DigFirst, EditRules.CheckPlaceTile(w, c, new TileCoord(1, 0), ObjectId.StoneWall));
            Assert.Equal(EditReply.NeedPlatform, EditRules.CheckPlaceTile(w, c, new TileCoord(2, 0), ObjectId.StoneWall));
            Assert.Equal(EditReply.Busy, EditRules.CheckPlaceTile(w, c, new TileCoord(3, 0), ObjectId.StoneWall));
            Assert.Equal(EditReply.NotAllowed, EditRules.CheckPlaceTile(w, c, new TileCoord(4, 0), ObjectId.Rock));
            Assert.Equal("враг ближе 2 тайлов", EditRules.Describe(EditReply.EnemyNear));
            Assert.Equal("занято существом", EditRules.Describe(EditReply.Occupied));
        }

        // ---------- Такт: существа и враги ----------

        /// <summary>
        /// `05` 1.3, backlog [стройка] (П по 0-004): непроходимое на тайл с существом (и на свой) — «занято существом»; непроходимое и
        /// ловушка ближе 2 тайлов к врагу — «враг ближе 2 тайлов»; ковёр — можно. Проверка — после движения такта.
        /// </summary>
        [Fact]
        public void D05_S1_3_Place_OnCreature_NearEnemy_Refused()
        {
            var sim = new Simulation(Flat());
            int me = sim.AddEntity(TileCenter(100, 100));
            sim.AddEnemy(TileCenter(110, 100));
            var events = new List<SimEvent>();

            EditResult Try(int x, int y, ushort obj)
            {
                sim.RequestPlayerEdit(new TileCoord(x, y), FloorId.Grass, obj, true, BuildRecord.OfPlayer(1, FloorId.Grass), me, 7);
                sim.Step();
                SimEvent e = sim.Events.ToArray().Single(v => v.Kind == SimEventKind.TileEdit);
                Assert.Equal(me, e.Requester);
                Assert.Equal(7, e.Tag);
                return e.Result;
            }

            Assert.Equal(EditResult.Occupied, Try(100, 100, ObjectId.StoneWall)); // на себя
            Assert.Equal(EditResult.Occupied, Try(110, 100, ObjectId.StoneWall)); // на врага
            Assert.Equal(EditResult.EnemyNear, Try(111, 101, ObjectId.StoneWall)); // √2 < 2
            Assert.Equal(EditResult.EnemyNear, Try(109, 101, ObjectId.Spikes)); // ловушка — тоже
            Assert.Equal(EditResult.Applied, Try(112, 100, ObjectId.StoneWall)); // ровно 2 — можно
            Assert.Equal(EditResult.Applied, Try(109, 100, ObjectId.Carpet)); // проходимое рядом с врагом
            Assert.Equal(EditResult.Applied, Try(100, 100, ObjectId.Carpet)); // и под себя
            Assert.Equal(ObjectId.Empty, sim.World.GetObject(new TileCoord(111, 101)));
        }

        /// <summary>
        /// «Нельзя выйти сквозь стену в 1 тайл» (backlog [стройка], П по 0-004): персонаж идёт на восток к стене толщиной 1 тайл и
        /// каждый такт ставит стену на тайл, который задевает (свой и следующий), — ни одна не встаёт, стену он не проходит.
        /// </summary>
        [Fact]
        public void D05_S1_3_PlaceUnderSelf_CannotWalkThroughOneTileWall()
        {
            var sim = new Simulation(Flat());
            int me = sim.AddEntity(new WorldPos(10 * U + U * 7 / 10, 50 * U + U / 2)); // задевает тайл 11
            for (int y = 45; y <= 55; y++)
            {
                sim.World.SetBaseTile(new TileCoord(13, y), FloorId.Grass, ObjectId.StoneWall);
            }

            int refused = 0;
            for (int t = 0; t < 90; t++)
            {
                WorldPos p = sim.GetEntity(me).Mover.Position;
                int tx = p.X >> Torus.SubTileBits;
                for (int dx = -1; dx <= 1; dx++)
                {
                    sim.RequestPlayerEdit(new TileCoord(tx + dx, 50), FloorId.Grass, ObjectId.StoneWall, true, BuildRecord.OfPlayer(1, FloorId.Grass), me, t);
                }

                sim.SetInput(me, new InputFrame(Input8.East));
                sim.Step();
                foreach (SimEvent e in sim.Events.ToArray())
                {
                    if (e.Kind == SimEventKind.TileEdit && e.Result == EditResult.Applied)
                    {
                        WorldPos q = sim.GetEntity(me).Mover.Position;
                        TileCoord placed = new TileCoord((e.Chunk.X << Torus.ChunkBits) + (e.TileIndex & 31), (e.Chunk.Y << Torus.ChunkBits) + (e.TileIndex >> 5));
                        Assert.False(Collision.CircleOverlapsTile(sim.Shape, q, placed), $"такт {t}: стена {placed} встала на персонажа {q}");
                    }
                    else if (e.Kind == SimEventKind.TileEdit && e.Result == EditResult.Occupied)
                    {
                        refused++;
                    }
                }

                Assert.True(sim.GetEntity(me).Mover.Position.X + Collision.RadiusUnits <= 13 * U, $"такт {t}: персонаж в стене или за ней");
            }

            Assert.True(refused > 30, $"отказов «занято существом» {refused}");
        }

        // ---------- По сети ----------

        /// <summary>
        /// Копание по времени сервера (3.7): клиент жмёт «копать» у скалы и держит кнопку; через 15 кадров своим счётом предсказывает
        /// «объект снят», сервер докапывает по тем же кадрам и отвечает «принято»; тайл у клиента = сервер, наблюдатель видит правку.
        /// Отпустил раньше — «прервано», скала на месте.
        /// </summary>
        [Fact]
        public void D07_S3_7_Dig_PredictThenConfirm_StopEarly_Interrupted()
        {
            WorldModel world = Flat();
            var rock = new TileCoord(301, 200);
            var rock2 = new TileCoord(301, 201);
            world.SetBaseTile(rock, FloorId.Stone, ObjectId.Rock);
            world.SetBaseTile(rock2, FloorId.Stone, ObjectId.Rock);
            using var rig = new EditRig(world, n => TileCenter(300 + 2 * n, 200));
            NetClient c = rig.Join();
            NetClient observer = rig.Join();
            var replies = new List<TileReplyMessage>();
            c.EditReplied = replies.Add;
            rig.Run(10);

            ushort id = c.StartDig(rock);
            rig.InputOf = k => k == c ? DigHold : InputFrame.None;
            int predictedAt = -1;
            for (int t = 0; t < 20 && replies.Count == 0; t++)
            {
                rig.Run(1);
                if (predictedAt < 0 && c.World!.World.GetObject(rock) == ObjectId.Empty)
                {
                    predictedAt = t;
                }
            }

            Assert.Single(replies);
            Assert.Equal(id, replies[0].RequestId);
            Assert.Equal(EditReply.Accepted, replies[0].Reply);
            Assert.InRange(predictedAt, 6, 8); // 15 кадров = 7,5 такта
            Assert.Equal(ObjectId.Empty, rig.Sim.World.GetObject(rock));
            rig.Run(2);
            ChunkCoord ch = Torus.ChunkOf(world.Shape, rock);
            Assert.True(c.World!.SameAs(rig.Sim.World, ch));
            Assert.True(observer.World!.SameAs(rig.Sim.World, ch));
            Assert.Equal(1, c.World.PredictionsConfirmed);

            // Отпустил раньше: «прервано», скала на месте, предсказания не было.
            replies.Clear();
            ushort id2 = c.StartDig(rock2);
            rig.Run(3);
            c.StopDig();
            rig.InputOf = null;
            rig.Run(10);
            Assert.Single(replies);
            Assert.Equal(id2, replies[0].RequestId);
            Assert.Equal(EditReply.Interrupted, replies[0].Reply);
            Assert.Equal(ObjectId.Rock, rig.Sim.World.GetObject(rock2));
            Assert.Equal(0, c.World.PredictionsRolledBack);
        }

        /// <summary>
        /// Досягаемость копания «персонаж + предмет в руках» (1-002, `06` 2.3): тело + длина предмета —
        /// у лука длины нет (только тело 102), у кирки 294, меч (358) длиннее кирки; «что копать» — тот же.
        /// </summary>
        [Fact]
        public void D07_S3_7_Rules_DigReach_FromItem()
        {
            MapShape map = MapShape.World(1024);
            WorldPos c = TileCenter(0, 0);
            Assert.Equal(294, EditRules.DigReachUnitsFor(Items.StonePickaxe)); // 102 + 192 — прежнее число
            Assert.Equal(358, EditRules.DigReachUnitsFor(Items.WoodenSword));  // 102 + 256
            Assert.Equal(102, EditRules.DigReachUnitsFor(Items.SimpleBow));    // длины нет — только тело
            Assert.Equal(102, EditRules.DigReachUnitsFor(Items.None));

            // Тайл по диагонали от центра тайла (181 unit) — киркой да, луком (102) нет.
            var diag = new TileCoord(1, 1);
            Assert.True(EditRules.InDigReach(map, c, diag, Items.StonePickaxe));
            Assert.False(EditRules.InDigReach(map, c, diag, Items.SimpleBow));

            // У меча рука длиннее кирки: тот же тайл из угла карты (348 unit) киркой — нет, мечом — да.
            WorldPos corner = new WorldPos(10, 10);
            Assert.False(EditRules.InDigReach(map, corner, diag, Items.StonePickaxe));
            Assert.True(EditRules.InDigReach(map, corner, diag, Items.WoodenSword));

            WorldModel w = Flat();
            w.SetBaseTile(diag, FloorId.Stone, ObjectId.Rock);
            var times = new DigTimes();
            Assert.Equal(EditReply.Accepted, EditRules.CheckDig(w, c, diag, times, Items.StonePickaxe));
            Assert.Equal(EditReply.OutOfReach, EditRules.CheckDig(w, c, diag, times, Items.SimpleBow));
            // Проверка без предмета — как с киркой (старые вызовы не меняются).
            Assert.Equal(EditRules.CheckDig(w, c, diag, times, Items.StonePickaxe), EditRules.CheckDig(w, c, diag, times));
        }

        /// <summary>
        /// В руках меч — копание не начинается (1-002, `02` 1.1 «Основное действие»): запрос клиента сервер
        /// молча игнорирует — правки нет и отказа нет (у меча своё действие — 1-004).
        /// </summary>
        [Fact]
        public void D07_S3_7_Dig_SwordInHands_NoEditNoRefusal()
        {
            WorldModel world = Flat();
            var rock = new TileCoord(301, 200);
            world.SetBaseTile(rock, FloorId.Stone, ObjectId.Rock);
            using var rig = new EditRig(world, _ => TileCenter(300, 200));
            NetClient c = rig.Join();
            var replies = new List<TileReplyMessage>();
            c.EditReplied = replies.Add;

            rig.InputOf = _ => new InputFrame(Input8.None, 0, 0, 3); // ячейка 3 — деревянный меч
            rig.Run(5);                                              // кадры дошли — на сервере в руках меч
            Assert.Equal(Items.WoodenSword, rig.Sim.Entities[rig.Sim.SlotOf(c.EntityId)].Inv.Held);

            c.StartDig(rock);
            rig.InputOf = _ => new InputFrame(Input8.None, InputButtons.Dig, 0, 3);
            rig.Run(30);
            Assert.True(rig.Server.EditRequests > 0, "запрос не дошёл до сервера");
            Assert.Empty(replies); // отказа нет
            Assert.Equal(ObjectId.Rock, rig.Sim.World.GetObject(rock)); // и правки нет
        }

        /// <summary>
        /// Смена на меч в середине копания — прогресс стоит (1-002); возврат на кирку — докапывает с того же места,
        /// ответ «принято».
        /// </summary>
        [Fact]
        public void D07_S3_7_Dig_SwitchToSword_ProgressStalls_ThenResumes()
        {
            WorldModel world = Flat();
            var rock = new TileCoord(301, 200);
            world.SetBaseTile(rock, FloorId.Stone, ObjectId.Rock);
            using var rig = new EditRig(world, _ => TileCenter(300, 200));
            NetClient c = rig.Join();
            var replies = new List<TileReplyMessage>();
            c.EditReplied = replies.Add;

            rig.InputOf = _ => new InputFrame(Input8.None, InputButtons.Dig, 0, 0); // кирка
            rig.Run(5); // игрок в мире
            ushort id = c.StartDig(rock);
            rig.Run(5); // ~10 кадров из 15 — не скопано
            Assert.Equal(ObjectId.Rock, rig.Sim.World.GetObject(rock));
            Assert.Empty(replies);

            rig.InputOf = _ => new InputFrame(Input8.None, InputButtons.Dig, 0, 3); // меч — прогресс стоит
            rig.Run(20);
            Assert.Equal(ObjectId.Rock, rig.Sim.World.GetObject(rock));
            Assert.Empty(replies);

            rig.InputOf = _ => new InputFrame(Input8.None, InputButtons.Dig, 0, 0); // кирка — с того же места
            for (int t = 0; t < 10 && replies.Count == 0; t++)
            {
                rig.Run(1);
            }

            Assert.Single(replies);
            Assert.Equal(id, replies[0].RequestId);
            Assert.Equal(EditReply.Accepted, replies[0].Reply);
            Assert.Equal(ObjectId.Empty, rig.Sim.World.GetObject(rock));
        }

        /// <summary>
        /// Время копания «база × модификатор» (слово Автора 08.11, 0-034): таблица по умолчанию — скала 15 кадров (0,25 с), дерево
        /// 18, залежь 30, пень 9, постройки 0; округление к ближайшему, не меньше кадра; база 0 или модификатор 0 — неразрушим.
        /// </summary>
        [Fact]
        public void D05_S1_3_DigTimes_BaseTimesModifier_ZeroIndestructible()
        {
            var t = new DigTimes();
            Assert.Equal(15, t.BaseFrames);
            Assert.Equal(15, t.FramesOf(ObjectId.Rock));
            Assert.Equal(18, t.FramesOf(ObjectId.Tree));
            Assert.Equal(30, t.FramesOf(ObjectId.Ore));
            Assert.Equal(9, t.FramesOf(ObjectId.Stump));
            Assert.Equal(0, t.FramesOf(ObjectId.StoneWall));
            Assert.Equal(0, t.FramesOf(ObjectId.Empty));
            Assert.Equal(0, t.FramesOf(ObjectId.MakePart(1, 0)));
            Assert.True(EditRules.IsDiggable(ObjectId.Stump, t));

            t.BaseFrames = 30; // база правится — все времена за ней
            Assert.Equal(30, t.FramesOf(ObjectId.Rock));
            Assert.Equal(18, t.FramesOf(ObjectId.Stump));
            t.SetModifier(ObjectId.Rock, 1); // 0,3 кадра — не меньше 1
            Assert.Equal(1, t.FramesOf(ObjectId.Rock));
            t.SetModifier(ObjectId.Stump, 0); // время 0 — разрушить нельзя
            Assert.Equal(0, t.FramesOf(ObjectId.Stump));
            Assert.False(EditRules.IsDiggable(ObjectId.Stump, t));
            t.BaseFrames = 0;
            Assert.Equal(0, t.FramesOf(ObjectId.Tree));
            Assert.Throws<ArgumentOutOfRangeException>(() => t.SetModifier(ObjectId.Empty, 100));
            Assert.Throws<ArgumentOutOfRangeException>(() => t.SetModifier(ObjectId.Rock, -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => t.BaseFrames = -1);
        }

        /// <summary>
        /// Копание по таблице на сервере и клиенте (0-034): залежь (×2) копается вдвое дольше скалы; правка таблицы (модификатор
        /// залежи ×0,5 у сервера и клиента) меняет время; пень с модификатором 0 — «нельзя», пень на месте, предсказания нет.
        /// </summary>
        [Fact]
        public void D05_S1_3_DigTimes_ServerAndClient_DifferentItems_EditChangesTime_ZeroRefused()
        {
            WorldModel world = Flat();
            var rock = new TileCoord(301, 200);
            var ore = new TileCoord(299, 200);
            var ore2 = new TileCoord(300, 201);
            var stump = new TileCoord(300, 199);
            world.SetBaseTile(rock, FloorId.Stone, ObjectId.Rock);
            world.SetBaseTile(ore, FloorId.Stone, ObjectId.Ore);
            world.SetBaseTile(ore2, FloorId.Stone, ObjectId.Ore);
            world.SetBaseTile(stump, FloorId.Grass, ObjectId.Stump);
            using var rig = new EditRig(world, n => TileCenter(300, 200));
            NetClient c = rig.Join();
            var replies = new List<TileReplyMessage>();
            c.EditReplied = replies.Add;
            rig.Run(10);

            int DigTicks(TileCoord tile, out EditReply reply)
            {
                replies.Clear();
                c.StartDig(tile);
                rig.InputOf = _ => DigHold;
                int t = 0;
                while (replies.Count == 0 && t < 60)
                {
                    rig.Run(1);
                    t++;
                }

                rig.InputOf = null;
                rig.Run(3);
                reply = replies.Count > 0 ? replies[0].Reply : EditReply.NotAllowed;
                return t;
            }

            int rockTicks = DigTicks(rock, out EditReply r1);
            Assert.Equal(EditReply.Accepted, r1);
            Assert.Equal(15, c.DigFrames);
            int oreTicks = DigTicks(ore, out EditReply r2);
            Assert.Equal(EditReply.Accepted, r2);
            Assert.Equal(30, c.DigFrames);
            Assert.InRange(oreTicks - rockTicks, 7, 8); // +15 кадров = 7,5 такта

            rig.Server.DigTimes.SetModifier(ObjectId.Ore, 50); // правка таблицы — у сервера и клиента
            c.DigTimes.SetModifier(ObjectId.Ore, 50);
            int ore2Ticks = DigTicks(ore2, out EditReply r3);
            Assert.Equal(EditReply.Accepted, r3);
            Assert.Equal(8, c.DigFrames); // 7,5 → 8
            Assert.InRange(ore2Ticks, rockTicks - 4, rockTicks - 3);

            rig.Server.DigTimes.SetModifier(ObjectId.Stump, 0);
            c.DigTimes.SetModifier(ObjectId.Stump, 0);
            DigTicks(stump, out EditReply r4);
            Assert.Equal(EditReply.NotAllowed, r4);
            Assert.Equal(ObjectId.Stump, rig.Sim.World.GetObject(stump));
            Assert.Equal(ObjectId.Stump, c.World!.World.GetObject(stump));
            Assert.Equal(0, c.World.PredictionsRolledBack);
            Assert.Equal(ObjectId.Empty, rig.Sim.World.GetObject(rock));
            Assert.Equal(ObjectId.Empty, rig.Sim.World.GetObject(ore));
            Assert.Equal(ObjectId.Empty, rig.Sim.World.GetObject(ore2));
        }

        /// <summary>
        /// Проверки сервера (3.7): копать дальше «персонаж + инструмент» — «далеко», пустой тайл — «пусто», постройку — «нельзя»; поставить дальше
        /// радиуса 6 — «далеко»; линия длиннее 32 не уходит. Тайлы сервера не меняются.
        /// </summary>
        [Fact]
        public void D07_S3_7_ServerChecks_Reach_Empty_Radius()
        {
            WorldModel world = Flat();
            world.SetBaseTile(new TileCoord(303, 200), FloorId.Stone, ObjectId.Rock);
            using var rig = new EditRig(world, n => TileCenter(300, 200));
            NetClient c = rig.Join();
            var replies = new List<TileReplyMessage>();
            c.EditReplied = replies.Add;
            rig.Run(10);
            WorldPos self = rig.Sim.GetEntity(c.EntityId).Mover.Position;

            c.StartDig(new TileCoord(303, 200)); // 3 тайла
            rig.InputOf = _ => DigHold;
            rig.Run(12);
            c.StartDig(new TileCoord(301, 200)); // пусто
            rig.Run(2);
            ushort far = c.Place(new TileCoord(307, 200), new TileCoord(307, 200), ObjectId.StoneWall, self); // 7 по оси
            rig.Run(2);
            Assert.Equal(0, c.Place(new TileCoord(300, 210), new TileCoord(340, 210), ObjectId.StoneWall, self));

            Assert.Equal(new[] { EditReply.OutOfReach, EditReply.Empty, EditReply.OutOfReach }, replies.Select(r => r.Reply).ToArray());
            Assert.Equal(far, replies[2].RequestId);
            Assert.Equal(ObjectId.Rock, rig.Sim.World.GetObject(new TileCoord(303, 200)));
            Assert.Equal(ObjectId.Empty, rig.Sim.World.GetObject(new TileCoord(307, 200)));
            Assert.Equal(0, c.World!.PendingPredictions);
        }

        /// <summary>
        /// 3.13 п. 8 (`05` 1.4): два клиента ставят стену в один тайл в один такт — правка одна (ревизия +1), стена первого,
        /// второй получил «занято» и откатил призрак (тайл = сервер). То же с копанием одной скалы: докопали в один такт — правка одна,
        /// второму «занято».
        /// </summary>
        [Fact]
        public void D07_S3_13_8_TwoClients_SameTile_SameTick_OneEdit_SecondBusy()
        {
            WorldModel world = Flat();
            var rock = new TileCoord(301, 201);
            world.SetBaseTile(rock, FloorId.Stone, ObjectId.Rock);
            using var rig = new EditRig(world, n => TileCenter(300 + 2 * n, 200));
            NetClient a = rig.Join();
            NetClient b = rig.Join();
            var ra = new List<TileReplyMessage>();
            var rb = new List<TileReplyMessage>();
            var flashes = new List<(TileCoord, EditReply)>();
            a.EditReplied = ra.Add;
            b.EditReplied = rb.Add;
            rig.Run(10);
            b.World!.RolledBack = (t, r) => flashes.Add((t, r));

            var wall = new TileCoord(301, 196);
            ChunkCoord ch = Torus.ChunkOf(world.Shape, wall);
            uint rev = rig.Sim.World.FindChunk(ch)!.Revision;
            a.Place(wall, wall, ObjectId.StoneWall, rig.Sim.GetEntity(a.EntityId).Mover.Position);
            b.Place(wall, wall, ObjectId.StoneWall, rig.Sim.GetEntity(b.EntityId).Mover.Position);
            Assert.Equal(ObjectId.StoneWall, b.World.World.GetObject(wall)); // призрак сразу
            rig.Run(3);

            Assert.Equal(EditReply.Accepted, Assert.Single(ra).Reply);
            Assert.Equal(EditReply.Busy, Assert.Single(rb).Reply);
            Assert.Equal(rev + 1, rig.Sim.World.FindChunk(ch)!.Revision);
            Assert.True(rig.Sim.World.TryGetBuild(wall, out BuildRecord rec));
            Assert.Equal((ushort)a.EntityId, rec.Owner);
            Assert.Equal((wall, EditReply.Busy), Assert.Single(flashes));
            Assert.True(a.World!.SameAs(rig.Sim.World, ch));
            Assert.True(b.World.SameAs(rig.Sim.World, ch));

            // Копание одной скалы обоими: правка одна, второму — «занято».
            ra.Clear();
            rb.Clear();
            ChunkCoord rc = Torus.ChunkOf(world.Shape, rock);
            uint rrev = rig.Sim.World.FindChunk(rc)!.Revision;
            a.StartDig(rock);
            b.StartDig(rock);
            rig.InputOf = _ => DigHold;
            rig.Run(12);
            Assert.Equal(EditReply.Accepted, Assert.Single(ra).Reply);
            Assert.Equal(EditReply.Busy, Assert.Single(rb).Reply);
            Assert.Equal(rrev + 1, rig.Sim.World.FindChunk(rc)!.Revision);
            Assert.True(a.World.SameAs(rig.Sim.World, rc));
            Assert.True(b.World.SameAs(rig.Sim.World, rc));
        }

        /// <summary>
        /// Отказ → откат (3.7 «Ответ»): RTT 100 мс, клиент ставит стену на тайл, где стоит второй игрок (клиент существ не проверяет —
        /// предсказывает); сервер — «занято существом»; тайл клиента = сервер не позже RTT + 1 такт после запроса, вспышка с причиной.
        /// </summary>
        [Theory]
        [InlineData(100)]
        [InlineData(200)]
        public void D07_S3_7_Refused_RollbackWithinRttPlusTick(int rttMs)
        {
            WorldModel world = Flat();
            using var rig = new EditRig(world, n => TileCenter(300 + 3 * n, 200));
            var link = new NetSimSettings { DelayMs = rttMs / 2, Seed = 3 };
            NetClient c = rig.Join(link);
            NetClient other = rig.Join();
            rig.Run(40);
            var flashes = new List<(TileCoord, EditReply)>();
            c.World!.RolledBack = (t, r) => flashes.Add((t, r));

            var target = new TileCoord(303, 200); // под вторым игроком
            long sentAt = rig.NowMicros;
            c.Place(target, target, ObjectId.StoneWall, rig.Sim.GetEntity(c.EntityId).Mover.Position);
            Assert.Equal(ObjectId.StoneWall, c.World.World.GetObject(target));
            long backAt = -1;
            rig.AfterClientPoll = k =>
            {
                if (k == c && backAt < 0 && c.World.World.GetObject(target) == rig.Sim.World.GetObject(target))
                {
                    backAt = rig.NowMicros;
                }
            };
            rig.Run(20);

            // Предел: RTT + 1 такт (запрос ждёт такта сервера) + 2 кадра 60 Гц — имитатор отпускает пакет только в Poll клиента,
            // раз в кадр, в каждую сторону.
            long limit = rttMs * 1000L + 1_000_000 / Simulation.TickRate + 2 * FrameMicros;
            _out.WriteLine($"RTT {rttMs} мс: откат через {(backAt - sentAt) / 1000.0:0.0} мс (предел RTT + 1 такт + 2 кадра = {limit / 1000.0:0.0} мс)");
            Assert.InRange(backAt - sentAt, rttMs * 1000L, limit);
            Assert.Equal((target, EditReply.Occupied), Assert.Single(flashes));
            rig.Run(10);
            Assert.True(c.World.SameAs(rig.Sim.World, Torus.ChunkOf(world.Shape, target)));
            Assert.Equal(1, c.World.PredictionsRolledBack);
        }

        /// <summary>
        /// Линия до 32 тайлов (`05` 1.5): тайлы по порядку, тайл с существом и рядом с врагом пропускается, линия идёт дальше; маска
        /// ответа — принятые тайлы; у клиента после ответа тайлы = сервер. Предела «20 тайлов/с» нет (слово Автора 08.11, 0-034):
        /// четыре линии по 13 без паузы встают целиком.
        /// </summary>
        [Fact]
        public void D05_S1_5_Line_SkipsCreatures_Mask_NoRateLimit()
        {
            WorldModel world = Flat();
            using var rig = new EditRig(world, n => TileCenter(300 + 4 * n, 200));
            NetClient c = rig.Join();
            NetClient other = rig.Join(); // стоит на (304, 200)
            rig.Sim.AddEnemy(TileCenter(299, 203));
            var replies = new List<TileReplyMessage>();
            c.EditReplied = replies.Add;
            rig.Run(10);

            WorldPos self = rig.Sim.GetEntity(c.EntityId).Mover.Position;
            c.Place(new TileCoord(296, 202), new TileCoord(306, 202), ObjectId.StoneWall, self); // мимо врага (299, 203)
            c.Place(new TileCoord(302, 200), new TileCoord(305, 200), ObjectId.WoodFence, self); // через второго игрока
            rig.Run(3);
            Assert.Equal(2, replies.Count);
            uint m1 = replies[0].Mask;
            Assert.Equal(EditReply.Accepted, replies[0].Reply);
            Assert.Equal(EditReply.EnemyNear, replies[0].FirstRefusal);
            Assert.Equal(0b111_1110_0011u, m1); // 296, 297 и 301…306; 298…300 — ближе 2 к врагу
            Assert.Equal(EditReply.Occupied, replies[1].FirstRefusal);
            Assert.Equal(0b1011u, replies[1].Mask); // 304 задевает второй игрок (радиус 0,4)
            for (int x = 290; x < 310; x++)
            {
                for (int y = 199; y <= 203; y++)
                {
                    Assert.Equal(rig.Sim.World.GetObject(new TileCoord(x, y)), c.World!.World.GetObject(new TileCoord(x, y)));
                }
            }

            // Предела нет: четыре линии по 13 без паузы (было: ведро 20/с пропускало 19) — принято 52.
            replies.Clear();
            for (int y = 194; y < 198; y++)
            {
                c.Place(new TileCoord(294, y), new TileCoord(306, y), ObjectId.Torch, self);
            }

            rig.Run(3);
            int placed = replies.Sum(r => System.Numerics.BitOperations.PopCount(r.Mask));
            Assert.Equal(52, placed);
            Assert.Equal(4, replies.Count);
            Assert.All(replies, r => Assert.Equal(EditReply.Accepted, r.FirstRefusal));
            Assert.All(replies, r => Assert.Equal(0x1FFFu, r.Mask));
        }

        /// <summary>
        /// Подделанный кадр в «копать: начало» (3.7: время копания — по кадрам сервера). Сырые запросы в обход <see cref="NetClient"/>:
        /// кадр 1 (давно прошёл, кнопка зажата 40 кадров) кредита не даёт — 12 новых кадров мало, 18 хватает; кадр на 10⁶ вперёд —
        /// копание не начинается за 60 кадров с зажатой кнопкой. Линия 33 тайла через шов — «нельзя» (проверка П по 0-028, З2).
        /// </summary>
        [Fact]
        public void D07_S3_7_ServerChecks_ForgedDigFrame_PastNoCredit_FutureNever_RawLineOver32()
        {
            WorldModel world = Flat();
            var rock = new TileCoord(301, 200);
            var rock2 = new TileCoord(299, 200);
            world.SetBaseTile(rock, FloorId.Stone, ObjectId.Rock);
            world.SetBaseTile(rock2, FloorId.Stone, ObjectId.Rock);
            using var rig = new EditRig(world, n => TileCenter(300, 200));
            NetClient c = rig.Join();
            var replies = new List<TileReplyMessage>();
            c.EditReplied = replies.Add;
            rig.InputOf = _ => DigHold; // кнопка зажата давно: 40 кадров «копания» до запроса
            rig.Run(20);

            // Прошлое: кадр 1 — засчитывать нечего, нужно 15 новых кадров (7,5 такта).
            RawRequest(c, new TileRequestMessage { RequestId = 900, Op = TileOp.DigStart, Frame = 1, From = rock, To = rock });
            rig.Run(6); // 12 кадров
            Assert.Empty(replies);
            Assert.Equal(ObjectId.Rock, rig.Sim.World.GetObject(rock));
            rig.Run(3);
            Assert.Single(replies);
            Assert.Equal(EditReply.Accepted, replies[0].Reply);
            Assert.Equal(ObjectId.Empty, rig.Sim.World.GetObject(rock));

            // Будущее: кадр далеко впереди — не начинается, не копается.
            replies.Clear();
            RawRequest(c, new TileRequestMessage { RequestId = 901, Op = TileOp.DigStart, Frame = c.Frame + 1_000_000, From = rock2, To = rock2 });
            rig.Run(30);
            Assert.Empty(replies);
            Assert.Equal(ObjectId.Rock, rig.Sim.World.GetObject(rock2));

            // Линия длиннее 32 сырым запросом — «нельзя»; объект не ставится.
            RawRequest(c, new TileRequestMessage { RequestId = 902, Op = TileOp.Place, Frame = c.Frame + 1, From = new TileCoord(1010, 5), To = new TileCoord(18, 5), Obj = ObjectId.StoneWall });
            rig.Run(2);
            Assert.Single(replies);
            Assert.Equal(EditReply.NotAllowed, replies[0].Reply);
            Assert.Equal(ObjectId.Empty, rig.Sim.World.GetObject(new TileCoord(1010, 5)));
        }

        /// <summary>
        /// Ответ на каждый запрос (проверка П по 0-028, З3): персонажа нет (в будущем — смерть до возрождения) — постановка и начало
        /// копания получают «нельзя», идущее копание — «прервано». Предсказанная стена откатывается, вечного призрака у клиента нет.
        /// </summary>
        [Fact]
        public void D07_S3_7_NoCharacter_EveryRequestAnswered_NoEternalPrediction()
        {
            WorldModel world = Flat();
            var rock = new TileCoord(301, 200);
            var rock2 = new TileCoord(299, 200);
            world.SetBaseTile(rock, FloorId.Stone, ObjectId.Rock);
            world.SetBaseTile(rock2, FloorId.Stone, ObjectId.Rock);
            using var rig = new EditRig(world, n => TileCenter(300, 200));
            NetClient c = rig.Join();
            var replies = new List<TileReplyMessage>();
            c.EditReplied = replies.Add;
            rig.Run(10);

            // Копание идёт — персонаж пропал: «прервано».
            ushort dig = c.StartDig(rock);
            rig.InputOf = _ => DigHold;
            rig.Run(3);
            Assert.Empty(replies);
            Assert.True(rig.Sim.RemoveEntity(c.EntityId));
            rig.Run(2);
            Assert.Single(replies);
            Assert.Equal(dig, replies[0].RequestId);
            Assert.Equal(EditReply.Interrupted, replies[0].Reply);
            Assert.False(c.Digging);
            Assert.Equal(ObjectId.Rock, rig.Sim.World.GetObject(rock));

            // Постановка без персонажа: клиент предсказал стену, сервер — «нельзя», предсказание снято.
            rig.InputOf = null;
            replies.Clear();
            var wallTile = new TileCoord(302, 202);
            ushort place = c.Place(wallTile, wallTile, ObjectId.StoneWall, TileCenter(300, 200));
            Assert.NotEqual(0, place);
            Assert.True(c.World!.IsPredicted(wallTile));
            Assert.Equal(1, c.World.PendingPredictions);
            rig.Run(2);
            Assert.Single(replies);
            Assert.Equal(place, replies[0].RequestId);
            Assert.Equal(EditReply.NotAllowed, replies[0].Reply);
            Assert.Equal(0, c.World.PendingPredictions);
            Assert.False(c.World.IsPredicted(wallTile));
            Assert.Equal(ObjectId.Empty, c.World.World.GetObject(wallTile));
            Assert.Equal(ObjectId.Empty, rig.Sim.World.GetObject(wallTile));

            // Начало копания без персонажа — «нельзя»; «конец» после него ответа не ждёт.
            replies.Clear();
            ushort dig2 = c.StartDig(rock2);
            rig.Run(2);
            Assert.Single(replies);
            Assert.Equal(dig2, replies[0].RequestId);
            Assert.Equal(EditReply.NotAllowed, replies[0].Reply);
            Assert.False(c.Digging);
            Assert.Equal(0, c.World.PendingPredictions);
        }

        private static void RawRequest(NetClient c, TileRequestMessage m)
        {
            var buf = new byte[TileRequestMessage.Bytes];
            int n = m.Write(buf);
            c.Transport.Send(LoopbackTransport.ServerPeer, NetChannel.Facts, new ReadOnlySpan<byte>(buf, 0, n));
            c.Transport.Flush();
        }

        /// <summary>«Золотые» байты сообщений 20 и 21 (протокол 7) и отказ испорченных.</summary>
        [Fact]
        public void D07_S2_10_GoldenBytes_TileRequest_TileReply()
        {
            MapShape map = MapShape.World(1024);
            var req = new TileRequestMessage { RequestId = 0x0102, Op = TileOp.Place, Frame = 0x0A0B0C0D, From = new TileCoord(5, 6), To = new TileCoord(7, 8), Obj = ObjectId.WoodWall };
            byte[] rb = NetTests.Encode(b => req.Write(b));
            Assert.Equal("14 02 01 03 0d 0c 0b 0a 05 00 06 00 07 00 08 00 04 00", string.Join(" ", rb.Select(x => x.ToString("x2"))));
            Assert.Equal(TileRequestMessage.Bytes, rb.Length);
            TileRequestMessage back = TileRequestMessage.Read(rb, map);
            Assert.Equal(req.To, back.To);
            Assert.Equal(TileOp.Place, back.Op);
            Assert.Equal(0x0A0B0C0Du, back.Frame);

            var rep = new TileReplyMessage { RequestId = 0x0102, Reply = EditReply.Accepted, FirstRefusal = EditReply.Occupied, Mask = 0x80000001 };
            byte[] pb = NetTests.Encode(b => rep.Write(b));
            Assert.Equal("15 02 01 00 04 01 00 00 80", string.Join(" ", pb.Select(x => x.ToString("x2"))));
            Assert.Equal(0x80000001u, TileReplyMessage.Read(pb).Mask);

            byte[] badOp = (byte[])rb.Clone();
            badOp[3] = 4;
            Assert.Throws<ProtocolException>(() => TileRequestMessage.Read(badOp, map));
            byte[] outside = (byte[])rb.Clone();
            outside[15] = 0x04; // y конца 1032
            Assert.Throws<ProtocolException>(() => TileRequestMessage.Read(outside, map));
            byte[] badMask = (byte[])pb.Clone();
            badMask[5] = 0; badMask[8] = 0; // «принято» без маски
            Assert.Throws<ProtocolException>(() => TileReplyMessage.Read(badMask));
            Assert.Equal(12, NetModule.ProtocolVersion); // 7 — сообщения 20–21 (0-028), 8 — опережение L (0-033), 9 — «имя занято» (0-035), 10 — пауза 22–23 (0-036), 11 — бой (1-001), 12 — прицел и ячейка в кадре ввода (1-002)
        }

        // ---------- Стенд ----------

        /// <summary>
        /// Сервер и клиенты в памяти: такт — два кадра клиентов (60 Гц; ввод — <see cref="InputOf"/>), <c>Poll</c> сервера, такт,
        /// рассылка, <see cref="AfterTick"/>.
        /// </summary>
        internal sealed class EditRig : IDisposable
        {
            private readonly LoopbackTransport _st;
            private long _frame;

            public EditRig(WorldModel world, Func<int, WorldPos> spawn, int maxPlayers = 8)
                : this(new Simulation(world, maxEntities: 32), spawn, null, maxPlayers)
            {
            }

            /// <param name="wrapServer">Обёртка транспорта сервера (счётчик отправленного в тестах замера).</param>
            public EditRig(Simulation sim, Func<int, WorldPos> spawn, Func<ITransport, ITransport>? wrapServer, int maxPlayers = 8)
            {
                Sim = sim;
                _st = LoopbackTransport.CreateServer(maxPlayers + 8);
                Server = new NetServer(wrapServer?.Invoke(_st) ?? _st, Sim, spawn, maxPlayers, NetModule.Version);
            }

            public Simulation Sim { get; private set; }
            public NetServer Server { get; private set; }
            public List<NetClient> Clients { get; } = new List<NetClient>();
            public Func<NetClient, InputFrame>? InputOf { get; set; }
            public Action? BeforeFrame { get; set; }
            public Action<NetClient>? AfterClientPoll { get; set; }
            public Action? AfterTick { get; set; }
            public long NowMicros => _frame * FrameMicros;
            public long Ticks { get; private set; }

            public NetClient Join(NetSimSettings? sim = null)
            {
                ITransport t = LoopbackTransport.ConnectTo(_st);
                if (sim.HasValue)
                {
                    t = new NetSimTransport(t, sim.Value);
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
                        BeforeFrame?.Invoke();
                        foreach (NetClient c in Clients)
                        {
                            c.Poll(NowMicros);
                            AfterClientPoll?.Invoke(c);
                            c.SendFrame(InputOf?.Invoke(c) ?? InputFrame.None);
                        }
                    }

                    Server.Poll(NowMicros);
                    Sim.Step();
                    Server.SendSnapshots();
                    Sim.RefillPools();
                    Ticks++;
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
