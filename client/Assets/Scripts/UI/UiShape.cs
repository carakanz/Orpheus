#nullable enable
using UnityEngine;
using UnityEngine.UI;

namespace Orpheus.Client.UI
{
    /// <summary>
    /// C-015 (слово Автора 15.11: шар рисовать аппаратно, без растровых текстур круга): круглая часть интерфейса — один
    /// четырёхугольник uGUI с шейдером <c>Orpheus/UiShape</c> (<c>Assets/Resources/UiShape.shader</c>). Форма — расстояние со
    /// знаком, край сглажен ровно в 1 экранный пиксель (fwidth), поэтому чёткая на любом разрешении и масштабе холста.
    /// Вершины: uv0 = −1…1 по стороне, uv1 = (<see cref="Kind"/>, <see cref="Fill"/>); цвет — цвет графики (альфа группы
    /// тоже). Материал общий — все фигуры холста идут одной пачкой. Маски uGUI работают (MaskableGraphic, трафарет и
    /// <c>_ClipRect</c> в шейдере).
    /// </summary>
    public sealed class UiShape : MaskableGraphic
    {
        /// <summary>Вид фигуры (номер — как в шейдере).</summary>
        public enum Shape : byte
        {
            /// <summary>Круг цвета графики (точки мини-карты).</summary>
            Disc = 0,

            /// <summary>Сфера здоровья / выносливости: кайма, золотой обод, стекло, «жидкость» цвета графики до <see cref="Fill"/>, блик.</summary>
            Orb = 1,

            /// <summary>Замок закрытой способности.</summary>
            Lock = 2,

            /// <summary>Линейка — значок режима постановки «линейка» (у быстрой панели).</summary>
            Ruler = 3,

            /// <summary>Зигзаг — значок режима постановки «поштучно».</summary>
            Zigzag = 4,
        }

        static Material? _material;
        Shape _kind;
        float _fill = 1f;

        /// <summary>Общий материал фигур (шейдер из Resources).</summary>
        public static Material SharedMaterial
        {
            get
            {
                if (_material == null)
                {
                    Shader shader = Shader.Find("Orpheus/UiShape");
                    _material = new Material(shader != null ? shader : Canvas.GetDefaultCanvasMaterial().shader)
                    {
                        name = "UiShape",
                        hideFlags = HideFlags.HideAndDontSave,
                    };
                }

                return _material;
            }
        }

        public override Material defaultMaterial => SharedMaterial;

        public Shape Kind
        {
            get => _kind;
            set
            {
                if (_kind != value)
                {
                    _kind = value;
                    SetVerticesDirty();
                }
            }
        }

        /// <summary>Уровень «жидкости» сферы 0…1 (снизу вверх).</summary>
        public float Fill
        {
            get => _fill;
            set
            {
                float v = Mathf.Clamp01(value);
                if (!Mathf.Approximately(v, _fill))
                {
                    _fill = v;
                    SetVerticesDirty();
                }
            }
        }

        /// <summary>Фигура в (x, y) родителя размером w × h (единицы холста).</summary>
        public static UiShape Create(Transform parent, string name, float x, float y, float w, float h, Shape kind, Color32 color)
        {
            var s = UiRoot.Node(parent, name, x, y, w, h).gameObject.AddComponent<UiShape>();
            s._kind = kind;
            s.color = color;
            s.raycastTarget = false;
            s.SetVerticesDirty();
            return s;
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            Rect r = GetPixelAdjustedRect();
            Color32 c = color;
            var data = new Vector4((float)_kind, _fill, 0f, 0f);
            Add(vh, r.xMin, r.yMin, -1f, -1f, c, data);
            Add(vh, r.xMin, r.yMax, -1f, 1f, c, data);
            Add(vh, r.xMax, r.yMax, 1f, 1f, c, data);
            Add(vh, r.xMax, r.yMin, 1f, -1f, c, data);
            vh.AddTriangle(0, 1, 2);
            vh.AddTriangle(2, 3, 0);
        }

        static void Add(VertexHelper vh, float x, float y, float u, float v, Color32 c, Vector4 data)
        {
            var vert = UIVertex.simpleVert;
            vert.position = new Vector3(x, y, 0f);
            vert.color = c;
            vert.uv0 = new Vector4(u, v, 0f, 0f);
            vert.uv1 = data;
            vh.AddVert(vert);
        }
    }
}
