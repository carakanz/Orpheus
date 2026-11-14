#nullable enable
using Orpheus.Core.Geometry;
using Orpheus.Core.World;
using UnityEngine;

namespace Orpheus.Client
{
    /// <summary>Строка таблицы «слой земли → пучки травы» (E1-11, план 1.12).</summary>
    public sealed class TuftRule
    {
        /// <summary>Слой земли (<see cref="GroundTable"/>).</summary>
        public byte Ground;

        /// <summary>Пучков на тайл: от и до (по хэшу тайла); <see cref="Chance"/> — доля тайлов, где пучки вообще есть.</summary>
        public int Min, Max;
        public float Chance = 1f;

        /// <summary>Гуще и реже пятнами: на столько меняется число пучков по крупному шуму (0 — ровно).</summary>
        public float Clump;

        /// <summary>Формы из листа пучков: первая и число.</summary>
        public int Shape, Shapes = 1;

        /// <summary>Размер пучка, тайлов.</summary>
        public float SizeMin = 0.3f, SizeMax = 0.6f;

        /// <summary>Цвет (умножение на светлую картинку) и разброс тона ± по хэшу.</summary>
        public Color32 Tint = new Color32(255, 255, 255, 255);
        public float Tone = 0.1f;
    }

    /// <summary>
    /// Пучки травы отдельным слоем (E1-11, план 1.12): сразу над землёй, под тенями, объектами и существами — персонаж
    /// идёт «по» траве. На чанк — <see cref="Bands"/> мешей по <see cref="BandRows"/> строк тайлов (правка тайла
    /// пересобирает одну полосу), один материал <c>Orpheus/Grass</c>: пучки качаются общим ветром и ложатся по следу
    /// существ (<see cref="LifeFx"/>) в вершинном шейдере. Где что растёт — таблица <see cref="Rules"/>; место, форма,
    /// размер и тон пучка — по хэшу координаты тайла мира (одинаково у всех игроков). Картинок пучков в наборе арта нет:
    /// лист рисуется кодом при запуске (клинья-травинки с тёмным контуром, два тона) — <see cref="BuildAtlas"/>.
    /// На тайле с объектом, постройкой или построенным полом пучков нет.
    /// </summary>
    public sealed class GrassLayer
    {
        const int Size = Torus.ChunkSize;
        public const int BandRows = 8;
        public const int Bands = Size / BandRows;

        /// <summary>Порядок отрисовки: над землёй (−10) и построенным полом (0), под слоем теней (3).</summary>
        public const int Order = 1;

        /// <summary>Формы листа: 0–3 — пучки травы, 4 — папоротник, 5 — камыш, 6 — редкая метёлка, 7 — лист (частицы <see cref="LeafFx"/>).</summary>
        public const int ShapeFern = 4, ShapeReed = 5, ShapeSeed = 6, ShapeLeaf = 7, ShapeCount = 8;

        const int MaxPerTile = 4;
        const int MaxQuads = BandRows * Size * MaxPerTile;

        public static readonly TuftRule[] Rules =
        {
            new TuftRule { Ground = GroundTable.Grass, Min = 2, Max = 4, Clump = 0.9f, Shape = 0, Shapes = 4, SizeMin = 0.3f, SizeMax = 0.62f, Tint = new Color32(128, 176, 66, 255) },
            new TuftRule { Ground = GroundTable.DryGrass, Min = 1, Max = 2, Chance = 0.7f, Clump = 0.5f, Shape = 0, Shapes = 4, SizeMin = 0.25f, SizeMax = 0.5f, Tint = new Color32(204, 172, 78, 255) },
            new TuftRule { Ground = GroundTable.Forest, Min = 1, Max = 1, Chance = 0.22f, Shape = ShapeFern, SizeMin = 0.4f, SizeMax = 0.62f, Tint = new Color32(96, 126, 56, 255) },
        };

        /// <summary>Камыш у воды: на суше, соседней с водой (по 4 сторонам).</summary>
        public static readonly TuftRule Reed = new TuftRule
        {
            Min = 1, Max = 2, Chance = 0.55f, Shape = ShapeReed, SizeMin = 0.7f, SizeMax = 0.95f, Tint = new Color32(150, 162, 84, 255), Tone = 0.08f,
        };

