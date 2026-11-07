using System;
using Orpheus.Core.Geometry;

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
    /// Структура: 2 × int32 (позиция) + 2 × int32 (остаток) + int32 (скорость), памяти в такте не выделяет.
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

        public Mover(WorldPos position, int unitsPerTickX10 = MovementTick.BaseSpeedX10)
        {
            Position = position;
            Carry = default;
            UnitsPerTickX10 = unitsPerTickX10;
        }

        /// <summary>Сущность в позиции, свёрнутой в карту (тор: координата за миром невозможна).</summary>
        public static Mover At(in MapShape map, WorldPos position, int unitsPerTickX10 = MovementTick.BaseSpeedX10) =>
            new Mover(Torus.Wrap(map, position), unitsPerTickX10);

        /// <summary>
        /// Один такт 30 Гц: направление ввода → скорость на оси (прямая 512, диагональ 362 unit × 10)
        /// → целый шаг с остатком → коллизия по осям (X, потом Y). Возвращает новую позицию.
        /// </summary>
        public WorldPos Step(in MapShape map, Input8 input, TileSolid solid)
        {
            Position = MovementTick.Tick(map, Position, Carry, input, UnitsPerTickX10, solid, out Carry);
            return Position;
        }

        /// <summary>
        /// Один кадр ввода 60 Гц — полшага такта (скорость <see cref="UnitsPerTickX10"/> ÷ <see cref="MovementTick.FramesPerTick"/>)
        /// с тем же остатком <see cref="Carry"/>. Так шагают сетевой персонаж на сервере и предсказание клиента
        /// (docs/07, 2.6 п. 5 и 2.7 «Применение ввода»; 0-019): одна функция, одни входы — одна позиция.
        /// </summary>
        public WorldPos StepFrame(in MapShape map, Input8 input, TileSolid solid)
        {
            Position = MovementTick.Tick(map, Position, Carry, input, UnitsPerTickX10 / MovementTick.FramesPerTick, solid, out Carry);
            return Position;
        }

        /// <summary>Один такт в пустом мире (тайлов нет): чистый пробег по тору.</summary>
        public WorldPos Step(in MapShape map, Input8 input) => Step(map, input, Collision.Empty);

        /// <summary>Тайл, в котором стоит сущность.</summary>
        public TileCoord TileOn(in MapShape map) => Torus.TileOf(map, Position);

        public override string ToString() => $"mover({Position.X}/256, {Position.Y}/256) v={UnitsPerTickX10}";
    }
}
