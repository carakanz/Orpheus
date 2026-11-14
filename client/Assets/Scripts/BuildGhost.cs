#nullable enable
using Orpheus.Core.Geometry;
using Orpheus.Core.World;
using Orpheus.Net.Session;
using UnityEngine;

namespace Orpheus.Client
{
    /// <summary>
    /// Призрак постройки и сетка режима стройки (E2-5, `05` 1.3): пока в руках молот — лёгкая сетка тайлов в радиусе
    /// постановки (квадрат 13 × 13 вокруг тайла персонажа, <see cref="EditRules.PlaceRadiusTiles"/>) и под курсором
    /// полупрозрачная картинка выбранной постройки в рамке: зелёная — можно, красная — нельзя (причину пишет строка у
    /// курсора, <c>UI.UiNote</c>), жёлтая рамка — починка. «Можно / нельзя» сюда приходит готовым (<see cref="BuildCheck"/>
    /// из <see cref="BuildHand.Check"/>) — своих правил нет. Квадраты — спрайтом 1 × 1 из кода (ассета нет).
    /// </summary>
    public sealed class BuildGhost
    {
        /// <summary>Слой отрисовки: над объектами «с высотой», под тоном суток.</summary>
        public const int Order = WorldView.TintOrder - 10;

        const int Slots = 2;
        const float Line = 0.05f;
        static readonly Color Ok = new Color(0.35f, 1f, 0.45f, 0.62f);
        static readonly Color Bad = new Color(1f, 0.28f, 0.22f, 0.62f);
        static readonly Color Fix = new Color(1f, 0.85f, 0.25f, 0.9f);
        static readonly Color GridColor = new Color(1f, 1f, 1f, 0.16f);

        readonly GameObject _root;
        readonly SpriteRenderer[] _ghost = new SpriteRenderer[Slots];
        readonly SpriteRenderer[][] _frame = new SpriteRenderer[Slots][];
        readonly SpriteRenderer[] _fill = new SpriteRenderer[Slots];
        readonly SpriteRenderer[] _grid;
        static Sprite? _white;

        public BuildGhost(Transform parent)
        {
            _root = new GameObject("BuildGhost");
            _root.transform.SetParent(parent, false);
            for (int s = 0; s < Slots; s++)
            {
                _fill[s] = Quad("Fill" + s, Order);
                _ghost[s] = Quad("Ghost" + s, Order + 1);
                _frame[s] = new SpriteRenderer[4];
                for (int i = 0; i < 4; i++)
                {
                    _frame[s][i] = Quad($"Frame{s}_{i}", Order + 2);
                }
            }

            int lines = 2 * (2 * EditRules.PlaceRadiusTiles + 2);
            _grid = new SpriteRenderer[lines];
            for (int i = 0; i < lines; i++)
            {
                _grid[i] = Quad("Grid" + i, Order - 1);
                _grid[i].color = GridColor;
            }

            _root.SetActive(false);
        }

        /// <summary>Скрыть всё (молот убран, курсор над окном).</summary>
        public void Hide()
        {
            if (_root.activeSelf)
            {
                _root.SetActive(false);
            }
        }

        /// <summary>
        /// Показать сетку вокруг персонажа и призрак № <paramref name="slot"/> на тайле (слот 1 — второй призрак сцены
        /// стенда снимков; в игре — только 0). <paramref name="show"/> = false у слота — скрыть его.
        /// </summary>
        public void Show(FloatingOrigin origin, MapShape map, WorldPos self, int slot, bool show, TileCoord tile, in BuildCheck check)
        {
            if (!_root.activeSelf)
            {
                _root.SetActive(true);
            }

            ShowGrid(origin, map, self);
            _ghost[slot].gameObject.SetActive(show);
            _fill[slot].gameObject.SetActive(show);
            foreach (SpriteRenderer f in _frame[slot])
            {
                f.gameObject.SetActive(show);
            }

            if (!show)
            {
                return;
            }

            Vector3 c = origin.ToUnity(EditRules.CenterOf(Torus.Wrap(map, tile)));
            bool repair = check.Action == BuildAction.Repair;
            Color col = repair ? (check.Ok ? Fix : Bad) : check.Ok ? Ok : Bad;
            string? name = repair ? null : BuildLook.NameOf(check.Key);
            Sprite? s = name != null ? BuildLook.Centered(name) : null;
            _ghost[slot].sprite = s;
            _ghost[slot].enabled = s != null;
            _ghost[slot].color = new Color(col.r, col.g, col.b, 0.72f);
            _ghost[slot].transform.position = c;
            _fill[slot].transform.position = c;
            _fill[slot].transform.localScale = new Vector3(1f, 1f, 1f);
            _fill[slot].color = new Color(col.r, col.g, col.b, repair ? 0.18f : 0.28f);
            Edge(_frame[slot][0], c + new Vector3(0f, 0.5f, 0f), 1f + Line, Line, col);
            Edge(_frame[slot][1], c + new Vector3(0f, -0.5f, 0f), 1f + Line, Line, col);
            Edge(_frame[slot][2], c + new Vector3(-0.5f, 0f, 0f), Line, 1f + Line, col);
            Edge(_frame[slot][3], c + new Vector3(0.5f, 0f, 0f), Line, 1f + Line, col);
        }

        // Линии сетки по границам тайлов квадрата 13 × 13 вокруг тайла персонажа.
        void ShowGrid(FloatingOrigin origin, MapShape map, WorldPos self)
        {
            const int r = EditRules.PlaceRadiusTiles;
            TileCoord me = Torus.TileOf(map, self);
            Vector3 c = origin.ToUnity(EditRules.CenterOf(me));
            float half = r + 0.5f, len = 2f * r + 1f;
            int n = 2 * r + 2;
            for (int i = 0; i < n; i++)
            {
                float o = -half + i;
                Edge(_grid[i], c + new Vector3(o, 0f, 0f), 0.03f, len, GridColor);
                Edge(_grid[n + i], c + new Vector3(0f, o, 0f), len, 0.03f, GridColor);
            }
        }

        static void Edge(SpriteRenderer r, Vector3 at, float w, float h, Color col)
        {
            r.transform.position = at;
            r.transform.localScale = new Vector3(w, h, 1f);
            r.color = col;
        }

        SpriteRenderer Quad(string name, int order)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_root.transform, false);
            var r = go.AddComponent<SpriteRenderer>();
            r.sprite = name.StartsWith("Ghost") ? null : White();
            r.sortingOrder = order;
            return r;
        }

        static Sprite White()
        {
            if (_white == null)
            {
                var tex = new Texture2D(1, 1) { filterMode = FilterMode.Point };
                tex.SetPixel(0, 0, Color.white);
                tex.Apply();
                _white = Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);
            }

            return _white;
        }
    }
}
