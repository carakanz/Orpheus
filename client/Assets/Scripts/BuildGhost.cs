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
        static readonly Color Spare = new Color(1f, 0.8f, 0.3f, 0.35f);
        static readonly Color GridColor = new Color(1f, 1f, 1f, 0.13f);

        readonly GameObject _root;
        readonly SpriteRenderer[] _ghost = new SpriteRenderer[Slots];
        readonly SpriteRenderer[][] _frame = new SpriteRenderer[Slots][];
        readonly SpriteRenderer[] _fill = new SpriteRenderer[Slots];
        readonly SpriteRenderer[] _grid;
        readonly System.Collections.Generic.List<SpriteRenderer> _plan = new System.Collections.Generic.List<SpriteRenderer>();
        static Sprite? _white;
        WorldModel? _world;

        /// <summary>Мир клиента (E2-15): центр мебели со сдвигом у рамки починки.</summary>
        public void SetWorld(WorldModel? world) => _world = world;

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
            // E2-15: мебель — тайл со сдвигом на полтайла (якорь и сдвиг — из BuildCheck, план ядра); при починке — её центр.
            bool furniture = BuildLayers.IsFurniture(check.Key);
            float fw = 1f, fh = 1f; // E2-16: размер мебели в тайлах по повороту (кровать 1 × 2, верстак 2 × 2)
            if (furniture && !repair)
            {
                BuildLayers.SizeOf(check.Key, out int cw, out int ch);
                fw = cw * 0.5f;
                fh = ch * 0.5f;
                WorldPos a = EditRules.CenterOf(Torus.Wrap(map, check.Anchor));
                c = origin.ToUnity(new WorldPos(a.X + (2 * check.OffsetX + cw - 2) * BuildCells.CellUnits / 2,
                    a.Y + (2 * check.OffsetY + ch - 2) * BuildCells.CellUnits / 2));
            }
            else if (repair && _world != null)
            {
                c = origin.ToUnity(BuildLayers.CenterAt(_world, Torus.Wrap(map, tile)));
            }

            // E2-12: стена, забор, дверь, E2-15: пол — ячейки 0,5 × 0,5: призрак — прямоугольник ячеек (BuildCheck.Cells) без картинки тайла.
            bool cells = !repair && !furniture && check.Cells != 0 && check.Cells != BuildCells.Full;
            float w = furniture && !repair ? fw : 1f, h = furniture && !repair ? fh : 1f;
            if (cells)
            {
                CellBox(check.Cells, out float ox, out float oy, out w, out h);
                c += new Vector3(ox, oy, 0f);
            }

            bool flip = false;
            string? name = repair || cells ? null : furniture ? BuildLook.TurnedName(check.Key, out flip) : BuildLook.NameOf(check.Key);
            Sprite? s = name == null ? null : furniture ? BuildLook.Centered(name, fh) : BuildLook.Centered(name);
            _ghost[slot].sprite = s;
            _ghost[slot].flipX = flip; // E2-16: 270° — вид 90° зеркально
            _ghost[slot].enabled = s != null;
            _ghost[slot].color = new Color(col.r, col.g, col.b, 0.72f);
            _ghost[slot].transform.position = c;
            _fill[slot].transform.position = c;
            _fill[slot].transform.localScale = new Vector3(w, h, 1f);
            _fill[slot].color = new Color(col.r, col.g, col.b, repair ? 0.18f : cells ? 0.55f : 0.28f);
            Edge(_frame[slot][0], c + new Vector3(0f, h / 2f, 0f), w + Line, Line, col);
            Edge(_frame[slot][1], c + new Vector3(0f, -h / 2f, 0f), w + Line, Line, col);
            Edge(_frame[slot][2], c + new Vector3(-w / 2f, 0f, 0f), Line, h + Line, col);
            Edge(_frame[slot][3], c + new Vector3(w / 2f, 0f, 0f), Line, h + Line, col);
        }

        /// <summary>
        /// План линейки (E2-13): сетка и клетка на каждую клетку плана — зелёная (можно), бледно-жёлтая (можно, но предметов
        /// не хватит), красная (нельзя — будет пропущена). Ячейки стен — квадрат 0,5, пол — тайл. Призраки слотов скрыты.
        /// «Можно / нельзя» — готовое из <see cref="BuildRuler"/> (проверка ядра), своих правил нет.
        /// </summary>
        public void ShowPlan(FloatingOrigin origin, MapShape map, WorldPos self, System.ReadOnlySpan<RulerSpot> spots)
        {
            if (!_root.activeSelf)
            {
                _root.SetActive(true);
            }

            ShowGrid(origin, map, self);
            for (int s = 0; s < Slots; s++)
            {
                _ghost[s].gameObject.SetActive(false);
                _fill[s].gameObject.SetActive(false);
                foreach (SpriteRenderer f in _frame[s])
                {
                    f.gameObject.SetActive(false);
                }
            }

            while (_plan.Count < spots.Length)
            {
                _plan.Add(Quad("Plan" + _plan.Count, Order));
            }

            for (int i = 0; i < _plan.Count; i++)
            {
                bool on = i < spots.Length;
                if (_plan[i].gameObject.activeSelf != on)
                {
                    _plan[i].gameObject.SetActive(on);
                }

                if (!on)
                {
                    continue;
                }

                RulerSpot sp = spots[i];
                Vector3 c = origin.ToUnity(EditRules.CenterOf(Torus.Wrap(map, sp.Tile)));
                float w = 0.94f, h = 0.94f;
                if (sp.Cells != 0 && sp.Cells != BuildCells.Full)
                {
                    CellBox(sp.Cells, out float ox, out float oy, out w, out h);
                    c += new Vector3(ox, oy, 0f);
                    w -= 0.04f;
                    h -= 0.04f;
                }

                _plan[i].transform.position = c;
                _plan[i].transform.localScale = new Vector3(w, h, 1f);
                _plan[i].color = !sp.Ok ? Bad : sp.Short ? Spare : new Color(Ok.r, Ok.g, Ok.b, 0.5f);
            }
        }

        /// <summary>Убрать план линейки (протягивание кончилось).</summary>
        public void HidePlan()
        {
            foreach (SpriteRenderer r in _plan)
            {
                if (r.gameObject.activeSelf)
                {
                    r.gameObject.SetActive(false);
                }
            }
        }

        /// <summary>
        /// Прямоугольник ячеек маски (E2-12) относительно центра тайла в единицах Unity: сдвиг (Y Unity вверх, ячейка qy 0 —
        /// северная, сверху) и размер (0,5 или 1 по оси).
        /// </summary>
        public static void CellBox(byte mask, out float ox, out float oy, out float w, out float h)
        {
            int x0 = 1, x1 = 0, y0 = 1, y1 = 0;
            for (int q = 0; q < 4; q++)
            {
                if ((mask & (1 << q)) == 0)
                {
                    continue;
                }

                int qx = q & 1, qy = q >> 1;
                x0 = Mathf.Min(x0, qx);
                x1 = Mathf.Max(x1, qx);
                y0 = Mathf.Min(y0, qy);
                y1 = Mathf.Max(y1, qy);
            }

            ox = (x0 + x1 + 1) * 0.25f - 0.5f;
            oy = -((y0 + y1 + 1) * 0.25f - 0.5f);
            w = (x1 - x0 + 1) * 0.5f;
            h = (y1 - y0 + 1) * 0.5f;
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
                Edge(_grid[i], c + new Vector3(o, 0f, 0f), 0.06f, len, GridColor); // 0,06 тайла: на 720p (32 px на тайл) — 2 px, не пропадает
                Edge(_grid[n + i], c + new Vector3(0f, o, 0f), len, 0.06f, GridColor);
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
