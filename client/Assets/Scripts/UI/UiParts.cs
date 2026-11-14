#nullable enable
using Orpheus.Core.Sim;
using UnityEngine;
using UnityEngine.UI;

namespace Orpheus.Client.UI
{
    /// <summary>
    /// Значок предмета с числом стопки (E2-2): картинка <c>item_&lt;ключ&gt;</c> из набора объектов (те же, что у куч на
    /// земле — <see cref="PileView.SpriteFor"/>) и число справа внизу с тёмной обводкой; одна штука — без числа.
    /// </summary>
    public sealed class UiItemIcon
    {
        public readonly RectTransform Rt;
        readonly Image _image;
        readonly Text _count;
        byte _item = 255;
        int _shown = -1;

        /// <summary>Значок в квадрате <paramref name="box"/> × <paramref name="box"/> (ячейка): картинка по центру.</summary>
        public UiItemIcon(Transform parent, float x, float y, float box)
        {
            Rt = UiRoot.Node(parent, "Item", x, y, box, box);
            float pad = (box - UiStyle.IconSize) / 2f;
            _image = UiRoot.Picture(Rt, "Icon", pad, pad, UiStyle.IconSize, UiStyle.IconSize, null, sliced: false);
            _count = UiRoot.Label(Rt, "Count", 0f, 4f, box - 6f, 24f, 20, TextAnchor.LowerRight, UiStyle.TextLight, outline: true, bold: true);
            Set(Items.None, 0);
        }

        /// <summary>Показать предмет и количество; нет предмета или 0 штук — пусто.</summary>
        public void Set(byte item, int count)
        {
            if (count <= 0)
            {
                item = Items.None;
            }

            if (item != _item)
            {
                _item = item;
                _image.sprite = item == Items.None ? null : PileView.SpriteFor(item);
                _image.enabled = item != Items.None;
            }

            int shown = item != Items.None && count > 1 ? count : 0;
            if (shown != _shown)
            {
                _shown = shown;
                _count.text = shown > 0 ? shown.ToString() : string.Empty;
            }
        }
    }

    /// <summary>
    /// Сетка ячеек (E2-2): фон ячейки, значок с числом, рамка выбранной, подсветка под курсором, подпись клавиши.
    /// Ячейки нумеруются слева направо, сверху вниз, начиная с <see cref="First"/> — номер ячейки сумки. Ничего не
    /// знает про сумку: что показывать, говорит владелец (<see cref="Set"/>), попадание — <see cref="Hit"/>.
    /// </summary>
    public sealed class UiSlotGrid
    {
        public readonly RectTransform Rt;
        public readonly int First;
        public readonly int Columns;
        public readonly int Rows;
        readonly Image[] _back;
        readonly UiItemIcon[] _icons;
        readonly Image _selected;
        readonly Sprite _plain;
        readonly Sprite _hoverSprite;
        int _hover = -1;
        int _sel = -1;

        public const float Step = UiStyle.CellSize + UiStyle.CellGap;

        public static float WidthOf(int columns) => columns * Step - UiStyle.CellGap;

        public UiSlotGrid(Transform parent, float x, float y, int columns, int rows, int first, Color32 fill, bool keyLabels = false)
        {
            First = first;
            Columns = columns;
            Rows = rows;
            Rt = UiRoot.Node(parent, "Grid", x, y, WidthOf(columns), WidthOf(rows));
            _plain = UiStyle.Frame(fill, UiStyle.Ink);
            _hoverSprite = UiStyle.Frame(UiStyle.CellHover, UiStyle.Ink);
            _back = new Image[columns * rows];
            _icons = new UiItemIcon[columns * rows];
            for (int i = 0; i < _back.Length; i++)
            {
                Vector2 at = CellOrigin(i);
                _back[i] = UiRoot.Picture(Rt, "Cell", at.x, at.y, UiStyle.CellSize, UiStyle.CellSize, _plain);
                _icons[i] = new UiItemIcon(Rt, at.x, at.y, UiStyle.CellSize);
                if (keyLabels)
                {
                    UiRoot.Label(Rt, "Key", at.x + UiStyle.CellSize - 28f, at.y + UiStyle.CellSize - 25f, 21f, 20f, 16, TextAnchor.UpperRight, UiStyle.TextDark, bold: true)
                        .text = (i + 1).ToString();
                }
            }

            _selected = UiRoot.Picture(Rt, "Selected", 0f, 0f, UiStyle.CellSize + 8f, UiStyle.CellSize + 8f,
                UiStyle.Frame(UiStyle.Clear, UiStyle.Ochre, thick: 5f, radius: 9f));
            _selected.enabled = false;
        }

        public int Count => _back.Length;

        Vector2 CellOrigin(int i) => new Vector2(i % Columns * Step, (Rows - 1 - i / Columns) * Step);

        /// <summary>Содержимое ячейки сетки <paramref name="i"/> (0…<see cref="Count"/>−1).</summary>
        public void Set(int i, byte item, int count) => _icons[i].Set(item, count);

        /// <summary>Рамка выбранной ячейки; −1 — нет.</summary>
        public void Select(int i)
        {
            if (i == _sel)
            {
                return;
            }

            _sel = i;
            _selected.enabled = (uint)i < (uint)_back.Length;
            if (_selected.enabled)
            {
                _selected.rectTransform.anchoredPosition = CellOrigin(i) - new Vector2(4f, 4f);
            }
        }

