#nullable enable
using Orpheus.Core.Sim;
using Orpheus.Net.Session;
using UnityEngine;
using UnityEngine.UI;

namespace Orpheus.Client.UI
{
    /// <summary>
    /// Нижняя панель (C-008, слова Автора 15.11: «ХП и абилки вынести вниз», «примерно как тут» — снимок Diablo IV):
    /// внизу по центру одна полупрозрачная панель — быстрая панель 1–8 и три рамки способностей (2 активные + 1
    /// пассивная, `docs/00` 7; до этапа 3 — с замком); слева сфера здоровья (красная, уровень = доля здоровья, число
    /// мелко на сфере), справа сфера выносливости (жёлто-зелёная). Сферы рисуются кодом: подложка, «жидкость»
    /// (<see cref="Image.Type.Filled"/> снизу вверх), блик. Остаётся на месте и при открытой сумке — тогда её ячейки
    /// работают как верхний ряд сумки.
    /// </summary>
    public sealed class BottomBar : UiGroup
    {
        // Размеры — общие с тестами (HudLayout в Net, C-008 проверка п. 3).
        public const float Orb = HudLayout.Orb, Overlap = HudLayout.Overlap, Pad = HudLayout.Pad, AbilityGap = HudLayout.AbilityGap;
        public const float HotbarW = HudLayout.HotbarW;
        public const float AbilitiesW = HudLayout.AbilitiesW;
        public const float PanelW = HudLayout.PanelW;
        public const float PanelH = HudLayout.PanelH;
        public const float PanelY = HudLayout.PanelY;
        public const float PanelX = HudLayout.PanelX;
        public const float Width = HudLayout.BarWidth;
        public const float Height = HudLayout.BarHeight;
        public const float Bottom = HudLayout.BarBottom;

        /// <summary>Что под курсором кроме ячеек: сферы и рамки способностей (подсказки).</summary>
        public enum Part : byte
        {
            None = 0,
            Health,
            Stamina,
            Ability,
            Passive,
        }

        public readonly UiSlotGrid Grid;
        readonly Image _health, _stamina;
        readonly Text _healthText;
        readonly float _abilitiesX;
        int _healthShown = -1;

        public BottomBar(UiRoot root)
            : base(root, "Hotbar", new Vector2(0.5f, 0f), new Vector2(-Width / 2f, Bottom), new Vector2(Width, Height))
        {
            UiRoot.Picture(Rt, "Panel", PanelX, PanelY, PanelW, PanelH, UiStyle.Frame(UiStyle.GlassPanel, UiStyle.Gold, thick: 2f, radius: 10f));
            Grid = new UiSlotGrid(Rt, PanelX + Pad, PanelY + Pad, Inventory.Slots, 1, 0, UiCellLook.Glass, keyLabels: true);

            // Рамки способностей: две активные и, через зазор, пассивная — пока с замком (способности — этап 3).
            _abilitiesX = PanelX + Pad + HotbarW + AbilityGap;
            Sprite off = UiStyle.Frame(UiStyle.GlassOff, UiStyle.GlassOffEdge, thick: 2f, radius: 6f);
            Sprite passive = UiStyle.Frame(UiStyle.GlassOff, UiStyle.GoldDark, thick: 2f, radius: 18f);
            Sprite lockIcon = UiStyle.Round(3, new Color32(200, 196, 220, 210));
            for (int k = 0; k < 3; k++)
            {
                float x = AbilityX(k);
                UiRoot.Picture(Rt, "Ability", x, PanelY + Pad, UiStyle.CellSize, UiStyle.CellSize, k == 2 ? passive : off);
                UiRoot.Picture(Rt, "Lock", x + 12f, PanelY + Pad + 12f, 24f, 24f, lockIcon, sliced: false);
            }

            _health = Orbs(0f, UiStyle.OrbHealth);
            _stamina = Orbs(Width - Orb, UiStyle.OrbStamina);
            _healthText = UiRoot.Label(Rt, "HealthText", 0f, Orb / 2f - 14f, Orb, 28f, 20, TextAnchor.MiddleCenter, UiStyle.TextLight, outline: true, bold: true);
        }

        float AbilityX(int k) => _abilitiesX + k * (UiStyle.CellSize + UiStyle.CellGap) + (k == 2 ? 12f : 0f);

        // Сфера: подложка (тёмное стекло, золотое кольцо), «жидкость» снизу вверх, блик.
        Image Orbs(float x, Color32 color)
        {
            UiRoot.Picture(Rt, "OrbBack", x, 0f, Orb, Orb, UiStyle.Round(1, default), sliced: false);
            const float inset = 11f;
            Image fill = UiRoot.Picture(Rt, "OrbFill", x + inset, inset, Orb - 2f * inset, Orb - 2f * inset, UiStyle.Round(0, color), sliced: false);
            fill.type = Image.Type.Filled;
            fill.fillMethod = Image.FillMethod.Vertical;
            fill.fillOrigin = (int)Image.OriginVertical.Bottom;
            UiRoot.Picture(Rt, "OrbShine", x + inset, inset, Orb - 2f * inset, Orb - 2f * inset, UiStyle.Round(2, default), sliced: false);
            return fill;
        }

        /// <summary>Ячейка панели (0…7) под точкой группы; мимо — −1.</summary>
        public int CellAt(Vector2 local) => Grid.Hit(local);

        /// <summary>Середина ячейки панели в координатах группы.</summary>
        public Vector2 CellCenter(int cell) => Grid.CenterOf(cell);

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
                float x = AbilityX(k), y = PanelY + Pad;
                if (local.x >= x && local.x < x + UiStyle.CellSize && local.y >= y && local.y < y + UiStyle.CellSize)
                {
                    return k == 2 ? Part.Passive : Part.Ability;
                }
            }

            return Part.None;
        }

        /// <summary>C-008: панель и сферы — интерфейс; над панелью между сферами — мир.</summary>
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
            float h = maxHealth > 0 ? Mathf.Clamp01(health / (float)maxHealth) : 0f;
            float s = maxStamina > 0 ? Mathf.Clamp01(stamina / (float)maxStamina) : 0f;
            if (!Mathf.Approximately(_health.fillAmount, h))
            {
                _health.fillAmount = h;
            }

            if (!Mathf.Approximately(_stamina.fillAmount, s))
            {
                _stamina.fillAmount = s;
            }

            int key = health * 1000 + maxHealth;
            if (key != _healthShown)
            {
                _healthShown = key;
                _healthText.text = $"{health}/{maxHealth}";
            }
        }
    }
}
