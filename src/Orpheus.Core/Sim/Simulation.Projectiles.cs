using System;
using Orpheus.Core.Combat;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
using Orpheus.Core.Rng;
using Orpheus.Core.Trig;

namespace Orpheus.Core.Sim
{
    /// <summary>Конец снаряда в такте (E3-2): для рассылки клиентам и тестов.</summary>
    public readonly struct ProjectileStop
    {
        public readonly int Id;
        public readonly int Owner;
        public readonly ProjectileEnd Reason;

        /// <summary>Точка остановки (обёрнута): у стены — последняя свободная точка перед ней.</summary>
        public readonly WorldPos Position;

        /// <summary>В кого попал (<see cref="ProjectileEnd.Hit"/>), иначе −1.</summary>
        public readonly int Victim;

        /// <summary>Стрела осталась предметом на земле.</summary>
        public readonly bool Dropped;

        public ProjectileStop(int id, int owner, ProjectileEnd reason, WorldPos position, int victim, bool dropped)
        {
            Id = id;
            Owner = owner;
            Reason = reason;
            Position = position;
            Victim = victim;
            Dropped = dropped;
        }

        public override string ToString() => $"снаряд #{Id} ({Owner}): {Reason} {Position}{(Victim >= 0 ? $" → #{Victim}" : "")}{(Dropped ? ", лежит" : "")}";
    }

    /// <summary>
    /// E3-2 (шаг 3.2): снаряды — лук персонажа и общий механизм для стрелков-врагов (`02` 4.2).
    /// <para>
    /// Порядок детерминирован (11.5): снаряды — по номеру; выстрел — в кадре отпускания ЛКМ (фаза движения 3), полёт —
    /// фаза 4.2 после боя. Полёт — отрезок такта шагами ≤ <see cref="Projectiles.StepUnits"/>: в каждой точке сначала
    /// препятствие (как линия удара: стена, закрытая дверь, скала, дерево, обрыв, мебель; забор, вода, куст — нет), затем
    /// существа стороны-цели (круг существа + радиус стрелы; ближайшее к точке). Шов тора — точки обёрнуты, дальность —
    /// по пройденному пути. Летящие снаряды не сохраняются (пропадают при загрузке); воткнутые стрелы — кучи.
    /// </para>
    /// </summary>
    public sealed partial class Simulation
    {
        /// <summary>Потолок летящих снарядов (лишние выстрелы — без снаряда, стрела не снимается).</summary>
        public const int MaxProjectiles = 512;

        private readonly Projectile[] _projectiles = new Projectile[MaxProjectiles];
        private int _projectileCount;
        private int _nextProjectileId = 1;
        private readonly Projectile[] _shotsThisTick = new Projectile[MaxProjectiles];
        private int _shotCount;
        private readonly ProjectileStop[] _stopsThisTick = new ProjectileStop[MaxProjectiles];
        private int _stopCount;

        /// <summary>Летящие снаряды по возрастанию номера.</summary>
        public ReadOnlySpan<Projectile> Projectiles => new ReadOnlySpan<Projectile>(_projectiles, 0, _projectileCount);

        /// <summary>Снаряды, выпущенные в последнем такте (в виде на миг выстрела).</summary>
        public ReadOnlySpan<Projectile> ShotsThisTick => new ReadOnlySpan<Projectile>(_shotsThisTick, 0, _shotCount);

        /// <summary>Снаряды, кончившиеся в последнем такте.</summary>
        public ReadOnlySpan<ProjectileStop> StopsThisTick => new ReadOnlySpan<ProjectileStop>(_stopsThisTick, 0, _stopCount);

        /// <summary>Следующий номер снаряда (1 — ни одного выстрела не было).</summary>
        public int NextProjectileId => _nextProjectileId;

