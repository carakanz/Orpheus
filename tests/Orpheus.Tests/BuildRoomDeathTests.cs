using System;
using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net.Protocol;
using Orpheus.Net.Session;
using Xunit;
using Xunit.Abstractions;

namespace Orpheus.Tests
{
    /// <summary>
    /// C-004: «урон за дверью» в тяжёлом <c>D08_3_1_BuildRoom_Udp…</c> оказался не ударом сквозь дверь. Трасса сервера:
    /// строителя убили на стройке (такт 1292), труп достроил стены и поставил дверь (правки от мёртвого проходили),
    /// бот смерть не заметил (в снимке мёртвый есть, здоровье 0), счёл дверь закрытой «изнутри» и после возрождения
    /// ждал рассвет в чистом поле у точки появления мира — там его и били. Здесь — правило ядра и сценарий бота.
    /// </summary>
    public class BuildRoomDeathTests
    {
        private const int U = Torus.SubTilesPerTile;
        private const int Size = 1024;
        private const int RoomX = 498, RoomY = 512; // комната в 14 тайлах от точки возрождения мира (512, 512)

        private readonly ITestOutputHelper _out;

        public BuildRoomDeathTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private static WorldPos At(int tx, int ty) => new WorldPos(tx * U + U / 2, ty * U + U / 2);

        /// <summary>Мёртвый не строит: запрос правки от трупа — отказ, от живого и от процесса мира — проходит.</summary>
        [Fact]
        public void C004_Dead_Character_Edit_Refused()
        {
            var sim = new Simulation(FlatWorldGenerator.Instance.Generate(MapShape.World(Size), 1321));
            int me = sim.AddEntity(At(20, 20));
            var wall = new TileCoord(20, 22);
            BuildRecord rec = BuildRecord.OfPlayer((ushort)me, FloorId.Grass);
            sim.Step();

            Assert.True(sim.ApplyStrike(me, 0, 500) > 0);
            Assert.False(sim.GetEntity(me).Alive);
            Assert.False(sim.RequestPlayerEdit(wall, FloorId.Grass, ObjectId.StoneWall, true, rec, me, 1)); // труп
            sim.Step();
            Assert.Equal(ObjectId.Empty, sim.World.GetObject(wall));

            Assert.True(sim.RequestPlayerEdit(wall, FloorId.Grass, ObjectId.StoneWall, true, rec, 0, 2)); // процесс мира

            for (int i = 0; i < 400 && !sim.GetEntity(me).Alive; i++)
            {
                sim.Step(); // возрождение через 5 с
            }

            Assert.True(sim.GetEntity(me).Alive);
            Assert.True(sim.RequestPlayerEdit(new TileCoord(30, 30), FloorId.Grass, ObjectId.StoneWall, true, rec, me, 3)); // живой
        }

        /// <summary>
        /// Бот <c>build_room</c> убит в комнате за закрытой дверью: смерть посчитана (раньше «смертей 0»), «дверь закрыта»
        /// снята, пока бот снаружи; после возрождения у точки появления мира он возвращается в обход стен, входит
        /// и закрывает дверь — «закрыта» снова только изнутри.
        /// </summary>
        [Fact]
        public void C004_BuildRoomBot_Killed_Death_Counted_Returns_Inside_Before_Closed()
        {
            MapShape shape = MapShape.World(Size);
            WorldModel w = FlatWorldGenerator.Instance.Generate(shape, 1);
            for (int dy = -10; dy <= 10; dy++)
            {
                for (int dx = -10; dx <= 10; dx++)
                {
                    int r = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    var t = Torus.Wrap(shape, new TileCoord(RoomX + dx, RoomY + dy));
                    if (r == 6 || r == 7)
                    {
                        w.SetBaseTile(t, FloorId.Grass, ObjectId.Rock);
                    }
                    else if (r == 10 && (dx + 10) % 3 == 0 && (dy + 10) % 3 == 0)
                    {
                        w.SetBaseTile(t, FloorId.Grass, ObjectId.Tree);
                    }
                }
            }

            var sim = new Simulation(w, maxEntities: 64);
            using var rig = new EditTests.EditRig(sim, n => At(RoomX, RoomY), null);
            NetClient client = rig.Join();
            rig.Run(3);
            var bot = new BuildRoomBot(shape, At(RoomX, RoomY));
            client.EditReplied = bot.OnReply;
            rig.InputOf = v => v == client && v.HasSnapshot && v.Latest.TryFind(v.EntityId, out SnapshotEntity se)
                ? bot.Next(v, se.Position, rig.NowMicros)
                : InputFrame.None;

            bool InRoom()
            {
                Int2 d = Torus.Delta(shape, new TileCoord(RoomX, RoomY), Torus.TileOf(shape, sim.GetEntity(client.EntityId).Mover.Position));
                return Math.Max(Math.Abs(d.X), Math.Abs(d.Y)) <= 1;
            }

            for (int i = 0; i < 14000 && !bot.DoorClosed; i += 25)
            {
                rig.Run(25);
            }

            Assert.True(bot.DoorClosed, $"комната не построена: этап {bot.Stage}");
            Assert.True(InRoom());
            Assert.Equal(0, bot.Deaths);

            Assert.True(sim.ApplyStrike(client.EntityId, 0, 500) > 0); // убит в комнате
            rig.Run(30);
            Assert.Equal(1, bot.Deaths);

            for (int i = 0; i < 400 && !sim.GetEntity(client.EntityId).Alive; i++)
            {
                rig.Run(1);
            }

            rig.Run(10);
            Assert.False(InRoom()); // возродился у точки появления мира
            Assert.False(bot.DoorClosed, "бот снаружи — «дверь закрыта и бот внутри» неверно");

            int back = 0;
            for (; back < 3000 && !bot.DoorClosed; back += 5)
            {
                rig.Run(5);
            }

            _out.WriteLine($"возврат: {back} тактов, этап {bot.Stage}, смертей {bot.Deaths}, в комнате {InRoom()}, отказ: {bot.FailReason}");
            Assert.True(bot.DoorClosed, $"не вернулся в комнату за {back} тактов: этап {bot.Stage}");
            Assert.True(InRoom(), "«закрыта» засчитана снаружи комнаты");
            Assert.Equal(1, bot.Deaths);
        }
    }
}
