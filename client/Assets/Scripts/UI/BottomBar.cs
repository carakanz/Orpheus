#nullable enable
using Orpheus.Core.Sim;
using Orpheus.Net.Session;
using UnityEngine;
using UnityEngine.UI;

namespace Orpheus.Client.UI
{
    /// <summary>
    /// Быстрая панель 1–8 (C-015, слова Автора 15.11: «верхний левый угол инвентарь»): ряд полупрозрачных ячеек в левом
    /// верхнем углу, всегда на экране, с подписями клавиш; по Q под ним раскрывается сумка (<see cref="BagWindow"/>), и
    /// ряд работает как её верхний ряд (как в Terraria). Справа от ряда — маленькая кнопка режима постановки (слово
    /// Автора: «маленькой иконкой линейки или зигзага около инвентаря»): линейка — режим «линейка», зигзаг — «поштучно»;
    /// щелчок переключает (как Tab). Значки — шейдером (<see cref="UiShape"/>). Места — <see cref="HudLayout"/>.
    /// </summary>
    public sealed class HotbarView : UiGroup
    {
        public readonly UiSlotGrid Grid;
        readonly Image _modeBack;
        readonly UiShape _modeIcon;
        readonly Sprite _modePlain, _modeHover;
        bool _ruler = true, _hover;

        public HotbarView(UiRoot root)
            : base(root, "Hotbar", new Vector2(0f, 1f), new Vector2(HudLayout.Margin, -HudLayout.Margin - HudLayout.HotbarH),
                new Vector2(HudLayout.ModeX + HudLayout.ModeButton - HudLayout.Margin, HudLayout.HotbarH))
        {
            Grid = new UiSlotGrid(Rt, 0f, 0f, Inventory.Slots, 1, 0, UiCellLook.Glass, keyLabels: true);
            float mx = HudLayout.ModeX - HudLayout.Margin, my = (HudLayout.HotbarH - HudLayout.ModeButton) / 2f;
            _modePlain = UiStyle.Frame(UiStyle.Glass, UiStyle.GlassEdge, thick: 2f, radius: 6f);
            _modeHover = UiStyle.Frame(UiStyle.GlassHover, UiStyle.Gold, thick: 2f, radius: 6f);
            _modeBack = UiRoot.Picture(Rt, "ModeBack", mx, my, HudLayout.ModeButton, HudLayout.ModeButton, _modePlain);
            const float inset = 5f;
            _modeIcon = UiShape.Create(Rt, "ModeIcon", mx + inset, my + inset, HudLayout.ModeButton - 2f * inset, HudLayout.ModeButton - 2f * inset,
                UiShape.Shape.Ruler, UiStyle.TextLight);
        }

        /// <summary>Курсор (точка холста) над кнопкой режима.</summary>
        public bool OverMode(Vector2 canvas, Vector2 point) => Visible && HudLayout.OverMode(canvas.y, point.x, point.y);

        /// <summary>Значок режима: линейка или зигзаг; подсветка под курсором.</summary>
        public void SetMode(bool ruler, bool hover)
        {
            if (ruler != _ruler)
            {
                _ruler = ruler;
                _modeIcon.Kind = ruler ? UiShape.Shape.Ruler : UiShape.Shape.Zigzag;
            }

            if (hover != _hover)
            {
                _hover = hover;
                _modeBack.sprite = hover ? _modeHover : _modePlain;
            }
        }

        /// <summary>Ячейка панели (0…7) под точкой группы; мимо — −1.</summary>
        public int CellAt(Vector2 local) => Grid.Hit(local);

        /// <summary>Середина ячейки панели в координатах группы.</summary>
        public Vector2 CellCenter(int cell) => Grid.CenterOf(cell);

        /// <summary>Ряд ячеек — интерфейс.</summary>
        public void AddZones(UiPointer p, Vector2 canvas)
        {
            if (Visible)
            {
                HudLayout.HotbarZones(p, canvas.y);
            }
        }
    }

