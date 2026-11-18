using System;
using System.Collections.Generic;
using Orpheus.Client.Anim;
using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net.Protocol;
using Orpheus.Net.Session;
using Xunit;

namespace Orpheus.Tests
{
    /// <summary>
    /// C-030: кровать двуспальная 2 × 2 тайла; «лечь в кровать» — F (или ПКМ) → заявка 33 → «принято» → клиент кладёт
    /// персонажа (<see cref="BedRest"/>, поза <see cref="RestAnim"/>), точка возрождения — якорь кровати.
    /// </summary>
    public sealed class C030BedTests
    {
        private const int U = Torus.SubTilesPerTile;

        private static WorldModel Flat(ulong seed = 3030) => FlatWorldGenerator.Instance.Generate(MapShape.World(1024), seed);

        private static WorldPos At(int tx, int ty) => new WorldPos(tx * U + U / 2, ty * U + U / 2);

        private static ushort Bed(int rot) => BuildLayers.Turned(ObjectId.Bed, rot);

        private static BuildRecord BedRecord => new BuildRecord(1, 40, (byte)BuildFlags.Wide, FloorId.Grass, 0);

        /// <summary>
        /// Весь путь клавиши по сети: кровать 2 × 2 (якорь 302/198); персонаж южнее; F без курсора (<see cref="ObjectUse.Find"/>)
        /// находит кровать по ближнему тайлу (хвосту) и отдаёт якорь; заявка 33 — «принято»; точка возрождения — якорь;
        /// ответ кладёт персонажа на эту кровать с её поворотом. Отказ (далеко) — не ложится.
        /// </summary>
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public void C030_F_Near_Bed_Sets_Respawn_And_Lies_Down(int rot)
        {
            using var rig = new EditTests.EditRig(Flat(), _ => EditTests.TileCenter(301, 201));
            NetClient c = rig.Join();
            rig.Run(5);
            var bed = new TileCoord(302, 198);
            Assert.True(rig.Sim.RequestEdit(bed, FloorId.Grass, Bed(rot), BedRecord));
            rig.Run(4);
            WorldModel w = c.World!.World;
            Assert.Equal(Bed(rot), w.GetObject(bed)); // мир клиента — тот же

            WorldPos self = rig.Sim.GetEntity(c.EntityId).Mover.Position;
            Assert.True(ObjectUse.Find(w, self, null, out TileCoord t, out UseKind kind));
            Assert.Equal((bed, UseKind.Bed), (t, kind));

            var uses = new List<UseReplyMessage>();
            var rest = new BedRest();
            c.UseReplied = m =>
            {
                uses.Add(m);
                if (m.Reply == EditReply.Accepted && ObjectUse.KindOf(m.Obj) == UseKind.Bed)
                {
                    rest.Begin(w, m.Tile);
                }
            };
            Assert.True(c.RequestUse(new TileCoord(303, 199))); // по юго-восточному тайлу кровати — тоже она
            rig.Run(3);
            Assert.Single(uses);
            Assert.Equal(EditReply.Accepted, uses[0].Reply);
            Assert.True(rig.Sim.GetEntity(c.EntityId).HasBed);
            Assert.Equal(bed, rig.Sim.GetEntity(c.EntityId).Bed);
            Assert.True(rest.Active);
            Assert.Equal(bed, rest.Bed);
            Assert.Equal(rot, rest.Rotation);
            Assert.Equal(1, rest.Lay);
        }

