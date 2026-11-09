using System;
using Orpheus.Core.Geometry;
using Orpheus.Core.Trig;

namespace Orpheus.Core.Movement
{
    /// <summary>
    /// Движение одной сущности за такт 30 Гц (docs/07, 4.2): позиция + целый остаток скорости +
    /// шаг. Никаких ссылок на Unity и на мир: есть только форма карты (<see cref="MapShape"/>),
    /// направление ввода (<see cref="Input8"/>) и вопрос «тайл твёрдый?» (<see cref="TileSolid"/>).
    /// <para>
    /// Скорость — <see cref="UnitsPerTickX10"/> в unit × 10 за такт: базовый бег 512 (6 тайлов/с
    /// при 30 Гц = 51,2 unit за такт, work/refs/movement.md); ускорения снаряжения — другие значения
    /// тем же полем. За такт <c>carry += v</c>, <c>шаг = carry / 10</c>, <c>carry %= 10</c> —
    /// дробные 0,2 unit накапливаются целым числом на сущности, float-накопления нет.
    /// Сдвиг позиции — только <see cref="Torus.Offset"/> (docs/07, 3.2).
    /// </para>
    /// Структура: 2 × int32 (позиция) + 2 × int32 (остаток) + int32 (скорость) + уворот 4 байта (<see cref="Dodge"/>, 0-029)
    /// + выносливость 4 байта (<see cref="Stamina"/>, 1-003), памяти в такте не выделяет.
    /// </summary>
    public struct Mover
    {
        /// <summary>Позиция существа в 1/256 тайла (docs/07, 3.2), свёрнутая в пределы карты.</summary>
        public WorldPos Position;

        /// <summary>
        /// Целый остаток скорости на осях в десятых долях unit (−9…9 на ось): <c>carry += v</c>,
        /// шаг = <c>carry / 10</c>, <c>carry %= 10</c>. После кратного числа тактов (5 — для базового
        /// бега) остаток равен 0, и путь кратен тайлу ровно.
        /// </summary>
        public Int2 Carry;

        /// <summary>Скорость за такт в unit × 10: базовый бег 512 (см. <see cref="MovementTick.BaseSpeedX10"/>).</summary>
        public int UnitsPerTickX10;

        /// <summary>Уворот (`02` 2.2; 0-029): рывок, неуязвимость, откат — шагается только покадрово.</summary>
        public Dodge Dodge;

        /// <summary>Выносливость (`02` 2.3; 1-003): запас 100, уворот −25, реген 20/с через 0,5 с — тот же кадр, что уворот.</summary>
        public Stamina Stamina;

        /// <summary>
        /// Рывок начат этим кадром (1-003, шаг 3): кадр, в котором нажатие уворота началось рывком.
        /// Хук отмен для 1-004 (замах/отход) и 1-006 (натяжение): те карточки по этому флагу в своём кадре
        /// гасят замах/стрелу. Гасится в начале следующего кадра (<see cref="StepFrame(in MapShape, Input8, bool, TileSolid)"/>).
        /// </summary>
        public bool DashStarted;

        /// <summary>
        /// Отбрасывание (`02` 6.5; 1-001): как рывок, но задаётся сервером (состояние mover'а, не ввод) —
        /// предсказание клиента воспроизводит его по снимку, как уворот 0-029. Кадров осталось; 0 — нет.
        /// </summary>
        public byte KnockLeft;

        /// <summary>Скорость отбрасывания за кадр в unit × 10: <see cref="KnockSpeedFor"/> (база ÷ масса).</summary>
        public int KnockSpeedX10;

        /// <summary>Направление отбрасывания: код 1…8 (<see cref="Input8.DirectionIndex"/> + 1), 0 — нет.</summary>
        public byte KnockDir;

        /// <summary>Кадров отбрасывания: 0,15 с × 60 = 9 (6.5).</summary>
        public const int KnockFrames = 9;

        /// <summary>База отбрасывания за 9 кадров в unit × 10: 1 тайл = 256 unit → 2560.</summary>
        public const int KnockBaseX10 = 2560;

        /// <summary>Скорость отбрасывания за кадр в unit × 10: база ÷ масса = 2560 ÷ (9 × масса): 284 при массе 1 (1 т), 94 у танка (0,33 т).</summary>
        public static int KnockSpeedFor(int mass)
        {
            if (mass < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(mass), mass, "Масса для отбрасывания < 1.");
            }

            return KnockBaseX10 / (KnockFrames * mass);
        }

