using System;
using System.Collections.Generic;
using Orpheus.Core.Geometry;

namespace Orpheus.Core.World
{
    /// <summary>
    /// Свет факелов и костров с учётом стен — <b>только для картинки</b> (C-017): круг света и тени от источников. Правила
    /// игры (появление врагов) берут <see cref="LightIndex"/> без стен — по-старому.
    /// <para>
    /// Сетка ячеек 0,5 тайла; точка источника — середина его тайла. Ячейка видна источнику, если луч от него до середины
    /// ячейки не проходит через ячейку-стену (<see cref="CellWindow.LightWall"/>: стена, закрытая дверь, скала; сама
    /// ячейка-цель может быть стеной — её освещённая грань); через угол двух стен по диагонали луч не проходит.
    /// Вклад — как у <see cref="LightIndex"/> (факел 3 / 6, костёр 4 / 8 тайлов), но по непрерывному расстоянию в четвертях
    /// тайла. Сетка на чанк — по штампу окна 3 × 3 (<see cref="CellWindow.StampOf"/>); окно без стен — без лучей.
    /// </para>
    /// Координаты «в четвертях» — четверть тайла (середина ячейки (cx, cy) — (2cx + 1, 2cy + 1)).
    /// </summary>
    public sealed class LightSight
    {
        private const int N = CellWindow.CellsPerChunk;
        private const int Side = CellWindow.Side;
        private const int HalfSide = Side / 2;

        /// <summary>Источник света окна чанка: середина тайла в четвертях от угла окна, радиусы в четвертях.</summary>
        public struct Source
        {
            public int X4, Y4, Inner4, Outer4;
            public ushort Id;
        }

        /// <summary>Вклад одного источника в точку.</summary>
        public struct Lit
        {
            /// <summary>Направление от источника к точке, четверти тайла (мир: X вправо, Y вниз).</summary>
            public int Dx4, Dy4;

            /// <summary>Вклад 0…1024 с учётом стен.</summary>
            public int X1024;
        }

        private sealed class Entry
        {
            public long Stamp;
            public readonly ushort[] Grid = new ushort[N * N];
            public byte[]? Cells; // копия окна (только если рядом есть источники)
            public bool AnyWall;
            public TileCoord Corner;
            public Source[] Sources = new Source[0];
            public int SourceCount;
        }

        private readonly WorldModel _world;
        private readonly CellWindow _window = new CellWindow();
        private readonly Dictionary<ChunkCoord, Entry> _entries = new Dictionary<ChunkCoord, Entry>();

        public LightSight(WorldModel world)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
        }

        /// <summary>Сколько раз сетка чанка пересчитана.</summary>
        public long Rebuilds { get; private set; }

        /// <summary>Лучей прослежено за всё время (замер).</summary>
        public long Rays { get; private set; }

        /// <summary>Свет ячеек чанка с учётом стен (64 × 64, индекс cy × 64 + cx, Y вниз; сумма источников, 1024 — полный).</summary>
        public ushort[] GridOf(ChunkCoord chunk) => EntryOf(chunk).Grid;

        /// <summary>Есть ли в окне 3 × 3 чанка источники света (иначе сетка нулевая).</summary>
        public bool AnySource(ChunkCoord chunk) => EntryOf(chunk).SourceCount > 0;

        /// <summary>
        /// До <paramref name="top"/>.Length сильнейших видимых источников в точке мира (<paramref name="x4"/>, <paramref name="y4"/>)
        /// четвертей тайла (точка — в чанке <paramref name="chunk"/>); по убыванию вклада. Возвращает их число.
        /// </summary>
        public int Strongest(ChunkCoord chunk, int x4, int y4, Span<Lit> top)
        {
            Entry e = EntryOf(chunk);
            if (e.SourceCount == 0 || top.Length == 0)
            {
                return 0;
            }

            MapShape map = _world.Shape;
            int px = Mod(x4 - e.Corner.X * 4, map.Wraps ? map.Width * 4 : int.MaxValue);
            int py = Mod(y4 - e.Corner.Y * 4, map.Wraps ? map.Height * 4 : int.MaxValue);
            if (px >= Side * 2 || py >= Side * 2)
            {
                return 0;
            }

            int n = 0;
            for (int i = 0; i < e.SourceCount; i++)
            {
                ref Source s = ref e.Sources[i];
                int v = Falloff(s, px - s.X4, py - s.Y4);
                if (v <= 0 || (e.AnyWall && !Visible(e.Cells!, s.X4, s.Y4, px, py)))
                {
                    continue;
                }

                // Вставка по убыванию вклада (при равенстве — раньше найденный: порядок источников фиксирован).
                int at = n < top.Length ? n++ : top.Length;
                while (at > 0 && top[at - 1].X1024 < v)
                {
                    if (at < top.Length)
                    {
                        top[at] = top[at - 1];
                    }

                    at--;
                }

                if (at < top.Length)
                {
                    top[at] = new Lit { Dx4 = px - s.X4, Dy4 = py - s.Y4, X1024 = v };
                }
            }

            return n;
        }

