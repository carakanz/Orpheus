#nullable enable
using System.Collections.Generic;
using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.Net.Protocol;
using Orpheus.Net.Session;
using UnityEngine;
using UnityEngine.UI;

namespace Orpheus.Client.UI
{
    /// <summary>
    /// Карта на весь экран (C-029 п. 8, слово Автора 17.11: «Нужна возможность раскрыть карту на весь экран»): M — открыть,
    /// M или Esc — закрыть. Открытые области — <see cref="ExploredMap"/> (чанки, которые клиент видел; остальное тёмное),
    /// колесо — масштаб (4 … 1/4 пикселя картинки на тайл), ЛКМ с перетаскиванием — сдвиг, ПКМ или Пробел — к себе. Метки:
    /// свой персонаж — золотая точка с каймой, другие игроки — голубые, кровати — оранжевые квадраты, Очаг — белое кольцо
    /// (<i>допущение C-029:</i> у клиента нет места Очага — это точка, где персонаж возрождается без кровати, а до первого
    /// возрождения — точка входа в мир). Пока карта открыта, мышь миру не отдаётся.
    /// </summary>
    public sealed class WorldMapView : UiGroup
    {
        /// <summary>Тайлов на пиксель картинки по шагам масштаба; по умолчанию — 1.</summary>
        public static readonly double[] Scales = { 0.25, 0.5, 1.0, 2.0, 4.0 };

        const int TexH = 270, MaxTexW = 640;
        const int MaxBeds = 16, MaxOthers = 16;

        readonly UiRoot _root;
        readonly RectTransform _body;
        readonly RawImage _image;
        readonly Text _title, _help, _coords;
        readonly UiShape _selfRing, _self, _hearth, _hearthHole;
        readonly UiShape[] _beds = new UiShape[MaxBeds];
        readonly UiShape[] _others = new UiShape[MaxOthers];
        Texture2D? _tex;
        uint[] _px = new uint[0];
        Color32[] _colors = new Color32[0];
        int _texW;
        int _zoom = 2;
        double _cx, _cy;      // центр показа, тайлы
        bool _follow = true;  // центр — за персонажем, пока карту не сдвинули
        bool _dragging;
        Vector2 _dragFrom;
        double _dragCx, _dragCy;
        int _paintedVersion = -1, _paintedZoom = -1;
        double _paintedCx = double.NaN, _paintedCy = double.NaN;
        float _nextPaint;

        public WorldMapView(UiRoot root)
            : base(root, "WorldMap", Vector2.zero, Vector2.zero, new Vector2(16f, 16f))
        {
            _root = root;
            _body = UiRoot.Node(Rt, "Body", 0f, 0f, 16f, 16f);
            UiRoot.Picture(_body, "Back", 0f, 0f, 16f, 16f, null, sliced: false).color = new Color32(10, 10, 18, 255);
            _image = UiRoot.Node(_body, "Map", 0f, 0f, 16f, 16f).gameObject.AddComponent<RawImage>();
            _image.raycastTarget = false;
            for (int i = 0; i < MaxBeds; i++)
            {
                _beds[i] = UiShape.Create(_body, "Bed", 0f, 0f, 14f, 14f, UiShape.Shape.Disc, new Color32(255, 160, 40, 255));
                _beds[i].enabled = false;
            }

            for (int i = 0; i < MaxOthers; i++)
            {
                _others[i] = UiShape.Create(_body, "Other", 0f, 0f, 12f, 12f, UiShape.Shape.Disc, new Color32(90, 200, 255, 255));
                _others[i].enabled = false;
            }

            _hearth = UiShape.Create(_body, "Hearth", 0f, 0f, 22f, 22f, UiShape.Shape.Disc, new Color32(255, 255, 255, 255));
            _hearthHole = UiShape.Create(_body, "HearthHole", 0f, 0f, 12f, 12f, UiShape.Shape.Disc, UiStyle.Ink);
            _selfRing = UiShape.Create(_body, "SelfRing", 0f, 0f, 20f, 20f, UiShape.Shape.Disc, UiStyle.Ink);
            _self = UiShape.Create(_body, "Self", 0f, 0f, 14f, 14f, UiShape.Shape.Disc, UiStyle.Gold);
            _title = UiRoot.Label(_body, "Title", 24f, 0f, 600f, 34f, 26, TextAnchor.MiddleLeft, UiStyle.TextLight, outline: true, bold: true);
            _title.text = UiStrings.WorldMapTitle;
            _help = UiRoot.Label(_body, "Help", 24f, 12f, 1400f, 26f, 18, TextAnchor.MiddleLeft, UiStyle.TextLight, outline: true, bold: true);
            _help.text = UiStrings.WorldMapHelp;
            _coords = UiRoot.Label(_body, "Coords", 0f, 12f, 400f, 26f, 18, TextAnchor.MiddleRight, UiStyle.TextLight, outline: true, bold: true);
            Visible = false;
        }