        public Mover(WorldPos position, int unitsPerTickX10 = MovementTick.BaseSpeedX10)
        {
            Position = position;
            Carry = default;
            UnitsPerTickX10 = unitsPerTickX10;
            Dodge = default;
            Stamina = Stamina.Full();
            DashStarted = false;
            KnockLeft = 0;
            KnockSpeedX10 = 0;
            KnockDir = 0;
        }

        /// <summary>
        /// Начать отбрасывание (1-001, `02` 6.5): рывок от <paramref name="origin"/> (источник удара) со скоростью
        /// <see cref="KnockSpeedFor"/> по направлению вектора origin → позиция (квант в 8 направлений,
        /// <see cref="Input8.FromAngle"/>; источник в позиции — направление <paramref name="fallback"/>).
        /// </summary>
        public void StartKnock(in MapShape map, WorldPos origin, int mass, Input8 fallback)
        {
            Int2 d = Torus.Delta(map, origin, Position);
            Input8 dir = (d.X == 0 && d.Y == 0) ? fallback : Input8.FromAngle(Angle16.Atan2(d.Y, d.X));
            if (!dir.IsMoving)
            {
                dir = fallback;
            }

            KnockLeft = KnockFrames;
            KnockSpeedX10 = KnockSpeedFor(mass);
            KnockDir = (byte)(dir.DirectionIndex + 1);
        }

        /// <summary>Направление отбрасывания как <see cref="Input8"/> (стойка, если направления нет).</summary>
        public Input8 KnockDirection => KnockDir == 0 ? Input8.None : Input8.FromIndex(KnockDir - 1);

        /// <summary>Сущность в позиции, свёрнутой в карту (тор: координата за миром невозможна).</summary>
        public static Mover At(in MapShape map, WorldPos position, int unitsPerTickX10 = MovementTick.BaseSpeedX10) =>
            new Mover(Torus.Wrap(map, position), unitsPerTickX10);

        /// <summary>
        /// Один такт 30 Гц: направление ввода → скорость на оси (прямая 512, диагональ 362 unit × 10)
        /// → целый шаг с остатком → коллизия по осям (X, потом Y). Возвращает новую позицию.
        /// </summary>
        public WorldPos Step(in MapShape map, Input8 input, TileSolid solid)
        {
            if (KnockLeft != 0)
            {
                // Отбрасывание задаётся кадров (0,15 с = 9 кадров ввода): такт — два кадра
                // того же ввода (60 Гц = 2 × 30 Гц, MovementTick.FramesPerTick) — тот же путь, что у сетевого персонажа.
                StepFrame(map, input, false, solid);
                if (KnockLeft != 0)
                {
                    StepFrame(map, input, false, solid);
                }

                return Position;
            }

            Position = MovementTick.Tick(map, Position, Carry, input, UnitsPerTickX10, solid, out Carry);
            return Position;
        }

        /// <summary>
        /// Один кадр ввода 60 Гц — полшага такта (скорость <see cref="UnitsPerTickX10"/> ÷ <see cref="MovementTick.FramesPerTick"/>)
        /// с тем же остатком <see cref="Carry"/>. Так шагают сетевой персонаж на сервере и предсказание клиента
        /// (docs/07, 2.6 п. 5 и 2.7 «Применение ввода»; 0-019): одна функция, одни входы — одна позиция.
        /// </summary>
        public WorldPos StepFrame(in MapShape map, Input8 input, TileSolid solid) => StepFrame(map, input, false, solid);

