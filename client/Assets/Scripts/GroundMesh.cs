#nullable enable
using System.Runtime.InteropServices;
using Orpheus.Core.Geometry;
using Orpheus.Core.World;
using UnityEngine;
using UnityEngine.Rendering;

namespace Orpheus.Client
{
    /// <summary>
    /// Меш земли одного чанка (E1-4): «двойная сетка» 32 × 32 квадрата между центрами тайлов. Квадрат (i, j) лежит между
    /// тайлами (i−1, j−1), (i, j−1), (i−1, j), (i, j) чанка и несёт номера их слоёв; чанк закрывает область
    /// [−0,5; 31,5] тайла, соседние чанки стыкуются без щели. Тайлы за краем берутся из соседних чанков мира
    /// (до <see cref="Halo"/> тайлов: 1 — для квадратов, ещё 2 — чтобы решить «отмель или глубина»).
    /// Сборщик один на игру, буферы общие — мусора на кадр нет.
    /// </summary>
    public sealed class GroundMesh
    {
        const int Size = Torus.ChunkSize;

        /// <summary>Сколько тайлов соседнего чанка влияет на меш этого: правка ближе к краю — перестроить и соседа.</summary>
        public const int Halo = 3;

        /// <summary>Вода ближе этого расстояния (в тайлах, по прямой) до суши — отмель: полоса 1–2 тайла.</summary>
        const int ShallowDist2 = 5; // dx² + dy² ≤ 5: до (2, 1)

        const int Pad = Size + 2 * Halo;
        const int Quads = Size * Size;

        static readonly VertexAttributeDescriptor[] Layout =
        {
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.UNorm8, 4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 4),
        };

        // Порядок полей в памяти должен совпасть с порядком атрибутов Unity (Position, Color, TexCoord0).
        [StructLayout(LayoutKind.Sequential)]
        struct VertexGpu
        {
            public Vector3 Pos;
            public Color32 Ids;  // слои тайлов: СЗ, СВ, ЮЗ, ЮВ
            public Vector4 Uv;   // xy — тайлы мира (Y вниз, по модулю 256), zw — место в квадрате 0…1
        }

        readonly WorldModel _world;
        readonly MapShape _map;
        readonly byte[] _floor = new byte[Pad * Pad];
        readonly byte[] _layer = new byte[Pad * Pad];
        readonly bool[] _water = new bool[Pad * Pad];
        readonly VertexGpu[] _verts = new VertexGpu[Quads * 4];
        readonly Chunk?[] _near = new Chunk?[9];
        static ushort[]? _indices;

        public GroundMesh(WorldModel world)
        {
            _world = world;
            _map = world.Shape;
        }

        /// <summary>Сколько мешей собрано за всё время.</summary>
        public int Builds { get; private set; }

