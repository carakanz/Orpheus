using System;
using Orpheus.Core.Geometry;

namespace Orpheus.Core.Movement
{
    /// <summary>
    /// Один такт движения 30 Гц как чистая функция (docs/07, 4.2 и 11.5): клиент в предсказании и
    /// сервер в решении вызывают именно её, на одних и тех же целых входах — поэтому предсказание
    /// клиента и решение сервера совпадают побитово.
    /// <para>
    /// Скорость — целое unit × 10 за такт (work/refs/movement.md): базовый бег 6 тайлов/с при 30 Гц
    /// = 0,2 тайла за такт = 51,2 unit → <see cref="BaseSpeedX10"/> = 512; диагональ на ось =
    /// <see cref="DiagonalPerAxisX10"/> = 362. За такт <c>carry += v</c>, шаг = <c>carry / 10</c>,
    /// <c>carry %= 10</c>: за 5 тактов ровно 256 unit = 1 тайл, дробный остаток живёт на сущности
    /// целым числом (см. <see cref="Mover.Carry"/>), float-накопления скорости нет.
    /// </summary>
    public static class MovementTick
    {
        /// <summary>Базовый бег в unit × 10 за такт: 6 т/с ÷ 30 Гц = 51,2 unit → 512 (docs/00, 5.1).</summary>
        public const int BaseSpeedX10 = 512;

        /// <summary>Диагональ на ось в unit × 10 за такт: 512 ÷ √2 = 362,04 → 362 (work/refs/movement.md).</summary>
        public const int DiagonalPerAxisX10 = 362;

        /// <summary>Множитель скорости: unit × 10 → unit за такт делением на 10.</summary>
        public const int SpeedScale = 10;

        /// <summary>Тактов базового бега на один тайл: 5 × 512 = 2560 unit × 10 = 256 unit.</summary>
        public const int TicksPerTile = 5;

        /// <summary>Тактов базового бега на полный оборот мира стороны W: W × 256 × 10 ÷ 512 = W × 5 (4096² → 20 480).</summary>
        public static int TicksPerLap(int worldSize) => worldSize * TicksPerTile;

        /// <summary>
        /// Шаг с накоплением скорости: принимает остаток <paramref name="carry"/> (десятые доли unit
        /// от прошлого такта), возвращает новую позицию и отдаёт новый остаток в <paramref name="newCarry"/>.
        /// Коллизия — <see cref="Collision.Move"/>: сначала X, потом Y (docs/00, 5.1).
        /// </summary>
        public static WorldPos Tick(
            in MapShape map,
            WorldPos pos,
            Int2 carry,
            Input8 input,
            int speedX10,
            TileSolid solid,
            out Int2 newCarry)
        {
            int cx = carry.X + input.SpeedX10(speedX10);
            int cy = carry.Y + input.SpeedY10(speedX10);
            int stepX = cx / SpeedScale;
            int stepY = cy / SpeedScale;
            newCarry = new Int2(cx % SpeedScale, cy % SpeedScale);
            return Collision.Move(map, pos, stepX, stepY, solid);
        }

        /// <summary>Шаг без остатка скорости (<c>carry = 0</c>, базовая скорость): 51 unit за такт. Для бега используй перегрузку с остатком или <see cref="Mover.Step"/>.</summary>
        public static WorldPos Tick(in MapShape map, WorldPos pos, Input8 input, TileSolid solid) =>
            Tick(map, pos, default, input, BaseSpeedX10, solid, out _);

        /// <summary>Шаг без остатка в пустом мире (тайлов нет).</summary>
        public static WorldPos Tick(in MapShape map, WorldPos pos, Input8 input) =>
            Tick(map, pos, default, input, BaseSpeedX10, Collision.Empty, out _);
    }
}
