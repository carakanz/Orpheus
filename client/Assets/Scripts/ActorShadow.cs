#nullable enable
using System.Collections.Generic;
using Orpheus.Core.Geometry;
using Orpheus.Core.World;
using UnityEngine;

namespace Orpheus.Client
{
    /// <summary>
    /// Тень существа (персонаж, враг; C-017 — тем же механизмом, что у предметов): один маленький меш в слое теней
    /// (<see cref="WorldFx.ShadowLayer"/>) из четырёх квадратов с картинкой мягкого пятна —
    /// <list type="bullet">
    /// <item>пятно у ног (канал «у ног»: есть всегда, и в комнате, и ночью);</item>
    /// <item>силуэт по солнцу: эллипс «стоя» высотой <see cref="Height"/>, верх сдвигает шейдер по вектору солнца
    /// (<see cref="SunTable"/>), как у деревьев;</item>
    /// <item>до двух теней от огня: эллипс от ног прочь от каждого из двух сильнейших видимых источников
    /// (<see cref="LightSight"/>), темнота ∝ вкладу — пересчёт каждый кадр (существо ходит).</item>
    /// </list>
    /// Масштаб и место — в мире (корень существа может быть отражён по X): ноги на <see cref="WorldFx.FootOffset"/> ниже корня.
    /// </summary>
    public sealed class ActorShadow : MonoBehaviour
    {
        /// <summary>Высота силуэта существа и его ширина, тайлов (предварительно).</summary>
        public const float Height = 1.0f, SunWidth = 0.42f, FireWidth = 0.5f;

        /// <summary>Длина тени от огня на тайл высоты: у источника и прирост за тайл, предел (как у предметов).</summary>
        const float FireLenNear = 0.6f, FireLenPerTile = 0.25f, FireLenMax = 1.8f;

        static readonly List<ActorShadow> All = new List<ActorShadow>();

        readonly Vector3[] _pos = new Vector3[16];
        readonly Color32[] _color = new Color32[16];
        readonly List<Vector2> _sun = new List<Vector2>(16);
        readonly LightSight.Lit[] _lit = new LightSight.Lit[2];
        Mesh? _mesh;
        float _width;

        /// <summary>Повесить тень на существо (потомок корня, слой теней).</summary>
        public static void Attach(Transform actor, float width)
        {
            Material? material = ObjectAtlas.ShadowMaterial;
            int blob = ObjectAtlas.Find(ObjectAtlas.BlobName);
            if (material == null || blob < 0)
            {
                return;
            }

            var go = new GameObject("Shadow") { layer = WorldFx.ShadowLayer };
            go.transform.SetParent(actor, false);
            var shadow = go.AddComponent<ActorShadow>();
            shadow._width = width;
            shadow.Build(ObjectAtlas.Entries[blob].Uv);
            go.AddComponent<MeshFilter>().sharedMesh = shadow._mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = material;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            shadow.Refresh();
        }

        void OnEnable() => All.Add(this);

        void OnDisable() => All.Remove(this);

        void OnDestroy()
        {
            if (_mesh != null)
            {
                Destroy(_mesh);
            }
        }

        void LateUpdate() => Refresh();

        /// <summary>Перед ручной отрисовкой (снимок): все тени существ — на место этого кадра.</summary>
        public static void UpdateAll()
        {
            for (int i = 0; i < All.Count; i++)
            {
                All[i].Refresh();
            }
        }

