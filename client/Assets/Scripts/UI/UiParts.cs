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
        public UiItemIcon(Transform parent, float x, float y, float box, float icon = UiStyle.IconSize)
        {
            Rt = UiRoot.Node(parent, "Item", x, y, box, box);
            float pad = (box - icon) / 2f;
            _image = UiRoot.Picture(Rt, "Icon", pad, pad, icon, icon, null, sliced: false);
            _count = UiRoot.Label(Rt, "Count", 0f, 1f, box - 4f, 22f, box >= 44f ? 19 : 18, TextAnchor.LowerRight, UiStyle.TextLight, outline: true, bold: true);
            Set(Items.None, 0);
        }

        /// <summary>Приглушить значок (рецепт, который сейчас сделать нельзя).</summary>
        public void Dim(bool dim)
        {
            if (dim != _dim)
            {
                _dim = dim;
                _image.color = dim ? new Color(1f, 1f, 1f, 0.4f) : Color.white;
            }
        }

        bool _dim;

        /// <summary>
        /// Значок рецепта (E2-9): предмет виден всегда, число — «сколько можно сделать» (и 1 тоже); 0 — без числа.
        /// </summary>
        public void SetBadge(byte item, int badge)
        {
            if (item != _item)
            {
                _item = item;
                _image.sprite = item == Items.None ? null : PileView.SpriteFor(item);
                _image.enabled = item != Items.None;
            }

            int shown = item != Items.None && badge > 0 ? badge : 0;
            if (shown != _shown)
            {
                _shown = shown;
                _count.text = shown > 0 ? shown.ToString() : string.Empty;
            }
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
    /// Вид ячеек сетки (C-008): картинки фона — обычная, под курсором, выключенная (рецепт, которого не сделать), рамка
    /// выбранной и цвет подписи клавиши. <see cref="Glass"/> — полупрозрачное «стекло» поверх мира, <see cref="Chest"/> —
    /// то же другого оттенка (сундук), <see cref="Parchment"/> — прежний пергамент (для будущих диалогов).
    /// </summary>
    public sealed class UiCellLook
    {
        public readonly Sprite Plain, Hover, Off, Selected;
        public readonly Color32 KeyColor;
        public readonly bool KeyOutline;

        public UiCellLook(Sprite plain, Sprite hover, Sprite off, Sprite selected, Color32 keyColor, bool keyOutline)
        {
            Plain = plain;
            Hover = hover;
            Off = off;
            Selected = selected;
            KeyColor = keyColor;
            KeyOutline = keyOutline;
        }

        static UiCellLook? _glass, _chest;

        public static UiCellLook Glass => _glass ??= new UiCellLook(UiStyle.Frame(UiStyle.Glass, UiStyle.GlassEdge, thick: 2f, radius: 6f),
            UiStyle.Frame(UiStyle.GlassHover, UiStyle.GlassEdge, thick: 2f, radius: 6f), UiStyle.Frame(UiStyle.GlassOff, UiStyle.GlassOffEdge, thick: 2f, radius: 6f),
            UiStyle.Frame(UiStyle.Clear, UiStyle.Gold, thick: 4f, radius: 8f), UiStyle.TextLight, true);

        public static UiCellLook Chest => _chest ??= new UiCellLook(UiStyle.Frame(UiStyle.GlassChest, UiStyle.GlassChestEdge, thick: 2f, radius: 6f),
            UiStyle.Frame(UiStyle.GlassHover, UiStyle.GlassChestEdge, thick: 2f, radius: 6f), UiStyle.Frame(UiStyle.GlassOff, UiStyle.GlassOffEdge, thick: 2f, radius: 6f),
            UiStyle.Frame(UiStyle.Clear, UiStyle.Gold, thick: 4f, radius: 8f), UiStyle.TextLight, true);

        public static UiCellLook Parchment(Color32 fill) => new UiCellLook(UiStyle.Frame(fill, UiStyle.Ink), UiStyle.Frame(UiStyle.CellHover, UiStyle.Ink),
            UiStyle.Frame(UiStyle.CellOff, UiStyle.InkSoft), UiStyle.Frame(UiStyle.Clear, UiStyle.Ochre, thick: 5f, radius: 9f), UiStyle.TextDark, false);
    }

    /// <summary>
    /// Сетка ячеек (E2-2): фон ячейки, значок с числом, рамка выбранной, подсветка под курсором, подпись клавиши.
    /// Ячейки нумеруются слева направо, сверху вниз, начиная с <see cref="First"/> — номер ячейки сумки. Ничего не
    /// знает про сумку: что показывать, говорит владелец (<see cref="Set"/>), попадание — <see cref="Hit"/>.
    /// C-008: размер ячейки и вид (<see cref="UiCellLook"/>) задаёт владелец; по умолчанию — 48 единиц.
    /// </summary>
    public sealed class UiSlotGrid
    {
        public readonly RectTransform Rt;
        public readonly int First;
        public readonly int Columns;
        public readonly int Rows;
        public readonly float Cell;
        readonly Image[] _back;
        readonly UiItemIcon[] _icons;
        readonly Image _selected;
        readonly UiCellLook _look;
        int _hover = -1;
        int _sel = -1;

        /// <summary>Шаг ячеек стандартного размера.</summary>
        public const float Step = UiStyle.CellSize + UiStyle.CellGap;

        public static float WidthOf(int columns) => columns * Step - UiStyle.CellGap;

        public static float WidthOf(int columns, float cell) => columns * (cell + UiStyle.CellGap) - UiStyle.CellGap;

        /// <summary>Шаг ячеек этой сетки.</summary>
        public float Pitch => Cell + UiStyle.CellGap;

        public float Width => WidthOf(Columns, Cell);

        public float Height => WidthOf(Rows, Cell);

        public UiSlotGrid(Transform parent, float x, float y, int columns, int rows, int first, Color32 fill, bool keyLabels = false)
            : this(parent, x, y, columns, rows, first, UiCellLook.Parchment(fill), keyLabels)
        {
        }

        public UiSlotGrid(Transform parent, float x, float y, int columns, int rows, int first, UiCellLook look, bool keyLabels = false,
            float cell = UiStyle.CellSize, float icon = UiStyle.IconSize)
        {
            First = first;
            Columns = columns;
            Rows = rows;
            Cell = cell;
            _look = look;
            Rt = UiRoot.Node(parent, "Grid", x, y, WidthOf(columns, cell), WidthOf(rows, cell));
            _off = new bool[columns * rows];
            _back = new Image[columns * rows];
            _icons = new UiItemIcon[columns * rows];
            for (int i = 0; i < _back.Length; i++)
            {
                Vector2 at = CellOrigin(i);
                _back[i] = UiRoot.Picture(Rt, "Cell", at.x, at.y, cell, cell, look.Plain);
                _icons[i] = new UiItemIcon(Rt, at.x, at.y, cell, Mathf.Min(icon, cell - 8f));
                if (keyLabels)
                {
                    // C-008: номер клавиши — мелко в левом верхнем углу (число стопки — справа внизу), как в Terraria.
                    UiRoot.Label(Rt, "Key", at.x + 4f, at.y + cell - 21f, 20f, 20f, 16, TextAnchor.UpperLeft, look.KeyColor, outline: look.KeyOutline, bold: true)
                        .text = (i + 1).ToString();
                }
            }

            _selected = UiRoot.Picture(Rt, "Selected", 0f, 0f, cell + 6f, cell + 6f, look.Selected);
            _selected.enabled = false;
        }

        public int Count => _back.Length;

        Vector2 CellOrigin(int i) => new Vector2(i % Columns * Pitch, (Rows - 1 - i / Columns) * Pitch);

        /// <summary>Середина ячейки сетки <paramref name="i"/> в координатах родителя сетки.</summary>
        public Vector2 CenterOf(int i) => Rt.anchoredPosition + CellOrigin(i) + new Vector2(Cell / 2f, Cell / 2f);

        /// <summary>Точка (в координатах родителя) внутри прямоугольника всей сетки, включая зазоры (C-008: это уже не мир).</summary>
        public bool Covers(Vector2 parentPoint)
        {
            Vector2 p = parentPoint - Rt.anchoredPosition;
            return Rt.gameObject.activeInHierarchy && p.x >= 0f && p.y >= 0f && p.x < Width && p.y < Height;
        }

        /// <summary>Содержимое ячейки сетки <paramref name="i"/> (0…<see cref="Count"/>−1).</summary>
        public void Set(int i, byte item, int count) => _icons[i].Set(item, count);

        /// <summary>Ячейка-рецепт (E2-9): значок, число «можно сделать», приглушена ли (сделать сейчас нельзя).</summary>
        public void SetRecipe(int i, byte item, int badge, bool off)
        {
            _icons[i].SetBadge(item, badge);
            _icons[i].Dim(off);
            if (_off[i] != off)
            {
                _off[i] = off;
                if (i != _hover)
                {
                    _back[i].sprite = off ? _look.Off : _look.Plain;
                }
            }
        }

        /// <summary>Показать / спрятать ячейку (сетка рецептов: ячейки за концом списка не видны).</summary>
        public void Show(int i, bool on)
        {
            if (_back[i].enabled != on)
            {
                _back[i].enabled = on;
                _icons[i].Rt.gameObject.SetActive(on);
            }
        }

        bool[] _off = System.Array.Empty<bool>();

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
                _selected.rectTransform.anchoredPosition = CellOrigin(i) - new Vector2(3f, 3f);
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
                _back[_hover].sprite = _off[_hover] ? _look.Off : _look.Plain;
            }

            _hover = i;
            if ((uint)i < (uint)_back.Length)
            {
                _back[i].sprite = _look.Hover;
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

            int col = (int)(p.x / Pitch), rowUp = (int)(p.y / Pitch);
            if (col >= Columns || rowUp >= Rows || p.x - col * Pitch >= Cell || p.y - rowUp * Pitch >= Cell)
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

    /// <summary>
    /// Кнопка или строка списка (E2-4): рамка с подписью внутри родителя; состояние — обычная, под курсором, выбрана,
    /// выключена (приглушена). Попадание считает владелец по <see cref="Contains"/> (точка в координатах родителя).
    /// </summary>
    public sealed class UiButton
    {
        public readonly RectTransform Rt;
        public readonly Text Label;
        readonly Image _back;
        readonly Sprite _plain, _hover, _selected, _off;
        readonly float _w, _h;
        readonly bool _glass;
        int _state = -1;

        public UiButton(Transform parent, float x, float y, float w, float h, string text, int size = 20, TextAnchor align = TextAnchor.MiddleCenter,
            float textPad = 0f, bool glass = false)
        {
            _w = w;
            _h = h;
            _glass = glass;
            Rt = UiRoot.Node(parent, "Button", x, y, w, h);
            if (glass)
            {
                // C-008: кнопка-«стекло» поверх мира: светлый текст с обводкой.
                _plain = UiStyle.Frame(UiStyle.Glass, UiStyle.GlassEdge, thick: 2f, radius: 6f);
                _hover = UiStyle.Frame(UiStyle.GlassHover, UiStyle.GlassEdge, thick: 2f, radius: 6f);
                _selected = UiStyle.Frame(UiStyle.GlassHover, UiStyle.Gold, thick: 3f, radius: 6f);
                _off = UiStyle.Frame(UiStyle.GlassOff, UiStyle.GlassOffEdge, thick: 2f, radius: 6f);
            }
            else
            {
                _plain = UiStyle.Frame(UiStyle.Cell, UiStyle.Ink);
                _hover = UiStyle.Frame(UiStyle.CellHover, UiStyle.Ink);
                _selected = UiStyle.Frame(UiStyle.CellHover, UiStyle.Ochre, thick: 4f);
                _off = UiStyle.Frame(UiStyle.CellOff, UiStyle.InkSoft);
            }

            _back = UiRoot.Picture(Rt, "Back", 0f, 0f, w, h, _plain);
            Label = UiRoot.Label(Rt, "Text", textPad, 0f, w - 2f * textPad, h, size, align, glass ? UiStyle.TextLight : UiStyle.TextDark, outline: glass, bold: true);
            Label.text = text;
            Set(false, false, true);
        }

        public Vector2 Center => Rt.anchoredPosition + new Vector2(_w / 2f, _h / 2f);

        public bool Visible
        {
            get => Rt.gameObject.activeSelf;
            set
            {
                if (Rt.gameObject.activeSelf != value)
                {
                    Rt.gameObject.SetActive(value);
                }
            }
        }

        /// <summary>Точка (в координатах родителя) внутри видимой кнопки.</summary>
        public bool Contains(Vector2 parentPoint)
        {
            Vector2 p = parentPoint - Rt.anchoredPosition;
            return Visible && p.x >= 0f && p.y >= 0f && p.x < _w && p.y < _h;
        }

        /// <summary>Вид: под курсором, выбрана, включена (выключенная — приглушена, подсветки нет).</summary>
        public void Set(bool hover, bool selected, bool enabled)
        {
            int state = !enabled ? (selected ? 4 : 3) : selected ? 2 : hover ? 1 : 0;
            if (state == _state)
            {
                return;
            }

            _state = state;
            _back.sprite = state == 3 ? _off : state == 2 || state == 4 ? _selected : state == 1 ? _hover : _plain;
            Label.color = _glass ? (enabled ? UiStyle.TextLight : new Color32(170, 176, 200, 200)) : enabled ? UiStyle.TextDark : UiStyle.TextDim;
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
        float _height = Height;

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
                // Несколько строк (подсказка рецепта, E2-9): высота по тексту, текст сверху вниз.
                int lines = 1;
                foreach (char ch in _shown)
                {
                    if (ch == '\n')
                    {
                        lines++;
                    }
                }

                _height = lines > 1 ? lines * 26f + 14f : Height;
                _rt.sizeDelta = new Vector2(w, _height);
                _back.rectTransform.sizeDelta = new Vector2(w, _height);
                _text.rectTransform.sizeDelta = new Vector2(w, _height);
            }

            float width = _rt.sizeDelta.x;
            _rt.anchoredPosition = new Vector2(Mathf.Clamp(at.x + 18f, 4f, canvas.x - width - 4f), Mathf.Clamp(at.y + 14f, 4f, canvas.y - _height - 4f));
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
