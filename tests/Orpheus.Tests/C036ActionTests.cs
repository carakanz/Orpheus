using System;
using System.Collections.Generic;
using System.Linq;
using Orpheus.Core.Combat;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net;
using Orpheus.Net.Protocol;
using Orpheus.Net.Session;
using Xunit;

namespace Orpheus.Tests
{
    /// <summary>
    /// C-036 (протокол 37): поле «действие» в снимке — 8 бит (инструмент 2 бита: кирка/лук + прогресс 6 бит).
    /// Кодек: упаковка/клампинг. По сети: наблюдатель видит чужой взмах кирки шагами прогресса (квант 16 кадров)
    /// и натяжение лука (сила), в покое — 0; дельта с битом Action — только при смене действия.
    /// </summary>
    public class C036ActionTests
    {
        private const int U = Torus.SubTilesPerTile;

        private static WorldModel Flat(int size = 1024) => FlatWorldGenerator.Instance.Generate(MapShape.World(size), 1);

        private static WorldPos Center(int x, int y) => EditTests.TileCenter(x, y);

        private static readonly InputFrame DigPick = new InputFrame(Input8.None, InputButtons.Dig, 0, 0); // ячейка 0 — кирка

        private static void Give(EditTests.EditRig rig, NetClient c, int cell, byte item)
        {
            var inv = rig.Sim.GetEntity(c.EntityId).Inv;
            inv.SetSlotAt(cell, item);
            Assert.True(rig.Sim.SetInventory(c.EntityId, inv));
        }

        // ---------- Кодек ----------

        [Fact]
        public void C036_Codec_Action_EncodeDecode_Clamps()
        {
            Assert.Equal(8, SnapshotLayout.ActionBits);
            Assert.Equal(64, (int)DeltaFields.Action); // бит 64 — маске хватило 7 бит (MaskBits)
            Assert.Equal(7, SnapshotDeltaMessage.MaskBits);

            for (int step = 0; step <= SnapshotLayout.ActionProgressMask; step++)
            {
                foreach (byte tool in new byte[] { SnapshotLayout.ActionToolPick, SnapshotLayout.ActionToolBow })
                {
                    byte a = SnapshotLayout.EncodeAction(tool, step);
                    Assert.Equal(tool, SnapshotLayout.ActionToolOf(a));
                    Assert.Equal(step, SnapshotLayout.ActionProgressOf(a));
                }
            }

            Assert.Equal(SnapshotLayout.ActionProgressMask,
                SnapshotLayout.ActionProgressOf(SnapshotLayout.EncodeAction(SnapshotLayout.ActionToolPick, 100))); // потолок
            Assert.Equal(0, SnapshotLayout.ActionProgressOf(SnapshotLayout.EncodeAction(SnapshotLayout.ActionToolBow, -5))); // пол
            Assert.Equal(0, SnapshotLayout.EncodeAction(0, 0)); // покой
            Assert.Equal((byte)(SnapshotLayout.ActionToolPick | 40 << SnapshotLayout.ActionProgressShift),
                SnapshotLayout.EncodeAction(SnapshotLayout.ActionToolPick, 40));
        }

        // ---------- Сеть: кирка ----------