        /// <summary>
        /// Кадр ввода 60 Гц с уворотом (`02` 2.2, 2.4; 0-029) и выносливостью (2.3; 1-003). Нажатие (<paramref name="dodgePressed"/>) при готовом увороте
        /// (<see cref="Dodge.Ready"/>) и направлении ввода начинает рывок в этом же кадре — если выносливости ≥ 25
        /// (трата в кадре начала); если &lt; 25 — рывка нет, на кадр ставится <see cref="Stamina.Clicked"/> («щёлк»).
        /// В рывке персонаж идёт
        /// <see cref="Movement.Dodge.SpeedX10"/> за кадр в направлении рывка, ввод не действует; рывок кончается через
        /// <see cref="Movement.Dodge.DashFrames"/> кадров или в кадре, где упёрся в стену по всем осям направления; с конца —
        /// откат. Без рывка — шаг бега (<see cref="StepFrame(in MapShape, Input8, TileSolid)"/>), откат убывает.
        /// Порядок фиксирован: начать → шагнуть → счётчики. Без выделений, только целые.
        /// </summary>
        public WorldPos StepFrame(in MapShape map, Input8 input, bool dodgePressed, TileSolid solid)
        {
            // Выносливость шагает до всех веток: и в беге, и в рывке, и в отбрасывании (2.2: реген прерывается
            // рывком, но не отбрасыванием; трата — только в ветке начала рывка ниже).
            Stamina.Tick(Dodge.Dashing);
            DashStarted = false;

            if (KnockLeft != 0)
            {
                // Отбрасывание (1-001, `02` 6.5): рывок задаёт сервер — ввод не действует («цель не действует»,
                // «ввод игрока копится»: после конца цель идёт по текущему вводу), уворот в это время недоступен
                // (<see cref="Movement.Dodge.Ready"/> проверяется ниже с условием KnockLeft == 0). Шаг — как рывок;
                // упёрся в стену — остановка без урона: рывок коротается (приёмка 6.10 п. 5).
                Input8 kdir = KnockDirection;
                int kwantX = (Carry.X + kdir.SpeedX10(KnockSpeedX10)) / MovementTick.SpeedScale;
                int kwantY = (Carry.Y + kdir.SpeedY10(KnockSpeedX10)) / MovementTick.SpeedScale;
                WorldPos kfrom = Position;
                Position = MovementTick.Tick(map, Position, Carry, kdir, KnockSpeedX10, solid, out Carry);
                Int2 kmoved = Torus.Delta(map, kfrom, Position);

                bool kstuckX = kwantX == 0 || kmoved.X != kwantX;
                bool kstuckY = kwantY == 0 || kmoved.Y != kwantY;
                KnockLeft--;
                if (kstuckX && kstuckY)
                {
                    KnockLeft = 0;
                }

                return Position;
            }

            if (dodgePressed && input.IsMoving && Dodge.Ready)
            {
                if (Stamina.CanDodge)
                {
                    Stamina.Spend();
                    Dodge.DashLeft = Movement.Dodge.DashFrames;
                    Dodge.Elapsed = 0;
                    Dodge.Direction = (byte)(input.DirectionIndex + 1);
                    DashStarted = true;
                }
                else
                {
                    Stamina.Clicked = true; // «щёлк» (2.3): выносливости < 25, рывка нет
                }
            }

            if (Dodge.Elapsed < Movement.Dodge.ElapsedCap && Dodge.Direction != 0)
            {
                Dodge.Elapsed++;
            }

            if (!Dodge.Dashing)
            {
                if (Dodge.Cooldown != 0)
                {
                    Dodge.Cooldown--;
                }

                Position = MovementTick.Tick(map, Position, Carry, input, UnitsPerTickX10 / MovementTick.FramesPerTick, solid, out Carry);
                return Position;
            }

            Input8 dir = Dodge.DashDirection;
            int wantX = (Carry.X + dir.SpeedX10(Movement.Dodge.SpeedX10)) / MovementTick.SpeedScale;
            int wantY = (Carry.Y + dir.SpeedY10(Movement.Dodge.SpeedX10)) / MovementTick.SpeedScale;
            WorldPos from = Position;
            Position = MovementTick.Tick(map, Position, Carry, dir, Movement.Dodge.SpeedX10, solid, out Carry);
            Int2 moved = Torus.Delta(map, from, Position);

            // Упёрся: по каждой оси направления пройдено не всё (по оси без движения — «упёрто» само собой).
            bool stuckX = wantX == 0 || moved.X != wantX;
            bool stuckY = wantY == 0 || moved.Y != wantY;
            Dodge.DashLeft--;
            if (stuckX && stuckY)
            {
                Dodge.DashLeft = 0;
            }

            if (Dodge.DashLeft == 0)
            {
                Dodge.Cooldown = Movement.Dodge.CooldownFrames;
            }

            return Position;
        }

        /// <summary>Один такт в пустом мире (тайлов нет): чистый пробег по тору.</summary>
        public WorldPos Step(in MapShape map, Input8 input) => Step(map, input, Collision.Empty);

        /// <summary>Тайл, в котором стоит сущность.</summary>
        public TileCoord TileOn(in MapShape map) => Torus.TileOf(map, Position);

        public override string ToString() => $"mover({Position.X}/256, {Position.Y}/256) v={UnitsPerTickX10} {Dodge}";
    }
}
