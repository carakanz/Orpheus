using System;
using System.Collections.Generic;
using Orpheus.Core.Geometry;
using Orpheus.Core.World;

namespace Orpheus.Net.Session
{
    /// <summary>
    /// Открытые области для карты на весь экран (C-029, п. 8): клиент помнит, как выглядел каждый чанк, который ему присылал
    /// сервер, — по пикселю на тайл (цвета — <see cref="MiniMap.ColorOf"/>), и где стоят кровати. Чанк, ушедший из окна
    /// клиента, на карте остаётся (каким его видели последний раз); не присланный ни разу — тёмный
    /// (<see cref="MiniMap.Unknown"/>). Память — только открытое (4 КБ на чанк). <i>Допущение C-029:</i> открытое помнит
    /// клиент на время сессии (туман войны в сохранении мира — `docs/01` раздел 7, позже).
    /// </summary>
    public sealed class ExploredMap
    {
        private const int Size = Torus.ChunkSize;

        private readonly MapShape _shape;
        private readonly int _chunksX;
        private readonly Dictionary<int, uint[]> _chunks = new Dictionary<int, uint[]>();
        private readonly Dictionary<int, List<TileCoord>> _beds = new Dictionary<int, List<TileCoord>>();
        private readonly HashSet<int> _dirty = new HashSet<int>();
        private readonly List<int> _order = new List<int>();

        public ExploredMap(in MapShape shape)
        {
            _shape = shape;
            _chunksX = Math.Max(1, (shape.Width + Size - 1) / Size);
        }

        /// <summary>Открытых чанков.</summary>
        public int Count => _chunks.Count;

        /// <summary>Чанков ждёт записи.</summary>
        public int Pending => _dirty.Count;

        /// <summary>Сколько раз менялось открытое (карта перерисовывает картинку, когда число сменилось).</summary>
        public int Version { get; private set; }

        private int Key(ChunkCoord c) => c.Y * _chunksX + c.X;

        private ChunkCoord CoordOf(int key) => new ChunkCoord(key % _chunksX, key / _chunksX);

        /// <summary>Чанк пришёл или изменился — записать при следующем <see cref="Update"/>.</summary>
        public void MarkDirty(ChunkCoord c)
        {
            if (_dirty.Add(Key(c)))
            {
                _order.Add(Key(c));
            }
        }

        /// <summary>Записать не больше <paramref name="maxChunks"/> ждущих чанков (по порядку прихода); вернуть, сколько записано.</summary>
        public int Update(WorldModel world, int maxChunks)
        {
            int done = 0;
            while (done < maxChunks && _order.Count > 0)
            {
                int key = _order[0];
                _order.RemoveAt(0);
                _dirty.Remove(key);
                if (Record(world, CoordOf(key)))
                {
                    done++;
                }
            }

            return done;
        }

        /// <summary>Записать чанк <paramref name="c"/> из мира клиента (нет в мире — false, прежняя запись остаётся).</summary>
        public bool Record(WorldModel world, ChunkCoord c)
        {
            if (world.FindChunk(c) == null)
            {
                return false;
            }

            int key = Key(c);
            if (!_chunks.TryGetValue(key, out uint[]? px))
            {
                px = new uint[Size * Size];
                _chunks[key] = px;
            }

            if (!_beds.TryGetValue(key, out List<TileCoord>? beds))
            {
                beds = new List<TileCoord>();
                _beds[key] = beds;
            }

            beds.Clear();
            TileCoord corner = Torus.CornerOf(c);
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    var t = new TileCoord(corner.X + x, corner.Y + y);
                    px[y * Size + x] = MiniMap.ColorOf(world, t);
                    if (ObjectId.IdOf(world.GetObject(t)) == ObjectId.Bed)
                    {
                        beds.Add(t);
                    }
                }
            }

            Version++;
            return true;
        }

        /// <summary>Открыт ли чанк тайла.</summary>
        public bool Explored(TileCoord t) => InMap(t) && _chunks.ContainsKey(Key(Torus.ChunkOf(_shape, Torus.Wrap(_shape, t))));

        /// <summary>Цвет тайла (RGBA, как у мини-карты); не открыт или вне карты — <see cref="MiniMap.Unknown"/>.</summary>
        public uint ColorAt(TileCoord t)
        {
            if (!InMap(t))
            {
                return MiniMap.Unknown;
            }

            TileCoord w = Torus.Wrap(_shape, t);
            return _chunks.TryGetValue(Key(Torus.ChunkOf(_shape, w)), out uint[]? px)
                ? px[(w.Y & (Size - 1)) * Size + (w.X & (Size - 1))]
                : MiniMap.Unknown;
        }

        /// <summary>Кровати в открытых чанках (якорь — тайл с объектом кровати).</summary>
        public List<TileCoord> Beds()
        {
            var keys = new List<int>(_beds.Keys);
            keys.Sort(); // порядок — по номеру чанка (правило ORP006: обход словаря не гарантирован)
            var all = new List<TileCoord>();
            foreach (int k in keys)
            {
                all.AddRange(_beds[k]);
            }

            return all;
        }

        /// <summary>
        /// Картинка <paramref name="w"/> × <paramref name="h"/> (строка 0 — юг, как у текстуры) с центром в точке
        /// (<paramref name="cx"/>, <paramref name="cy"/>) тайлов, <paramref name="tilesPerPixel"/> тайлов на пиксель.
        /// </summary>
        public void Paint(double cx, double cy, double tilesPerPixel, int w, int h, uint[] dest)
        {
            for (int row = 0; row < h; row++)
            {
                int ty = (int)Math.Floor(cy + (h / 2.0 - row - 0.5) * tilesPerPixel);
                for (int col = 0; col < w; col++)
                {
                    int tx = (int)Math.Floor(cx + (col + 0.5 - w / 2.0) * tilesPerPixel);
                    dest[row * w + col] = ColorAt(new TileCoord(tx, ty));
                }
            }
        }

        private bool InMap(TileCoord t) => _shape.Wraps || ((uint)t.X < (uint)_shape.Width && (uint)t.Y < (uint)_shape.Height);
    }
}
