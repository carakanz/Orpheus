using Orpheus.Core.Sim;

namespace Orpheus.Net.Session
{
    /// <summary>
    /// Раскладка постоянных частей экрана (C-008): нижняя панель со сферами (как Diablo IV) и мини-карта справа вверху.
    /// Чистый C# без Unity: размеры и «интерфейсные» прямоугольники считает один код — клиент (<c>BottomBar</c>,
    /// <c>MiniMapView</c>) кладёт их в <see cref="UiPointer"/> этими функциями, тесты <c>C008PointerTests</c> — тоже.
    /// Единицы — холст (левый нижний угол — 0, 0).
    /// </summary>
    public static class HudLayout
    {
        /// <summary>Ячейка и зазор интерфейса (<c>UiStyle.CellSize</c>, <c>UiStyle.CellGap</c>).</summary>
        public const float Cell = 48f, Gap = 4f;

        /// <summary>Нижняя панель: сфера, наезд панели на сферу, поля, зазор перед рамками способностей.</summary>
        public const float Orb = 132f, Overlap = 24f, Pad = 8f, AbilityGap = 16f;

        public const float HotbarW = Inventory.Slots * (Cell + Gap) - Gap;
        public const float AbilitiesW = 3f * Cell + Gap + 12f;
        public const float PanelW = Pad + HotbarW + AbilityGap + AbilitiesW + Pad;
        public const float PanelH = Cell + 2f * Pad;
        public const float PanelY = 10f;
        public const float PanelX = Orb - Overlap;
        public const float BarWidth = 2f * (Orb - Overlap) + PanelW;
        public const float BarHeight = Orb;
        public const float BarBottom = 8f;

        /// <summary>Мини-карта: сторона картинки, рамка, отступ от угла.</summary>
        public const float MapSide = 220f, MapBorder = 6f, MapMargin = 20f;
        public const float MapSize = MapSide + 2f * MapBorder;

        /// <summary>Левый нижний угол нижней панели (группы) на холсте: по центру снизу.</summary>
        public static float BarX(float canvasW) => canvasW / 2f - BarWidth / 2f;

        /// <summary>Левый нижний угол мини-карты на холсте: правый верхний угол.</summary>
        public static float MapX(float canvasW) => canvasW - MapMargin - MapSize;

        public static float MapY(float canvasH) => canvasH - MapMargin - MapSize;

        /// <summary>Прямоугольники нижней панели в координатах группы: панель (ячейки 1–8 и рамки) и две сферы.</summary>
        public static void BarZones(UiPointer p, float ox, float oy)
        {
            p.Add(ox + PanelX, oy + PanelY, PanelW, PanelH);
            p.Add(ox + 8f, oy + 8f, Orb - 16f, Orb - 16f);
            p.Add(ox + BarWidth - Orb + 8f, oy + 8f, Orb - 16f, Orb - 16f);
        }

        /// <summary>Мини-карта — интерфейс целиком (развёрнутая).</summary>
        public static void MapZones(UiPointer p, float ox, float oy) => p.Add(ox, oy, MapSize, MapSize);

        /// <summary>Постоянные части экрана холста <paramref name="canvasW"/> × <paramref name="canvasH"/>: панель, сферы, карта.</summary>
        public static void AddHud(UiPointer p, float canvasW, float canvasH, bool mapOpen)
        {
            BarZones(p, BarX(canvasW), BarBottom);
            if (mapOpen)
            {
                MapZones(p, MapX(canvasW), MapY(canvasH));
            }
        }

        /// <summary>Середина ячейки панели <paramref name="slot"/> (0…7) на холсте.</summary>
        public static (float X, float Y) SlotCenter(float canvasW, int slot) =>
            (BarX(canvasW) + PanelX + Pad + slot * (Cell + Gap) + Cell / 2f, BarBottom + PanelY + Pad + Cell / 2f);

        /// <summary>Середины сфер (здоровье — слева, выносливость — справа) на холсте.</summary>
        public static (float X, float Y) OrbCenter(float canvasW, bool stamina) =>
            (BarX(canvasW) + (stamina ? BarWidth - Orb / 2f : Orb / 2f), BarBottom + Orb / 2f);
    }
}
