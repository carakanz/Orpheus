using System;
using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net.Protocol;

namespace Orpheus.Net.Session
{
    /// <summary>
    /// Постановка по плану заявки (E2-12 → E2-15; записки <c>work/design/half_tile_walls.md</c>, <c>half_tile_all.md</c>):
    /// <see cref="BuildPlanner.Plan"/> — тот же план, что у предсказания клиента. Стена, забор, пол — линия ячеек 0,5 (ячейки
    /// тайла — одна правка), дверь — пара ячеек, мебель — одна правка тайла-якоря со сдвигом, ковёр и шипы, настил — линия
    /// тайлов. Ответ — бит на клетку плана (у мебели и двери — бит 0).
    /// </summary>
    public sealed partial class NetServer
    {
        // Число клеток правки − 1 (0…3) в метке события: ответ ставит столько бит подряд с номера первой клетки.
        private const int TagCountShift = 22;

        private readonly PlannedEdit[] _plan = new PlannedEdit[EditRules.MaxLineTiles];

        private void PlacePlan(Player p, EditState s, WorldPos pos, in TileRequestMessage m)
        {
            MapShape shape = _sim.Shape;
            int n = BuildPlanner.Plan(_sim.World, pos, m.Obj, m.From, m.To, m.Quad, (ushort)p.EntityId, _plan);
            if (n < 0)
            {
                Reply(p, m.RequestId, EditReply.NotAllowed, EditReply.NotAllowed, 0);
                return;
            }

            bool clearance = EditRules.NeedsClearance(m.Obj);
            byte cell = m.FromMenu ? Simulation.CraftCell : m.Cell; // E2-16: из меню — любая ячейка сумки, недостающее изготовить
            EditReply first = EditReply.Accepted;
            int queued = 0;
            for (int i = 0; i < n; i++)
            {
                PlannedEdit e = _plan[i];
                EditReply check = e.Check;
                if (check == EditReply.Accepted && clearance && !e.FloorOnly && _hasDummy && EditRules.EnemyTooClose(shape, _dummy.Position, e.Tile))
                {
                    check = EditReply.EnemyNear;
                }

                int tag = (m.RequestId << TagLineBits) | e.First | ((e.Count - 1) << TagCountShift);
                if (check == EditReply.Accepted
                    && !_sim.RequestPlayerEdit(e.Tile, e.Floor, e.Obj, true, e.Record, p.EntityId, tag, cell, e.FloorOnly,
                        e.Moves, e.MoveTile, e.MoveObj, e.MoveRecord)) // C-011: перенос якоря мебели — вместе со стеной
                {
                    check = QueueRefusal(p.EntityId); // очередь такта полна или персонаж мёртв (C-004)
                }

                if (check != EditReply.Accepted)
                {
                    if (first == EditReply.Accepted)
                    {
                        first = check;
                    }

                    continue;
                }

                queued++;
            }

            if (queued == 0)
            {
                Reply(p, m.RequestId, first, first, 0);
                return;
            }

            int k = s.PendingCount++;
            s.PendingId[k] = m.RequestId;
            s.PendingMask[k] = 0;
            s.PendingFirst[k] = first;
            s.PendingLeft[k] = queued;
        }
    }
}
