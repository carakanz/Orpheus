#nullable enable
using System.Collections.Generic;
using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using UnityEngine;

namespace Orpheus.Client
{
    /// <summary>
    /// Предметы на земле (E2-1, шаг 2.1): куча — маленький значок предмета с пятном-тенью, слегка покачивается.
    /// Подобрана — значок за <see cref="FlySeconds"/> «прыгает» к поднявшему и исчезает; истекла или ушла из обзора —
    /// исчезает сразу. Только вид: что лежит и кто поднял, решает сервер (сообщения 27 и 28).
    /// <para>
    /// Значки — картинки <c>item_&lt;ключ предмета&gt;</c> из набора объектов (<see cref="ObjectSet"/>, заглушки
    /// <c>TEMP_item_*.png</c>); нет своей — общий мешочек <c>item_generic</c>; нет набора — цветной квадрат.
    /// </para>
    /// </summary>
    public sealed class PileView
    {
        /// <summary>Сколько секунд значок летит к поднявшему.</summary>
        public const float FlySeconds = 0.16f;

        /// <summary>Размах покачивания, тайлов, и его частота, рад/с.</summary>
        public const float BobTiles = 0.045f;

        /// <summary>Во сколько раз значок крупнее картинки (64 px = полтайла): 1,6 → 0,8 тайла — читается на обзоре 64.</summary>
        public const float IconScale = 1.6f;
        public const float BobSpeed = 3.2f;

        sealed class Pile
        {
            public GameObject Root = null!;
            public Transform Icon = null!;
            public SpriteRenderer Renderer = null!;
            public WorldPos Position;
            public float Phase;
            public byte Item;

            // Полёт к поднявшему.
            public bool Flying;
            public float FlyTime;
            public Vector3 FlyFrom;
            public Transform? Target;
        }

        readonly Transform _parent;
        readonly Dictionary<int, Pile> _piles = new Dictionary<int, Pile>();
        readonly List<Pile> _flying = new List<Pile>();
        static readonly Dictionary<byte, Sprite?> _sprites = new Dictionary<byte, Sprite?>();
        static Sprite? _square;
        float _time;

        public PileView(Transform parent)
        {
            _parent = parent;
        }

        /// <summary>Куч на экране (без улетающих).</summary>
        public int Count => _piles.Count;

        /// <summary>Куча появилась или изменила количество.</summary>
        public void Set(int id, byte item, int count, WorldPos position)
        {
            if (!_piles.TryGetValue(id, out Pile? p))
            {
                p = Create(id, item);
                _piles[id] = p;
            }
            else if (p.Item != item)
            {
                p.Item = item;
                p.Renderer.sprite = SpriteFor(item);
            }

            p.Position = position;
            float s = IconScale * (count >= 20 ? 1.25f : count >= 5 ? 1.1f : 1f); // большая куча чуть крупнее
            p.Icon.localScale = new Vector3(s, s, 1f);
        }

        /// <summary>Куча исчезла; <paramref name="taker"/> — к кому «прыгнуть» (подбор), null — просто убрать.</summary>
        public void Remove(int id, Transform? taker)
        {
            if (!_piles.TryGetValue(id, out Pile? p))
            {
                return;
            }

            _piles.Remove(id);
            if (taker == null)
            {
                Object.Destroy(p.Root);
                return;
            }

            p.Flying = true;
            p.FlyTime = 0f;
            p.FlyFrom = p.Root.transform.position;
            p.Target = taker;
            _flying.Add(p);
        }

        /// <summary>Убрать всё (новый вход в мир).</summary>
        public void Clear()
        {
            foreach (Pile p in _piles.Values)
            {
                Object.Destroy(p.Root);
            }

            foreach (Pile p in _flying)
            {
                Object.Destroy(p.Root);
            }

            _piles.Clear();
            _flying.Clear();
        }

        /// <summary>Кадр: места от плавающего начала, покачивание, порядок отрисовки по нижней кромке, полёт подобранных.</summary>
        public void Frame(FloatingOrigin origin, float dt)
        {
            _time += dt;
            foreach (Pile p in _piles.Values)
            {
                Vector3 at = origin.ToUnity(p.Position);
                p.Root.transform.position = at;
                float bob = Mathf.Sin(_time * BobSpeed + p.Phase) * BobTiles;
                p.Icon.localPosition = new Vector3(0f, -0.2f + BobTiles + bob, 0f);
                p.Renderer.sortingOrder = WorldView.SortOrderAt(at.y - 0.12f);
            }

            for (int i = _flying.Count - 1; i >= 0; i--)
            {
                Pile p = _flying[i];
                p.FlyTime += dt;
                float k = Mathf.Clamp01(p.FlyTime / FlySeconds);
                if (k >= 1f || p.Target == null)
                {
                    Object.Destroy(p.Root);
                    _flying.RemoveAt(i);
                    continue;
                }

                Vector3 to = p.Target.position;
                Vector3 at = Vector3.Lerp(p.FlyFrom, to, k * k);
                at.y += Mathf.Sin(k * Mathf.PI) * 0.35f; // дуга прыжка
                p.Root.transform.position = at;
                float s = IconScale * (1f - 0.6f * k);
                p.Icon.localScale = new Vector3(s, s, 1f);
                p.Renderer.sortingOrder = WorldView.SortOrderAt(at.y - 0.12f) + 1;
            }
        }

