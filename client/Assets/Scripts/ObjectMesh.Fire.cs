#nullable enable
using System;
using Orpheus.Core.Geometry;
using Orpheus.Core.World;
using UnityEngine;

namespace Orpheus.Client
{
    /// <summary>
    /// Тени от огня (C-017, слова Автора 15.11 «должны быть тени от факелов; в случае нескольких источников —
    /// высчитываться; тень за стены попадать не должна»). Меш чанка: каждый предмет «с высотой» (не стена и не гора —
    /// за них свет не проходит сам, <see cref="LightSight"/>) отбрасывает силуэт прочь от каждого из
    /// <see cref="FireShadowsPerObject"/> сильнейших видимых источников; темнота ∝ вкладу источника. Направление
    /// неподвижно (источники стоят), поэтому меш собирается на CPU и пересобирается, только когда меняется окно
    /// 3 × 3 чанка (<see cref="CellWindow.StampOf"/>, <see cref="WorldView"/>). Тень за стеной гасит слой наложения:
    /// тень огня видна только там, куда свет доходит с учётом стен.
    /// </summary>
    public sealed partial class ObjectMesh
    {
        /// <summary>Источников на предмет — не больше (сильнейшие).</summary>
        public const int FireShadowsPerObject = 3;

        /// <summary>Длина тени огня на тайл высоты: у источника — <see cref="FireLenNear"/>, дальше растёт на
        /// <see cref="FireLenPerTile"/> за тайл, не длиннее <see cref="FireLenMax"/> (предварительно).</summary>
        const float FireLenNear = 0.45f, FireLenPerTile = 0.22f, FireLenMax = 1.6f;

        /// <summary>Слабее этого вклада (из 1024) тень огня не строится.</summary>
        const int FireMinX1024 = 80;

        /// <summary>Непрозрачность тени огня при полном вкладе (из 255; больше 255 — полный цвет раньше полного вклада).</summary>
        public const int FireAlpha = 380;

        readonly LightSight.Lit[] _lit = new LightSight.Lit[FireShadowsPerObject];

        // Расширить рамку меша теней: шейдер сдвигает верх по солнцу на несколько тайлов (длинная вечерняя тень горы),
        // иначе камера теней отсекает чанк, чья тень уже на экране.
        const float ShadowReach = 10f;

        void FinishShadowMesh(Mesh mesh)
        {
            while (_info.Count < _pos.Count)
            {
                _info.Add(Vector4.zero);
            }

            while (_sun.Count < _pos.Count)
            {
                _sun.Add(Vector2.zero);
            }

            mesh.SetVertices(_pos);
            mesh.SetUVs(0, _uv);
            mesh.SetUVs(1, _info);
            mesh.SetUVs(2, _sun);
            mesh.SetColors(_color);
            mesh.SetTriangles(_index, 0, calculateBounds: true);
            Bounds b = mesh.bounds;
            b.Expand(new Vector3(2f * ShadowReach, 2f * ShadowReach, 0f));
            mesh.bounds = b;
        }

        /// <summary>Меш теней от огня чанка; возвращает число теней (0 — рядом нет источников).</summary>
        public int BuildFireShadows(Chunk? chunk, ChunkCoord c, Mesh mesh)
        {
            Builds++;
            mesh.Clear();
            LightSight sight = _world.Sight;
            if (chunk == null || !sight.AnySource(c))
            {
                return 0;
            }

            Begin();
            ObjectAtlas.Entry[] entries = ObjectAtlas.Entries;
            TileCoord corner = Torus.CornerOf(c);
            int shadows = 0;
            for (int ly = 0; ly < Size; ly++)
            {
                int n = Collect(chunk, c, ly);
                for (int i = 0; i < n; i++)
                {
                    ref Item it = ref _items[i];
                    float k = it.Look.Shadow;
                    if (k <= 0f || it.Cell || it.Look.Block || it.Look.Id == ObjectId.Torch || it.Look.Id == ObjectId.Campfire)
                    {
                        continue;
                    }

                    // Опора в мире, четверти тайла: X вправо, Y вниз (в узле чанка Y вверх, тайл ly — от −ly − 1 до −ly).
                    int x4 = Mathf.RoundToInt((corner.X + it.X) * 4f), y4 = Mathf.RoundToInt((corner.Y - it.Y) * 4f);
                    int lit = sight.Strongest(c, x4, y4, _lit);
                    if (lit == 0)
                    {
                        continue;
                    }

                    ref ObjectAtlas.Entry e = ref entries[it.Place.Sprite];
                    float s = it.Place.Scale;
                    float x0 = -e.PivotX * s, x1 = x0 + e.Width * s;
                    float h0 = -e.PivotY * s, h1 = h0 + e.Height * s;
                    float sway = SwayOf(it.Look, corner.X + it.Lx, corner.Y + ly);
                    for (int j = 0; j < lit; j++)
                    {
                        ref LightSight.Lit l = ref _lit[j];
                        if (l.X1024 < FireMinX1024)
                        {
                            break;
                        }

                        // Прочь от источника (Unity: Y вверх).
                        var u = new Vector2(l.Dx4, -l.Dy4) * 0.25f;
                        float dist = u.magnitude;
                        if (dist < 0.5f)
                        {
                            continue; // вплотную к огню — без тени
                        }

                        u /= dist;
                        var side = new Vector2(u.y, -u.x); // «ширина» картинки — поперёк луча
                        float len = Mathf.Min(FireLenMax, FireLenNear + FireLenPerTile * dist) * k;
                        var a = new Vector2(it.X, it.Y);
                        var c32 = new Color32(0, 0, 0, (byte)Mathf.Min(255, l.X1024 * FireAlpha / DayClock.LightScale));
                        Quad(a + side * x0 + u * (h0 * len), a + side * x1 + u * (h0 * len),
                            a + side * x0 + u * (h1 * len), a + side * x1 + u * (h1 * len), e, it.Place.Flip, c32);
                        Sun4(0f, 0f, FireChannel);
                        while (_info.Count < _pos.Count - 4)
                        {
                            _info.Add(Vector4.zero);
                        }

                        _info.Add(new Vector4(it.X, it.Y, h0, sway));
                        _info.Add(new Vector4(it.X, it.Y, h0, sway));
                        _info.Add(new Vector4(it.X, it.Y, h1, sway));
                        _info.Add(new Vector4(it.X, it.Y, h1, sway));
                        shadows++;
                    }
                }
            }

            if (shadows == 0)
            {
                return 0;
            }

            FinishShadowMesh(mesh);
            return shadows;
        }
    }
}
