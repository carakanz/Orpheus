using System;
using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net.Protocol;

namespace Orpheus.Net.Session
{
    /// <summary>
    /// Постановка построек-ячеек (E2-12, протокол 26; записка <c>work/design/half_tile_walls.md</c>): стена и забор — линия
    /// ячеек 0,5 × 0,5 от ячейки начала до ячейки конца (до 32 ячеек, <see cref="BuildCells.Line"/>), ячейки одного тайла —
    /// одна правка (маска := прежняя | добавленные, прочность и владелец тайла сохраняются); дверь — пара ячеек тайла начала
    /// (<see cref="BuildCells.DoorMaskFor"/>). Ответ — бит на ячейку линии (у двери — бит 0).
    /// </summary>
    public sealed partial class NetServer
    {
        // Число ячеек правки − 1 (0…3) в метке события: ответ ставит столько бит подряд с номера первой ячейки.
        private const int TagCountShift = 22;

        private readonly CellGroup[] _cellGroups = new CellGroup[EditRules.MaxLineTiles];

        private void PlaceCells(Player p, EditState s, WorldPos pos, in TileRequestMessage m)
        {
            MapShape shape = _sim.Shape;
            int n;
            if (ObjectId.IdOf(m.Obj) == ObjectId.Door)
            {
                byte door = BuildCells.DoorMaskFor(_sim.World, m.From, m.Quad & 3);
                _cellGroups[0] = new CellGroup(Torus.Wrap(shape, m.From), door, 0, 1);
                n = 1;
            }
            else
            {
                n = BuildCells.Line(shape, BuildCells.CellOf(m.From, m.Quad & 3), BuildCells.CellOf(m.To, (m.Quad >> 2) & 3), _cellGroups);
            }

            if (n < 0)
            {
                Reply(p, m.RequestId, EditReply.NotAllowed, EditReply.NotAllowed, 0);
                return;
            }

            ushort integrity = BuildDefs.TryGet(m.Obj, out BuildDef def) ? def.MaxIntegrity : BuildRecord.MaxIntegrity;
            EditReply first = EditReply.Accepted;
            int queued = 0;
            for (int i = 0; i < n; i++)
            {
                CellGroup g = _cellGroups[i];
                TileCoord t = g.Tile;
                EditReply check = BuildCells.CheckPlace(_sim.World, pos, t, m.Obj, g.Mask, out byte mask, out _);
                if (check == EditReply.Accepted && _hasDummy && EditRules.EnemyTooClose(shape, _dummy.Position, t))
                {
                    check = EditReply.EnemyNear;
                }

                int tag = (m.RequestId << TagLineBits) | g.First | ((g.Count - 1) << TagCountShift);
                byte floor = _sim.World.GetFloor(t);
                BuildRecord rec = _sim.World.GetObject(t) == m.Obj && _sim.World.TryGetBuild(t, out BuildRecord old)
                    ? old.WithCells(mask)
                    : new BuildRecord((ushort)p.EntityId, integrity, 0, floor, 0, mask);
                if (check == EditReply.Accepted && !_sim.RequestPlayerEdit(t, floor, m.Obj, true, rec, p.EntityId, tag, m.Cell))
                {
                    check = QueueRefusal(p.EntityId);
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
