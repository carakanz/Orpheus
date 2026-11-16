#nullable enable
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
    /// Мини-карта в правом верхнем углу (C-008 п. 8, слово Автора 15.11: «А справа вверху мини-карту»): квадрат 220 единиц
    /// в полупрозрачной рамке, мир вокруг персонажа из чанков клиента (<see cref="MiniMap.Paint"/> — цвета таблицей, тор,
    /// север вверху), персонаж — золотая точка в центре, другие игроки — голубые точки, враги — красные (только те, что
    /// пришли в снимке, то есть в пределах видимости). Картинка перерисовывается не каждый кадр: по 32 строки за кадр,
    /// полный проход — раз в 0,3 с. M — свернуть / развернуть; колесо над картой — масштаб (радиус 48 / 80 / 128 тайлов).
    /// </summary>
    public sealed class MiniMapView : UiGroup
    {
        public const float Side = HudLayout.MapSide, Border = HudLayout.MapBorder;
        public const float Size = HudLayout.MapSize;
        const int Pixels = 128;
        const int RowsPerFrame = 32;
        const float RefreshSeconds = 0.3f;
        const int MaxMarks = 32;

        /// <summary>Радиусы масштаба, тайлов; по умолчанию — средний.</summary>
        public static readonly int[] Radii = { 48, 80, 128 };

        readonly Texture2D _tex;
        readonly Color32[] _colors = new Color32[Pixels * Pixels];
        readonly RectTransform _body;
        readonly Text _collapsed;
        readonly UiShape[] _marks = new UiShape[MaxMarks];
        int _zoom = 1;
        int _row = Pixels;
        float _next;
        bool _open = true;
        TileCoord _center;

        public MiniMapView(UiRoot root)
            : base(root, "MiniMap", new Vector2(1f, 1f), new Vector2(-HudLayout.MapMargin - Size, -HudLayout.MapMargin - Size), new Vector2(Size, Size))
        {
            _body = UiRoot.Node(Rt, "Body", 0f, 0f, Size, Size);
            UiRoot.Picture(_body, "Frame", 0f, 0f, Size, Size, UiStyle.Frame(UiStyle.GlassPanel, UiStyle.Gold, thick: 3f, radius: 8f));
            _tex = new Texture2D(Pixels, Pixels, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
            };
            var map = UiRoot.Node(_body, "Map", Border, Border, Side, Side).gameObject.AddComponent<RawImage>();
            map.texture = _tex;
            map.raycastTarget = false;
            map.color = new Color(1f, 1f, 1f, 0.88f);

            // C-015: точки — круги шейдером (UiShape), край в 1 пиксель на любом разрешении.
            for (int i = 0; i < _marks.Length; i++)
            {
                _marks[i] = UiShape.Create(_body, "Mark", 0f, 0f, 9f, 9f, UiShape.Shape.Disc, new Color32(255, 255, 255, 255));
                _marks[i].enabled = false;
            }

            // Персонаж — золотая точка с тёмной каймой в центре (поверх остальных).
            UiShape.Create(_body, "SelfRing", Size / 2f - 8f, Size / 2f - 8f, 16f, 16f, UiShape.Shape.Disc, UiStyle.Ink);
            UiShape.Create(_body, "Self", Size / 2f - 6f, Size / 2f - 6f, 12f, 12f, UiShape.Shape.Disc, UiStyle.Gold);
            UiRoot.Label(_body, "North", Size / 2f - 10f, Size - 26f, 20f, 22f, 18, TextAnchor.MiddleCenter, UiStyle.TextLight, outline: true, bold: true).text = "С";

            _collapsed = UiRoot.Label(Rt, "Collapsed", 0f, Size - 26f, Size - 4f, 24f, 18, TextAnchor.MiddleRight, UiStyle.TextLight, outline: true, bold: true);
            _collapsed.text = UiStrings.MapCollapsed;
            _collapsed.enabled = false;
        }

        /// <summary>Карта развёрнута.</summary>
        public bool Open => _open;

        /// <summary>Текущий радиус, тайлов.</summary>
        public int Radius => Radii[_zoom];

        /// <summary>M — свернуть / развернуть.</summary>
        public void Toggle()
        {
            _open = !_open;
            _body.gameObject.SetActive(_open);
            _collapsed.enabled = !_open;
            _row = Pixels; // развернули — перерисовать сразу
            _next = 0f;
        }

        /// <summary>Колесо над картой: вверх — ближе (меньше радиус).</summary>
        public void Zoom(float wheel)
        {
            int z = Mathf.Clamp(_zoom + (wheel > 0f ? -1 : 1), 0, Radii.Length - 1);
            if (z != _zoom)
            {
                _zoom = z;
                _row = Pixels;
                _next = 0f;
            }
        }

        /// <summary>C-008: развёрнутая карта — интерфейс (щелчок по ней не копает); свёрнутая — только подпись.</summary>
        public void AddZones(UiPointer p, Vector2 canvas)
        {
            if (_open && Visible)
            {
                Vector2 o = Origin(canvas);
                HudLayout.MapZones(p, o.x, o.y);
            }
        }

        /// <summary>Курсор над картой (колесо — масштаб, не обзор).</summary>
        public bool Covers(Vector2 canvas, Vector2 point) => _open && Contains(canvas, point);

        /// <summary>
        /// Кадр карты: мир клиента, место персонажа, свежий снимок (точки игроков и врагов) и свой номер. Без мира —
        /// карта тёмная.
        /// </summary>
        public void Frame(WorldModel? world, WorldPos self, Snapshot? snap, int selfId)
        {
            if (!_open)
            {
                return;
            }

            if (world != null)
            {
                Repaint(world, self);
            }

            int used = 0;
            if (world != null && snap != null)
            {
                for (int i = 0; i < snap.Count && used < MaxMarks; i++)
                {
                    SnapshotEntity e = snap.Entities[i];
                    bool enemy = e.Kind == SimEntity.KindEnemy;
                    if (e.Id == selfId || (!enemy && e.Kind != SimEntity.KindCharacter) || e.HealthPct == 0
                        || !MiniMap.Marker(world.Shape, self, e.Position, Radius, out float x, out float y))
                    {
                        continue;
                    }

                    UiShape m = _marks[used++];
                    m.enabled = true;
                    m.color = enemy ? new Color32(255, 60, 50, 255) : new Color32(90, 200, 255, 255);
                    m.rectTransform.anchoredPosition = new Vector2(Border + (x + 1f) * Side / 2f - 4.5f, Border + (y + 1f) * Side / 2f - 4.5f);
                }
            }

            for (int i = used; i < _marks.Length; i++)
            {
                if (_marks[i].enabled)
                {
                    _marks[i].enabled = false;
                }
            }
        }

        // Перерисовка по частям: новый проход — раз в 0,3 с (центр берётся в начале прохода), по 32 строки за кадр.
        void Repaint(WorldModel world, WorldPos self)
        {
            if (_row >= Pixels)
            {
                if (Time.unscaledTime < _next)
                {
                    return;
                }

                _next = Time.unscaledTime + RefreshSeconds;
                _center = Torus.TileOf(world.Shape, self);
                _row = 0;
            }

            int radius = Radius, end = Mathf.Min(Pixels, _row + RowsPerFrame);
            for (int row = _row; row < end; row++)
            {
                int dy = MiniMap.TileOffset(Pixels - 1 - row, radius, Pixels);
                for (int col = 0; col < Pixels; col++)
                {
                    uint c = MiniMap.ColorOf(world, new TileCoord(_center.X + MiniMap.TileOffset(col, radius, Pixels), _center.Y + dy));
                    _colors[row * Pixels + col] = new Color32((byte)c, (byte)(c >> 8), (byte)(c >> 16), (byte)(c >> 24));
                }
            }

            _row = end;
            if (_row >= Pixels)
            {
                _tex.SetPixels32(_colors);
                _tex.Apply(false, false);
            }
        }
    }
}