        /// <summary>
        /// Выпустить снаряд (общий механизм: лук игрока, стрелок-враг шага 3.3). <paramref name="hostile"/> — снаряд врага
        /// (вредит персонажам). Возвращает номер, 0 — потолок снарядов.
        /// </summary>
        public int SpawnProjectile(int owner, bool hostile, byte kind, WorldPos origin, ushort aimRaw, int speedX256,
            int rangeUnits, int damage, byte dropItem)
        {
            if (_projectileCount == MaxProjectiles)
            {
                return 0;
            }

            var p = new Projectile
            {
                Id = _nextProjectileId++,
                Owner = owner,
                Hostile = hostile,
                Kind = kind,
                Damage = (byte)Math.Max(0, Math.Min(255, damage)),
                DropItem = dropItem,
                SpawnTick = Tick + 1,
                Origin = Torus.Wrap(_shape, origin),
                AimRaw = aimRaw,
                SpeedX256 = Math.Max(1, speedX256),
                RangeUnits = Math.Max(1, rangeUnits),
                Age = 0,
            };
            _projectiles[_projectileCount++] = p;
            if (_shotCount < _shotsThisTick.Length)
            {
                _shotsThisTick[_shotCount++] = p;
            }

            return p.Id;
        }

        /// <summary>
        /// Выстрел из лука сущности <paramref name="slot"/> (флаг <see cref="BowAction.Fired"/> кадра): строка лука в руках,
        /// одна стрела из сумки — первая стопка от быстрой панели слева направо (`06` 4.4 «боеприпасы»). Стрел нет (выбросил
        /// в натяжении) или лука нет — выстрела нет.
        /// </summary>
        private void FireBow(int slot)
        {
            ref SimEntity e = ref _entities[slot];
            ref BowAction bow = ref e.Mover.Bow;
            if (!bow.TakeShot())
            {
                return;
            }

            byte held = e.Inv.Held;
            if (!Combat.Projectiles.TryGetBow(held, out BowDef def) || _projectileCount == MaxProjectiles)
            {
                return;
            }

            int cell = -1;
            for (int c = 0; c < Inventory.Cells; c++)
            {
                if (e.Inv.ItemAt(c) == def.Ammo && e.Inv.CountIn(c) > 0)
                {
                    cell = c;
                    break;
                }
            }

            if (cell < 0)
            {
                return;
            }

            e.Inv.TakeFrom(cell, 1);
            int power = BowAction.PowerQ8(bow.ShotDraw);
            SpawnProjectile(e.Id, e.Kind == SimEntity.KindEnemy, Combat.Projectiles.KindOfBow(held),
                new WorldPos(bow.ShotX, bow.ShotY), bow.ShotAim, def.SpeedX256At(power), def.RangeUnits, def.DamageAt(power), def.Ammo);
        }

        /// <summary>Вход лука кадра сущности (<see cref="BowInput"/>): лук в руках, ЛКМ, стрелы в сумке — тот же гейт у предсказания.</summary>
        private byte BowInputOf(int slot, in InputFrame f)
        {
            ref SimEntity e = ref _entities[slot];
            byte held = e.Inv.Held;
            if (Items.KindOf(held) != Items.KindRanged)
            {
                return 0;
            }

            return BowInput.Of(true, f.AttackPressed, e.Inv.Count(Items.Arrows) > 0);
        }

        /// <summary>
        /// Сущность на вводе 30 Гц (бот ядра, тест) с луком: ЛКМ зажата или лук занят — такт идёт двумя кадрами
        /// (как серия меча, <see cref="Mover.Step(in MapShape, Input8, TileSolid)"/>), выстрел — в своём кадре. Иначе false.
        /// </summary>
        private bool BowFrames30(int slot)
        {
            ref SimEntity e = ref _entities[slot];
            InputFrame f = e.Input;
            bool ranged = Items.KindOf(e.Inv.Held) == Items.KindRanged;
            if (!e.Mover.Bow.Active && (!ranged || !f.AttackPressed) ||
                e.Mover.KnockLeft != 0 || e.Mover.Melee.Active)
            {
                return false;
            }

            for (int k = 0; k < MovementTick.FramesPerTick; k++)
            {
                e.Mover.StepFrame(_shape, f.Direction, false, false, f.Angle16, BowInputOf(slot, f), _solid);
                if (e.Mover.Bow.Fired)
                {
                    FireBow(slot);
                }
            }

            return true;
        }

        /// <summary>Сбросить буферы выстрелов и остановок такта (начало такта).</summary>
        private void BeginProjectileTick()
        {
            _shotCount = 0;
            _stopCount = 0;
        }