        /// <summary>
        /// Наблюдатель видит чужой взмах кирки: шаги прогресса растут по кванту (окно 24 кадра — шаги 1, 2),
        /// вне копания у наблюдателя 0; инструмент в сегменте копки — только кирка.
        /// </summary>
        [Fact]
        public void C036_Net_DigPick_ObserverSeesSteps_RestingZero()
        {
            WorldModel world = Flat();
            var rock = new TileCoord(301, 200);
            world.SetBaseTile(rock, FloorId.Stone, ObjectId.Rock);
            using var rig = new EditTests.EditRig(world, n => n == 0 ? Center(300, 200) : Center(296, 200));
            NetClient a = rig.Join();
            NetClient b = rig.Join();
            rig.Run(5);
            Assert.Equal(NetClientState.InGame, b.State);

            var seen = new List<byte>();
            b.SnapshotApplied = s =>
            {
                if (s.TryFind(a.EntityId, out SnapshotEntity e))
                    seen.Add(e.Action);
            };
            Assert.True(b.Latest.TryFind(a.EntityId, out SnapshotEntity idle));
            Assert.Equal(0, idle.Action); // в покое действия нет

            Give(rig, a, 0, Items.StonePickaxe);
            var replies = new List<TileReplyMessage>();
            a.EditReplied = replies.Add;
            rig.InputOf = k => k == a ? DigPick : InputFrame.None;
            ushort req = a.StartDig(rock, rig.Sim.GetEntity(a.EntityId).Mover.Position);
            Assert.NotEqual((ushort)0, req);
            rig.Run(14); // взмах 24 кадра = 12 тактов: шаги 1 (кадры 1…16), 2 (17…24), удар
            Assert.Contains(replies, r => r.RequestId == req);

            Assert.DoesNotContain(seen, x => SnapshotLayout.ActionToolOf(x) == 3); // инструмент 3 не кодируется
            var picks = seen.Where(x => SnapshotLayout.ActionToolOf(x) == SnapshotLayout.ActionToolPick)
                .Select(SnapshotLayout.ActionProgressOf).ToList();
            Assert.Contains(1, picks); // шаг 1 (окно 16 кадров)
            Assert.Contains(2, picks); // шаг 2 (кадры 17…24)
            int last = -1; // шаги внутри сегмента копки не убывают
            foreach (byte x in seen)
            {
                if (SnapshotLayout.ActionToolOf(x) == SnapshotLayout.ActionToolPick)
                {
                    int step = SnapshotLayout.ActionProgressOf(x);
                    Assert.True(step >= last, $"шаг прогресса упал: {step} после {last}");
                    last = step;
                }
                else
                {
                    Assert.Equal(0, x); // вне копки — покой (сегмент кончился)
                    last = -1;
                }
            }

            Assert.Equal(0, seen[seen.Count - 1]); // после удара персонаж стоит — наблюдатель видит 0
        }

        // ---------- Сеть: лук ----------

        /// <summary>
        /// Наблюдатель видит натяжение лука: сила (прогресс) растёт до полного натяжения (клампинг 63), после
        /// выстрела и отхода — 0 (отдачу клиент рисует по сообщению снаряда, E3-2).
        /// </summary>
        [Fact]
        public void C036_Net_BowDraw_ObserverSeesPower_ThenZero()
        {
            WorldModel world = Flat();
            using var rig = new EditTests.EditRig(world, n => n == 0 ? Center(300, 200) : Center(296, 200));
            NetClient a = rig.Join();
            NetClient b = rig.Join();
            rig.Run(10);
            Assert.Equal(NetClientState.InGame, b.State);

            var seen = new List<byte>();
            b.SnapshotApplied = s =>
            {
                if (s.TryFind(a.EntityId, out SnapshotEntity e))
                    seen.Add(e.Action);
            };

            int frame = 0;
            rig.InputOf = k =>
            {
                if (k != a)
                    return InputFrame.None;
                frame++;
                ushort buttons = frame <= 36 ? InputButtons.Attack : (ushort)0; // зажатая ЛКМ — непрерывная стрельба (C-031): отпускаем на полном натяжении
                return new InputFrame(Input8.None, buttons, 0, 4); // ячейка 4 — простой лук (стартовый набор)
            };
            rig.Run(30); // натяжение до полного (36 кадров = 18 тактов), отпускание, выстрел, отход 12 кадров

            Assert.Equal(1, a.Projectiles.ShotsSeen); // выстрел состоялся
            Assert.DoesNotContain(seen, x => SnapshotLayout.ActionToolOf(x) == SnapshotLayout.ActionToolPick); // кирки не было
            int first = seen.FindIndex(x => SnapshotLayout.ActionToolOf(x) == SnapshotLayout.ActionToolBow);
            Assert.True(first >= 0, "наблюдатель не видел натяжения лука");
            int lastBow = seen.FindLastIndex(x => SnapshotLayout.ActionToolOf(x) == SnapshotLayout.ActionToolBow);
            Assert.All(seen.Take(first), x => Assert.Equal(0, x)); // до натяжения — покой
            Assert.All(seen.Skip(lastBow + 1), x => Assert.Equal(0, x)); // после выстрела и отхода — покой (отдача — по сообщению снаряда)
            var bows = seen.GetRange(first, lastBow - first + 1).Select(SnapshotLayout.ActionProgressOf).ToList();
            for (int i = 1; i < bows.Count; i++)
                Assert.True(bows[i] >= bows[i - 1], $"сила натяжения упала: {bows[i]} после {bows[i - 1]}");
            Assert.InRange(bows[bows.Count - 1], 56, SnapshotLayout.ActionProgressMask); // почти полное натяжение (на полном выстрел
            // срабатывает в тот же кадр — снимок успевает увидеть только ~58; клампинг 63 проверяет кодек-тест)
            Assert.Equal(0, seen[seen.Count - 1]);
        }
    }
}