        /// <summary>Открыта.</summary>
        public bool Open => Visible;

        /// <summary>Тайлов на пиксель картинки.</summary>
        public double Scale => Scales[_zoom];

        public void Toggle()
        {
            Visible = !Visible;
            _dragging = false;
            _follow = true;
            _paintedVersion = -1;
        }

        public void Close()
        {
            Visible = false;
            _dragging = false;
        }

        /// <summary>Колесо: вверх — крупнее (меньше тайлов на пиксель).</summary>
        public void Zoom(float wheel)
        {
            _zoom = Mathf.Clamp(_zoom + (wheel > 0f ? -1 : 1), 0, Scales.Length - 1);
        }

        /// <summary>Вернуть центр к своему персонажу (ПКМ, Пробел).</summary>
        public void Recenter() => _follow = true;

        /// <summary>
        /// Ввод открытой карты: ЛКМ зажата — тянуть (точка холста <paramref name="pointer"/>), колесо — масштаб.
        /// </summary>
        public void Input(bool leftDown, bool leftHeld, Vector2 pointer, float wheel)
        {
            if (Mathf.Abs(wheel) > 0.01f)
            {
                Zoom(wheel);
            }

            if (leftDown)
            {
                _dragging = true;
                _dragFrom = pointer;
                _dragCx = _cx;
                _dragCy = _cy;
            }

            if (_dragging && leftHeld)
            {
                double perUnit = Scale * _texW / Mathf.Max(1f, _root.Size.x); // тайлов на единицу холста
                Vector2 d = pointer - _dragFrom;
                if (d.sqrMagnitude > 4f)
                {
                    _follow = false;
                }

                if (!_follow)
                {
                    _cx = _dragCx - d.x * perUnit;
                    _cy = _dragCy + d.y * perUnit; // холст: Y вверх, мир: Y на юг
                }
            }
            else
            {
                _dragging = false;
            }
        }

