#nullable enable
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Orpheus.Client.UI
{
    /// <summary>
    /// C-015: чёткий текст на холсте в мировом пространстве. uGUI сам подгоняет текст к пикселям только у экранного холста
    /// (<c>Canvas.pixelPerfect</c>); у нашего (<see cref="UiRoot"/>, мировое пространство) глифы, растрированные под пиксели
    /// кадра, вставали на дробные пиксели — атлас шрифта читался билинейно между пикселями, буквы «мылились». Здесь вся
    /// надпись сдвигается так, чтобы первая вершина легла ровно на пиксель кадра (глифы внутри надписи уже стоят на целых
    /// пикселях — генератор шрифта работает в пикселях кадра). Обводка (<see cref="Outline"/> = true) — 8 копий тёмным
    /// цветом на целое число пикселей во все стороны (вместо 4 диагональных uGUI <c>Outline</c>, которые давали рваный
    /// край): толщина ≈ 1,5 единицы холста, не меньше 1 пикселя.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class UiTextFx : BaseMeshEffect
    {
        static readonly List<UIVertex> _verts = new List<UIVertex>(256);
        static readonly List<UIVertex> _out = new List<UIVertex>(2304);

        /// <summary>Рисовать обводку.</summary>
        public bool Outline;

        /// <summary>Цвет обводки.</summary>
        public Color32 OutlineColor = UiStyle.Ink;

        /// <summary>Толщина обводки, единиц холста (в пикселях кадра — округляется, не меньше 1).</summary>
        public float OutlineWidth = 1.5f;

        public override void ModifyMesh(VertexHelper vh)
        {
            if (!IsActive() || vh.currentVertCount == 0)
            {
                return;
            }

            float ppu = UiRoot.PixelsPerUnit;
            RectTransform? canvas = UiRoot.Current?.Transform;
            vh.GetUIVertexStream(_verts);
            Vector3 shift = Vector3.zero;
            if (canvas != null && ppu > 0f)
            {
                // Первая вершина → координаты холста (начало — середина) → пиксели кадра от левого нижнего угла.
                Vector3 first = canvas.InverseTransformPoint(transform.TransformPoint(_verts[0].position));
                Vector2 size = UiRoot.Current!.Size;
                float px = (first.x + size.x / 2f) * ppu, py = (first.y + size.y / 2f) * ppu;
                shift = new Vector3((Mathf.Round(px) - px) / ppu, (Mathf.Round(py) - py) / ppu, 0f);
            }

            _out.Clear();
            if (Outline)
            {
                int d = Mathf.Max(1, Mathf.RoundToInt(OutlineWidth * Mathf.Max(ppu, 0.25f)));
                int diag = Mathf.Max(1, Mathf.RoundToInt(d * 0.75f));
                float u = 1f / Mathf.Max(ppu, 0.25f);
                AddCopy(shift + new Vector3(d * u, 0f, 0f), true);
                AddCopy(shift + new Vector3(-d * u, 0f, 0f), true);
                AddCopy(shift + new Vector3(0f, d * u, 0f), true);
                AddCopy(shift + new Vector3(0f, -d * u, 0f), true);
                AddCopy(shift + new Vector3(diag * u, diag * u, 0f), true);
                AddCopy(shift + new Vector3(-diag * u, diag * u, 0f), true);
                AddCopy(shift + new Vector3(diag * u, -diag * u, 0f), true);
                AddCopy(shift + new Vector3(-diag * u, -diag * u, 0f), true);
            }

            AddCopy(shift, false);
            vh.Clear();
            vh.AddUIVertexTriangleStream(_out);
        }

        void AddCopy(Vector3 offset, bool outline)
        {
            for (int i = 0; i < _verts.Count; i++)
            {
                UIVertex v = _verts[i];
                v.position += offset;
                if (outline)
                {
                    Color32 c = OutlineColor;
                    c.a = (byte)(c.a * v.color.a / 255);
                    v.color = c;
                }

                _out.Add(v);
            }
        }
    }
}
