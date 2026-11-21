#nullable enable
using System.Collections.Generic;
using Orpheus.Core.Geometry;
using Orpheus.Core.World;
using UnityEngine;

namespace Orpheus.Client
{
    /// <summary>
    /// C-049: искры у поставленного костра (слово Автора 21.11: «костру и свечам нужно будет анимацию
    /// приделать», карточка п. 3 — «искры у костра»). Пламя колышет ветром шейдер (Sway), свет и тени
    /// огня дышат по формуле DayTint/ShadowComposite, а из огня поднимаются искры — горстка процедурных
    /// точек на костёр: фаза от хэша тайла (каждый костёр со своей росписью), подъём и угасание по
    /// часам кадра (в снимках время кадра фиксировано — лента кадров повторяема). Искры рисуются в
    /// «низкой полосе» строки (WorldView.LowRowOrderAt) — персонаж у костра перед ними, как перед огнём.
    /// Костров в окне много не бывает: пул на <see cref="MaxFires"/> костров, лишний пул спит.
    /// </summary>
    public sealed class CampfireSparkFx
    {
        const int MaxFires = 8;
        const int SparksPerFire = 4;

        readonly WorldModel _world;
        readonly MapShape _map;
        readonly GameObject _root;
        readonly SpriteRenderer[] _spark = new SpriteRenderer[MaxFires * SparksPerFire];
        readonly TileCoord[] _fires = new TileCoord[MaxFires];
        readonly float[] _phase = new float[MaxFires * SparksPerFire];
        readonly List<TileCoord> _found = new List<TileCoord>(MaxFires);

        int _pooled; // рендереров создано (ленивая пул)

        public CampfireSparkFx(Transform parent, WorldModel world, MapShape map)
        {
            _world = world;
            _map = map;
            _root = new GameObject("CampfireSparks");
            _root.transform.SetParent(parent, false);
        }

        /// <summary>Сколько костров с искрами видно в кадре (для smoke и снимков).</summary>
        public int FiresVisible { get; private set; }

        /// <summary>
        /// Кадр игры: найти костры в построенных узлах окна и поднять искры.
        /// <paramref name="nowSec"/> — часы прогона (в снимках шагают по 1/60: лента повторяема).
        /// </summary>
        public void Frame(WorldView view, FloatingOrigin origin, float nowSec)
        {
            _found.Clear();
            foreach (WorldView.ChunkNode node in view.Nodes)
            {
                Chunk? chunk = _world.FindChunk(node.Coord);
                if (chunk == null)
                {
                    continue;
                }

                TileCoord corner = Torus.CornerOf(node.Coord);
                for (int ly = 0; ly < Torus.ChunkSize; ly++)
                {
                    for (int lx = 0; lx < Torus.ChunkSize; lx++)
                    {
                        if (chunk.GetObject(Chunk.IndexOf(lx, ly)) != ObjectId.Campfire)
                        {
                            continue;
                        }

                        _found.Add(Torus.Wrap(_map, new TileCoord(corner.X + lx, corner.Y + ly)));
                        if (_found.Count == MaxFires)
                        {
                            break;
                        }
                    }

                    if (_found.Count == MaxFires)
                    {
                        break;
                    }
                }

                if (_found.Count == MaxFires)
                {
                    break;
                }
            }

            for (int f = 0; f < _found.Count; f++)
            {
                TileCoord t = _found[f];
                Vector3 at = origin.ToUnity(new WorldPos(
                    t.X * Torus.SubTilesPerTile + Torus.SubTilesPerTile / 2,
                    t.Y * Torus.SubTilesPerTile + Torus.SubTilesPerTile / 2));
                int order = WorldView.LowRowOrderAt(at.y - 0.5f + ObjectTable.BaseLift); // та же полоса, что у огня
                for (int i = 0; i < SparksPerFire; i++)
                {
                    int slot = f * SparksPerFire + i;
                    SpriteRenderer sr = Renderer(slot);
                    float ph = _phase[slot];
                    if (_fires[f] != t)
                    {
                        _fires[f] = t;
                        for (int j = 0; j < SparksPerFire; j++)
                        {
                            _phase[f * SparksPerFire + j] = GroundAtlas.Hash(t.X, t.Y, 211u + (uint)j);
                        }

                        ph = _phase[slot];
                    }

                    float life = Mathf.Repeat(nowSec * 0.55f + ph, 1f); // подъём: 0 у огня → 1 на гаснущей высоте
                    float rise = 0.30f + 0.65f * life;
                    float sway = 0.09f * Mathf.Sin(nowSec * 2.3f + ph * 6.2832f) * life;
                    float a = Mathf.Sin(life * Mathf.PI);
                    sr.transform.position = at + new Vector3(sway, rise, 0f);
                    sr.transform.localScale = Vector3.one * (0.045f + 0.055f * a);
                    sr.sprite = HearthMarkerView.SparkAsset();
                    sr.sortingOrder = order;
                    sr.color = new Color32(255, 176, 84, (byte)(235 * a));
                    sr.enabled = a > 0.03f;
                }
            }

            for (int s = _found.Count * SparksPerFire; s < _pooled; s++)
            {
                _spark[s].enabled = false;
            }

            FiresVisible = _found.Count;
        }

        SpriteRenderer Renderer(int slot)
        {
            SpriteRenderer? sr = _spark[slot];
            if (sr == null)
            {
                var go = new GameObject("spark " + slot);
                go.transform.SetParent(_root.transform, false);
                sr = go.AddComponent<SpriteRenderer>();
                sr.enabled = false;
                _spark[slot] = sr;
                if (slot + 1 > _pooled)
                {
                    _pooled = slot + 1;
                }
            }

            return sr;
        }

        public void Dispose()
        {
            Object.Destroy(_root);
        }
    }
}
