using System;
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

        /// <summary>Предмет-постройка (E2-11, <see cref="Items.WoodWall"/>…): постановка снимает один такой предмет из руки,
        /// снос возвращает один; материалы выше — цена рецепта (<see cref="RecipeTable"/>) и цена починки.</summary>
        public readonly byte Item;

        public BuildDef(Material material1, int count1, Material material2, int count2, ushort maxIntegrity, bool isFloor, byte item)
        {
            Item = item;
            Material1 = material1;
            Count1 = count1;
            Material2 = material2;
            Count2 = count2;
            MaxIntegrity = maxIntegrity;
            IsFloor = isFloor;
        }
    }

    /// <summary>
    /// Палитра построек вехи 1 (1-011): таблица <see cref="BuildDef"/> по ключу постройки. Ключ — id объекта или
    /// <b>ключ пола</b> <see cref="FloorKey"/> = <see cref="FloorKeyBase"/> + id построенного пола (E2-5: раньше ключом пола
    /// был сам id пола 8…10 и он совпадал с id ворот, сундука и кровати — сундук и кровать нельзя было поставить, а снос
    /// сундука возвращал цену каменного пола).
    /// </summary>
    public static class BuildDefs
    {
        /// <summary>Ключи полов начинаются здесь (id объектов — до <see cref="ObjectId.MaxKnownId"/>, в 10 битах id).</summary>
        public const ushort FloorKeyBase = 0x100;

        /// <summary>Ключи построенных полов (E2-5).</summary>
        public const ushort WoodFloorKey = FloorKeyBase + FloorId.WoodFloor;
        public const ushort StoneFloorKey = FloorKeyBase + FloorId.StoneFloor;
        public const ushort PlatformKey = FloorKeyBase + FloorId.Platform;

        /// <summary>Наибольший ключ объекта в таблице.</summary>
        public const int MaxKey = ObjectId.MaxKnownId;

        private static readonly BuildDef[] Table = new BuildDef[MaxKey + 1];
        private static readonly BuildDef[] Floors = new BuildDef[FloorId.MaxKnown + 1];

        /// <summary>Ключ постройки «пол» по id пола.</summary>
        public static ushort FloorKey(byte floor) => (ushort)(FloorKeyBase + floor);

        /// <summary>Id пола по ключу пола (смысл есть, если <see cref="IsFloorKeyRange"/>).</summary>
        public static byte FloorOf(ushort key) => (byte)(key - FloorKeyBase);

        /// <summary>Ключ лежит в диапазоне ключей полов.</summary>
        public static bool IsFloorKeyRange(ushort key) => key >= FloorKeyBase && key <= FloorKeyBase + FloorId.MaxKnown;

        static BuildDefs()
        {
            // Акт I (`05` 1.3, цены из `00` 5.2): пол 1, стена 2 / 120 и 2 / 300, забор 1 / 45, дверь 6 / 90, факел 1 + 1 смола / 10.
            Table[ObjectId.Torch] = new BuildDef(Material.Wood, 1, Material.Resin, 1, 10, false, Items.Torch);
            Table[ObjectId.WoodWall] = new BuildDef(Material.Wood, 2, Material.Wood, 0, 120, false, Items.WoodWall);
            Table[ObjectId.StoneWall] = new BuildDef(Material.Stone, 2, Material.Stone, 0, 300, false, Items.StoneWall);
            Table[ObjectId.WoodFence] = new BuildDef(Material.Wood, 1, Material.Wood, 0, 45, false, Items.WoodFence);
            Table[ObjectId.Door] = new BuildDef(Material.Wood, 6, Material.Wood, 0, 90, false, Items.Door);

            // Костёр (`05` 101, `06` 339: 8 камня + 4 древесины, прочность 60; свет 4 / 8 — `01` 6, 1-014).
            Table[ObjectId.Campfire] = new BuildDef(Material.Stone, 8, Material.Wood, 4, 60, false, Items.Campfire);

            // Верстак I (E2-3; `06` 3.3: 10 древесины + 6 камня, руками, прочность 100; ставится молотом — `06` 3.7 п. 1).
            // В документе 2 × 2; пока 1 × 1 — многотайловой постановки в ядре нет (допущение E2-3).
            Table[ObjectId.Workbench] = new BuildDef(Material.Wood, 10, Material.Stone, 6, 100, false, Items.Workbench);

            // Сундук и кровать (E2-5; `05` 1.3: сундук 8 древесины / 100, кровать 10 древесины + 5 волокна / 40).
            // Кровать в документе 1 × 2; пока 1 × 1 — многотайловой постановки в ядре нет (допущение E2-5, как у верстака).
            Table[ObjectId.Chest] = new BuildDef(Material.Wood, 8, Material.Wood, 0, 100, false, Items.Chest);
            Table[ObjectId.Bed] = new BuildDef(Material.Wood, 10, Material.Fiber, 5, 40, false, Items.Bed);

            // Полы: «не бьют» — прочность полная, снос возвращает рецепт целиком; над глубиной настил 2 — за генератором (`01` 5, `[док]`).
            Floors[FloorId.WoodFloor] = new BuildDef(Material.Wood, 1, Material.Wood, 0, BuildRecord.MaxIntegrity, true, Items.WoodFloor);
            Floors[FloorId.StoneFloor] = new BuildDef(Material.Stone, 1, Material.Stone, 0, BuildRecord.MaxIntegrity, true, Items.StoneFloor);
            Floors[FloorId.Platform] = new BuildDef(Material.Wood, 1, Material.Wood, 0, BuildRecord.MaxIntegrity, true, Items.Platform);
        }

        private static readonly ushort[] PaletteKeys =
        {
            WoodFloorKey, StoneFloorKey, PlatformKey,
            ObjectId.WoodWall, ObjectId.StoneWall, ObjectId.WoodFence, ObjectId.Door,
            ObjectId.Torch, ObjectId.Campfire, ObjectId.Workbench, ObjectId.Chest, ObjectId.Bed,
        };

        /// <summary>
        /// Лента построек режима стройки (E2-5, `05` 1.3 «палитра»): ключи таблицы в порядке показа — полы, стены, забор,
        /// дверь, свет, станки, хранение, мебель. Калитки отдельной нет: калитка — дверь в заборе (`05` 3.3).
        /// </summary>
        public static ReadOnlySpan<ushort> Palette => PaletteKeys;

        /// <summary>Предмет-постройка по ключу (E2-11); не постройка — <see cref="Items.None"/>.</summary>
        public static byte ItemOf(ushort key) => TryGet(key, out BuildDef def) ? def.Item : Items.None;

        /// <summary>
        /// Ключ постройки по предмету в руке (E2-11): стена — <see cref="ObjectId.WoodWall"/>, пол — ключ пола; не постройка — false.
        /// Перебор ленты (12 строк) — без словаря и рефлексии (IL2CPP).
        /// </summary>
        public static bool TryKeyOf(byte item, out ushort key)
        {
            if (item != Items.None)
            {
                foreach (ushort k in PaletteKeys)
                {
                    if (ItemOf(k) == item)
                    {
                        key = k;
                        return true;
                    }
                }
            }

            key = 0;
            return false;
        }

        /// <summary>Хватает ли в сумке материалов на постройку (та же смета, что списывает такт).</summary>
        public static bool Affordable(in BuildDef def, in Inventory bag)
        {
            if (def.Count2 > 0 && def.Material2 == def.Material1)
            {
                return bag.CountAt(def.Material1) >= def.Count1 + def.Count2;
            }

            return bag.CountAt(def.Material1) >= def.Count1 && (def.Count2 <= 0 || bag.CountAt(def.Material2) >= def.Count2);
        }

        /// <summary>
        /// Цена починки (`05` 1.3): по каждому материалу рецепта ⌈количество × недостающая доля прочности⌉
        /// (каменная стена на половине прочности — 1 камень).
        /// </summary>
        public static int RepairCostOf(int count, ushort integrity, ushort maxIntegrity) =>
            maxIntegrity == 0 || integrity >= maxIntegrity ? 0 : (int)(((long)count * (maxIntegrity - integrity) + maxIntegrity - 1) / maxIntegrity);

        /// <summary>Описание постройки по ключу (id объекта или ключ пола <see cref="FloorKey"/>); вне таблицы — false.</summary>
        public static bool TryGet(ushort key, out BuildDef def)
        {
            if (key > ObjectId.IdMask && !ObjectId.IsPart(key))
            {
                key = ObjectId.IdOf(key); // E2-16: повёрнутый объект (биты 10–11) — та же постройка
            }

            if (key <= MaxKey && Table[key].Count1 > 0)
            {
                def = Table[key];
                return true;
            }

            if (IsFloorKeyRange(key) && Floors[key - FloorKeyBase].Count1 > 0)
            {
                def = Floors[key - FloorKeyBase];
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

        /// <summary>Такт жизни флага «урон последние 3 с — чинить нельзя» (<see cref="BuildFlags.RepairBlocked"/>, `05` 1.3, C-005).</summary>
        public const int RepairBlockTicks = 90;

        /// <summary>
        /// Самовосстановление (E2-14, слово Автора 15.11 «повреждения сами лечатся по 3 % в секунду»): повреждённая постройка
        /// (верх тайла) набирает 3 % максимальной прочности в секунду — целочисленно, накопителем в 1/3000 максимума за такт.
        /// </summary>
        public const int HealPercentPerSecond = 3;

        /// <summary>Восстановление начинается через 3 с после последнего урона — та же задержка, что у починки (E2-14, C-005).</summary>
        public const int HealDelayTicks = RepairBlockTicks;

        /// <summary>
        /// Шаг записи восстановления в мир — раз в секунду на постройку (или раньше — на такте, когда она станет целой):
        /// каждая запись — правка тайла и патч чанка клиентам, чаще канал при осаде не нужен (E2-14).
        /// </summary>
        public const int HealStepTicks = 30;

        /// <summary>Знаменатель накопителя: 100 % × 30 тактов в секунду — прибавка такта = 3 × максимум / 3000 (E2-14).</summary>
        public const int HealDenominator = 100 * HealStepTicks;

        /// <summary>Правок восстановления за такт не больше (E2-14): 500 построек, повреждённых разом, расходятся по тактам.</summary>
        public const int HealEditsPerTick = 64;

        /// <summary>
        /// Время починки — 0,5 с (`05` 1.3 «одно применение, 0,5 с», C-005): кадров ввода 60 Гц, как время копания
        /// (<see cref="DigTimes"/>). Прогресс ведёт сервер; отошёл из радиуса, убрал молот, умер — «прервано».
        /// </summary>
        public const int RepairFrames = 30;
    }
}