        static readonly TuftRule?[] ByLayer = BuildByLayer();

        static TuftRule?[] BuildByLayer()
        {
            var t = new TuftRule?[16];
            foreach (TuftRule r in Rules)
            {
                t[r.Ground] = r;
            }

            return t;
        }

        // ---------- Узел чанка ----------

        public sealed class Node
        {
            public readonly Mesh[] Meshes = new Mesh[Bands];
            public readonly MeshRenderer[] Renderers = new MeshRenderer[Bands];

            /// <summary>Пучков в полосе сейчас.</summary>
            public readonly int[] Counts = new int[Bands];

            /// <summary>Полосы, которым нужна пересборка (биты); правка тайла — пересобрать до отрисовки этого же кадра.</summary>
            public int Dirty;

            /// <summary>Чанк только что вошёл в окно: полосы собираются по нескольку за кадр (вход — далеко за краем экрана).</summary>
            public bool Fresh;
        }

        public Node CreateNode(Transform parent)
        {
            var node = new Node();
            for (int b = 0; b < Bands; b++)
            {
                var mesh = new Mesh { name = "Grass" };
                mesh.MarkDynamic();
                var go = new GameObject("Grass " + b);
                go.transform.SetParent(parent, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = Material;
                mr.sortingOrder = Order;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
                mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
                mr.enabled = false;
                node.Meshes[b] = mesh;
                node.Renderers[b] = mr;
            }

            return node;
        }

        /// <summary>Чанк вошёл в окно (узел новый или из пула): старое убрать с экрана, сборку — в очередь.</summary>
        public static void MarkFresh(Node node)
        {
            node.Fresh = true;
            node.Dirty = (1 << Bands) - 1;
            for (int b = 0; b < Bands; b++)
            {
                node.Renderers[b].enabled = false;
            }
        }

        /// <summary>Сменился пол или объект в строке ly: её полоса (и соседняя — камыш смотрит на тайл рядом).</summary>
        public static void MarkRow(Node node, int ly)
        {
            node.Dirty |= 1 << (ly / BandRows);
            if (ly % BandRows == 0 && ly > 0) node.Dirty |= 1 << ((ly - 1) / BandRows);
            if (ly % BandRows == BandRows - 1 && ly < Size - 1) node.Dirty |= 1 << ((ly + 1) / BandRows);
        }

        // ---------- Сборка ----------

        readonly WorldModel _world;
        readonly MapShape _map;
        readonly Vector3[] _pos = new Vector3[MaxQuads * 4];
        readonly Vector2[] _uv = new Vector2[MaxQuads * 4];
        readonly Vector4[] _info = new Vector4[MaxQuads * 4];
        readonly Color32[] _color = new Color32[MaxQuads * 4];
        static readonly ushort[] Indices = BuildIndices();

        /// <summary>Полос собрано за всё время и пучков в последней собранной.</summary>
        public int Builds { get; private set; }

        public GrassLayer(WorldModel world)
        {
            _world = world;
            _map = world.Shape;
        }

        static ushort[] BuildIndices()
        {
            var ix = new ushort[MaxQuads * 6];
            for (int q = 0; q < MaxQuads; q++)
            {
                int v = q * 4, i = q * 6;
                ix[i] = (ushort)v;
                ix[i + 1] = (ushort)(v + 2);
                ix[i + 2] = (ushort)(v + 1);
                ix[i + 3] = (ushort)(v + 1);
                ix[i + 4] = (ushort)(v + 2);
                ix[i + 5] = (ushort)(v + 3);
            }

            return ix;
        }

        bool WaterAt(Chunk chunk, TileCoord corner, int lx, int ly)
        {
            if ((uint)lx < Size && (uint)ly < Size)
            {
                return GroundTable.Of(chunk.GetFloor(Chunk.IndexOf(lx, ly))).Kind == GroundKind.Water;
            }

            TileCoord t = Torus.Wrap(_map, new TileCoord(corner.X + lx, corner.Y + ly));
            Chunk? near = _world.FindChunk(Torus.ChunkOf(_map, t));
            return near != null && GroundTable.Of(near.GetFloor(Chunk.IndexOf(t.X & (Size - 1), t.Y & (Size - 1)))).Kind == GroundKind.Water;
        }

        /// <summary>Собрать полосу band чанка; возвращает число пучков. Выставляет видимость отрисовщика.</summary>
        public int Build(Node node, Chunk? chunk, ChunkCoord c, int band)
        {
            Builds++;
            Mesh mesh = node.Meshes[band];
            int quads = 0;
            if (chunk != null)
            {
                TileCoord corner = Torus.CornerOf(c);
                int y0 = band * BandRows;
                for (int ly = y0; ly < y0 + BandRows; ly++)
                {
                    for (int lx = 0; lx < Size; lx++)
                    {
                        int index = Chunk.IndexOf(lx, ly);
                        if (chunk.GetObject(index) != ObjectId.Empty)
                        {
                            continue; // под объектом и постройкой травы нет
                        }

                        GroundRow row = GroundTable.Of(chunk.GetFloor(index));
                        if (row.Kind != GroundKind.Ground)
                        {
                            continue; // вода, построенный пол
                        }

                        int tx = corner.X + lx, ty = corner.Y + ly;
                        TuftRule? rule = ByLayer[row.Layer];
                        if (rule != null)
                        {
                            quads = Tile(rule, tx, ty, lx, ly, quads, 0);
                        }

                        if (row.Layer != GroundTable.Rocky
                            && (WaterAt(chunk, corner, lx + 1, ly) || WaterAt(chunk, corner, lx - 1, ly)
                                || WaterAt(chunk, corner, lx, ly + 1) || WaterAt(chunk, corner, lx, ly - 1)))
                        {
                            quads = Tile(Reed, tx, ty, lx, ly, quads, 40);
                        }
                    }
                }
            }

            mesh.Clear(keepVertexLayout: true);
            if (quads > 0)
            {
                int n = quads * 4;
                mesh.SetVertices(_pos, 0, n);
                mesh.SetUVs(0, _uv, 0, n);
                mesh.SetUVs(1, _info, 0, n);
                mesh.SetColors(_color, 0, n);
                mesh.SetIndices(Indices, 0, quads * 6, MeshTopology.Triangles, 0, calculateBounds: false);
                // Запас по краям: пучок ложится и качается за границу своей полосы.
                mesh.bounds = new Bounds(new Vector3(Size * 0.5f, -(band + 0.5f) * BandRows, 0f), new Vector3(Size + 3f, BandRows + 3f, 1f));
            }

            node.Renderers[band].enabled = quads > 0 && Material != null && !WorldFx.Off("nograss");
            return quads;
        }

        // Пучки одного тайла по правилу; salt разводит хэши правил (трава и камыш на одном тайле).
        int Tile(TuftRule rule, int tx, int ty, int lx, int ly, int quads, uint salt)
        {
            if (rule.Chance < 1f && GroundAtlas.Hash(tx, ty, 301 + salt) >= rule.Chance)
            {
                return quads;
            }

            float pick = GroundAtlas.Hash(tx, ty, 307 + salt);
            if (rule.Clump > 0f)
            {
                // Гуще и реже пятнами ~6 тайлов.
                pick = Mathf.Clamp01(pick + (ObjectTable.Smooth(tx / 6f, ty / 6f, 311) - 0.5f) * rule.Clump * 2f);
            }

            int n = Mathf.Min(rule.Min + (int)(pick * (rule.Max - rule.Min + 0.999f)), MaxPerTile);
            // Пучки тайла — от дальнего к ближнему: ближний перекрывает.
            float slot = 1f / n;
            for (int k = 0; k < n && quads < MaxQuads; k++)
            {
                uint s = salt + (uint)k * 16u;
                float size = Mathf.Lerp(rule.SizeMin, rule.SizeMax, GroundAtlas.Hash(tx, ty, 313 + s));
                float fx = 0.08f + 0.84f * GroundAtlas.Hash(tx, ty, 317 + s);
                float fy = (k + 0.1f + 0.8f * GroundAtlas.Hash(tx, ty, 331 + s)) * slot; // от верхнего края тайла вниз
                int shape = rule.Shape + (int)(GroundAtlas.Hash(tx, ty, 337 + s) * rule.Shapes);
                if (rule.Shapes > 1 && GroundAtlas.Hash(tx, ty, 359 + s) < 0.06f)
                {
                    shape = ShapeSeed; // изредка — метёлка
                }

                bool flip = GroundAtlas.Hash(tx, ty, 347 + s) < 0.5f;
                float tone = 1f + (GroundAtlas.Hash(tx, ty, 349 + s) * 2f - 1f) * rule.Tone;
                float warm = (GroundAtlas.Hash(tx, ty, 353 + s) * 2f - 1f) * rule.Tone * 0.6f; // чуть желтее / зеленее
                var color = new Color32(
                    (byte)Mathf.Clamp(rule.Tint.r * tone * (1f + warm), 0f, 255f),
                    (byte)Mathf.Clamp(rule.Tint.g * tone, 0f, 255f),
                    (byte)Mathf.Clamp(rule.Tint.b * tone * (1f - warm), 0f, 255f), 255);

                float bx = lx + fx, by = -ly - fy; // опора: низ пучка по центру
                float x0 = bx - size * 0.5f, x1 = bx + size * 0.5f, y1 = by + size;
                float u0 = (shape + (flip ? 1f : 0f)) / ShapeCount, u1 = (shape + (flip ? 0f : 1f)) / ShapeCount;
                var info = new Vector4(bx, by, GroundAtlas.Hash(tx, ty, 367 + s), 1f);
                int v = quads * 4;
                _pos[v] = new Vector3(x0, by);
                _pos[v + 1] = new Vector3(x1, by);
                _pos[v + 2] = new Vector3(x0, y1);
                _pos[v + 3] = new Vector3(x1, y1);
                _uv[v] = new Vector2(u0, 0f);
                _uv[v + 1] = new Vector2(u1, 0f);
                _uv[v + 2] = new Vector2(u0, 1f);
                _uv[v + 3] = new Vector2(u1, 1f);
                _info[v] = info;
                _info[v + 1] = info;
                _info[v + 2] = info;
                _info[v + 3] = info;
                _color[v] = color;
                _color[v + 1] = color;
                _color[v + 2] = color;
                _color[v + 3] = color;
                quads++;
            }

            return quads;
        }

        // ---------- Лист пучков и материал ----------

        const int Cell = 128;
        const float K = Cell / 64f; // формы заданы в клетке 64 — множитель
        static Material? _material;
        static bool _tried;

        /// <summary>Материал пучков (и листьев <see cref="LeafFx"/>); null — нет видеокарты или шейдера.</summary>
        public static Material? Material
        {
            get
            {
                if (!_tried)
                {
                    _tried = true;
                    try
                    {
                        _material = BuildMaterial();
                    }
                    catch (System.Exception e)
                    {
                        Debug.LogError("[grass] материал травы не собран: " + e);
                    }
                }

                return _material;
            }
        }

        static Material? BuildMaterial()
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
            {
                return null;
            }

            Shader? shader = Resources.Load<Shader>("Grass");
            if (shader == null)
            {
                Debug.LogError("[grass] нет Resources/Grass.shader — трава не рисуется");
                return null;
            }

            var mat = new Material(shader) { name = "Grass" };
            mat.SetTexture("_MainTex", BuildAtlas());
            Debug.Log($"[grass] материал: шейдер {shader.name} (поддержан: {shader.isSupported}), лист {Cell * ShapeCount}x{Cell}, форм {ShapeCount}");
            return mat;
        }