        /// <summary>Фаза 4.2 такта (E3-2): полёт каждого снаряда по номеру; кончившиеся убираются с сохранением порядка.</summary>
        private void StepProjectiles(long tick)
        {
            int w = 0;
            for (int i = 0; i < _projectileCount; i++)
            {
                if (FlyOneTick(tick, ref _projectiles[i]))
                {
                    _projectiles[w++] = _projectiles[i];
                }
            }

            _projectileCount = w;
        }

        /// <summary>Один такт полёта: false — снаряд кончился (попал, воткнулся, долетел).</summary>
        private bool FlyOneTick(long tick, ref Projectile p)
        {
            long from = (long)p.SpeedX256 * p.Age;
            long to = from + p.SpeedX256;
            long range = (long)p.RangeUnits << 8;
            bool last = to >= range;
            if (last)
            {
                to = range;
            }

            int segUnits = (int)((to - from) >> 8);
            int steps = Math.Max(1, (segUnits + Combat.Projectiles.StepUnits - 1) / Combat.Projectiles.StepUnits);
            WorldPos prev = Torus.Wrap(_shape, p.PositionAtTravel(from));
            for (int k = p.Age == 0 ? 0 : 1; k <= steps; k++)
            {
                long t = from + (to - from) * k / steps;
                WorldPos pt = Torus.Wrap(_shape, p.PositionAtTravel(t));
                TileCoord tile = Torus.TileOf(_shape, pt);
                if (StrikeTileBlocked(tile) && StrikeCellSolid(tile, pt))
                {
                    Stop(tick, p, ProjectileEnd.Wall, prev, -1);
                    return false;
                }

                int victim = ProjectileVictim(p, pt);
                if (victim >= 0)
                {
                    ref SimEntity v = ref _entities[victim];
                    int vid = v.Id;
                    int dealt = ApplyStrike(vid, p.Owner, p.Damage, prev, knockback: true, damageKind: Combat.Projectiles.DamagePierce);
                    if (dealt > 0 && v.Mover.KnockLeft != 0)
                    {
                        v.Mover.KnockSpeedX10 /= Combat.Projectiles.ArrowKnockDivisor; // слабое: 0,5 т (`02` 4.2)
                    }

                    Stop(tick, p, ProjectileEnd.Hit, pt, vid);
                    return false;
                }

                prev = pt;
            }

            p.Age++;
            if (last)
            {
                Stop(tick, p, ProjectileEnd.Range, prev, -1);
                return false;
            }

            return true;
        }

        /// <summary>
        /// Существо под точкой полёта: сторона-цель (снаряд персонажа — враги, врага — персонажи; PvE — без дружественного
        /// огня), живое, не в неуязвимости уворота; круг существа + радиус стрелы; ближайшее к точке, равные — по месту в списке.
        /// </summary>
        private int ProjectileVictim(in Projectile p, WorldPos pt)
        {
            const long reach = Collision.RadiusUnits + Combat.Projectiles.ArrowRadiusUnits;
            int best = -1;
            long bestD = reach * reach + 1;
            for (int i = 0; i < _entityCount; i++)
            {
                ref SimEntity e = ref _entities[i];
                if (!e.Alive || e.Id == p.Owner)
                {
                    continue;
                }

                bool target = p.Hostile ? e.IsCharacter : e.Kind == SimEntity.KindEnemy;
                if (!target || (e.IsCharacter && e.Mover.Dodge.Invulnerable))
                {
                    continue;
                }

                long d = Torus.Dist2(_shape, pt, e.Mover.Position);
                if (d < bestD)
                {
                    bestD = d;
                    best = i;
                }
            }

            return best;
        }

        /// <summary>Конец снаряда: запись остановки; стрела, остановленная стеной или дальностью, с шансом 50 % ложится кучей.</summary>
        private void Stop(long tick, in Projectile p, ProjectileEnd reason, WorldPos at, int victim)
        {
            bool dropped = false;
            if (reason != ProjectileEnd.Hit && p.DropItem != 0 &&
                (int)(SplitMix64.Hash(p.Id, (int)reason, 0x3E2, _world.Seed) & 0xFF) < Combat.Projectiles.ArrowDropChanceQ8)
            {
                dropped = SpawnPile(tick, p.DropItem, 1, at, LootPickupDelayTicks) != 0;
            }

            if (_stopCount < _stopsThisTick.Length)
            {
                _stopsThisTick[_stopCount++] = new ProjectileStop(p.Id, p.Owner, reason, at, victim, dropped);
            }
        }
    }
}