        /// <summary>Встать: движение / F / клик, смерть, снос кровати; лежит — время идёт. Не кровать — не ложится.</summary>
        [Fact]
        public void C030_BedRest_Wakes_On_Move_Death_And_Demolish()
        {
            var sim = new Simulation(Flat());
            var bed = new TileCoord(100, 100);
            Assert.True(sim.RequestEdit(bed, FloorId.Grass, Bed(1), BedRecord));
            sim.Step();
            var rest = new BedRest();

            Assert.False(rest.Begin(sim.World, new TileCoord(110, 100))); // пусто
            Assert.False(rest.Active);

            Assert.True(rest.Begin(sim.World, new TileCoord(101, 101))); // хвост → якорь
            Assert.Equal(bed, rest.Bed);
            Assert.Equal(1, rest.Rotation);
            Assert.True(rest.Update(sim.World, alive: true, wake: false, 0.5f));
            Assert.True(rest.Update(sim.World, alive: true, wake: false, 0.25f));
            Assert.Equal(0.75f, rest.Age, 3);
            Assert.False(rest.Update(sim.World, alive: true, wake: true, 0.1f));
            Assert.False(rest.Active);

            Assert.True(rest.Begin(sim.World, bed));
            Assert.False(rest.Update(sim.World, alive: false, wake: false, 0.1f));

            Assert.True(rest.Begin(sim.World, bed));
            Assert.True(sim.RequestEdit(bed, FloorId.Grass, ObjectId.Empty));
            sim.Step();
            Assert.False(rest.Update(sim.World, alive: true, wake: false, 0.1f));
            Assert.Equal(3, rest.Lay);
        }

        /// <summary>
        /// Поза: макушка — у изголовья (по направлению поворота), всё тело — внутри кровати 2 × 2 (центр ± 1 т), на одной её
        /// половине; поворот спрайта ведёт «верх» спрайта к изголовью; одеяло — со второй половины «ложится»; до этого — нет.
        /// </summary>
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public void C030_RestPose_Head_At_Headboard_Inside_Bed(int rot)
        {
            RestAnim.HeadDir(rot, out float hx, out float hy);
            RestAnim.Spot(rot, out float sx, out float sy);
            float lift = RestAnim.MattressLift;
            // Макушка = опора + «верх» спрайта, повёрнутый на BodyDeg, × длина тела.
            double a = RestAnim.BodyDeg(rot) * Math.PI / 180.0;
            float ux = (float)-Math.Sin(a), uy = (float)Math.Cos(a);
            Assert.Equal(hx, ux, 3);
            Assert.Equal(hy, uy, 3);
            float headX = sx + ux * RestAnim.BodyLength, headY = sy - lift + uy * RestAnim.BodyLength;
            float feetX = sx, feetY = sy - lift;
            Assert.Equal(RestAnim.HeadFrom(rot), headX * hx + headY * hy, 3); // макушка у изголовья
            Assert.True(RestAnim.HeadFrom(rot) >= 0.5f);                        // в изголовной половине
            foreach (float v in new[] { headX, headY, feetX, feetY })
            {
                Assert.InRange(v, -1f, 1f); // в пределах кровати 2 × 2
            }

            float side = feetX * -hy + feetY * hx; // поперёк — своя половина
            Assert.InRange(side, 0.2f, 0.8f);

            Pose p = RestAnim.Apply(Pose.Identity, 0.05f, rot, 2f, 3f);
            Assert.Equal(0f, p.Cover);
            p = RestAnim.Apply(Pose.Identity, 1f, rot, 2f, 3f);
            Assert.Equal(RestAnim.Cover, p.Cover);
            Assert.Equal(2f + sx, p.OffsetX, 3);
            Assert.Equal(3f + sy - ActionAnim.SpriteFootY, p.OffsetY, 3); // опора спрайта — на SpriteFootY ниже корня
            Assert.Equal(RestAnim.BodyDeg(rot), p.RotationDeg, 3);
            Assert.True(p.HideHand);
            Assert.True(RestAnim.Dim(1f) > 0.3f && RestAnim.Dim(0f) == 0f);
        }

        /// <summary>Двуспальная: двое ложатся в одну кровать — у каждого точка возрождения на ней.</summary>
        [Fact]
        public void C030_Two_Players_One_Double_Bed()
        {
            var sim = new Simulation(Flat());
            var bed = new TileCoord(100, 100);
            Assert.True(sim.RequestEdit(bed, FloorId.Grass, Bed(0), BedRecord));
            sim.Step();
            int a = sim.AddEntity(At(99, 102));
            int b = sim.AddEntity(At(102, 102));
            Assert.Equal(EditReply.Accepted, sim.RequestSetBed(a, new TileCoord(100, 101)));
            Assert.Equal(EditReply.Accepted, sim.RequestSetBed(b, new TileCoord(101, 101)));
            Assert.Equal(bed, sim.GetEntity(a).Bed);
            Assert.Equal(bed, sim.GetEntity(b).Bed);
        }
    }
}
