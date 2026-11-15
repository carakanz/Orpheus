#nullable enable
using UnityEngine;
using UnityEngine.UI;

namespace Orpheus.Client.UI
{
    /// <summary>
    /// Холст интерфейса (E2-2): uGUI, всё создаётся кодом. Холст стоит в мировом пространстве перед основной камерой
    /// (её дочерний объект) и каждый кадр подгоняется под кадр: высота холста всегда <see cref="RefHeight"/> единиц,
    /// ширина — по соотношению сторон. Так интерфейс попадает и в окно, и в снимок, который камера рисует в текстуру
    /// другого размера (<see cref="Fit"/> зовёт <c>WorldFx.BeforeManualRender</c>), а текст растрируется под пиксели
    /// именно этого кадра. Порядок отрисовки — выше слоя тона суток: ночь интерфейс не красит.
    /// <para>Координаты холста: начало — левый нижний угол, X вправо, Y вверх, единица = 1 px на экране 1080.</para>
    /// </summary>
    public sealed class UiRoot
    {
        public const float RefHeight = 1080f;

        /// <summary>Порядок отрисовки холста: выше тона суток (<see cref="WorldView.TintOrder"/>).</summary>
        public const int SortOrder = WorldView.TintOrder + 1000;

        /// <summary>Холст последнего созданного интерфейса (для снимка).</summary>
        public static UiRoot? Current { get; private set; }

        readonly Camera _camera;
        readonly Canvas _canvas;
        readonly RectTransform _rt;
        float _aspect = -1f;
        float _pixelHeight = -1f;
        float _ortho = -1f;

        public UiRoot(Camera camera)
        {
            _camera = camera;
            var go = new GameObject("UI") { layer = 5 };
            go.transform.SetParent(camera.transform, false);
            _canvas = go.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.WorldSpace;
            _canvas.worldCamera = camera;
            _canvas.sortingOrder = SortOrder;
            _rt = (RectTransform)go.transform;
            _rt.pivot = new Vector2(0.5f, 0.5f);
            Current = this;
            FitToScreen();
        }

        /// <summary>Корень: сюда вешаются группы (<see cref="UiGroup"/>).</summary>
        public RectTransform Transform => _rt;

        /// <summary>Размер холста в его единицах: (1080 × соотношение сторон, 1080).</summary>
        public Vector2 Size { get; private set; } = new Vector2(1920f, RefHeight);

        /// <summary>
        /// Размер кадра вместо окна (режим снимка, <c>--screenshot</c>): снимок рисуется в текстуру своего размера, окно
        /// стенда — другое; холст сразу живёт в размере снимка, чтобы места у курсора (подсказка, «рука») совпали с кадром.
        /// </summary>
        public static Vector2Int? FixedFrame { get; set; }

        /// <summary>Подогнать под окно игры (каждый кадр); в режиме снимка — под кадр снимка.</summary>
        public void FitToScreen()
        {
            int w = FixedFrame?.x ?? Screen.width, h = FixedFrame?.y ?? Screen.height;
            Fit(w / (float)Mathf.Max(1, h), h);
        }

        /// <summary>Подогнать холст под кадр: соотношение сторон и высота кадра в пикселях (окно или текстура снимка).</summary>
        public void Fit(float aspect, float pixelHeight)
        {
            float ortho = _camera.orthographicSize;
            if (Mathf.Approximately(aspect, _aspect) && Mathf.Approximately(pixelHeight, _pixelHeight) && Mathf.Approximately(ortho, _ortho))
            {
                return;
            }

            bool resized = !Mathf.Approximately(aspect, _aspect) || !Mathf.Approximately(pixelHeight, _pixelHeight);
            _aspect = aspect;
            _pixelHeight = pixelHeight;
            _ortho = ortho;
            Size = new Vector2(RefHeight * aspect, RefHeight);
            _rt.sizeDelta = Size;
            float s = 2f * ortho / RefHeight;
            _rt.localScale = new Vector3(s, s, 1f);
            _rt.localPosition = new Vector3(0f, 0f, 1f);
            if (resized)
            {
                // Текст растрируется под пиксели кадра: 1 единица холста = pixelHeight / 1080 px.
                _canvas.scaleFactor = Mathf.Max(0.25f, pixelHeight / RefHeight);
                foreach (Graphic g in _rt.GetComponentsInChildren<Graphic>(true))
                {
                    g.SetAllDirty();
                }
            }

            Canvas.ForceUpdateCanvases();
        }

        /// <summary>Точка мира Unity → координаты холста (через видовую область камеры; стенд снимков, E2-5).</summary>
        public Vector2 FromWorld(Vector3 world)
        {
            // Не через видовую область камеры: в снимке кадр — текстура своего размера, а камера знает соотношение окна.
            Vector3 d = world - _camera.transform.position;
            float k = RefHeight / (2f * _camera.orthographicSize);
            return Size / 2f + new Vector2(d.x * k, d.y * k);
        }

