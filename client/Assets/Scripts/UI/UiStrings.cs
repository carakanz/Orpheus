#nullable enable
using System.Collections.Generic;
using Orpheus.Core.Sim;

namespace Orpheus.Client.UI
{
    /// <summary>
    /// Строки интерфейса (E2-2) — все русские подписи окон в одном файле. Названия предметов — по ключу строки
    /// таблицы предметов (<see cref="ItemDef.Key"/>); нет подписи — показывается сам ключ (новая строка таблицы).
    /// </summary>
    public static class UiStrings
    {
        public const string BagTitle = "Сумка";
        public const string BagHint = "ЛКМ — взять / положить      ПКМ — взять половину\nShift+ЛКМ — перекинуть      ЛКМ мимо окна — выбросить";
        public const string BagDead = "Вы погибли: сумка откроется для действий после возрождения";
        public const string BagKeys = "Tab / I";

        static readonly Dictionary<string, string> ItemNames = new Dictionary<string, string>
        {
            ["stone_pickaxe"] = "Каменная кирка",
            ["stone_axe"] = "Каменный топор",
            ["hammer"] = "Молот",
            ["wooden_sword"] = "Деревянный меч",
            ["simple_bow"] = "Простой лук",
            ["torch"] = "Факел",
            ["copper_pickaxe"] = "Медная кирка",
            ["wood"] = "Древесина",
            ["stone"] = "Камень",
            ["resin"] = "Смола",
            ["fiber"] = "Волокно",
            ["copper_ore"] = "Медная руда",
            ["arrows"] = "Стрелы",
            ["copper_ingot"] = "Медный слиток",
            ["rope"] = "Верёвка",
            ["copper_axe"] = "Медный топор",
            ["copper_sword"] = "Медный меч",
            ["copper_bow"] = "Медный лук",
            ["meat"] = "Мясо",
            ["roast_meat"] = "Жареное мясо",
        };

        // ---------- окно крафта (E2-4) ----------

        public const string CraftTitle = "Изготовление";
        public const string CraftKey = "C";
        public const string CraftNeeds = "Нужно:";
        public const string CraftQueue = "Очередь — щелчок по позиции отменяет её";
        public const string CraftQueueEmpty = "Очередь пуста";
        public const string CraftQueueStopped = "Стоит: подойдите к станку";
        public const string CraftDead = "Вы погибли: изготовление — после возрождения";
        public const string CraftMake1 = "Сделать 1";
        public const string CraftMake5 = "Сделать 5";
        public const string CraftMakeAll = "Сделать всё";

        /// <summary>Кнопка «всё», когда известно, на сколько хватает.</summary>
        public static string CraftMakeAllOf(int n) => $"Все {n}";
        public const string CraftHint = "Enter — сделать 1    Shift+Enter — 5    Ctrl+Enter — всё    Del — отменить последнюю\nPgUp / PgDn — рецепт    Home / End — вкладка    C, Esc — закрыть";

        /// <summary>Вкладка окна крафта: название станка.</summary>
        public static string StationName(CraftStation s) => s switch
        {
            CraftStation.Workbench => "Верстак",
            CraftStation.Campfire => "Костёр",
            _ => "Руками",
        };

        /// <summary>Пометка рецепта, станка которого рядом нет.</summary>
        public static string StationNeeded(CraftStation s) => s switch
        {
            CraftStation.Workbench => "нужен верстак",
            CraftStation.Campfire => "нужен костёр",
            _ => string.Empty,
        };

        /// <summary>Подсказка у быстрой панели: рядом станок, окно крафта — клавиша C.</summary>
        public static string StationHint(CraftStation s) => s == CraftStation.Campfire ? "C — костёр" : "C — верстак";

        /// <summary>Название рецепта = название того, что получается (в срезе у предмета один рецепт).</summary>
        public static string RecipeName(int recipe) => ItemName(RecipeTable.Get(recipe).Result);

        /// <summary>Строка под названием в карточке: сколько получится и за сколько.</summary>
        public static string RecipeYield(in RecipeDef def) =>
            $"Получится: {def.ResultCount}    Время: {def.Ticks / 30f:0.#} с    {StationName(def.Station)}";

        /// <summary>Состояние рецепта в карточке: «можно сделать N» или почему нельзя.</summary>
        public static string CraftState(CraftReply state, CraftStation station, int affordable) => state switch
        {
            CraftReply.Accepted => $"Хватает на {affordable}",
            CraftReply.NoStation => station == CraftStation.Campfire ? "Нужен костёр рядом" : "Нужен верстак рядом",
            CraftReply.StationFar => station == CraftStation.Campfire ? "Костёр слишком далеко" : "Верстак слишком далеко",
            CraftReply.NoIngredients => "Не хватает материалов",
            CraftReply.QueueFull => "Очередь полна",
            CraftReply.Dead => "Вы погибли",
            _ => CraftRefusal(state, station),
        };

        /// <summary>Отказ сервера (сообщение 31) — короткая строка внизу окна.</summary>
        public static string CraftRefusal(CraftReply reply, CraftStation station) => reply switch
        {
            CraftReply.NoStation => station == CraftStation.Campfire ? "Отказ: рядом нет костра" : "Отказ: рядом нет верстака",
            CraftReply.StationFar => "Отказ: слишком далеко от станка",
            CraftReply.NoIngredients => "Отказ: не хватает материалов",
            CraftReply.QueueFull => "Отказ: очередь полна",
            CraftReply.Dead => "Отказ: вы погибли",
            CraftReply.Paused => "Отказ: игра на паузе",
            CraftReply.Busy => "Отказ: слишком много заявок, повторите",
            CraftReply.NoEntry => "Отказ: такой позиции в очереди уже нет",
            CraftReply.NoRecipe => "Отказ: нет такого рецепта",
            CraftReply.BadCount => "Отказ: неверное количество",
            CraftReply.NoEntity => "Отказ: персонажа нет в мире",
            _ => "Отказ сервера",
        };

        /// <summary>Подсказка позиции очереди.</summary>
        public static string QueueTip(int recipe, int left) => $"{RecipeName(recipe)}: осталось {left} — щелчок отменит";

        /// <summary>Название предмета для подсказки.</summary>
        public static string ItemName(byte item)
        {
            string key = ItemTable.Get(item).Key;
            return ItemNames.TryGetValue(key, out string? name) ? name : key;
        }

        /// <summary>Подсказка ячейки: название и количество в стопке.</summary>
        public static string ItemTip(byte item, int count) => count > 1 ? $"{ItemName(item)} × {count}" : ItemName(item);
    }
}
