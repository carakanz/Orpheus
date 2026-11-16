namespace Orpheus.Core.World
{
    /// <summary>
    /// Замедляющая местность (C-018, слово Автора 15.11: «кусты и воду проходимыми, но замедляют скорость») — единственная
    /// таблица множителей скорости ядра. Множитель — в 1/256 (<see cref="One"/> = обычный ход), берётся по тайлу под центром
    /// (ногами) существа: пол × объект. Движение (<see cref="Movement.Mover"/>, бег и уворот), путь врагов (стоимость ~1/множитель)
    /// и столкновения (такой тайл не твёрдый) читают только эту таблицу.
    /// <para>
    /// Флаги <see cref="FloorId.Properties.Passable"/> и <see cref="ObjectId.Properties.Solid"/> у воды и куста не менялись:
    /// по ним работает генератор (связность, залежи — его вывод не меняется). «Проходима для существ» = флаг проходимости
    /// или строка этой таблицы (<see cref="WalkFloor"/>, <see cref="WalkObject"/>). Строить на воде по-прежнему нельзя
    /// (вода не <see cref="FloorId.Properties.Solid"/>, <see cref="EditRules"/>).
    /// </para>
    /// </summary>
    public static class TerrainSlow
    {
        /// <summary>Обычный ход: множитель 1 = 256/256.</summary>
        public const int One = 256;

        /// <summary>Вода (<see cref="FloorId.Water"/>): ×0,45 (предварительно, C-018) — 115/256.</summary>
        public const int WaterQ8 = 115;

        /// <summary>Куст (<see cref="ObjectId.Bush"/>): ×0,6 (предварительно, C-018) — 154/256.</summary>
        public const int BushQ8 = 154;

        /// <summary>Множитель пола (1/256): вода — <see cref="WaterQ8"/>, прочее — <see cref="One"/>.</summary>
        public static int FloorQ8(byte floor) => floor == FloorId.Water ? WaterQ8 : One;

        /// <summary>Множитель объекта по сырому значению слоя (1/256): куст — <see cref="BushQ8"/>; часть многотайлового и прочее — <see cref="One"/>.</summary>
        public static int ObjectQ8(ushort raw) => !ObjectId.IsPart(raw) && ObjectId.IdOf(raw) == ObjectId.Bush ? BushQ8 : One;

        /// <summary>Множитель тайла: пол × объект (куст в воде — оба), в 1/256.</summary>
        public static int TileQ8(byte floor, ushort raw)
        {
            int f = FloorQ8(floor);
            int o = ObjectQ8(raw);
            return o == One ? f : f == One ? o : (f * o) >> 8;
        }

        /// <summary>Пол проходим для существ: флаг проходимости или замедляющий пол (вода).</summary>
        public static bool WalkFloor(byte floor) => FloorId.IsPassable(floor) || FloorQ8(floor) != One;

        /// <summary>Объект замедляет, а не держит (куст): для столкновений и пути он не твёрдый.</summary>
        public static bool WalkObject(ushort raw) => ObjectQ8(raw) != One;

        /// <summary>
        /// Множитель стоимости шага пути (в половинках, <see cref="Pathing.PathCost"/>): ≈ 2 / множитель, округление к ближайшему,
        /// не меньше <see cref="Pathing.PathCost.Normal"/>. Вода 115 → 4 (×2), куст 154 → 3 (×1,5).
        /// </summary>
        public static byte PathFactor(int q8)
        {
            if (q8 >= One)
            {
                return Pathing.PathCost.Normal;
            }

            int f = (2 * One + q8 / 2) / q8;
            return (byte)(f < Pathing.PathCost.Normal ? Pathing.PathCost.Normal : f > 250 ? 250 : f);
        }
    }
}
