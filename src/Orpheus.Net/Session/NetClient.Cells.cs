using System;
using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net.Protocol;

namespace Orpheus.Net.Session
{
    /// <summary>
    /// Предсказание постановки построек-ячеек (E2-12, протокол 26): те же функции ядра, что у сервера
    /// (<see cref="BuildCells.Line"/>, <see cref="BuildCells.DoorMaskFor"/>, <see cref="BuildCells.CheckPlace"/>) — тайл
    /// линии становится «маска := прежняя | добавленные», по предмету из ячейки панели на добавленную ячейку (дверь — один).
    /// </summary>
    public sealed partial class NetClient
    {
        private readonly CellGroup[] _cellGroups = new CellGroup[EditRules.MaxLineTiles];

        private ushort PlaceCells(TileCoord from, TileCoord to, ushort obj, WorldPos self, byte cell, byte quad)
        {
            if (State != NetClientState.InGame || World == null || cell >= Inventory.Slots)
            {
                return 0;
            }

            MapShape shape = World.Shape;
            bool door = ObjectId.IdOf(obj) == ObjectId.Door;
            int n;
            if (door)
            {
                _cellGroups[0] = new CellGroup(Torus.Wrap(shape, from), BuildCells.DoorMaskFor(World.World, from, quad & 3), 0, 1);
                n = 1;
            }
            else
            {
                n = BuildCells.Line(shape, BuildCells.CellOf(from, quad & 3), BuildCells.CellOf(to, (quad >> 2) & 3), _cellGroups);
            }

            if (n < 0)
            {
                return 0;
            }

            ushort id = NextRequest();
            BuildDefs.TryGet(obj, out BuildDef def);
            var inv = Inv;
            for (int i = 0; i < n; i++)
            {
                CellGroup g = _cellGroups[i];
                TileCoord t = g.Tile;
                if (BuildCells.CheckPlace(World.World, self, t, obj, g.Mask, out byte mask, out byte add) != EditReply.Accepted)
                {
                    continue;
                }

                int units = door ? 1 : BuildCells.Count(add);
                if (inv.ItemAt(cell) != def.Item || inv.CountIn(cell) < units || inv.TakeFrom(cell, units) != units)
                {
                    break; // предметы кончились посреди линии (`05` 1.4): остаток не предсказываем
                }

                byte floor = World.World.GetFloor(t);
                BuildRecord rec = World.World.GetObject(t) == obj && World.World.TryGetBuild(t, out BuildRecord old)
                    ? old.WithCells(mask)
                    : new BuildRecord((ushort)EntityId, def.MaxIntegrity, 0, floor, 0, mask);
                if (!World.Predict(id, g.First, t, new TileEdit(0, floor, obj, rec)))
                {
                    inv.SetCell(cell, def.Item, inv.CountIn(cell) + units); // чанка нет — предмет на место
                    continue;
                }

                _spends.Add(new SpendEntry { Request = id, LineIndex = g.First, Item = def.Item, Cell = cell, InvVersion = InvVersion, Count = (byte)units });
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