        /// <summary>Точка окна (пиксели, начало слева внизу) → координаты холста.</summary>
        public Vector2 FromScreen(Vector2 screen) =>
            new Vector2(screen.x / Mathf.Max(1, Screen.width) * Size.x, screen.y / Mathf.Max(1, Screen.height) * Size.y);

        // ---------- кирпичи ----------

        /// <summary>Пустой узел: левый нижний угол в (x, y) родителя, размер w × h.</summary>
        public static RectTransform Node(Transform parent, string name, float x, float y, float w, float h)
        {
            var go = new GameObject(name, typeof(RectTransform)) { layer = 5 };
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = rt.pivot = Vector2.zero;
            rt.anchoredPosition = new Vector2(x, y);
            rt.sizeDelta = new Vector2(w, h);
            return rt;
        }

        /// <summary>Картинка (рамка с 9 срезами или значок).</summary>
        public static Image Picture(Transform parent, string name, float x, float y, float w, float h, Sprite? sprite, bool sliced = true)
        {
            var img = Node(parent, name, x, y, w, h).gameObject.AddComponent<Image>();
            img.sprite = sprite;
            img.type = sliced && sprite != null ? Image.Type.Sliced : Image.Type.Simple;
            img.raycastTarget = false;
            return img;
        }

        /// <summary>Надпись; <paramref name="outline"/> — тёмная обводка (числа поверх значков).</summary>
        public static Text Label(Transform parent, string name, float x, float y, float w, float h, int size, TextAnchor align,
            Color32 color, bool outline = false, bool bold = false)
        {
            var t = Node(parent, name, x, y, w, h).gameObject.AddComponent<Text>();
            t.font = UiStyle.Font;
            t.fontSize = Mathf.Max(size, UiStyle.MinFont); // E2-4: мельче 12 px на 720p не пишем
            t.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
            t.alignment = align;
            t.color = color;
            t.raycastTarget = false;
            t.supportRichText = false;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            if (outline)
            {
                var o = t.gameObject.AddComponent<Outline>();
                o.effectColor = UiStyle.Ink;
                o.effectDistance = new Vector2(2f, -2f); // C-008: цифры поверх мира — обводка толще
            }

            return t;
        }
    }

    /// <summary>
    /// Группа верхнего уровня (панель, окно, полосы): прямоугольник, привязанный к точке холста (0…1 по осям) со сдвигом
    /// левого нижнего угла. Положение считает сама — по нему же проверяется попадание курсора.
    /// </summary>
    public class UiGroup
    {
        public readonly RectTransform Rt;
        readonly Vector2 _anchor;
        Vector2 _offset;
        readonly Vector2 _size;

        /// <summary>Сдвиг левого нижнего угла от точки привязки (E2-4: окна встают рядом, когда открыты оба).</summary>
        public Vector2 Offset
        {
            get => _offset;
            set
            {
                if (_offset != value)
                {
                    _offset = value;
                    Rt.anchoredPosition = value;
                }
            }
        }

        public UiGroup(UiRoot root, string name, Vector2 anchor, Vector2 offset, Vector2 size)
        {
            _anchor = anchor;
            _offset = offset;
            _size = size;
            Rt = UiRoot.Node(root.Transform, name, 0f, 0f, size.x, size.y);
            Rt.anchorMin = Rt.anchorMax = anchor;
            Rt.anchoredPosition = offset;
        }

        public Vector2 Size => _size;

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

        /// <summary>Левый нижний угол группы на холсте размера <paramref name="canvas"/>.</summary>
        public Vector2 Origin(Vector2 canvas) => new Vector2(canvas.x * _anchor.x + _offset.x, canvas.y * _anchor.y + _offset.y);

        /// <summary>Точка холста → точка внутри группы.</summary>
        public Vector2 Local(Vector2 canvas, Vector2 point) => point - Origin(canvas);

        /// <summary>
        /// C-008: часть видимой группы (прямоугольник в её координатах) — «интерфейс, не мир» для
        /// <see cref="Orpheus.Net.Session.UiPointer"/>; вне таких частей щелчок идёт в мир.
        /// </summary>
        public void Zone(Orpheus.Net.Session.UiPointer p, Vector2 canvas, float x, float y, float w, float h)
        {
            if (Visible)
            {
                Vector2 o = Origin(canvas);
                p.Add(o.x + x, o.y + y, w, h);
            }
        }

        /// <summary>Курсор над группой (видимой).</summary>
        public bool Contains(Vector2 canvas, Vector2 point)
        {
            Vector2 p = Local(canvas, point);
            return Visible && p.x >= 0f && p.y >= 0f && p.x < _size.x && p.y < _size.y;
        }
    }
}
