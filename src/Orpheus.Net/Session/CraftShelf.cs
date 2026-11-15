using System;
using System.Collections.Generic;
using Orpheus.Core.Sim;

namespace Orpheus.Net.Session
{
    /// <summary>Раздел окна изготовления (E2-9): ключ и ключи подразделов по порядку.</summary>
    public sealed class ShelfSection
    {
        /// <summary>Ключ раздела (подпись — <c>UiStrings</c> клиента по ключу).</summary>
        public readonly string Key;

        /// <summary>Ключи подразделов; один пустой ключ — раздел без подразделов.</summary>
        public readonly string[] Subs;

        public ShelfSection(string key, params string[] subs)
        {
            Key = key;
            Subs = subs.Length > 0 ? subs : new[] { string.Empty };
        }

        /// <summary>У раздела нет своих подразделов (строка подразделов в окне пустая).</summary>
        public bool Flat => Subs.Length == 1 && Subs[0].Length == 0;
    }

    /// <summary>
    /// «Полки» окна изготовления (E2-9, <c>BRIEF</c> п. 11e (1)): разделы (Здания, Мебель, Инструменты, Оружие, Броня,
    /// Материалы, Еда), их подразделы и раскладка рецептов по ним — данными: строка «ключ результата → раздел /
    /// подраздел». Каждый рецепт <see cref="RecipeTable"/> лежит ровно в одном подразделе (тест <c>CraftShelfTests</c>).
    /// Правил игры здесь нет: что можно сделать, считает <see cref="CraftDesk.LineOf"/>; здесь — только где рецепт
    /// лежит в окне, фильтр «только доступные» и число на значке. Окно клиента и тесты зовут одни и те же функции.
    /// <para>Новый рецепт = новая строка в <see cref="Rows"/> (иначе тест красный). Новый подраздел — ключ в
    /// <see cref="Sections"/> и подпись в <c>UiStrings</c>.</para>
    /// </summary>
    public static class CraftShelf
    {
        /// <summary>Разделы по порядку вкладок и их подразделы. Пустой подраздел — нормально (рецепты появятся позже).</summary>
        public static readonly ShelfSection[] Sections =
        {
            new ShelfSection("buildings", "wood", "stone", "copper"),            // по материалам; постройки-предметы — E2-11
            new ShelfSection("furniture", "stations", "storage", "sleep", "light", "misc"), // по типу
            new ShelfSection("tools", "picks", "axes", "hammers"),
            new ShelfSection("weapons", "swords", "bows", "ammo"),
            new ShelfSection("armor", "head", "body", "legs"),                    // рецептов брони пока нет
            new ShelfSection("materials"),
            new ShelfSection("food"),
        };

        /// <summary>Раскладка: ключ предмета-результата (<see cref="ItemDef.Key"/>) → раздел и подраздел (ключи).</summary>
        private static readonly (string Item, string Section, string Sub)[] Rows =
        {
            ("stone_pickaxe", "tools", "picks"),
            ("copper_pickaxe", "tools", "picks"),
            ("stone_axe", "tools", "axes"),
            ("copper_axe", "tools", "axes"),
            ("hammer", "tools", "hammers"),
            ("torch", "furniture", "light"),
            ("wooden_sword", "weapons", "swords"),
            ("copper_sword", "weapons", "swords"),
            ("simple_bow", "weapons", "bows"),
            ("copper_bow", "weapons", "bows"),
            ("arrows", "weapons", "ammo"),
            ("copper_ingot", "materials", ""),
            ("rope", "materials", ""),
            ("roast_meat", "food", ""),
            // Постройки-предметы (стены, полы, двери, верстак, костёр, сундук, кровать) — строки сюда в шаге 2.11.
        };

        private static readonly int[][][] Shelves = Build(out PlaceOf);
        private static readonly (int Section, int Sub)[] PlaceOf;

