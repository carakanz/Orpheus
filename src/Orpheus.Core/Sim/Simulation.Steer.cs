using Orpheus.Core.Combat;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Pathing;

namespace Orpheus.Core.Sim
{
    /// <summary>
    /// Цель погони и ход по пути (E2-12b): путь — точки решётки через 0,5 тайла (<see cref="PathFinder"/>), враг идёт по ним
    /// тем же вводом из 8 направлений, что и персонаж; у косяка проёма, куда прямой ход упирается, — подруливает.
    /// </summary>
    public sealed partial class Simulation
    {
        /// <summary>
        /// Только для тестов (C-009): подруливание у косяка выключено — враг идёт прямо к точке пути, и упор в угол проверяет
        /// вторую линию («застрял» → переспрос, сдача через 20 с). По умолчанию выключен; не в протоколе, сохранении и хэше.
        /// </summary>
        internal bool SteerOffForTests { get; set; }

        /// <summary>
        /// Точка цели погони: точка решётки 0,5, ближайшая к цели. Центр круга, не задевающего твёрдое, всегда в ≤ 0,25 тайла по
        /// осям от свободной точки (круг 0,4 в ней задевает только 4 ячейки вокруг), так что у тонкой стены (игрок прижат) цель —
        /// на его стороне стены, и «цель в замкнутой области» срабатывает. Цель всё же в твёрдом (дверь закрыли на ней) —
        /// свободная соседняя точка (8, сначала прямые), ближайшая к цели; нет такой — сама точка.
        /// </summary>
        private WorldPos ChaseGoal(WorldPos target)
        {
            WorldPos p = PathFinder.SnapToLattice(_shape, target);
            if (!Collision.TouchesSolid(_shape, p, _solid))
            {
                return p;
            }

            WorldPos best = p;
            long bestD = long.MaxValue;
            for (int k = 0; k < 8; k++)
            {
                WorldPos n = Torus.Offset(_shape, p,
                    new Int2(PathFinder.NeighborDx[k] * PathFinder.PointUnits, PathFinder.NeighborDy[k] * PathFinder.PointUnits));
                if (Collision.TouchesSolid(_shape, n, _solid))
                {
                    continue;
                }

                long d = Torus.Dist2(_shape, target, n);
                if (d < bestD)
                {
                    best = n;
                    bestD = d;
                }
            }

            return best;
        }

        /// <summary>
        /// «Упёрся», а цель ушла (E2-12b): тайл точки цели дальше <see cref="MeleeRogue.RepathGoalDelta"/> от тайла, к которому
        /// искали путь (<see cref="EnemyBrain.GoalX"/>/<see cref="EnemyBrain.GoalY"/>).
        /// </summary>
        private bool BlockedGoalMoved(ref EnemyBrain b, WorldPos target)
        {
            TileCoord gt = Torus.TileOf(_shape, ChaseGoal(target));
            long d2 = Torus.Dist2(_shape, gt, new TileCoord(b.GoalX, b.GoalY)) * (long)Torus.SubTilesPerTile * Torus.SubTilesPerTile;
            return d2 > (long)MeleeRogue.RepathGoalDelta * MeleeRogue.RepathGoalDelta;
        }

        /// <summary>
        /// Приближение к цели в эпизоде «упёрся» (C-009): <paramref name="at"/> ближе к цели, чем контрольная точка, не меньше чем
        /// на шаг продвижения (<see cref="MeleeRogue.ProgressMove2"/>, 48 единиц) — по прямой до текущей позиции цели (цель ушла —
        /// идущий за ней враг ближе к ней, чем точка позади него).
        /// </summary>
        private bool CloserToTarget(WorldPos at, WorldPos checkpoint, WorldPos target) =>
            ISqrt(Torus.Dist2(_shape, checkpoint, target)) - ISqrt(Torus.Dist2(_shape, at, target)) >= MeleeRogue.ProgressStep;

        /// <summary>Только для тестов (C-009): перенести сущность (толкотня/отбрасывание сбили врага с пути; игрок вышел из дома).</summary>
        internal void PlaceForTests(int entityId, WorldPos position)
        {
            _entities[SlotOf(entityId)].Mover.Position = position;
        }

        /// <summary>Поставить поиск пути погони от позиции врага к точке <paramref name="goal"/>; true — запрос в очереди.</summary>
        private bool EnqueueChasePath(ref SimEntity e, int slot, WorldPos goal, long tick)
        {
            if (!_paths.Enqueue(slot, e.Mover.Position, goal, PathMode.Walker, tick))
            {
                return false;
            }

            ref EnemyBrain b = ref e.Brain;
            TileCoord gt = Torus.TileOf(_shape, goal);
            b.PathStamp = (int)tick;
            b.PathPos = 0;
            b.PathRev = _editRev;
            b.EnclosedRev = 0;
            b.GoalX = gt.X;
            b.GoalY = gt.Y;
            return true;
        }

