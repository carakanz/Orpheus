using System;
using System.Collections.Generic;
using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.WorldGen;
using Xunit;
using Xunit.Abstractions;

namespace Orpheus.Tests
{
    /// <summary>
    /// E2-12b, слово Автора 15.11: «иногда застревают среди деревьев и не могут обойти». Настоящий мир генератора v7, участки
    /// леса с плотностью деревьев 40–55 %: враг догоняет игрока за разумное время и не стоит дольше 2 с без движения, пока путь
    /// есть (путь проверяется поиском в ширину по тайлам); вариант «игрок за купой деревьев» — прямая от врага к игроку идёт
    /// сквозь деревья.
    /// </summary>
    public sealed class ForestChaseTests
    {
        private const int U = Torus.SubTilesPerTile;
        private const int Size = 1024;
        private const int Box = 24;
        private static readonly object Lock = new object();
        private static WorldModel? _world;
        private readonly ITestOutputHelper _out;

        public ForestChaseTests(ITestOutputHelper output)
        {
            _out = output;
        }

        private static WorldModel World()
        {
            lock (Lock)
            {
                return _world ??= new GeneratorV1().Generate(MapShape.World(Size), 1);
            }
        }

        private static bool Tree(WorldModel w, int x, int y) =>
            ObjectId.IdOf(w.GetObject(Torus.Wrap(w.Shape, new TileCoord(x, y)))) == ObjectId.Tree;

        /// <summary>Участки Box × Box с долей деревьев 40–55 % (по сетке с шагом Box), по порядку обхода.</summary>
        private static List<TileCoord> DenseBoxes(WorldModel w, int max)
        {
            var list = new List<TileCoord>();
            for (int by = 0; by + Box <= Size && list.Count < max; by += Box)
            {
                for (int bx = 0; bx + Box <= Size && list.Count < max; bx += Box)
                {
                    int trees = 0;
                    for (int y = 0; y < Box; y++)
                    {
                        for (int x = 0; x < Box; x++)
                        {
                            trees += Tree(w, bx + x, by + y) ? 1 : 0;
                        }
                    }

                    int pct = trees * 100 / (Box * Box);
                    if (pct >= 40 && pct <= 55)
                    {
                        list.Add(new TileCoord(bx, by));
                    }
                }
            }

            return list;
        }

        /// <summary>Расстояния поиска в ширину по тайлам (8 соседей, без срезания углов) от тайла в окне ±30; −1 — недостижим.</summary>
        private static int[] Bfs(Simulation sim, TileCoord from, out int originX, out int originY)
        {
            const int N = 61;
            originX = from.X - 30;
            originY = from.Y - 30;
            var dist = new int[N * N];
            Array.Fill(dist, -1);
            var q = new Queue<int>();
            int s = 30 * N + 30;
            dist[s] = 0;
            q.Enqueue(s);
            MapShape map = sim.Shape;
            int ox = originX, oy = originY;
            bool Blocked(int lx, int ly) => sim.IsBlocked(map, Torus.Wrap(map, new TileCoord(ox + lx, oy + ly)));
            while (q.Count > 0)
            {
                int i = q.Dequeue();
                int x = i % N, y = i / N;
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if ((dx == 0 && dy == 0) || nx < 0 || ny < 0 || nx >= N || ny >= N || dist[ny * N + nx] >= 0 || Blocked(nx, ny))
                        {
                            continue;
                        }

                        if (dx != 0 && dy != 0 && (Blocked(x + dx, y) || Blocked(x, y + dy)))
                        {
                            continue;
                        }

                        dist[ny * N + nx] = dist[i] + 1;
                        q.Enqueue(ny * N + nx);
                    }
                }
            }