        private static int[][][] Build(out (int, int)[] place)
        {
            place = new (int, int)[RecipeTable.Count];
            var lists = new List<int>[Sections.Length][];
            for (int s = 0; s < Sections.Length; s++)
            {
                lists[s] = new List<int>[Sections[s].Subs.Length];
                for (int k = 0; k < lists[s].Length; k++)
                {
                    lists[s][k] = new List<int>();
                }
            }

            for (int r = 0; r < place.Length; r++)
            {
                place[r] = (-1, -1);
            }

            for (int r = 1; r < RecipeTable.Count; r++)
            {
                string key = ItemTable.Get(RecipeTable.Get(r).Result).Key;
                (int s, int k) = Find(key);
                place[r] = (s, k);
                if (s >= 0)
                {
                    lists[s][k].Add(r);
                }
            }

            var shelves = new int[Sections.Length][][];
            for (int s = 0; s < Sections.Length; s++)
            {
                shelves[s] = new int[lists[s].Length][];
                for (int k = 0; k < lists[s].Length; k++)
                {
                    shelves[s][k] = lists[s][k].ToArray();
                }
            }

            return shelves;
        }

        // Первая строка раскладки с этим ключом → номера раздела и подраздела; нет строки или ключи неверны — (−1, −1).
        private static (int, int) Find(string itemKey)
        {
            foreach ((string item, string section, string sub) in Rows)
            {
                if (item != itemKey)
                {
                    continue;
                }

                for (int s = 0; s < Sections.Length; s++)
                {
                    if (Sections[s].Key == section)
                    {
                        int k = Array.IndexOf(Sections[s].Subs, sub);
                        return k >= 0 ? (s, k) : (-1, -1);
                    }
                }

                return (-1, -1);
            }

            return (-1, -1);
        }

        /// <summary>Сколько строк раскладки называют предмет <paramref name="itemKey"/> (проверка «ровно в одном»).</summary>
        public static int RowsFor(string itemKey)
        {
            int n = 0;
            foreach ((string item, _, _) in Rows)
            {
                if (item == itemKey)
                {
                    n++;
                }
            }

            return n;
        }

        /// <summary>Все строки раскладки: ключ предмета, раздел, подраздел (проверки).</summary>
        public static IReadOnlyList<(string Item, string Section, string Sub)> Layout => Rows;

        /// <summary>Где лежит рецепт: раздел и подраздел; рецепта нет в раскладке — (−1, −1).</summary>
        public static (int Section, int Sub) Place(int recipe) => RecipeTable.Exists(recipe) ? PlaceOf[recipe] : (-1, -1);

        /// <summary>Рецепты подраздела по порядку таблицы рецептов (неверный номер — пусто).</summary>
        public static int[] Recipes(int section, int sub) =>
            (uint)section < (uint)Shelves.Length && (uint)sub < (uint)Shelves[section].Length ? Shelves[section][sub] : Array.Empty<int>();

        /// <summary>Рецептов во всём разделе.</summary>
        public static int CountIn(int section)
        {
            int n = 0;
            for (int k = 0; (uint)section < (uint)Shelves.Length && k < Shelves[section].Length; k++)
            {
                n += Shelves[section][k].Length;
            }

            return n;
        }

        /// <summary>
        /// Что показать в сетке подраздела: рецепты подраздела, а с фильтром «только доступные» — только те, что можно
        /// сделать сейчас (<see cref="CraftLine.CanMake"/>). <paramref name="lines"/> — строки по номеру рецепта
        /// (<see cref="CraftDesk.LineOf"/>). Список <paramref name="into"/> очищается и заполняется.
        /// </summary>
        public static void Shown(int section, int sub, CraftLine[] lines, bool onlyAvailable, List<int> into)
        {
            into.Clear();
            foreach (int r in Recipes(section, sub))
            {
                if (!onlyAvailable || (r < lines.Length && lines[r].CanMake))
                {
                    into.Add(r);
                }
            }
        }

        /// <summary>Сколько рецептов подраздела можно сделать сейчас (метка на подразделе / разделе).</summary>
        public static int Available(int section, int sub, CraftLine[] lines)
        {
            int n = 0;
            foreach (int r in Recipes(section, sub))
            {
                if (r < lines.Length && lines[r].CanMake)
                {
                    n++;
                }
            }

            return n;
        }

        /// <summary>
        /// Число на значке рецепта: сколько штук получится из того, что есть (изготовлений × выход рецепта; стрелы × 10 при
        /// материалах на 5 изготовлений — 50). Станка нет рядом — число всё равно видно (значок приглушён): «материалы есть,
        /// нужен станок». Материалов нет — 0 (значок без числа).
        /// </summary>
        public static int Badge(in CraftLine line) =>
            line.Recipe == 0 ? 0 : line.Affordable * RecipeTable.Get(line.Recipe).ResultCount;
    }
}
