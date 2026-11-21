using System;
using Orpheus.Core.Combat;
using Orpheus.Core.Geometry;
using Orpheus.Core.Movement;
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
    /// фаза 4.2 после боя. Полёт — отрезок такта: преграда — обход ячеек 0,5 по точкам через 1 unit
    /// (стык стен углом закрыт; как линия удара: стена, закрытая дверь, скала, дерево, обрыв, мебель; забор, вода, куст — нет),
    /// существа стороны-цели — точное пересечение отрезка с кругом (0,4 + 0,2 т); что раньше по пути, то и случилось. Шов тора —
    /// точки обёрнуты, дальность — по пути. Летящие снаряды и счётчик номеров не сохраняются (после загрузки номера — с 1);
    /// воткнутые стрелы — кучи.
    /// </para>
    /// </summary>
    public sealed partial class Simulation
    {
        /// <summary>Потолок летящих снарядов (лишние выстрелы — без снаряда, стрела не снимается).</summary>
        public const int MaxProjectiles = 512;

        /// <summary>
        /// Потолок полёта, тактов: 10 с *(прикидка C-031)*. Лук долетает свои 30–33 т за ≤ 1,7 с; потолок держит любой
        /// снаряд (медленный вражеский шага 3.3, скорость 1) — стрела не летит вечно (`BRIEF` 11n).
        /// </summary>
        public const int MaxFlightTicks = 10 * 30;

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
        /// Загрузка летящих снарядов из сохранения (E3-3, формат 15): только в симуляцию без снарядов; строго по
        /// возрастанию номера; следующий номер — больше всех загруженных. Полёт продолжается с следующего такта
        /// (позиция — отрезок от <c>Speed × Age</c>, как при выстреле).
        /// </summary>
        public void RestoreProjectiles(ReadOnlySpan<Projectile> projectiles, int nextProjectileId)
        {
            if (_projectileCount != 0)
            {
                throw new InvalidOperationException("Загрузка снарядов — только в симуляцию без снарядов.");
            }

            if (projectiles.Length > MaxProjectiles)
            {
                throw new ArgumentException($"Снарядов {projectiles.Length} — больше потолка {MaxProjectiles}.", nameof(projectiles));
            }

            int maxId = 0;
            for (int i = 0; i < projectiles.Length; i++)
            {
                if (projectiles[i].Id <= maxId || projectiles[i].Id <= 0 || projectiles[i].SpeedX256 < 1 || projectiles[i].RangeUnits < 1
                    || projectiles[i].Age < 1 || projectiles[i].Age >= MaxFlightTicks)
                {
                    throw new ArgumentException($"Снаряд № {i}: {projectiles[i].Id} — номер не растёт или полёт вне правил.", nameof(projectiles));
                }

                maxId = projectiles[i].Id;
            }

            projectiles.CopyTo(_projectiles);
            _projectileCount = projectiles.Length;
            _nextProjectileId = Math.Max(nextProjectileId, maxId + 1);
        }

        /// <summary>
        /// Выпустить снаряд (общий механизм: лук игрока, стрелок-враг шага 3.3). <paramref name="hostile"/> — снаряд врага
        /// (вредит персонажам). Возвращает номер, 0 — потолок снарядов.
        /// </summary>
        public int SpawnProjectile(int owner, bool hostile, byte kind, WorldPos origin, ushort aimRaw, int speedX256,
            int rangeUnits, int damage, byte dropItem, bool strong = false)
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
                Strong = strong,
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
                RefuseShot(e.Id, new WorldPos(bow.ShotX, bow.ShotY));
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
                RefuseShot(e.Id, new WorldPos(bow.ShotX, bow.ShotY));
                return;
            }

            e.Inv.TakeFrom(cell, 1);
            int power = BowAction.PowerQ8(bow.ShotDraw);
            SpawnProjectile(e.Id, e.Kind == SimEntity.KindEnemy, Combat.Projectiles.KindOfBow(held),
                new WorldPos(bow.ShotX, bow.ShotY), bow.ShotAim, def.SpeedX256At(power), def.RangeUnits, def.DamageAt(power), def.Ammo,
                strong: bow.ShotDraw >= BowAction.FullDrawFrames);
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

        /// <summary>Выстрела не было — запись «отказ» (номер 0) для владельца: клиент гасит предсказанную стрелу (П E3-2, п. 5).</summary>
        private void RefuseShot(int owner, WorldPos at)
        {
            if (_stopCount < _stopsThisTick.Length)
            {
                _stopsThisTick[_stopCount++] = new ProjectileStop(0, owner, ProjectileEnd.Refused, Torus.Wrap(_shape, at), -1, false);
            }
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

        /// <summary>
        /// Один такт полёта: false — снаряд кончился (попал, воткнулся, долетел). Отрезок такта — от пути <c>Age</c> до
        /// <c>Age + 1</c> (с потолком дальности). Преграда — первая твёрдая ячейка 0,5 на отрезке (<see cref="FirstBlockedStep"/>:
        /// обход по точкам через 1 unit, ячейка смотрится при смене; угол к углу — обе промежуточные ячейки), существо —
        /// точное пересечение отрезка с кругом (<see cref="FirstHitQ16"/>). Что раньше по пути — то и случилось.
        /// </summary>
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

            WorldPos a = p.PositionAtTravel(from);
            WorldPos b = p.PositionAtTravel(to);
            Int2 d = Torus.Delta(_shape, a, b); // отрезок такта короче полмира — разность по тору
            WorldPos start = Torus.Wrap(_shape, a);

            int n = Math.Max(1, Math.Max(Math.Abs(d.X), Math.Abs(d.Y)));
            int wallStep = FirstBlockedStep(start, d, n, p.Age == 0); // 0…n; n + 1 — преграды нет
            int victim = FirstHitQ16(p, start, d, out int hitQ16);
            if (victim >= 0 && (wallStep > n || (long)hitQ16 * n <= (long)wallStep << 16))
            {
                WorldPos at = Torus.Wrap(_shape, new WorldPos(start.X + (int)((long)d.X * hitQ16 >> 16), start.Y + (int)((long)d.Y * hitQ16 >> 16)));
                WorldPos knockFrom = Torus.Wrap(_shape, new WorldPos(at.X - Math.Sign(d.X) * 64, at.Y - Math.Sign(d.Y) * 64));
                ref SimEntity v = ref _entities[victim];
                int vid = v.Id;
                int dealt = ApplyStrike(vid, p.Owner, p.Damage, knockFrom, knockback: true, damageKind: Combat.Projectiles.DamagePierce,
                    strong: p.Strong); // E3-1: событие «урон» (37) — цифра у жертвы и у стрелка
                if (dealt > 0 && v.Mover.KnockLeft != 0)
                {
                    v.Mover.KnockSpeedX10 /= Combat.Projectiles.ArrowKnockDivisor; // слабое: 0,5 т (`02` 4.2)
                }

                Stop(tick, p, ProjectileEnd.Hit, at, vid);
                return false;
            }

            if (wallStep <= n)
            {
                Stop(tick, p, ProjectileEnd.Wall, StepPoint(start, d, Math.Max(0, wallStep - 1), n), -1); // последняя свободная точка
                return false;
            }

            p.Age++;
            if (!_shape.Wraps && !InsideMap(b))
            {
                // C-031: мир без шва (тесты, стенды) — за краем стрела пропадает, а не летит по зажатым координатам
                Stop(tick, p, ProjectileEnd.Range, Torus.Wrap(_shape, b), -1, lost: true);
                return false;
            }

            if (last || p.Age >= MaxFlightTicks)
            {
                // C-031 «стрела не должна лететь вечно»: дальность, и сверху — потолок тактов полёта на любой скорости
                Stop(tick, p, ProjectileEnd.Range, Torus.Wrap(_shape, b), -1);
                return false;
            }

            return true;
        }

        /// <summary>Точка внутри мира (для карты без шва).</summary>
        private bool InsideMap(WorldPos p) =>
            (uint)p.X < (uint)_shape.WidthUnits && (uint)p.Y < (uint)_shape.HeightUnits;

        /// <summary>Точка <paramref name="k"/> из <paramref name="n"/> на отрезке (обёрнута).</summary>
        private WorldPos StepPoint(WorldPos start, Int2 d, int k, int n) =>
            Torus.Wrap(_shape, new WorldPos(start.X + (int)((long)d.X * k / n), start.Y + (int)((long)d.Y * k / n)));

        /// <summary>
        /// Первая точка отрезка в твёрдой для стрелы ячейке 0,5 × 0,5 (как линия удара: стена, стена в полтайла, закрытая дверь,
        /// скала, дерево, обрыв, мебель; забор, вода, куст — нет). Точки через ≤ 1 unit по каждой оси — ячейка (128 unit) не
        /// перепрыгивается при любой скорости; ячейка смотрится только при смене. Переход в соседнюю по диагонали ячейку (угол к
        /// углу, «лесенка» стен) проверяет обе промежуточные — стык двух стен углом стрелу не пропускает (П E3-2, п. 1).
        /// <paramref name="withStart"/> — проверять и начальную точку (первый такт). Возвращает номер точки 0…n; n + 1 — преграды нет.
        /// </summary>
        private int FirstBlockedStep(WorldPos start, Int2 d, int n, bool withStart) =>
            FirstBlockedCellStep(start, d, n, n, withStart);

        /// <summary>
        /// Первое существо стороны-цели, чей круг (0,4 т) + радиус стрелы (0,2 т) пересекает отрезок: точный корень квадратного
        /// уравнения в целых (<see cref="ISqrt"/>), доля пути <paramref name="hitQ16"/> (0…65 536). Стороны: снаряд персонажа —
        /// враги, врага — персонажи (PvE, без дружественного огня); мёртвые и увернувшиеся (неуязвимость рывка) — мимо.
        /// Равные доли — по месту в списке. Нет — −1.
        /// </summary>
        private int FirstHitQ16(in Projectile p, WorldPos start, Int2 d, out int hitQ16)
        {
            const long reach = Collision.RadiusUnits + Combat.Projectiles.ArrowRadiusUnits;
            long a = (long)d.X * d.X + (long)d.Y * d.Y;
            long len = ISqrt(a) + 1;
            int best = -1;
            hitQ16 = int.MaxValue;
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

                Int2 rel = Torus.Delta(_shape, start, e.Mover.Position);
                if (Math.Abs(rel.X) > len + reach || Math.Abs(rel.Y) > len + reach)
                {
                    continue;
                }

                long c = (long)rel.X * rel.X + (long)rel.Y * rel.Y - reach * reach;
                int q;
                if (c <= 0)
                {
                    q = 0; // уже в круге (в упор, `02` 4.3)
                }
                else
                {
                    long bdot = (long)d.X * rel.X + (long)d.Y * rel.Y;
                    if (a == 0 || bdot <= 0)
                    {
                        continue; // стоит на месте или цель позади
                    }

                    long disc = bdot * bdot - a * c;
                    if (disc < 0)
                    {
                        continue; // мимо
                    }

                    long num = bdot - ISqrt(disc);
                    if (num > a)
                    {
                        continue; // касание дальше конца отрезка
                    }

                    q = (int)((Math.Max(0L, num) << 16) / a);
                }

                if (q < hitQ16)
                {
                    hitQ16 = q;
                    best = i;
                }
            }

            return best;
        }

        /// <summary>
        /// Конец снаряда: запись остановки. C-031 (`BRIEF` 11n): стрела, остановленная преградой или дальностью, остаётся
        /// всегда — сама предмет на земле в точке остановки (воткнулась в преграду / в землю, <see cref="SpawnStuckArrow"/>);
        /// в воде тонет. Попавшая в существо пропадает (`02` 4.2, как в E3-2). Ушедшая за край мира без шва — пропадает.
        /// </summary>
        private void Stop(long tick, in Projectile p, ProjectileEnd reason, WorldPos at, int victim, bool lost = false)
        {
            bool dropped = false;
            if (reason != ProjectileEnd.Hit && p.DropItem != 0 && !lost)
            {
                byte kind = reason == ProjectileEnd.Wall ? GroundPile.KindArrowWall : GroundPile.KindArrowGround;
                dropped = SpawnStuckArrow(tick, p.SpawnTick, p.DropItem, at, p.AimRaw, kind) != 0;
            }

            if (_stopCount < _stopsThisTick.Length)
            {
                _stopsThisTick[_stopCount++] = new ProjectileStop(p.Id, p.Owner, reason, at, victim, dropped);
            }
        }
    }
}
