using System.Collections.Generic;
using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net.Session;
using Xunit;

namespace Orpheus.Tests
{
    /// <summary>
    /// C-029 п. 1 (слово Автора 17.11: «Если я строю стену из меню строительства и она уже есть в рюкзаке, она должна браться
    /// из рюкзака»): рецепт постройки, у которой готовые лежат в сумке, виден при «только доступные» и выбирается, даже без
    /// материалов; число на значке — готовые + изготовимые. Постановка из меню берёт готовые (сервер — PayOrCraft).
    /// </summary>
    public sealed class C029MenuTests
    {
        [Fact]
        public void C029_Ready_Building_Shown_Without_Materials()
        {
            WorldModel world = FlatWorldGenerator.Instance.Generate(MapShape.World(1024), 1);
            var pos = new WorldPos(300 * Torus.SubTilesPerTile, 200 * Torus.SubTilesPerTile);
            var bag = new Inventory();
            bag.Add(Items.StoneWall, 3); // готовые стены, камня нет
            var queue = new CraftQueue();
            int wall = CraftRules.RecipeFor(Items.StoneWall);
            int floor = CraftRules.RecipeFor(Items.StoneFloor);
            CraftLine w = CraftDesk.LineOf(wall, bag, queue, world, pos);
            CraftLine f = CraftDesk.LineOf(floor, bag, queue, world, pos);
            Assert.False(w.CanMake);
            Assert.True(w.Shown);
            Assert.Equal(3, w.Ready);
            Assert.Equal(3, CraftShelf.Badge(w));
            Assert.False(f.Shown); // каменного пола нет ни готового, ни из чего сделать

            var lines = new CraftLine[RecipeTable.Count];
            for (int r = 1; r < lines.Length; r++)
            {
                lines[r] = CraftDesk.LineOf(r, bag, queue, world, pos);
            }

            bool found = false;
            var shown = new List<int>();
            for (int s = 0; s < CraftShelf.Sections.Length && !found; s++)
            {
                for (int sub = 0; sub < System.Math.Max(1, CraftShelf.Sections[s].Subs.Length) && !found; sub++)
                {
                    CraftShelf.Shown(s, sub, lines, onlyAvailable: true, shown);
                    found = shown.Contains(wall);
                    Assert.DoesNotContain(floor, shown);
                }
            }

            Assert.True(found);
            // Не постройка (стрелы) с готовыми в сумке — по-прежнему только по материалам.
            bag.Add(Items.Arrows, 5);
            CraftLine a = CraftDesk.LineOf(CraftRules.RecipeFor(Items.Arrows), bag, queue, world, pos);
            Assert.False(a.Shown);
            Assert.Equal(0, a.Ready);
        }
    }
}