        // Светлый тон, тень и контур: картинка почти белая — цвет даёт вершина (контур остаётся тёмным).
        static readonly Color Light = new Color(1f, 1f, 1f), Dark = new Color(0.70f, 0.78f, 0.66f), Line = new Color(0.17f, 0.16f, 0.15f);

        /// <summary>
        /// Лист 8 форм по 64 × 64: травинки — изогнутые клинья от общей опоры (низ по центру клетки), светлая и теневая
        /// половины, тёмный контур ~2 точки. Поля клетки прозрачные — формы не затекают друг в друга на mip-уровнях.
        /// </summary>
        public static Texture2D BuildAtlas()
        {
            int w = Cell * ShapeCount;
            var px = new Color[w * Cell];
            var rnd = new System.Random(20261114);
            float R(float a, float b) => a + (float)rnd.NextDouble() * (b - a);

            for (int shape = 0; shape < 4; shape++)
            {
                int blades = 3 + shape % 3 + (shape == 3 ? 2 : 0); // 3, 4, 5, 5
                for (int b = 0; b < blades; b++)
                {
                    float side = blades == 1 ? 0f : (b / (float)(blades - 1)) * 2f - 1f; // −1…1
                    float lean = side * R(14f, 22f) + R(-3f, 3f);
                    float height = R(34f, 50f) * (1f - 0.25f * Mathf.Abs(side));
                    Blade(px, w, shape, 32f + side * 5f, 5f, 32f + lean, 5f + height, side * R(3f, 8f), R(4.2f, 5.6f));
                }
            }

            // Папоротник: низкие широкие дуги в стороны.
            for (int b = 0; b < 6; b++)
            {
                float side = (b / 5f) * 2f - 1f;
                Blade(px, w, ShapeFern, 32f + side * 3f, 5f, 32f + side * R(22f, 27f), 5f + R(20f, 40f) * (1f - 0.45f * Mathf.Abs(side)),
                    side * R(9f, 14f), R(6.5f, 8f));
            }

            // Камыш: три тонких высоких стебля с тёмными початками.
            for (int b = 0; b < 3; b++)
            {
                float side = b - 1f;
                float tipX = 32f + side * R(7f, 11f), tipY = 5f + R(44f, 54f);
                Blade(px, w, ShapeReed, 32f + side * 3f, 5f, tipX, tipY, side * 2f, 3.4f);
                Dot(px, w, ShapeReed, tipX - side * 0.6f, tipY - 7f, 3.1f, 6.5f, new Color(0.42f, 0.30f, 0.22f));
            }

            // Метёлка: пучок пониже и два стебля с колоском.
            for (int b = 0; b < 3; b++)
            {
                float side = b - 1f;
                Blade(px, w, ShapeSeed, 32f + side * 4f, 5f, 32f + side * R(12f, 18f), 5f + R(24f, 32f), side * 5f, 4.6f);
            }

            for (int b = 0; b < 2; b++)
            {
                float side = b * 2f - 1f;
                float tipX = 32f + side * R(4f, 9f), tipY = 5f + R(46f, 54f);
                Blade(px, w, ShapeSeed, 32f, 5f, tipX, tipY, side * 3f, 2.8f);
                Dot(px, w, ShapeSeed, tipX, tipY - 4f, 2.6f, 5f, new Color(1f, 0.93f, 0.62f));
            }

            // Лист (частица): овал с контуром, светлая и теневая половины.
            Dot(px, w, ShapeLeaf, 32f, 32f, 18f, 27f, Color.white);

            var tex = new Texture2D(w, Cell, TextureFormat.RGBA32, mipChain: true)
            {
                name = "GrassAtlas",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 2,
            };
            tex.SetPixels(px);
            tex.Apply(updateMipmaps: true, makeNoLongerReadable: true);
            return tex;
        }