        private Entry EntryOf(ChunkCoord chunk)
        {
            ChunkCoord c = Torus.Wrap(_world.Shape, chunk);
            long stamp = CellWindow.StampOf(_world, c);
            if (_entries.TryGetValue(c, out Entry? e) && e.Stamp == stamp)
            {
                return e;
            }

            if (e == null)
            {
                e = new Entry();
                _entries[c] = e;
            }

            e.Stamp = stamp;
            Rebuild(c, e);
            Rebuilds++;
            return e;
        }

        private void Rebuild(ChunkCoord center, Entry e)
        {
            Array.Clear(e.Grid, 0, e.Grid.Length);
            _window.Load(_world, center);
            e.Corner = _window.Corner;
            e.SourceCount = 0;
            e.AnyWall = _window.AnyLightWall;
            if (_window.SourceCount == 0)
            {
                return;
            }

            if (e.Sources.Length < _window.SourceCount)
            {
                e.Sources = new Source[_window.SourceCount];
            }

            ushort[] src = _window.Sources;
            for (int i = 0; i < src.Length; i++)
            {
                if (src[i] == 0)
                {
                    continue;
                }

                bool fire = src[i] == ObjectId.Campfire;
                e.Sources[e.SourceCount++] = new Source
                {
                    X4 = (i % HalfSide) * 4 + 2,
                    Y4 = (i / HalfSide) * 4 + 2,
                    Inner4 = 4 * (fire ? LightIndex.CampfireInner : LightIndex.TorchInner),
                    Outer4 = 4 * (fire ? LightIndex.CampfireOuter : LightIndex.TorchOuter),
                    Id = src[i],
                };
            }

            if (e.AnyWall)
            {
                if (e.Cells == null)
                {
                    e.Cells = new byte[Side * Side];
                }

                Array.Copy(_window.Cells, e.Cells, e.Cells.Length);
            }

            // Ячейки центрального чанка: окно [N; 2N) по обеим осям, в четвертях — [2N; 4N).
            for (int i = 0; i < e.SourceCount; i++)
            {
                ref Source s = ref e.Sources[i];
                int x0 = Math.Max(N, (s.X4 - s.Outer4) >> 1), x1 = Math.Min(2 * N - 1, (s.X4 + s.Outer4) >> 1);
                int y0 = Math.Max(N, (s.Y4 - s.Outer4) >> 1), y1 = Math.Min(2 * N - 1, (s.Y4 + s.Outer4) >> 1);
                for (int wy = y0; wy <= y1; wy++)
                {
                    for (int wx = x0; wx <= x1; wx++)
                    {
                        int tx = 2 * wx + 1, ty = 2 * wy + 1;
                        int v = Falloff(s, tx - s.X4, ty - s.Y4);
                        if (v <= 0 || (e.AnyWall && !Visible(e.Cells!, s.X4, s.Y4, tx, ty)))
                        {
                            continue;
                        }

                        ref ushort g = ref e.Grid[(wy - N) * N + wx - N];
                        int sum = g + v;
                        g = (ushort)(sum > ushort.MaxValue ? ushort.MaxValue : sum);
                    }
                }
            }
        }

        /// <summary>Вклад источника в точку на (dx, dy) четвертей от него: полный до Inner, линейно до 0 на Outer.</summary>
        private static int Falloff(in Source s, int dx, int dy)
        {
            int d2 = dx * dx + dy * dy;
            if (d2 >= s.Outer4 * s.Outer4)
            {
                return 0;
            }

            if (d2 <= s.Inner4 * s.Inner4)
            {
                return DayClock.LightScale;
            }

            int d = ISqrt(d2);
            return DayClock.LightScale * (s.Outer4 - d) / (s.Outer4 - s.Inner4);
        }

        /// <summary>
        /// Луч от (sx, sy) до (tx, ty) (четверти тайла в окне) не проходит через ячейку-стену, кроме ячейки цели; при шаге
        /// по диагонали через угол двух стен — закрыт.
        /// </summary>
        private bool Visible(byte[] cells, int sx, int sy, int tx, int ty)
        {
            Rays++;
            int dx = tx - sx, dy = ty - sy;
            int n = Math.Max(Math.Abs(dx), Math.Abs(dy));
            int goalX = tx >> 1, goalY = ty >> 1;
            int px = sx >> 1, py = sy >> 1;
            for (int k = 1; k <= n; k++)
            {
                int cx = (sx * n + dx * k) / n >> 1;
                int cy = (sy * n + dy * k) / n >> 1;
                if (cx == px && cy == py)
                {
                    continue;
                }

                if (cx != px && cy != py &&
                    (cells[py * Side + cx] & CellWindow.LightWall) != 0 && (cells[cy * Side + px] & CellWindow.LightWall) != 0)
                {
                    return false; // угол двух стен по диагонали
                }

                if (cx == goalX && cy == goalY)
                {
                    return true;
                }

                if ((cells[cy * Side + cx] & CellWindow.LightWall) != 0)
                {
                    return false;
                }

                px = cx;
                py = cy;
            }

            return true;
        }

        private static int Mod(int v, int m)
        {
            int r = v % m;
            return r < 0 ? r + m : r;
        }

        private static int ISqrt(int v)
        {
            int r = (int)Math.Sqrt(v);
            while (r * r > v)
            {
                r--;
            }

            while ((r + 1) * (r + 1) <= v)
            {
                r++;
            }

            return r;
        }
    }
}