        /// <summary>
        /// Кадр открытой карты: открытое, место своего персонажа, точка Очага (null — неизвестна), снимок (другие игроки).
        /// </summary>
        public void Frame(ExploredMap? explored, MapShape shape, WorldPos self, WorldPos? hearth, Snapshot? snap, int selfId)
        {
            if (!Visible)
            {
                return;
            }

            Vector2 canvas = _root.Size;
            if (Rt.sizeDelta != canvas)
            {
                Rt.sizeDelta = canvas;
                _body.sizeDelta = canvas;
                foreach (Transform child in _body)
                {
                    var rt = (RectTransform)child;
                    if (child.name == "Back" || child.name == "Map")
                    {
                        rt.sizeDelta = canvas;
                    }
                }

                _title.rectTransform.anchoredPosition = new Vector2(24f, canvas.y - 50f);
                _coords.rectTransform.anchoredPosition = new Vector2(canvas.x - 424f, 12f);
            }

            int texW = Mathf.Clamp(Mathf.RoundToInt(TexH * canvas.x / Mathf.Max(1f, canvas.y)), 64, MaxTexW);
            if (_tex == null || texW != _texW)
            {
                if (_tex != null)
                {
                    Object.Destroy(_tex);
                }

                _texW = texW;
                _tex = new Texture2D(texW, TexH, TextureFormat.RGBA32, false)
                {
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                    hideFlags = HideFlags.HideAndDontSave,
                };
                _px = new uint[texW * TexH];
                _colors = new Color32[texW * TexH];
                _image.texture = _tex;
                _paintedVersion = -1;
            }

            double sx = (self.X + 0.0) / Torus.SubTilesPerTile, sy = (self.Y + 0.0) / Torus.SubTilesPerTile;
            if (_follow)
            {
                _cx = sx;
                _cy = sy;
            }

            int version = explored?.Version ?? 0;
            bool moved = _cx != _paintedCx || _cy != _paintedCy || _zoom != _paintedZoom;
            if (moved || (version != _paintedVersion && Time.unscaledTime >= _nextPaint))
            {
                _nextPaint = Time.unscaledTime + 0.5f;
                _paintedVersion = version;
                _paintedZoom = _zoom;
                _paintedCx = _cx;
                _paintedCy = _cy;
                if (explored != null)
                {
                    explored.Paint(_cx, _cy, Scale, _texW, TexH, _px);
                }
                else
                {
                    System.Array.Fill(_px, MiniMap.Unknown);
                }

                for (int i = 0; i < _px.Length; i++)
                {
                    uint c = _px[i];
                    _colors[i] = new Color32((byte)c, (byte)(c >> 8), (byte)(c >> 16), 255);
                }

                _tex!.SetPixels32(_colors);
                _tex.Apply(false, false);
            }

            // Метки: точка мира → холст (по тору — ближайшая копия к центру показа).
            Place(_selfRing, shape, sx, sy, canvas);
            Place(_self, shape, sx, sy, canvas);
            bool hasHearth = hearth.HasValue;
            _hearth.enabled = _hearthHole.enabled = hasHearth;
            if (hearth.HasValue)
            {
                double hx = hearth.Value.X / (double)Torus.SubTilesPerTile, hy = hearth.Value.Y / (double)Torus.SubTilesPerTile;
                Place(_hearth, shape, hx, hy, canvas);
                Place(_hearthHole, shape, hx, hy, canvas);
            }

            int nb = 0;
            if (explored != null)
            {
                List<TileCoord> beds = explored.Beds();
                for (int i = 0; i < beds.Count && nb < MaxBeds; i++)
                {
                    _beds[nb].enabled = true;
                    Place(_beds[nb++], shape, beds[i].X + 0.5, beds[i].Y + 0.5, canvas);
                }
            }

            for (int i = nb; i < MaxBeds; i++)
            {
                _beds[i].enabled = false;
            }

            int no = 0;
            for (int i = 0; snap != null && i < snap.Count && no < MaxOthers; i++)
            {
                SnapshotEntity e = snap.Entities[i];
                if (e.Id == selfId || e.Kind != SimEntity.KindCharacter || e.HealthPct == 0)
                {
                    continue;
                }

                _others[no].enabled = true;
                Place(_others[no++], shape, e.Position.X / (double)Torus.SubTilesPerTile, e.Position.Y / (double)Torus.SubTilesPerTile, canvas);
            }

            for (int i = no; i < MaxOthers; i++)
            {
                _others[i].enabled = false;
            }

            _coords.text = UiStrings.WorldMapCoords((int)System.Math.Floor(sx), (int)System.Math.Floor(sy), Scale);
        }

        void Place(UiShape m, MapShape shape, double x, double y, Vector2 canvas)
        {
            double dx = x - _cx, dy = y - _cy;
            if (shape.Wraps)
            {
                dx -= System.Math.Round(dx / shape.Width) * shape.Width;
                dy -= System.Math.Round(dy / shape.Height) * shape.Height;
            }

            double unitsPerTile = canvas.x / (_texW * Scale);
            float px = (float)(canvas.x / 2 + dx * unitsPerTile), py = (float)(canvas.y / 2 - dy * unitsPerTile);
            Vector2 size = m.rectTransform.sizeDelta;
            m.rectTransform.anchoredPosition = new Vector2(px - size.x / 2f, py - size.y / 2f);
        }

        /// <summary>Пока открыта — весь экран интерфейс (мышь миру не отдаётся).</summary>
        public void AddZones(UiPointer p, Vector2 canvas)
        {
            if (Visible)
            {
                p.Add(0f, 0f, canvas.x, canvas.y);
            }
        }
    }
}
