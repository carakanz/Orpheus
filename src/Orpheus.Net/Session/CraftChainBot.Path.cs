using System;
using System.Collections.Generic;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net.Protocol;

namespace Orpheus.Net.Session
{
    /// <summary>Бот <c>craft_chain</c> (E2-3): поиск пути по своему миру клиента, разведка по клеткам, проверка «верстак не запрёт».</summary>
    public sealed partial class CraftChainBot
    {
        /// <summary>
        /// Поиск в ширину от своего тайла: ближайший по пути тайл, у которого сосед по стороне — цель. Нашёл — путь и цель.
        /// Режим <paramref name="explore"/> (цели в окне нет): путь к ближайшему достижимому тайлу в ещё не виденной клетке
        /// 16 × 16 (виденные — в 48 тайлах от мест, где бот уже искал); все видены — к самому дальнему от старта.
        /// </summary>
        private bool Plan(NetClient client, ClientWorld world, TileCoord me, Func<ushort, bool> isTarget, bool allowDig, bool explore = false)
        {
            _gen++;
            int head = 0, tail = 0;
            int startIdx = R * Side + R;
            _seen[startIdx] = _gen;
            _prev[startIdx] = -1;
            _queue[tail++] = startIdx;
            int far = -1, farScore = -1, fresh = -1;
            if (explore)
            {
                for (int cy = -3; cy <= 3; cy++)
                {
                    for (int cx = -3; cx <= 3; cx++)
                    {
                        _seenCells.Add(CellKey(Torus.Wrap(_shape, new TileCoord(me.X + cx * 16, me.Y + cy * 16))));
                    }
                }
            }

            while (head < tail)
            {
                int idx = _queue[head++];
                int cx = idx % Side - R, cy = idx / Side - R;
                var here = Torus.Wrap(_shape, new TileCoord(me.X + cx, me.Y + cy));
                for (int k = 0; k < 4; k++)
                {
                    var n = Torus.Wrap(_shape, new TileCoord(here.X + Dx4[k], here.Y + Dy4[k]));
                    ushort obj = world.World.GetObject(n);
                    if (!explore && obj != ObjectId.Empty && !ObjectId.IsPart(obj) && isTarget(ObjectId.IdOf(obj)) && !Skipped(n)
                        && !world.IsPredicted(n) && ToolSlotFor(client, obj) != NoTool)
                    {
                        BuildPath(me, idx);
                        _target = n;
                        _hasTarget = true;
                        _pathDigs = allowDig;
                        DigThroughs += allowDig ? 1 : 0;
                        return true;
                    }
                }

                if (explore)
                {
                    if (fresh < 0 && !_seenCells.Contains(CellKey(here)))
                    {
                        fresh = idx;
                        break; // ближайший по пути тайл новой клетки — туда
                    }

                    Int2 fromStart = Torus.Delta(_shape, _start, here);
                    int score = Math.Max(Math.Abs(fromStart.X), Math.Abs(fromStart.Y));
                    if (score > farScore)
                    {
                        farScore = score;
                        far = idx;
                    }
                }

                for (int k = 0; k < 4; k++)
                {
                    int nx = cx + Dx4[k], ny = cy + Dy4[k];
                    if (Math.Abs(nx) > R || Math.Abs(ny) > R)
                    {
                        continue;
                    }

                    int nidx = (ny + R) * Side + nx + R;
                    if (_seen[nidx] == _gen)
                    {
                        continue;
                    }

                    var n = Torus.Wrap(_shape, new TileCoord(me.X + nx, me.Y + ny));
                    if (Skipped(n))
                    {
                        continue;
                    }

                    bool ok = Walkable(world, n);
                    if (!ok && allowDig && world.World.FindChunk(Torus.ChunkOf(_shape, n)) != null
                        && FloorId.IsPassable(world.World.GetFloor(n)))
                    {
                        ok = Diggable(client, world.World.GetObject(n));
                    }

                    if (ok)
                    {
                        _seen[nidx] = _gen;
                        _prev[nidx] = idx;
                        _queue[tail++] = nidx;
                    }
                }
            }

            LastPlanTiles = tail;
            Plans++;
            int go = fresh >= 0 ? fresh : far;
            if (!explore || go < 0 || go == startIdx)
            {
                return false;
            }

            BuildPath(me, go); // цели в окне нет — идём в новые места, окно чанков поедет следом
            _hasTarget = false;
            _pathDigs = allowDig;
            Explores++;
            return true;
        }

        /// <summary>
        /// Сколько тайлов достижимо пешком от <paramref name="me"/> (не больше <see cref="FloodLimit"/>), если тайл
        /// <paramref name="wall"/> считать занятым (<paramref name="useWall"/>).
        /// </summary>
        private int Flood(ClientWorld world, TileCoord me, TileCoord wall, bool useWall)
        {
            _gen++;
            int head = 0, tail = 0;
            int startIdx = R * Side + R;
            _seen[startIdx] = _gen;
            _queue[tail++] = startIdx;
            while (head < tail && tail < FloodLimit)
            {
                int idx = _queue[head++];
                int cx = idx % Side - R, cy = idx / Side - R;
                for (int k = 0; k < 4; k++)
                {
                    int nx = cx + Dx4[k], ny = cy + Dy4[k];
                    if (Math.Abs(nx) > R || Math.Abs(ny) > R)
                    {
                        continue;
                    }

                    int nidx = (ny + R) * Side + nx + R;
                    var n = Torus.Wrap(_shape, new TileCoord(me.X + nx, me.Y + ny));
                    if (_seen[nidx] == _gen || (useWall && n.X == wall.X && n.Y == wall.Y) || !Walkable(world, n))
                    {
                        continue;
                    }

                    _seen[nidx] = _gen;
                    _queue[tail++] = nidx;
                }
            }

            return tail;
        }

        private const int FloodLimit = 400;

        private readonly HashSet<int> _seenCells = new HashSet<int>();

        private static int CellKey(TileCoord t) => (t.X >> 4) | ((t.Y >> 4) << 12);

        private void BuildPath(TileCoord me, int idx)
        {
            _path.Clear();
            for (int i = idx; i >= 0 && _prev[i] >= 0; i = _prev[i])
            {
                _path.Add(Torus.Wrap(_shape, new TileCoord(me.X + i % Side - R, me.Y + i / Side - R)));
            }

            _path.Reverse();
            if (_path.Count == 0)
            {
                _path.Add(me); // цель рядом — встать по центру своего тайла
            }
        }
    }
}
