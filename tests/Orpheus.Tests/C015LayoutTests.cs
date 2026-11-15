using Orpheus.Core.Sim;
using Orpheus.Net.Session;
using Xunit;

namespace Orpheus.Tests
{
    /// <summary>
    /// C-015 (слова Автора 15.11: «верхний левый угол инвентарь, внизу способности, правый верхний миникарта»): раскладка
    /// постоянных частей экрана — <see cref="HudLayout"/>, та же, что у клиента. Холст всегда 1080 по высоте, ширина — по
    /// соотношению сторон (4:3 … 21:9). Быстрая панель — левый верхний угол, сетка сумки — сразу под ней; мини-карта —
    /// правый верхний угол; сферы и способности — внизу по центру; окно изготовления (левый нижний угол, размеры как у
    /// клиентского <c>CraftWindow</c>) не задевает ни сумку, ни сферу здоровья; части не перекрываются.
    /// </summary>
    public class C015LayoutTests
    {
        private const float H = 1080f;

        // Окно изготовления клиента (CraftWindow: 20, 20; 434 × 548) и сетка сумки 8 × 4 с её строкой клавиш (24 + 4).
        private const float CraftX = 20f, CraftY = 20f, CraftW = 434f, CraftH = 548f;
        private const float BagGridH = 4f * (HudLayout.Cell + HudLayout.Gap) - HudLayout.Gap;
        private const float BagHintH = 28f;

        private readonly struct Box
        {
            public readonly float X, Y, W, H;

            public Box(float x, float y, float w, float h)
            {
                X = x;
                Y = y;
                W = w;
                H = h;
            }

            public bool Overlaps(Box o) => X < o.X + o.W && o.X < X + W && Y < o.Y + o.H && o.Y < Y + H;

            public override string ToString() => $"({X}, {Y}, {W} × {H})";
        }

        [Theory]
        [InlineData(1440f)] // 4:3
        [InlineData(1728f)] // 16:10
        [InlineData(1920f)] // 16:9
        [InlineData(2520f)] // 21:9
        public void C015_Hud_Corners_And_NoOverlaps(float w)
        {
            var hotbar = new Box(HudLayout.Margin, HudLayout.HotbarY(H), HudLayout.HotbarW, HudLayout.HotbarH);
            var bag = new Box(HudLayout.Margin, H - HudLayout.BagTop - BagGridH - BagHintH, HudLayout.HotbarW, BagGridH + BagHintH);
            var map = new Box(HudLayout.MapX(w), HudLayout.MapY(H), HudLayout.MapSize, HudLayout.MapSize);
            var bar = new Box(HudLayout.BarX(w), HudLayout.BarBottom, HudLayout.BarWidth, HudLayout.BarHeight);
            var craft = new Box(CraftX, CraftY, CraftW, CraftH);

            // Углы: панель — левый верхний, карта — правый верхний, сферы — внизу по центру.
            Assert.Equal(H - HudLayout.Margin, hotbar.Y + hotbar.H);
            Assert.True(hotbar.X <= HudLayout.Margin);
            Assert.Equal(w - HudLayout.Margin, map.X + map.W);
            Assert.Equal(H - HudLayout.Margin, map.Y + map.H);
            Assert.Equal(w / 2f, bar.X + bar.W / 2f, 3);
            Assert.True(bar.Y < 20f);

            // Сетка сумки — сразу под быстрой панелью (зазор как между ячейками).
            Assert.Equal(H - HudLayout.BagTop, hotbar.Y - HudLayout.Gap, 3);

            Box[] all = { hotbar, bag, map, bar, craft };
            string[] names = { "панель", "сумка", "карта", "сферы", "изготовление" };
            for (int i = 0; i < all.Length; i++)
            {
                Assert.True(all[i].X >= 0f && all[i].Y >= 0f && all[i].X + all[i].W <= w && all[i].Y + all[i].H <= H, $"{names[i]} {all[i]} за краем при {w}");
                for (int j = i + 1; j < all.Length; j++)
                {
                    if (i == 0 && j == 1)
                    {
                        continue; // панель и сумка встык — проверено выше
                    }

                    Assert.False(all[i].Overlaps(all[j]), $"{names[i]} {all[i]} перекрывает {names[j]} {all[j]} при {w}");
                }
            }
        }

        [Fact]
        public void C015_Hotbar_Abilities_Orbs_AreInterface_WorldBetween()
        {
            float w = 1920f;
            var p = new UiPointer();
            HudLayout.AddHud(p, w, H, mapOpen: true);

            for (int slot = 0; slot < Inventory.Slots; slot++)
            {
                (float x, float y) = HudLayout.SlotCenter(H, slot);
                Assert.True(p.Over(x, y), $"ячейка {slot}");
                Assert.True(y > H - 100f && x < 500f, "быстрая панель — левый верхний угол");
            }

            for (int k = 0; k < 3; k++)
            {
                (float ax, float ay) = HudLayout.AbilityCenter(w, k);
                Assert.True(p.Over(ax, ay), $"способность {k}");
                Assert.True(ay < 100f, "способности — внизу");
                Assert.True(ax > HudLayout.OrbCenter(w, false).X && ax < HudLayout.OrbCenter(w, true).X, "способности — между сферами");
            }

            // Значок режима «линейка / поштучно» — справа от панели, по середине её высоты; интерфейс, щелчок попадает в него.
            float modeX = HudLayout.ModeX + HudLayout.ModeButton / 2f, modeY = HudLayout.ModeY(H) + HudLayout.ModeButton / 2f;
            Assert.True(HudLayout.ModeX > HudLayout.Margin + HudLayout.HotbarW);
            Assert.Equal(HudLayout.SlotCenter(H, 0).Y, modeY, 3);
            Assert.True(p.Over(modeX, modeY));
            Assert.True(HudLayout.OverMode(H, modeX, modeY));
            Assert.False(HudLayout.OverMode(H, modeX, modeY - HudLayout.Cell));
            Assert.True(HudLayout.ModeX + HudLayout.ModeButton < HudLayout.MapX(1440f));

            // Под быстрой панелью (сумка закрыта) и центр экрана — мир.
            (float sx, float sy) = HudLayout.SlotCenter(H, 2);
            Assert.False(p.Over(sx, sy - 2f * HudLayout.Cell));
            Assert.False(p.Over(w / 2f, H / 2f));
        }
    }
}