    /// <summary>
    /// Нижняя панель (C-008 → C-015, слова Автора 15.11: «внизу способности», «ХП и абилки вынести вниз», снимок Diablo IV):
    /// внизу по центру — сфера здоровья слева, сфера выносливости справа, между ними полупрозрачная панель с тремя
    /// рамками способностей (2 активные + 1 пассивная, `docs/00` 7; до этапа 3 — с замком). Быстрой панели здесь больше
    /// нет — она в левом верхнем углу (<see cref="HotbarView"/>). Сферы и замки рисуются шейдером на GPU
    /// (<see cref="UiShape"/>): край, обод, блик и уровень «жидкости» — чёткие на любом разрешении; число здоровья — на сфере.
    /// </summary>
    public sealed class BottomBar : UiGroup
    {
        // Размеры — общие с тестами (HudLayout в Net).
        public const float Orb = HudLayout.Orb, Overlap = HudLayout.Overlap, Pad = HudLayout.Pad;
        public const float AbilitiesW = HudLayout.AbilitiesW;
        public const float PanelW = HudLayout.PanelW;
        public const float PanelH = HudLayout.PanelH;
        public const float PanelY = HudLayout.PanelY;
        public const float PanelX = HudLayout.PanelX;
        public const float Width = HudLayout.BarWidth;
        public const float Height = HudLayout.BarHeight;
        public const float Bottom = HudLayout.BarBottom;

        /// <summary>Что под курсором: сферы и рамки способностей (подсказки).</summary>
        public enum Part : byte
        {
            None = 0,
            Health,
            Stamina,
            Ability,
            Passive,
        }

        readonly UiShape _health, _stamina;
        readonly Text _healthText;
        int _healthShown = -1;

        public BottomBar(UiRoot root)
            : base(root, "OrbBar", new Vector2(0.5f, 0f), new Vector2(-Width / 2f, Bottom), new Vector2(Width, Height))
        {
            UiRoot.Picture(Rt, "Panel", PanelX, PanelY, PanelW, PanelH, UiStyle.Frame(UiStyle.GlassPanel, UiStyle.Gold, thick: 2f, radius: 10f));

            // Рамки способностей: две активные и, через зазор, пассивная — пока с замком (способности — этап 3).
            Sprite off = UiStyle.Frame(UiStyle.GlassOff, UiStyle.GlassOffEdge, thick: 2f, radius: 6f);
            Sprite passive = UiStyle.Frame(UiStyle.GlassOff, UiStyle.GoldDark, thick: 2f, radius: 18f);
            for (int k = 0; k < 3; k++)
            {
                float x = HudLayout.AbilityX(k);
                UiRoot.Picture(Rt, "Ability", x, PanelY + Pad, UiStyle.CellSize, UiStyle.CellSize, k == 2 ? passive : off);
                UiShape.Create(Rt, "Lock", x + 12f, PanelY + Pad + 12f, 24f, 24f, UiShape.Shape.Lock, new Color32(200, 196, 220, 210));
            }

            _health = UiShape.Create(Rt, "HealthOrb", 0f, 0f, Orb, Orb, UiShape.Shape.Orb, UiStyle.OrbHealth);
            _stamina = UiShape.Create(Rt, "StaminaOrb", Width - Orb, 0f, Orb, Orb, UiShape.Shape.Orb, UiStyle.OrbStamina);
            _healthText = UiRoot.Label(Rt, "HealthText", 0f, Orb / 2f - 15f, Orb, 30f, 22, TextAnchor.MiddleCenter, UiStyle.TextLight, outline: true, bold: true);
        }

        /// <summary>Сфера или рамка способности под точкой группы.</summary>
        public Part PartAt(Vector2 local)
        {
            float r = Orb / 2f;
            if ((local - new Vector2(r, r)).sqrMagnitude < r * r)
            {
                return Part.Health;
            }

            if ((local - new Vector2(Width - r, r)).sqrMagnitude < r * r)
            {
                return Part.Stamina;
            }

            for (int k = 0; k < 3; k++)
            {
                float x = HudLayout.AbilityX(k), y = PanelY + Pad;
                if (local.x >= x && local.x < x + UiStyle.CellSize && local.y >= y && local.y < y + UiStyle.CellSize)
                {
                    return k == 2 ? Part.Passive : Part.Ability;
                }
            }

            return Part.None;
        }

        /// <summary>C-008: панель способностей и сферы — интерфейс; над панелью между сферами — мир.</summary>
        public void AddZones(UiPointer p, Vector2 canvas)
        {
            if (Visible)
            {
                Vector2 o = Origin(canvas);
                HudLayout.BarZones(p, o.x, o.y);
            }
        }

        /// <summary>Здоровье и выносливость (0…max): уровень «жидкости» в сферах, число здоровья на сфере.</summary>
        public void Set(int health, int maxHealth, int stamina, int maxStamina)
        {
            _health.Fill = maxHealth > 0 ? Mathf.Clamp01(health / (float)maxHealth) : 0f;
            _stamina.Fill = maxStamina > 0 ? Mathf.Clamp01(stamina / (float)maxStamina) : 0f;

            int key = health * 1000 + maxHealth;
            if (key != _healthShown)
            {
                _healthShown = key;
                _healthText.text = $"{health}/{maxHealth}";
            }
        }
    }
}