        Pile Create(int id, byte item)
        {
            var root = new GameObject($"Pile {id}");
            root.transform.SetParent(_parent, false);

            // Пятно-тень: помощник ставит его на 0,4 тайла ниже корня (ноги существа) — поднимаем якорь, чтобы легло под значок.
            var shadowAnchor = new GameObject("ShadowAnchor");
            shadowAnchor.transform.SetParent(root.transform, false);
            shadowAnchor.transform.localPosition = new Vector3(-0.1f, WorldFx.FootOffset - 0.1f, 0f);
            WorldFx.AddActorShadow(shadowAnchor.transform, 0.75f);

            var icon = new GameObject("Icon");
            icon.transform.SetParent(root.transform, false);
            var sr = icon.AddComponent<SpriteRenderer>();
            sr.sprite = SpriteFor(item);
            return new Pile
            {
                Root = root,
                Icon = icon.transform,
                Renderer = sr,
                Item = item,
                Phase = (id * 0.6180339f % 1f) * Mathf.PI * 2f, // соседние кучи качаются не в такт
            };
        }

        /// <summary>Значок предмета: своя картинка, общий мешочек или цветной квадрат.</summary>
        public static Sprite? SpriteFor(byte item)
        {
            if (_sprites.TryGetValue(item, out Sprite? cached))
            {
                return cached;
            }

            Sprite? found = Find("item_" + ItemTable.Get(item).Key) ?? Find("item_generic") ?? Square();
            found = found != null ? Unmarked(found) : null;
            _sprites[item] = found;
            return found;
        }

        /// <summary>
        /// Значок без метки заглушки (E2-4): пурпурный уголок 8 × 8 px остаётся в файле PNG (правило заглушек), но в
        /// ячейках интерфейса и у куч на земле не рисуется — берётся копия картинки с прозрачным левым верхним углом.
        /// У готовой картинки (без уголка) и без видеокарты значок остаётся как есть.
        /// </summary>
        static Sprite Unmarked(Sprite s)
        {
            const int mark = 8;
            try
            {
                Texture2D t = s.texture;
                if (t == null || !t.isReadable || t.width < 2 * mark || t.height < 2 * mark
                    || SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
                {
                    return s;
                }

                Color32[] px = t.GetPixels32(0);
                int w = t.width, h = t.height;
                Color32 c = px[(h - 1) * w]; // левый верхний угол (строки текстуры идут снизу вверх)
                if (!(c.a > 200 && c.r > 200 && c.b > 200 && c.g < 80))
                {
                    return s;
                }

                for (int y = h - mark; y < h; y++)
                {
                    for (int x = 0; x < mark; x++)
                    {
                        px[y * w + x] = new Color32(0, 0, 0, 0);
                    }
                }

                var copy = new Texture2D(w, h, TextureFormat.RGBA32, false)
                {
                    name = t.name + " (без уголка)",
                    filterMode = t.filterMode,
                    wrapMode = TextureWrapMode.Clamp,
                };
                copy.SetPixels32(px);
                copy.Apply(false, true);
                return Sprite.Create(copy, new Rect(0, 0, w, h), new Vector2(s.pivot.x / s.rect.width, s.pivot.y / s.rect.height),
                    s.pixelsPerUnit, 0, SpriteMeshType.FullRect);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[piles] значок без уголка не собран: " + e.Message);
                return s;
            }
        }

        static Sprite? Find(string name)
        {
            ObjectAtlas.Ensure();
            ObjectSet? set = ObjectAtlas.Set;
            if (set == null)
            {
                return null;
            }

            int n = Mathf.Min(set.Names.Length, set.Sprites.Length);
            for (int i = 0; i < n; i++)
            {
                if (set.Names[i] == name)
                {
                    return set.Sprites[i];
                }
            }

            return null;
        }

        static Sprite Square()
        {
            if (_square == null)
            {
                var tex = new Texture2D(8, 8, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
                var px = new Color32[64];
                for (int i = 0; i < px.Length; i++)
                {
                    px[i] = new Color32(214, 170, 92, 255);
                }

                tex.SetPixels32(px);
                tex.Apply();
                _square = Sprite.Create(tex, new Rect(0, 0, 8, 8), new Vector2(0.5f, 0f), 24f);
            }

            return _square;
        }

        /// <summary>
        /// Показ без сервера (<c>--demo-piles</c>, сцена снимков E2-1): кучи всех значков вокруг точки —
        /// на проходимых тайлах, с тем же разбросом внутри тайла, что даёт ядро.
        /// </summary>
        public void AddDemo(MapShape map, WorldPos around, System.Func<TileCoord, bool> blocked)
        {
            (byte Item, int Count, int Dx, int Dy)[] demo =
            {
                (Items.Wood, 3, 2, 0), (Items.Wood, 12, 3, 1), (Items.Stone, 2, -2, 1), (Items.Stone, 30, -3, -1),
                (Items.CopperOre, 3, 1, 2), (Items.Fiber, 1, -1, -2), (Items.Resin, 1, 2, -2), (Items.Arrows, 20, 4, -1),
                (Items.Torch, 2, -4, 1), (Items.StoneAxe, 1, 0, 3), (Items.Wood, 3, 5, 2), (Items.Stone, 1, -5, 0),
            };
            TileCoord center = Torus.TileOf(map, around);
            int id = 1;
            foreach ((byte item, int count, int dx, int dy) in demo)
            {
                TileCoord t = Torus.Wrap(map, new TileCoord(center.X + dx, center.Y + dy));
                if (blocked(t))
                {
                    continue;
                }

                int ox = (id * 37 % 97) - 48, oy = (id * 61 % 97) - 48;
                WorldPos c = Torus.CornerOf(t);
                Set(id++, item, count, new WorldPos(c.X + Torus.SubTilesPerTile / 2 + ox, c.Y + Torus.SubTilesPerTile / 2 + oy));
            }
        }
    }
}