        static void Over(Color[] px, int at, Color c, float a)
        {
            if (a <= 0f)
            {
                return;
            }

            Color d = px[at];
            float outA = a + d.a * (1f - a);
            px[at] = new Color((c.r * a + d.r * d.a * (1f - a)) / outA, (c.g * a + d.g * d.a * (1f - a)) / outA, (c.b * a + d.b * d.a * (1f - a)) / outA, outA);
        }

        // Травинка: клин вдоль дуги от (x0, y0) к (x1, y1), прогиб bend в сторону, ширина у опоры width.
        static void Blade(Color[] px, int texW, int cell, float x0, float y0, float x1, float y1, float bend, float width)
        {
            const int segs = 8;
            const float outline = 1.5f * K;
            x0 *= K; y0 *= K; x1 *= K; y1 *= K; bend *= K; width *= K;
            var pts = new Vector2[segs + 1];
            Vector2 a = new Vector2(x0, y0), b = new Vector2(x1, y1);
            Vector2 n = new Vector2(-(b.y - a.y), b.x - a.x).normalized;
            for (int i = 0; i <= segs; i++)
            {
                float t = i / (float)segs;
                // Прогиб растёт к вершине: травинка стоит у земли и клонится наверху.
                pts[i] = Vector2.Lerp(a, b, t) + n * (bend * t * t) - n * (bend * t);
            }

            // Рамка травинки — считаем только точки рядом с ней.
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (Vector2 q in pts)
            {
                minX = Mathf.Min(minX, q.x);
                maxX = Mathf.Max(maxX, q.x);
                minY = Mathf.Min(minY, q.y);
                maxY = Mathf.Max(maxY, q.y);
            }

            float pad = width * 0.5f + outline + 1f;
            int fromX = Mathf.Max(2, (int)(minX - pad)), toX = Mathf.Min(Cell - 2, (int)(maxX + pad) + 1);
            int fromY = Mathf.Max(2, (int)(minY - pad)), toY = Mathf.Min(Cell - 2, (int)(maxY + pad) + 1);
            for (int y = fromY; y < toY; y++)
            {
                for (int x = fromX; x < toX; x++)
                {
                    var p = new Vector2(x + 0.5f, y + 0.5f);
                    float best = float.MaxValue, bestT = 0f, bestSide = 0f;
                    for (int i = 0; i < segs; i++)
                    {
                        Vector2 s = pts[i], e = pts[i + 1];
                        Vector2 se = e - s;
                        float u = Mathf.Clamp01(Vector2.Dot(p - s, se) / se.sqrMagnitude);
                        Vector2 q = s + se * u;
                        float d = (p - q).magnitude;
                        if (d < best)
                        {
                            best = d;
                            bestT = (i + u) / segs;
                            bestSide = se.x * (p.y - s.y) - se.y * (p.x - s.x);
                        }
                    }

                    float half = width * 0.5f * (1f - bestT * bestT * 0.92f); // клин: к вершине сходит почти на нет
                    float cover = Mathf.Clamp01(half + outline + 0.5f - best);
                    if (cover <= 0f)
                    {
                        continue;
                    }

                    int at = y * texW + cell * Cell + x;
                    Over(px, at, Line, cover);
                    float fill = Mathf.Clamp01(half + 0.5f - best);
                    Over(px, at, bestSide > 0f ? Light : Dark, fill);
                }
            }
        }

        // Овал (початок камыша, колосок, лист): контур и заливка в два тона.
        static void Dot(Color[] px, int texW, int cell, float cx, float cy, float rx, float ry, Color tone)
        {
            const float outline = 1.5f * K;
            cx *= K; cy *= K; rx *= K; ry *= K;
            for (int y = 1; y < Cell - 1; y++)
            {
                for (int x = 1; x < Cell - 1; x++)
                {
                    float dx = (x + 0.5f - cx) / rx, dy = (y + 0.5f - cy) / ry;
                    float d = (Mathf.Sqrt(dx * dx + dy * dy) - 1f) * Mathf.Min(rx, ry); // ≈ расстояние до края, точек
                    float cover = Mathf.Clamp01(outline + 0.5f - d);
                    if (cover <= 0f)
                    {
                        continue;
                    }

                    int at = y * texW + cell * Cell + x;
                    Over(px, at, Line, cover);
                    Color c = dx > 0.15f ? new Color(tone.r * Dark.r, tone.g * Dark.g, tone.b * Dark.b) : tone;
                    Over(px, at, c, Mathf.Clamp01(0.5f - d));
                }
            }
        }
    }
}
