using Orpheus.Client.UI;
using Orpheus.Core.Sim;
using Orpheus.Net.Session;
using Xunit;

namespace Orpheus.Tests
{
    /// <summary>
    /// C-032 (хвост C-029): подсказка значка рецепта (<see cref="UiStrings.RecipeTip"/>) — одна последняя строка:
    /// готовая постройка в сумке — «В сумке: N — ставится из сумки» без «что мешает»; иначе — что мешает.
    /// «Не хватает» и «в сумке» одновременно в подсказке быть не может.
    /// </summary>
    public sealed class C029HintTests
    {
        private const int Wall = RecipeTable.StoneWall; // руками, 2 камня

        [Fact]
        public void C029_RecipeTip_Ready_In_Bag_Says_Placement_Not_Shortage()
        {
            var line = new CraftLine((byte)Wall, CraftReply.NoIngredients, 0, ready: 10);
            string tip = UiStrings.RecipeTip(Wall, line, 2, 2, 0);
            Assert.Contains("В сумке: 10", tip);
            Assert.DoesNotContain("Не хватает", tip);
            Assert.DoesNotContain("Нужен верстак", tip);
            Assert.Contains("2 / 2", tip); // строки ингредиентов «есть / нужно» остаются
        }

        [Fact]
        public void C029_RecipeTip_Ready_In_Bag_Silent_About_No_Station()
        {
            // станок не нужен и рядом — не мешает; готовое в сумке — только про сумку
            var line = new CraftLine((byte)Wall, CraftReply.NoStation, 0, ready: 4);
            string tip = UiStrings.RecipeTip(Wall, line, 2, 2, 0);
            Assert.Contains("В сумке: 4", tip);
            Assert.DoesNotContain("Нужен верстак", tip);
            Assert.DoesNotContain("Нужен костёр", tip);
        }

        [Fact]
        public void C029_RecipeTip_Can_Make_Says_Affordable_Without_In_Bag()
        {
            var line = new CraftLine((byte)Wall, CraftReply.Accepted, 3);
            string tip = UiStrings.RecipeTip(Wall, line, 20, 5, 0);
            Assert.Contains("Хватает на 3", tip);
            Assert.DoesNotContain("В сумке", tip);
        }

        [Fact]
        public void C029_RecipeTip_Short_Without_Bag_Says_Shortage()
        {
            var line = new CraftLine((byte)Wall, CraftReply.NoIngredients, 0);
            string tip = UiStrings.RecipeTip(Wall, line, 0, 2, 0);
            Assert.Contains("Не хватает материалов", tip);
            Assert.DoesNotContain("В сумке", tip);
        }

        [Fact]
        public void C029_RecipeTip_Dead_Says_Death_Even_With_Bed_In_Bag()
        {
            var line = new CraftLine((byte)Wall, CraftReply.Dead, 0, ready: 10); // мёртвый — CanPlace false
            string tip = UiStrings.RecipeTip(Wall, line, 0, 2, 0);
            Assert.Contains("Вы погибли", tip);
            Assert.DoesNotContain("В сумке", tip);
        }
    }
}
