using System.Collections.Generic;
using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net.Session;
using Orpheus.WorldGen;
using Xunit;

namespace Orpheus.Tests
{
    /// <summary>
    /// E2-9 (шаг 2.9): разделы и подразделы окна изготовления (<see cref="CraftShelf"/>) — раскладка рецептов данными,
    /// фильтр «только доступные» и число «можно сделать» на значке. Окно клиента зовёт те же функции
    /// (<see cref="CraftShelf.Shown"/>, <see cref="CraftShelf.Badge"/>) над строками <see cref="CraftDesk.LineOf"/>.
    /// </summary>
    public class CraftShelfTests
    {
        private const int U = Torus.SubTilesPerTile;

        private static WorldPos Center(int x, int y) => new WorldPos(x * U + U / 2, y * U + U / 2);

        [Fact]
        public void E29_Shelf_Sections_AsAuthorAsked()
        {
            string[] keys = { "buildings", "furniture", "tools", "weapons", "armor", "materials", "food" };
            Assert.Equal(keys.Length, CraftShelf.Sections.Length);
            for (int s = 0; s < keys.Length; s++)
            {
                Assert.Equal(keys[s], CraftShelf.Sections[s].Key);
            }

            // Здания — по материалам, мебель — по типу (станки, хранение, сон, свет), оружие — по типу.
            Assert.Equal(new[] { "wood", "stone", "copper" }, CraftShelf.Sections[0].Subs);
            Assert.Contains("stations", CraftShelf.Sections[1].Subs);
            Assert.Contains("storage", CraftShelf.Sections[1].Subs);
            Assert.Contains("sleep", CraftShelf.Sections[1].Subs);
            Assert.Contains("light", CraftShelf.Sections[1].Subs);
            Assert.Equal(new[] { "swords", "bows", "ammo" }, CraftShelf.Sections[3].Subs);
            Assert.True(CraftShelf.Sections[5].Flat);
            Assert.True(CraftShelf.Sections[6].Flat);
        }

        /// <summary>Каждый рецепт таблицы — ровно в одном подразделе; строки раскладки называют существующие предметы и полки.</summary>
        [Fact]
        public void E29_Shelf_EveryRecipe_InExactlyOneSub()
        {
            var seen = new int[RecipeTable.Count];
            for (int s = 0; s < CraftShelf.Sections.Length; s++)
            {
                for (int k = 0; k < CraftShelf.Sections[s].Subs.Length; k++)
                {
                    foreach (int r in CraftShelf.Recipes(s, k))
                    {
                        seen[r]++;
                        Assert.Equal((s, k), CraftShelf.Place(r));
                    }
                }
            }

            for (int r = 1; r < RecipeTable.Count; r++)
            {
                string key = ItemTable.Get(RecipeTable.Get(r).Result).Key;
                Assert.True(seen[r] == 1, $"рецепт {r} ({key}) лежит в {seen[r]} подразделах");
                Assert.Equal(1, CraftShelf.RowsFor(key));
            }

            var itemKeys = new HashSet<string>();
            for (int i = 0; i < 256; i++)
            {
                if (ItemTable.Exists((byte)i))
                {
                    itemKeys.Add(ItemTable.Get((byte)i).Key);
                }
            }

            foreach ((string item, string section, string sub) in CraftShelf.Layout)
            {
                Assert.True(itemKeys.Contains(item), $"раскладка: нет предмета {item}");
                ShelfSection? sec = System.Array.Find(CraftShelf.Sections, x => x.Key == section);
                Assert.True(sec != null, $"раскладка: нет раздела {section}");
                Assert.Contains(sub, sec!.Subs);
            }

            // Примеры: медная кирка — Инструменты / Кирки, стрелы — Оружие / Стрелы, факел — Мебель / Свет.
            Assert.Equal((2, 0), CraftShelf.Place(RecipeTable.CopperPickaxe));
            Assert.Equal((3, 2), CraftShelf.Place(RecipeTable.Arrows));
            Assert.Equal((1, 3), CraftShelf.Place(RecipeTable.Torch));
            Assert.Equal((-1, -1), CraftShelf.Place(0));
        }

        /// <summary>
        /// Счёт «можно сделать» и фильтр — тем же путём, что окно: строки <see cref="CraftDesk.LineOf"/> по сумке, очереди и
        /// миру с верстаком рядом → <see cref="CraftShelf.Badge"/> и <see cref="CraftShelf.Shown"/>.
        /// </summary>
        [Fact]
        public void E29_Shelf_Badge_And_OnlyAvailable_LikeWindow()
        {
            WorldModel world = FlatWorldGenerator.Instance.Generate(MapShape.World(1024), 1);
            world.SetBaseTile(new TileCoord(302, 200), FloorId.Grass, ObjectId.Workbench);
            WorldPos at = Center(300, 200), far = Center(330, 200);
            var bag = new Inventory();
            bag.Add(Items.Wood, 17);
            bag.Add(Items.Stone, 4);
            bag.Add(Items.CopperIngot, 9);
            var queue = new CraftQueue();

            CraftLine[] Lines(WorldPos pos)
            {
                var lines = new CraftLine[RecipeTable.Count];
                for (int r = 1; r < lines.Length; r++)
                {
                    lines[r] = CraftDesk.LineOf(r, bag, queue, world, pos);
                }

                return lines;
            }

            CraftLine[] near = Lines(at);
            // Стрелы × 10: 1 древесина + 1 камень → 4 изготовления → 40 штук. Медная кирка: 8 слитков + 3 древесины → 1.
            Assert.Equal(40, CraftShelf.Badge(near[RecipeTable.Arrows]));
            Assert.Equal(1, CraftShelf.Badge(near[RecipeTable.CopperPickaxe]));
            Assert.Equal(2, CraftShelf.Badge(near[RecipeTable.WoodenSword])); // 17 / 8
            Assert.Equal(0, CraftShelf.Badge(near[RecipeTable.CopperIngot])); // руды нет
            Assert.Equal(0, CraftShelf.Badge(default));

            var shown = new List<int>();
            CraftShelf.Shown(3, 0, near, onlyAvailable: false, shown); // Оружие / Мечи
            Assert.Equal(new[] { RecipeTable.WoodenSword, RecipeTable.CopperSword }, shown.ConvertAll(x => (byte)x));
            CraftShelf.Shown(3, 1, near, onlyAvailable: true, shown); // Оружие / Луки: верёвки нет — пусто
            Assert.Empty(shown);
            CraftShelf.Shown(2, 0, near, onlyAvailable: true, shown); // Кирки: каменная (3 камня + 2 древесины) и медная
            Assert.Equal(new[] { RecipeTable.StonePickaxe, RecipeTable.CopperPickaxe }, shown.ConvertAll(x => (byte)x));
            Assert.Equal(2, CraftShelf.Available(2, 0, near));

            // Отошёл от верстака: число на значке осталось (материалы есть), но «доступно» — только ручные рецепты.
            CraftLine[] away = Lines(far);
            Assert.Equal(40, CraftShelf.Badge(away[RecipeTable.Arrows]));
            Assert.False(away[RecipeTable.Arrows].CanMake);
            CraftShelf.Shown(2, 0, away, onlyAvailable: true, shown);
            Assert.Equal(new[] { RecipeTable.StonePickaxe }, shown.ConvertAll(x => (byte)x));
            CraftShelf.Shown(3, 2, away, onlyAvailable: true, shown);
            Assert.Empty(shown);
        }
    }
}
