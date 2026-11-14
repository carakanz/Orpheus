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
        };

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