        /// <summary>
        /// Ход по готовому пути: пройденные точки (ближе <see cref="MeleeRogue.PathPointPass2"/>) пропускаются, к следующей —
        /// <see cref="SteerTo"/>. Точки кончились — false (дальше — прямо к цели).
        /// </summary>
        private bool FollowPath(ref SimEntity e, PathBuffer buf)
        {
            ref EnemyBrain b = ref e.Brain;
            int steps = buf.Length;
            while (b.PathPos < steps && Torus.Dist2(_shape, e.Mover.Position, buf.Points[b.PathPos]) <= MeleeRogue.PathPointPass2)
            {
                b.PathPos++;
            }

            if (b.PathPos >= steps)
            {
                return false;
            }

            e.Mover.UnitsPerTickX10 = MeleeRogue.ChaseSpeedX10;
            e.Input = new InputFrame(SteerTo(e.Mover.Position, e.Mover.UnitsPerTickX10, buf.Points[b.PathPos]));
            return true;
        }

        /// <summary>
        /// Направление к точке пути (E2-12b, дефект «застревает у косяка»): ввод — 8 направлений, и ход «прямо к точке», сдвинутый
        /// от линии проёма на 0,1–0,4 тайла, упирается в угол косяка: столкновения раздельные по осям, поперёк хода круг не
        /// сползает — враг стоял до «упёрся». Поэтому ход пробуется шагом такта (<see cref="Collision.Move"/>, те же стены):
        /// направление к точке, затем соседние (у прямого — диагонали в сторону поперечного смещения, затем само смещение;
        /// у диагонали подруливать нечем — C-009); берётся первое, после которого точка ближе. Ни одно не приближает — прямое.
        /// Целочисленно, порядок фиксирован (детерминизм).
        /// </summary>
        internal Input8 SteerTo(WorldPos at, int speedX10, WorldPos node)
        {
            Int2 d = Torus.Delta(_shape, at, node);
            Input8 q = Input8.FromStick(d.X, d.Y);
            long d0 = d.LengthSquared;
            if (!q.IsMoving || SteerOffForTests || Closer(at, speedX10, q, node, d0))
            {
                return q;
            }

            if (q.IsDiagonal)
            {
                // C-009: диагональ не приближает, только если оба её осевых сдвига нулевые (столкновения по осям, X затем Y, без
                // отката за старт) — тогда и чисто осевые шаги стоят; подруливать нечем (тест C009_Steer_DiagonalBlocked…).
                return q;
            }

            // Прямой ход: поперечное смещение (знак) — сначала диагональ в его сторону, затем в другую, затем само смещение.
            int side = q.DX == 0 ? (d.X > 0 ? 1 : d.X < 0 ? -1 : 0) : (d.Y > 0 ? 1 : d.Y < 0 ? -1 : 0);
            int first = side >= 0 ? 1 : -1;
            Input8 d1 = q.DX == 0 ? new Input8(first, q.DY) : new Input8(q.DX, first);
            Input8 d2 = q.DX == 0 ? new Input8(-first, q.DY) : new Input8(q.DX, -first);
            if (Closer(at, speedX10, d1, node, d0))
            {
                return d1;
            }

            if (Closer(at, speedX10, d2, node, d0))
            {
                return d2;
            }

            if (side != 0)
            {
                Input8 s = q.DX == 0 ? new Input8(side, 0) : new Input8(0, side);
                if (Closer(at, speedX10, s, node, d0))
                {
                    return s;
                }
            }

            return q;
        }

        /// <summary>Только для тестов (C-009): круг в точке задевает твёрдое (те же стены, что у движения).</summary>
        internal bool TouchesSolidForTests(WorldPos p) => Collision.TouchesSolid(_shape, p, _solid);

        /// <summary>Шаг такта направлением <paramref name="dir"/> (с упором в стены) приближает к точке.</summary>
        internal bool Closer(WorldPos at, int speedX10, Input8 dir, WorldPos node, long d0)
        {
            WorldPos p = Collision.Move(_shape, at, dir.SpeedX10(speedX10) / MovementTick.SpeedScale,
                dir.SpeedY10(speedX10) / MovementTick.SpeedScale, _solid);
            return Torus.Dist2(_shape, p, node) < d0;
        }
    }
}