        /// <summary>Подсветка ячейки под курсором; −1 — нет.</summary>
        public void Hover(int i)
        {
            if (i == _hover)
            {
                return;
            }

            if ((uint)_hover < (uint)_back.Length)
            {
                _back[_hover].sprite = _plain;
            }

            _hover = i;
            if ((uint)i < (uint)_back.Length)
            {
                _back[i].sprite = _hoverSprite;
            }
        }

        /// <summary>Ячейка сетки под точкой (в координатах родителя сетки); мимо или в зазоре — −1.</summary>
        public int Hit(Vector2 parentPoint)
        {
            Vector2 p = parentPoint - Rt.anchoredPosition;
            if (p.x < 0f || p.y < 0f)
            {
                return -1;
            }

            int col = (int)(p.x / Step), rowUp = (int)(p.y / Step);
            if (col >= Columns || rowUp >= Rows || p.x - col * Step >= UiStyle.CellSize || p.y - rowUp * Step >= UiStyle.CellSize)
            {
                return -1;
            }

            return (Rows - 1 - rowUp) * Columns + col;
        }
    }

    /// <summary>Окно (E2-2): группа с рамкой-пергаментом и заголовком. Основа окна сумки и будущего окна крафта.</summary>
    public class UiWindow : UiGroup
    {
        public const float Pad = 18f;
        public const float TitleHeight = 40f;
        protected readonly Text Title;

        public UiWindow(UiRoot root, string name, string title, Vector2 anchor, Vector2 offset, Vector2 size)
            : base(root, name, anchor, offset, size)
        {
            UiRoot.Picture(Rt, "Back", 0f, 0f, size.x, size.y, UiStyle.Frame(UiStyle.Parchment, UiStyle.Ink, thick: 4f, radius: 10f));
            Title = UiRoot.Label(Rt, "Title", Pad, size.y - Pad - TitleHeight, size.x - 2f * Pad, TitleHeight, 26, TextAnchor.MiddleLeft,
                UiStyle.TextDark, bold: true);
            Title.text = title;
            Visible = false;
        }
    }

    /// <summary>Подсказка у курсора (E2-2): тёмная плашка с названием; не вылезает за край холста.</summary>
    public sealed class UiTooltip
    {
        readonly RectTransform _rt;
        readonly Image _back;
        readonly Text _text;
        string _shown = string.Empty;
        const float Height = 34f;

        public UiTooltip(UiRoot root)
        {
            _rt = UiRoot.Node(root.Transform, "Tooltip", 0f, 0f, 100f, Height);
            _back = UiRoot.Picture(_rt, "Back", 0f, 0f, 100f, Height, UiStyle.Frame(UiStyle.Ink, UiStyle.Ink, radius: 6f));
            _text = UiRoot.Label(_rt, "Text", 12f, 0f, 100f, Height, 20, TextAnchor.MiddleLeft, UiStyle.TextLight);
            _rt.gameObject.SetActive(false);
        }

        /// <summary>Показать текст у точки холста; пустой текст — скрыть.</summary>
        public void Show(string? text, Vector2 canvas, Vector2 at)
        {
            bool on = !string.IsNullOrEmpty(text);
            if (_rt.gameObject.activeSelf != on)
            {
                _rt.gameObject.SetActive(on);
            }

            if (!on)
            {
                return;
            }

            if (text != _shown)
            {
                _shown = text!;
                _text.text = _shown;
                float w = Mathf.Ceil(_text.preferredWidth) + 24f;
                _rt.sizeDelta = new Vector2(w, Height);
                _back.rectTransform.sizeDelta = new Vector2(w, Height);
            }

            float width = _rt.sizeDelta.x;
            _rt.anchoredPosition = new Vector2(Mathf.Clamp(at.x + 18f, 4f, canvas.x - width - 4f), Mathf.Clamp(at.y + 14f, 4f, canvas.y - Height - 4f));
        }
    }

    /// <summary>Предмет «в руке» у курсора (E2-2): значок с числом, рисуется поверх окон.</summary>
    public sealed class UiCarry
    {
        readonly UiItemIcon _icon;

        public UiCarry(UiRoot root)
        {
            _icon = new UiItemIcon(root.Transform, 0f, 0f, UiStyle.CellSize);
        }

        /// <summary>Показать предмет у точки холста; 0 штук — скрыть.</summary>
        public void Show(byte item, int count, Vector2 at)
        {
            _icon.Set(item, count);
            // Одна штука без числа; значок чуть правее и ниже острия курсора.
            _icon.Rt.anchoredPosition = at - new Vector2(UiStyle.CellSize * 0.35f, UiStyle.CellSize * 0.65f);
        }
    }

    /// <summary>Полоса (здоровье, выносливость): тёмная рамка, плоская заливка слева направо.</summary>
    public sealed class UiBar
    {
        readonly RectTransform _fill;
        readonly float _width;
        readonly float _height;
        float _value = -1f;
        const float Inset = 3f;

        public UiBar(Transform parent, float x, float y, float w, float h, Color32 color)
        {
            _width = w - 2f * Inset;
            _height = h - 2f * Inset;
            UiRoot.Picture(parent, "BarBack", x, y, w, h, UiStyle.Frame(UiStyle.BarBack, UiStyle.Ink, radius: 5f));
            Image fill = UiRoot.Picture(parent, "BarFill", x + Inset, y + Inset, _width, _height, null, sliced: false);
            fill.color = color;
            _fill = fill.rectTransform;
        }

        /// <summary>Доля 0…1.</summary>
        public void Set(float value)
        {
            value = Mathf.Clamp01(value);
            if (!Mathf.Approximately(value, _value))
            {
                _value = value;
                _fill.sizeDelta = new Vector2(_width * value, _height);
            }
        }
    }
}
