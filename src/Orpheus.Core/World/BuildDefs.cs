using Orpheus.Core.Sim;

namespace Orpheus.Core.World
{
    /// <summary>
    /// Описание постройки данными (1-011, <c>05</c> 1.3 «Что можно построить», акт I): рецепт (материалы и
    /// количество), максимум прочности и признак «пол». Из таблицы читают сервер (списание стоимости и возврат
    /// при сносе), клиент (предсказание списания и возврата) и палитра Unity (1-020) — одно место, без рефлексии (IL2CPP).
    /// <para>
    /// Ключ таблицы: id постройки (<see cref="ObjectId.Torch"/>, <see cref="ObjectId.WoodWall"/>, …) или id
    /// построенного пола (<see cref="FloorId.WoodFloor"/>…<see cref="FloorId.Platform"/>) — тайл пола несёт пол,
    /// а не объект, нового ObjectId полам не нужен. Значения 8…10 в слое объекта — постройки
    /// (<see cref="ObjectId.Gate"/>…); как ключ постановки пола они не пересекаются: запрос пола сервер берёт
    /// из <c>TileRequestMessage.Obj</c>, а в слой кладётся пол, объект — 0.
    /// </para>
    /// </summary>
    public readonly struct BuildDef
    {
        /// <summary>Первый материал рецепта.</summary>
        public readonly Material Material1;

        /// <summary>Сколько первого материала стоит.</summary>
        public readonly int Count1;

        /// <summary>Второй материал рецепта (считается, только если <see cref="Count2"/> &gt; 0).</summary>
        public readonly Material Material2;

        /// <summary>Сколько второго материала стоит; 0 — рецепту хватает одного.</summary>
        public readonly int Count2;

        /// <summary>Максимум прочности (<see cref="BuildRecord.Integrity"/> при полной постановке); полы — <see cref="BuildRecord.MaxIntegrity"/>.</summary>
        public readonly ushort MaxIntegrity;

        /// <summary>Пол: ставится в слой пола, объект на тайле остаётся пустым.</summary>
        public readonly bool IsFloor;

        public BuildDef(Material material1, int count1, Material material2, int count2, ushort maxIntegrity, bool isFloor)
        {
            Material1 = material1;
            Count1 = count1;
            Material2 = material2;
            Count2 = count2;
            MaxIntegrity = maxIntegrity;
            IsFloor = isFloor;
        }
    }

    /// <summary>
    /// Палитра построек вехи 1 (1-011): таблица <see cref="BuildDef"/> по ключу (id объекта или id построенного пола).
    /// </summary>
    public static class BuildDefs
    {
        /// <summary>Наибольший ключ таблицы (id построек ≤ <see cref="ObjectId.MaxKnownId"/> (костёр 26, 1-014), полы — до <see cref="FloorId.MaxKnown"/>).</summary>
        public const int MaxKey = ObjectId.MaxKnownId > FloorId.MaxKnown ? ObjectId.MaxKnownId : FloorId.MaxKnown;

        private static readonly BuildDef[] Table = new BuildDef[MaxKey + 1];

        static BuildDefs()
        {
            // Акт I (`05` 1.3, цены из `00` 5.2): пол 1, стена 2 / 120 и 2 / 300, забор 1 / 45, дверь 6 / 90, факел 1 + 1 смола / 10.
            Table[ObjectId.Torch] = new BuildDef(Material.Wood, 1, Material.Resin, 1, 10, false);
            Table[ObjectId.WoodWall] = new BuildDef(Material.Wood, 2, Material.Wood, 0, 120, false);
            Table[ObjectId.StoneWall] = new BuildDef(Material.Stone, 2, Material.Stone, 0, 300, false);
            Table[ObjectId.WoodFence] = new BuildDef(Material.Wood, 1, Material.Wood, 0, 45, false);
            Table[ObjectId.Door] = new BuildDef(Material.Wood, 6, Material.Wood, 0, 90, false);

            // Костёр (`05` 101, `06` 339: 8 камня + 4 древесины, прочность 60; свет 4 / 8 — `01` 6, 1-014).
            Table[ObjectId.Campfire] = new BuildDef(Material.Stone, 8, Material.Wood, 4, 60, false);

            // Полы: «не бьют» — прочность полная, снос возвращает рецепт целиком; над глубиной настил 2 — за генератором (`01` 5, `[док]`).
            Table[FloorId.WoodFloor] = new BuildDef(Material.Wood, 1, Material.Wood, 0, BuildRecord.MaxIntegrity, true);
            Table[FloorId.StoneFloor] = new BuildDef(Material.Stone, 1, Material.Stone, 0, BuildRecord.MaxIntegrity, true);
            Table[FloorId.Platform] = new BuildDef(Material.Wood, 1, Material.Wood, 0, BuildRecord.MaxIntegrity, true);
        }

        /// <summary>Описание постройки по ключу (id объекта или id построенного пола); вне таблицы — false.</summary>
        public static bool TryGet(ushort key, out BuildDef def)
        {
            if (key <= MaxKey && Table[key].Count1 > 0)
            {
                def = Table[key];
                return true;
            }

            def = default;
            return false;
        }

        /// <summary>
        /// Возврат при сносе (<c>05</c> 1.3): неповреждённой постройки — рецепт целиком; повреждённой — по каждому
        /// материалу ⌊количество × прочность ÷ максимум⌋.
        /// </summary>
        public static int RefundOf(int count, ushort integrity, ushort maxIntegrity) =>
            maxIntegrity == 0 ? 0 : (int)((long)count * integrity / maxIntegrity);

        /// <summary>Такт конца обломков: 10 с после сноса (`05` 1.3) при такте 30 Гц — 300 тиков.</summary>
        public const int DebrisTicks = 300;

        /// <summary>Такт жизни флага «урон последние 10 с» (<see cref="BuildFlags.DamagedRecent"/>).</summary>
        public const int DamageFlagTicks = 300;
    }
}
