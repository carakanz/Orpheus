using System;

namespace Orpheus.Core.World
{
    /// <summary>
    /// Идентификатор пола — 1 байт на тайл (docs/07, 3.3; docs/05, 1.3). Пол бывает
    /// природный (по биому, `01`) и построенный игроком (дощатый, каменный, настил).
    /// 0 — пусто: пола нет (проход, провал).
    /// <para>
    /// Свойства id — статическая таблица <see cref="Properties"/> по индексу id, без рефлексии:
    /// «твёрдый» (по тайлу стоят — сушая опора), «проходимый» (через тайл проходят),
    /// «природный» (его записывают в <see cref="BuildRecord.NaturalFloor"/>, когда сверху встаёт постройка),
    /// «построенный» (снимают молотом за 0,5 с, `05`, 1.3).
    /// </para>
    /// <para>
    /// Набор id в этой карточке — эталонный для тестов ядра: точная таблица материалов и рецептов — `06`,
    /// она дополняет таблицу, а не меняет смысл флагов. Новые id — только в конец таблицы.
    /// </para>
    /// </summary>
    public static class FloorId
    {
        /// <summary>0 — пола нет.</summary>
        public const byte Empty = 0;

        /// <summary>Природные полы по биому (`01`).</summary>
        public const byte Grass = 1;
        public const byte Dirt = 2;
        public const byte Stone = 3;
        public const byte Sand = 4;
        public const byte Snow = 5;

        /// <summary>Жидкости: по воде идут (в брод), лава — нет.</summary>
        public const byte Water = 6;
        public const byte Lava = 7;

        /// <summary>Построенные полы (`05`, 1.3): настил — настил над жидкостью.</summary>
        public const byte WoodFloor = 8;
        public const byte StoneFloor = 9;
        public const byte Platform = 10;

        /// <summary>Наибольший описанный id: таблица заполнена до него включительно.</summary>
        public const byte MaxKnown = 10;

        /// <summary>Размер таблицы свойств (id 0…<see cref="MaxKnown"/>).</summary>
        public const int TableSize = MaxKnown + 1;

        /// <summary>Свойства пола (docs/05, 1.3: проходимость и природный пол под постройкой).</summary>
        [Flags]
        public enum Properties : byte
        {
            None = 0,

            /// <summary>По тайлу стоят: сушая опора (wood floor, stone, grass). Вода и лава — не твёрдые.</summary>
            Solid = 1,

            /// <summary>Через тайл проходят: вода — да, лава и скала — нет.</summary>
            Passable = 2,

            /// <summary>Природный пол: под построенным его хранят в <see cref="BuildRecord.NaturalFloor"/>.</summary>
            Natural = 4,

            /// <summary>Построенный игроком: снимается молотом за 0,5 с, возвращается 100 %.</summary>
            Built = 8,
        }

        // Таблица: индекс = id. Порядок строк = порядок id, без рефлексии и без словаря.
        private static readonly Properties[] Table =
        {
            Properties.Passable,                                  // Empty
            Properties.Solid | Properties.Passable | Properties.Natural, // Grass
            Properties.Solid | Properties.Passable | Properties.Natural, // Dirt
            Properties.Solid | Properties.Passable | Properties.Natural, // Stone (почва, не скала-объект)
            Properties.Solid | Properties.Passable | Properties.Natural, // Sand
            Properties.Solid | Properties.Passable | Properties.Natural, // Snow
            Properties.Passable | Properties.Natural,             // Water
            Properties.Natural,                                   // Lava: идут в неё — нет
            Properties.Solid | Properties.Passable | Properties.Built, // WoodFloor
            Properties.Solid | Properties.Passable | Properties.Built, // StoneFloor
            Properties.Solid | Properties.Passable | Properties.Built, // Platform (настил)
        };

        /// <summary>Свойства id; неизвестный id (выше <see cref="MaxKnown"/>) — <see cref="Properties.None"/>.</summary>
        public static Properties Of(byte id) => id <= MaxKnown ? Table[id] : Properties.None;

        /// <summary>По тайлу можно стоять (сушая опора).</summary>
        public static bool IsSolid(byte id) => (Of(id) & Properties.Solid) != 0;

        /// <summary>Через тайл можно пройти.</summary>
        public static bool IsPassable(byte id) => (Of(id) & Properties.Passable) != 0;

        /// <summary>Пол природный: его записывают под построенный.</summary>
        public static bool IsNatural(byte id) => (Of(id) & Properties.Natural) != 0;

        /// <summary>Пол построенный игроком.</summary>
        public static bool IsBuilt(byte id) => (Of(id) & Properties.Built) != 0;

        /// <summary>Id описан в таблице (0…<see cref="MaxKnown"/>).</summary>
        public static bool Known(byte id) => id <= MaxKnown;
    }
}