        void Build(Rect uv)
        {
            _mesh = new Mesh { name = "ActorShadow" };
            _mesh.MarkDynamic();
            var uvs = new Vector2[16];
            var info = new Vector4[16];
            var tris = new int[24];
            for (int q = 0; q < 4; q++)
            {
                uvs[q * 4] = new Vector2(uv.xMin, uv.yMin);
                uvs[q * 4 + 1] = new Vector2(uv.xMax, uv.yMin);
                uvs[q * 4 + 2] = new Vector2(uv.xMin, uv.yMax);
                uvs[q * 4 + 3] = new Vector2(uv.xMax, uv.yMax);
                int v = q * 4, t = q * 6;
                tris[t] = v;
                tris[t + 1] = v + 2;
                tris[t + 2] = v + 1;
                tris[t + 3] = v + 1;
                tris[t + 4] = v + 2;
                tris[t + 5] = v + 3;
            }

            // Пятно у ног — сплющенный эллипс чуть правее ног (как прежде).
            float w = _width, hh = _width * 0.42f;
            _pos[0] = new Vector3(0.1f - w * 0.5f, -0.02f - hh * 0.5f);
            _pos[1] = new Vector3(0.1f + w * 0.5f, -0.02f - hh * 0.5f);
            _pos[2] = new Vector3(0.1f - w * 0.5f, -0.02f + hh * 0.5f);
            _pos[3] = new Vector3(0.1f + w * 0.5f, -0.02f + hh * 0.5f);
            // Силуэт по солнцу: эллипс «стоя» у ног; высоту точек сдвигает шейдер.
            _pos[4] = _pos[6] = new Vector3(-SunWidth * 0.5f, 0f);
            _pos[5] = _pos[7] = new Vector3(SunWidth * 0.5f, 0f);
            for (int i = 0; i < 16; i++)
            {
                _color[i] = new Color32(0, 0, 0, (byte)(i < 4 ? 235 : i < 8 ? 210 : 0));
                _sun.Add(new Vector2(i == 6 || i == 7 ? Height : 0f, i < 4 ? ObjectMesh.FootChannel : i < 8 ? ObjectMesh.SunChannel : ObjectMesh.FireChannel));
            }

            _mesh.vertices = _pos;
            _mesh.uv = uvs;
            _mesh.SetUVs(1, info);
            _mesh.SetUVs(2, _sun);
            _mesh.colors32 = _color;
            _mesh.triangles = tris;
            // Рамка с запасом: силуэт по солнцу и тени огня уходят на пару тайлов.
            _mesh.bounds = new Bounds(Vector3.zero, new Vector3(8f, 8f, 1f));
        }

        void Refresh()
        {
            if (_mesh == null)
            {
                return;
            }

            Transform t = transform;
            Transform? root = t.parent;
            if (root != null)
            {
                Vector3 ls = root.lossyScale;
                t.localScale = new Vector3(Mathf.Abs(ls.x) > 1e-4f ? 1f / ls.x : 1f, Mathf.Abs(ls.y) > 1e-4f ? 1f / ls.y : 1f, 1f);
                Vector3 p = root.position;
                t.position = new Vector3(p.x, p.y - WorldFx.FootOffset, p.z);
            }

            int lit = 0;
            WorldModel? world = WorldFx.World;
            FloatingOrigin? origin = WorldFx.Origin;
            if (world != null && origin != null)
            {
                WorldPos feet = origin.FromUnity(t.position);
                TileCoord tile = Torus.TileOf(world.Shape, feet);
                lit = world.Sight.Strongest(Torus.ChunkOf(world.Shape, tile), feet.X >> (Torus.SubTileBits - 2), feet.Y >> (Torus.SubTileBits - 2), _lit);
            }

            bool changed = false;
            for (int j = 0; j < 2; j++)
            {
                int v = 8 + j * 4;
                Vector3 a = Vector3.zero, b = Vector3.zero, c = Vector3.zero, d = Vector3.zero;
                byte alpha = 0;
                if (j < lit)
                {
                    var u = new Vector2(_lit[j].Dx4, -_lit[j].Dy4) * 0.25f;
                    float dist = u.magnitude;
                    if (dist > 0.4f)
                    {
                        u /= dist;
                        var side = new Vector2(u.y, -u.x) * (FireWidth * 0.5f);
                        float len = Mathf.Min(FireLenMax, FireLenNear + FireLenPerTile * dist) * Height;
                        Vector2 foot = -u * 0.12f;
                        a = foot - side;
                        b = foot + side;
                        c = foot + u * len - side;
                        d = foot + u * len + side;
                        alpha = (byte)Mathf.Min(255, _lit[j].X1024 * ObjectMesh.FireAlpha / DayClock.LightScale);
                    }
                }

                changed |= _pos[v] != a || _pos[v + 1] != b || _pos[v + 2] != c || _pos[v + 3] != d || _color[v].a != alpha;
                _pos[v] = a;
                _pos[v + 1] = b;
                _pos[v + 2] = c;
                _pos[v + 3] = d;
                for (int k = 0; k < 4; k++)
                {
                    _color[v + k] = new Color32(0, 0, 0, alpha);
                }
            }

            if (changed)
            {
                _mesh.vertices = _pos;
                _mesh.colors32 = _color;
                _mesh.bounds = new Bounds(Vector3.zero, new Vector3(8f, 8f, 1f));
            }
        }
    }
}