            return dist;
        }

        /// <summary>Деревьев на отрезке между центрами тайлов (шаг 1/8 тайла).</summary>
        private static int TreesOnLine(WorldModel w, TileCoord a, TileCoord b)
        {
            Int2 d = Torus.Delta(w.Shape, a, b);
            var seen = new HashSet<(int, int)>();
            for (int k = 0; k <= 64; k++)
            {
                int x = (int)Math.Floor(a.X + 0.5 + d.X * k / 64.0);
                int y = (int)Math.Floor(a.Y + 0.5 + d.Y * k / 64.0);
                if (Tree(w, x, y))
                {
                    seen.Add((x, y));
                }
            }

            return seen.Count;
        }

        /// <summary>
        /// Пары (игрок, враг) в густом лесу: враг в 7–12 тайлах (в зрении), путь по тайлам есть и не длиннее 2× прямого;
        /// <paramref name="behindTrees"/> — на прямой между ними ≥ 3 дерева.
        /// </summary>
        private static List<(TileCoord Player, TileCoord Enemy)> Pairs(WorldModel w, int count, bool behindTrees)
        {
            var sim = new Simulation(w);
            var pairs = new List<(TileCoord, TileCoord)>();
            foreach (TileCoord box in DenseBoxes(w, 400))
            {
                TileCoord c = new TileCoord(box.X + Box / 2, box.Y + Box / 2);
                TileCoord player = default;
                bool found = false;
                for (int r = 0; r < 4 && !found; r++)
                {
                    for (int dy = -r; dy <= r && !found; dy++)
                    {
                        for (int dx = -r; dx <= r && !found; dx++)
                        {
                            TileCoord t = Torus.Wrap(w.Shape, new TileCoord(c.X + dx, c.Y + dy));
                            if (!sim.IsBlocked(w.Shape, t))
                            {
                                player = t;
                                found = true;
                            }
                        }
                    }
                }

                if (!found)
                {
                    continue;
                }

                int[] dist = Bfs(sim, player, out int ox, out int oy);
                bool added = false;
                for (int dy = -12; dy <= 12 && !added; dy++)
                {
                    for (int dx = -12; dx <= 12 && !added; dx++)
                    {
                        int cheb = Math.Max(Math.Abs(dx), Math.Abs(dy));
                        if (cheb < 7 || dx * dx + dy * dy > 12 * 12) // в зрении (14 т) с запасом
                        {
                            continue;
                        }

                        int i = (30 + dy) * 61 + 30 + dx;
                        TileCoord e = Torus.Wrap(w.Shape, new TileCoord(player.X + dx, player.Y + dy));
                        if (dist[i] < 0 || dist[i] > 2 * cheb || sim.IsBlocked(w.Shape, e))
                        {
                            continue;
                        }

                        if (behindTrees && TreesOnLine(w, e, player) < 3)
                        {
                            continue;
                        }

                        pairs.Add((player, e));
                        added = true;
                    }
                }

                if (pairs.Count >= count)
                {
                    break;
                }
            }

            return pairs;
        }

        /// <summary>
        /// Густой лес (40–55 % деревьев), враг в 7–12 тайлах (в зрении), путь есть: за 20 с доходит до игрока (в зоне удара, «в атаке»),
        /// и до этого ни разу не стоит дольше 2 с (60 тактов) без смещения на 0,2 тайла.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void E212b_Forest_EnemyCatchesPlayer_NoStandStill(bool behindTrees)
        {
            WorldModel w = World();
            List<(TileCoord Player, TileCoord Enemy)> pairs = Pairs(w, 12, behindTrees);
            Assert.True(pairs.Count >= 8, $"пар в густом лесу: {pairs.Count}");
            var fails = new List<string>();
            foreach ((TileCoord player, TileCoord enemy) in pairs)
            {
                var sim = new Simulation(w); // мир не правится (дверей и построек нет) — общий
                int ch = sim.AddEntity(EditRules.CenterOf(player));
                int rogue = sim.AddMeleeRogue(EditRules.CenterOf(enemy));
                int reached = -1;
                int still = 0, maxStill = 0;
                WorldPos mark = sim.GetEntity(rogue).Mover.Position;
                for (int t = 0; t < 600 && reached < 0; t++)
                {
                    sim.SetInput(ch, InputFrame.None);
                    sim.Step();
                    SimEntity e = sim.GetEntity(rogue);
                    if (e.Brain.State == EnemyState.Attack &&
                        Torus.Dist2(sim.Shape, e.Mover.Position, sim.GetEntity(ch).Mover.Position) <= MeleeRogue.StrikeRange2)
                    {
                        reached = t;
                        break;
                    }

                    if (Torus.Dist2(sim.Shape, e.Mover.Position, mark) >= 48L * 48 || e.Brain.TargetId != ch)
                    {
                        mark = e.Mover.Position;
                        still = 0;
                    }
                    else
                    {
                        still++;
                        maxStill = Math.Max(maxStill, still);
                    }
                }

                SimEntity end = sim.GetEntity(rogue);
                string line = $"игрок {player}, враг {enemy}: дошёл на такте {reached}, стоял подряд {maxStill} т, конец {Torus.TileOf(sim.Shape, end.Mover.Position)} {end.Brain.State}";
                _out.WriteLine(line);
                if (reached < 0 || maxStill > 60)
                {
                    fails.Add(line);
                }
            }

            Assert.True(fails.Count == 0, $"{fails.Count} из {pairs.Count}:\n" + string.Join("\n", fails));
        }
    }
}
