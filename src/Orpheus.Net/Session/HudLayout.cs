using Orpheus.Core.Sim;

namespace Orpheus.Net.Session
{
    /// <summary>
    /// Раскладка постоянных частей экрана (C-008 → C-015, слова Автора 15.11: «верхний левый угол инвентарь, внизу
    /// способности, правый верхний миникарта»): быстрая панель 1–8 — левый верхний угол (по Q под ней раскрывается сумка,
    /// как в Terraria); внизу по центру — сферы здоровья и выносливости и между ними панель способностей (как Diablo IV);
    /// мини-карта — правый верхний угол. Чистый C# без Unity: размеры и «интерфейсные» прямоугольники считает один код —
    /// клиент (<c>HotbarView</c>, <c>BottomBar</c>, <c>BagWindow</c>, <c>ChestWindow</c>, <c>MiniMapView</c>) кладёт их в
    /// <see cref="UiPointer"/> этими функциями, тесты <c>C008PointerTests</c>, <c>C015LayoutTests</c> — тоже.
    /// Единицы — холст (левый нижний угол — 0, 0; высота всегда 1080).
    /// </summary>
    public static class HudLayout
    {
        /// <summary>Ячейка и зазор интерфейса (<c>UiStyle.CellSize</c>, <c>UiStyle.CellGap</c>).</summary>
        public const float Cell = 48f, Gap = 4f;

        /// <summary>Отступ от краёв экрана (быстрая панель, сумка, изготовление, мини-карта).</summary>
        public const float Margin = 20f;

        /// <summary>Быстрая панель 1–8 — левый верхний угол (C-015).</summary>
        public const float HotbarW = Inventory.Slots * (Cell + Gap) - Gap;
        public const float HotbarH = Cell;

        /// <summary>
        /// Значок режима постановки «линейка / поштучно» (C-015, слово Автора: «маленькой иконкой линейки или зигзага около
        /// инвентаря») — квадратная кнопка справа от быстрой панели, по середине её высоты; щелчок — как Tab.
        /// </summary>
        public const float ModeButton = 36f, ModeGap = 8f;
        public const float ModeX = Margin + HotbarW + ModeGap;

        /// <summary>Низ значка режима на холсте высоты <paramref name="canvasH"/>.</summary>
        public static float ModeY(float canvasH) => HotbarY(canvasH) + (HotbarH - ModeButton) / 2f;

        /// <summary>Верх сетки сумки, единиц от верхнего края холста: сразу под быстрой панелью (её ряд — как верхний ряд сумки).</summary>
        public const float BagTop = Margin + HotbarH + Gap;

        /// <summary>Нижняя панель: сфера, наезд панели способностей на сферу, поля.</summary>
        public const float Orb = 132f, Overlap = 24f, Pad = 8f;

        /// <summary>Рамки способностей: две активные и через зазор 12 — пассивная.</summary>
        public const float PassiveGap = 12f;
        public const float AbilitiesW = 3f * Cell + 2f * Gap + PassiveGap;
        public const float PanelW = Pad + AbilitiesW + Pad;
        public const float PanelH = Cell + 2f * Pad;
        public const float PanelY = 10f;
        public const float PanelX = Orb - Overlap;
        public const float BarWidth = 2f * (Orb - Overlap) + PanelW;
        public const float BarHeight = Orb;
        public const float BarBottom = 8f;

        /// <summary>Мини-карта: сторона картинки, рамка, отступ от угла.</summary>
        public const float MapSide = 220f, MapBorder = 6f, MapMargin = Margin;
        public const float MapSize = MapSide + 2f * MapBorder;

        /// <summary>Низ быстрой панели на холсте высоты <paramref name="canvasH"/>.</summary>
        public static float HotbarY(float canvasH) => canvasH - Margin - HotbarH;

        /// <summary>Левый нижний угол нижней панели (группы) на холсте: по центру снизу.</summary>
        public static float BarX(float canvasW) => canvasW / 2f - BarWidth / 2f;

        /// <summary>Левый нижний угол мини-карты на холсте: правый верхний угол.</summary>
        public static float MapX(float canvasW) => canvasW - MapMargin - MapSize;

        public static float MapY(float canvasH) => canvasH - MapMargin - MapSize;

        /// <summary>Левый край рамки способности <paramref name="k"/> (0, 1 — активные, 2 — пассивная) в координатах группы панели.</summary>
        public static float AbilityX(int k) => PanelX + Pad + k * (Cell + Gap) + (k == 2 ? PassiveGap : 0f);

        /// <summary>Прямоугольники нижней панели в координатах группы: панель способностей и две сферы.</summary>
        public static void BarZones(UiPointer p, float ox, float oy)
        {
            p.Add(ox + PanelX, oy + PanelY, PanelW, PanelH);
            p.Add(ox + 8f, oy + 8f, Orb - 16f, Orb - 16f);
            p.Add(ox + BarWidth - Orb + 8f, oy + 8f, Orb - 16f, Orb - 16f);
        }

        /// <summary>Быстрая панель — ряд ячеек целиком (зазоры тоже).</summary>
        public static void HotbarZones(UiPointer p, float canvasH)
        {
            p.Add(Margin, HotbarY(canvasH), HotbarW, HotbarH);
            p.Add(ModeX, ModeY(canvasH), ModeButton, ModeButton);
        }

        /// <summary>Точка холста над значком режима «линейка / поштучно».</summary>
        public static bool OverMode(float canvasH, float x, float y) =>
            x >= ModeX && x < ModeX + ModeButton && y >= ModeY(canvasH) && y < ModeY(canvasH) + ModeButton;

        /// <summary>Мини-карта — интерфейс целиком (развёрнутая).</summary>
        public static void MapZones(UiPointer p, float ox, float oy) => p.Add(ox, oy, MapSize, MapSize);

        /// <summary>Постоянные части экрана холста <paramref name="canvasW"/> × <paramref name="canvasH"/>: быстрая панель, сферы и способности, карта.</summary>
        public static void AddHud(UiPointer p, float canvasW, float canvasH, bool mapOpen)
        {
            HotbarZones(p, canvasH);
            BarZones(p, BarX(canvasW), BarBottom);
            if (mapOpen)
            {
                MapZones(p, MapX(canvasW), MapY(canvasH));
            }
        }

        /// <summary>Середина ячейки быстрой панели <paramref name="slot"/> (0…7) на холсте высоты <paramref name="canvasH"/>.</summary>
        public static (float X, float Y) SlotCenter(float canvasH, int slot) =>
            (Margin + slot * (Cell + Gap) + Cell / 2f, HotbarY(canvasH) + Cell / 2f);

        /// <summary>Середина рамки способности <paramref name="k"/> на холсте.</summary>
        public static (float X, float Y) AbilityCenter(float canvasW, int k) =>
            (BarX(canvasW) + AbilityX(k) + Cell / 2f, BarBottom + PanelY + Pad + Cell / 2f);

        /// <summary>Середины сфер (здоровье — слева, выносливость — справа) на холсте.</summary>
        public static (float X, float Y) OrbCenter(float canvasW, bool stamina) =>
            (BarX(canvasW) + (stamina ? BarWidth - Orb / 2f : Orb / 2f), BarBottom + Orb / 2f);
    }
}
