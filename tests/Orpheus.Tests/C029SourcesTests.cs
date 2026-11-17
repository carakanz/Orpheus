using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net.Session;
using Xunit;

namespace Orpheus.Tests
{
    /// <summary>C-029 п. 6: подсказка ингредиента — базовый ли, где взять, «?» для неоткрытого биома / врага.</summary>
    public sealed class C029SourcesTests
    {
        [Fact]
        public void C029_Base_Items_And_Where_With_Discovery()
        {
            Assert.True(ItemSources.IsBase(Items.Wood));
            Assert.True(ItemSources.IsBase(Items.CopperOre));
            Assert.False(ItemSources.IsBase(Items.CopperIngot)); // есть рецепт — ЛКМ открывает его
            Assert.False(ItemSources.IsBase(Items.Rope));

            var d = new Discovery();
            Assert.Equal(new[] { "?" }, ItemSources.Where(Items.CopperOre, d));
            d.NoteFloor(FloorId.Grass);
            Assert.Equal(new[] { "Луга: валуны, киркой", "?" }, ItemSources.Where(Items.Stone, d));
            d.NoteFloor(FloorId.Stone); // горы и пещеры
            Assert.Equal(new[] { "Луга: валуны, киркой", "Горы: скалы, киркой (больше всего)", "Пещеры: гряды и сталагмиты, киркой" },
                ItemSources.Where(Items.Stone, d));
            Assert.Equal(new[] { "?" }, ItemSources.Where(Items.Meat, d)); // зверей ещё никто не убивал
            d.NoteKill(ItemSources.Wolf);
            Assert.Equal(new[] { "Волк: волки", "?" }, ItemSources.Where(Items.Meat, d));

            // У каждого базового ингредиента рецептов есть описание и хоть один источник.
            for (int r = 1; r < RecipeTable.Count; r++)
            {
                if (!RecipeTable.Exists(r))
                {
                    continue;
                }

                ref readonly RecipeDef def = ref RecipeTable.Get(r);
                for (int k = 0; k < def.IngredientCount; k++)
                {
                    byte item = def.IngredientItem(k);
                    Assert.NotEqual(string.Empty, ItemSources.Description(item));
                    if (ItemSources.IsBase(item))
                    {
                        Assert.NotEmpty(ItemSources.Of(item));
                    }
                }
            }
        }

        [Fact]
        public void C029_Discovery_Save_Load()
        {
            var d = new Discovery();
            d.NoteFloor(FloorId.ForestFloor);
            d.NoteKill(ItemSources.Bandit);
            Assert.True(d.Dirty);
            string s = d.Save();
            Assert.False(d.Dirty);
            Discovery back = Discovery.Load(s);
            Assert.True(back.KnowsBiome(ItemSources.Forest));
            Assert.False(back.KnowsBiome(ItemSources.Meadow));
            Assert.True(back.KnowsEnemy(ItemSources.Bandit));
            Assert.False(Discovery.Load("мусор;b:x,1").KnowsBiome(0));
            Assert.True(Discovery.Load("мусор;b:x,1").KnowsBiome(1));
        }
    }
}