        /// <summary>Пустой меш чанка: индексы и границы постоянные, вершины кладёт <see cref="Build"/>.</summary>
        public static Mesh Create()
        {
            var mesh = new Mesh { name = "Ground" };
            mesh.MarkDynamic();
            mesh.SetVertexBufferParams(Quads * 4, Layout);
            if (_indices == null)
            {
                var idx = new ushort[Quads * 6];
                for (int q = 0; q < Quads; q++)
                {
                    int v = q * 4, k = q * 6;
                    idx[k] = (ushort)v;
                    idx[k + 1] = (ushort)(v + 1);
                    idx[k + 2] = (ushort)(v + 2);
                    idx[k + 3] = (ushort)(v + 2);
                    idx[k + 4] = (ushort)(v + 1);
                    idx[k + 5] = (ushort)(v + 3);
                }

                _indices = idx;
            }

            mesh.SetIndexBufferParams(Quads * 6, IndexFormat.UInt16);
            mesh.SetIndexBufferData(_indices, 0, 0, Quads * 6, MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
            mesh.subMeshCount = 1;
            mesh.SetSubMesh(0, new SubMeshDescriptor(0, Quads * 6), MeshUpdateFlags.DontRecalculateBounds);
            mesh.bounds = new Bounds(new Vector3(Size / 2f - 0.5f, -Size / 2f + 0.5f, 0f), new Vector3(Size, Size, 0.1f));
            return mesh;
        }

        /// <summary>
        /// Собрать меш чанка из мира ядра. Возвращает false, если какого-то из соседних чанков в мире ещё нет
        /// (сеть: не пришёл) — край собран по своим тайлам, меш надо пересобрать, когда сосед появится.
        /// </summary>
        public bool Build(ChunkCoord c, Mesh mesh)
        {
            bool complete = true;
            for (int ny = 0; ny < 3; ny++)
            {
                for (int nx = 0; nx < 3; nx++)
                {
                    Chunk? ch = _world.FindChunk(Torus.Wrap(_map, new ChunkCoord(c.X + nx - 1, c.Y + ny - 1)));
                    _near[ny * 3 + nx] = ch;
                    complete &= ch != null;
                }
            }

            Chunk? own = _near[4];

            // 1. Полы с полями: свой чанк и по Halo тайлов соседей; нет соседа — ближайший свой тайл.
            for (int py = 0; py < Pad; py++)
            {
                int ty = py - Halo;
                int cy = ty < 0 ? 0 : (ty >= Size ? 2 : 1);
                for (int px = 0; px < Pad; px++)
                {
                    int tx = px - Halo;
                    int cx = tx < 0 ? 0 : (tx >= Size ? 2 : 1);
                    Chunk? ch = _near[cy * 3 + cx];
                    byte floor;
                    if (ch != null)
                    {
                        floor = ch.GetFloor(Chunk.IndexOf(tx, ty)); // IndexOf берёт младшие 5 бит: −1 → 31, 32 → 0
                    }
                    else if (own != null)
                    {
                        floor = own.GetFloor(Chunk.IndexOf(Mathf.Clamp(tx, 0, Size - 1), Mathf.Clamp(ty, 0, Size - 1)));
                    }
                    else
                    {
                        floor = FloorId.Grass;
                    }

                    int p = py * Pad + px;
                    _floor[p] = floor;
                    GroundRow row = GroundTable.Of(floor);
                    _layer[p] = row.Layer;
                    _water[p] = row.Kind == GroundKind.Water;
                }
            }

            // 2. Вода: отмель у суши, глубина дальше. Нужны тайлы −1…31 (углы квадратов), смотрим на 2 тайла вокруг.
            for (int py = Halo - 1; py < Halo + Size; py++)
            {
                for (int px = Halo - 1; px < Halo + Size; px++)
                {
                    int p = py * Pad + px;
                    if (!_water[p])
                    {
                        continue;
                    }

                    bool shore = false;
                    for (int dy = -2; dy <= 2 && !shore; dy++)
                    {
                        for (int dx = -2; dx <= 2; dx++)
                        {
                            if (dx * dx + dy * dy <= ShallowDist2 && !_water[p + dy * Pad + dx])
                            {
                                shore = true;
                                break;
                            }
                        }
                    }

                    _layer[p] = shore ? GroundTable.WaterShallow : GroundTable.WaterDeep;
                }
            }

            // 3. Вершины. Координата мира — по модулю 256 тайлов: картинки (4), шум (16, 64) в неё укладываются, а числа малы.
            float ox = ((c.X * Size) & 255) - 0.5f;
            float oy = ((c.Y * Size) & 255) - 0.5f;
            int v = 0;
            for (int j = 0; j < Size; j++)
            {
                int rowN = (j + Halo - 1) * Pad + Halo - 1; // тайл (−1, j−1)
                int rowS = rowN + Pad;
                float y0 = -(j - 0.5f), y1 = y0 - 1f;      // Unity: Y вверх
                for (int i = 0; i < Size; i++)
                {
                    var ids = new Color32(_layer[rowN + i], _layer[rowN + i + 1], _layer[rowS + i], _layer[rowS + i + 1]);
                    float x0 = i - 0.5f, x1 = x0 + 1f;
                    float ux = ox + i, uy = oy + j;
                    _verts[v].Pos = new Vector3(x0, y0, 0f);
                    _verts[v].Ids = ids;
                    _verts[v].Uv = new Vector4(ux, uy, 0f, 0f);
                    v++;
                    _verts[v].Pos = new Vector3(x1, y0, 0f);
                    _verts[v].Ids = ids;
                    _verts[v].Uv = new Vector4(ux + 1f, uy, 1f, 0f);
                    v++;
                    _verts[v].Pos = new Vector3(x0, y1, 0f);
                    _verts[v].Ids = ids;
                    _verts[v].Uv = new Vector4(ux, uy + 1f, 0f, 1f);
                    v++;
                    _verts[v].Pos = new Vector3(x1, y1, 0f);
                    _verts[v].Ids = ids;
                    _verts[v].Uv = new Vector4(ux + 1f, uy + 1f, 1f, 1f);
                    v++;
                }
            }

            mesh.SetVertexBufferData(_verts, 0, 0, _verts.Length, 0, MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
            Builds++;
            return complete;
        }
    }
}
