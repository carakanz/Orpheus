using System;
using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net.Protocol;

namespace Orpheus.Net.Session
{
    /// <summary>
    /// Предсказание постановки (E2-12 → E2-15): тот же план, что у сервера (<see cref="BuildPlanner.Plan"/>) — стена, забор, пол
    /// ячейками 0,5, дверь парой ячеек, мебель тайлом-якорем со сдвигом, прочее тайлами; по предмету из ячейки панели на
    /// правку (стена, забор, пол — на добавленную ячейку). Ковёр и шипы (вне таблицы) — без предмета.
    /// </summary>
    public sealed partial class NetClient
    {
        private readonly PlannedEdit[] _plan = new PlannedEdit[EditRules.MaxLineTiles];

        private ushort PlacePlan(TileCoord from, TileCoord to, ushort obj, WorldPos self, byte cell, byte quad)
        {
            if (State != NetClientState.InGame || World == null || cell >= Inventory.Slots)
            {
                return 0;
            }

            MapShape shape = World.Shape;
            int n = BuildPlanner.Plan(World.World, self, obj, from, to, quad, (ushort)EntityId, _plan);
            if (n < 0)
            {
                return 0;
            }

            ushort id = NextRequest();
            bool free = !BuildDefs.TryGet(obj, out BuildDef def); // ковёр, шипы — вне таблицы: без предмета (как у сервера)
            var inv = Inv; // Inventory — структура: правим копию и пишем обратно (1-010)
            for (int i = 0; i < n; i++)
            {
                PlannedEdit e = _plan[i];
                if (e.Check != EditReply.Accepted)
                {
                    continue;
                }

                int units = e.Units;
                if (!free && (inv.ItemAt(cell) != def.Item || inv.TakeFromThenAny(cell, def.Item, units) != units))
                {
                    break; // предметы кончились посреди линии (`05` 1.4): остаток не предсказываем
                }

                if (!World.Predict(id, e.First, e.Tile, new TileEdit(0, e.Floor, e.Obj, e.Record)))
                {
                    if (!free)
                    {
                        inv.SetCell(cell, def.Item, inv.CountIn(cell) + units); // чанка нет — предмет на место
                    }

                    continue;
                }

                if (!free)
                {
                    _spends.Add(new SpendEntry { Request = id, LineIndex = e.First, Item = def.Item, Cell = cell, InvVersion = InvVersion, Count = (byte)units });
                }
            }

            Inv = inv;
            SendRequest(new TileRequestMessage
            {
                RequestId = id, Op = TileOp.Place, Frame = _input.LastFrame + 1, From = Torus.Wrap(shape, from), To = Torus.Wrap(shape, to), Obj = obj,
                Cell = cell, Quad = quad,
            });
            return id;
        }
    }
}
