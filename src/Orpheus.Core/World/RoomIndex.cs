using System;
using System.Collections.Generic;
using Orpheus.Core.Geometry;

namespace Orpheus.Core.World
{
    /// <summary>
    /// Закрытые комнаты (C-017, слово Автора 15.11): область ячеек 0,5 тайла, замкнутая стенами и дверями (открытая дверь
    /// комнату не размыкает, забор — не стена), поиск заливкой по 4 соседям. Комната — связная область свободных ячеек,
    /// рамка которой не шире и не выше <see cref="MaxRoomCells"/> (32 тайла) и которая не доходит до края карты или
    /// отсутствующего чанка; иначе — «снаружи». Пол не нужен.
    /// <para>
    /// Ядро: в комнате с факелом/костром враги не появляются (<c>Spawner.IsSpawnTileOk</c>); клиент: в комнате нет
    /// солнечной тени. Метки на чанк (64 × 64 ячейки) пересчитываются, когда меняется ревизия любого чанка окна 3 × 3:
    /// рамка комнаты ≤ 64 ячеек, поэтому комната с ячейкой в чанке и её стены лежат в этом окне. Детерминированно:
    /// целые, обход в фиксированном порядке, итог от порядка не зависит (связные области).
    /// </para>
    /// </summary>
    public sealed class RoomIndex
    {
        /// <summary>Наибольшая сторона рамки комнаты, ячеек (32 тайла; шире — открытое место).</summary>
        public const int MaxRoomCells = 64;

        /// <summary>Метка ячейки: снаружи (или стена).</summary>
        public const byte Outside = 0;

        /// <summary>Метка ячейки: в комнате без источника света.</summary>
        public const byte Room = 1;

        /// <summary>Метка ячейки: в комнате, где есть факел или костёр.</summary>
        public const byte LitRoom = 2;

        private const int N = CellWindow.CellsPerChunk;
        private const int Side = CellWindow.Side;

        private readonly WorldModel _world;
        private readonly CellWindow _window = new CellWindow();
        private readonly int[] _queue = new int[Side * Side];
        private readonly int[] _seen = new int[Side * Side];
        private int _seenStamp;

        private sealed class Entry
        {
            public long Stamp;
            public readonly byte[] Labels = new byte[N * N];
        }

        private readonly Dictionary<ChunkCoord, Entry> _entries = new Dictionary<ChunkCoord, Entry>();

        public RoomIndex(WorldModel world)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
        }

        /// <summary>Сколько раз метки чанка пересчитаны (тест кэша).</summary>
        public long Rebuilds { get; private set; }

        /// <summary>
        /// C-034: убрать запись меток чанка (выгрузка чанка у клиента, <c>WorldModel.RemoveChunk</c>).
        /// Запись — чистая функция окна 3 × 3, при следующем запросе пересчитается детерминированно.
        /// </summary>
        public void Drop(ChunkCoord chunk) => _entries.Remove(Torus.Wrap(_world.Shape, chunk));

        /// <summary>Число записей кэша (тест C-034: при выгрузке чанков не должен расти за пределы видимого окна).</summary>
        public int EntryCount => _entries.Count;

        /// <summary>Метки ячеек чанка (64 × 64, индекс cy × 64 + cx; Y вниз): <see cref="Outside"/>, <see cref="Room"/>, <see cref="LitRoom"/>.</summary>
        public byte[] LabelsOf(ChunkCoord chunk)
        {
            ChunkCoord c = Torus.Wrap(_world.Shape, chunk);
            long stamp = CellWindow.StampOf(_world, c);
            if (_entries.TryGetValue(c, out Entry? e) && e.Stamp == stamp)
            {
                return e.Labels;
            }

            if (e == null)
            {
                e = new Entry();
                _entries[c] = e;
            }

            e.Stamp = stamp;
            Rebuild(c, e.Labels);
            Rebuilds++;
            return e.Labels;
        }

        /// <summary>Метка ячейки (координаты сетки ячеек: тайл × 2 + четверть).</summary>
        public byte LabelAt(TileCoord cell)
        {
            MapShape map = _world.Shape;
            TileCoord tile = Torus.Wrap(map, BuildCells.TileOfCell(cell));
            byte[] labels = LabelsOf(Torus.ChunkOf(map, tile));
            int cx = ((tile.X & (Torus.ChunkSize - 1)) << 1) | (cell.X & 1);
            int cy = ((tile.Y & (Torus.ChunkSize - 1)) << 1) | (cell.Y & 1);
            return labels[cy * N + cx];
        }

        /// <summary>Хоть одна ячейка тайла — в комнате с источником света (запрет появления врагов, C-017).</summary>
        public bool LitRoomInTile(TileCoord tile)
        {
            MapShape map = _world.Shape;
            TileCoord t = Torus.Wrap(map, tile);
            byte[] labels = LabelsOf(Torus.ChunkOf(map, t));
            int i = ((t.Y & (Torus.ChunkSize - 1)) << 1) * N + ((t.X & (Torus.ChunkSize - 1)) << 1);
            return labels[i] == LitRoom || labels[i + 1] == LitRoom || labels[i + N] == LitRoom || labels[i + N + 1] == LitRoom;
        }

        private void Rebuild(ChunkCoord center, byte[] labels)
        {
            Array.Clear(labels, 0, labels.Length);
            _window.Load(_world, center);
            if (!_window.AnyRoomWall)
            {
                return; // в окне ни одной стены — комнат нет
            }

            byte[] cells = _window.Cells;
            if (_seenStamp > int.MaxValue - 2 * N * N)
            {
                Array.Clear(_seen, 0, _seen.Length);
                _seenStamp = 0;
            }

            _rebuildFirstStamp = _seenStamp;
            for (int cy = 0; cy < N; cy++)
            {
                for (int cx = 0; cx < N; cx++)
                {
                    int start = (cy + N) * Side + cx + N;
                    if (cells[start] != 0 || labels[cy * N + cx] != Outside || Visited(start))
                    {
                        continue;
                    }

                    Fill(start, labels);
                }
            }
        }

        // Ячейки, уже пройденные заливкой с открытым итогом, в этом пересчёте: метка «снаружи» уже верна.
        private int _rebuildFirstStamp;

        private bool Visited(int w) => _seen[w] > _rebuildFirstStamp;

        private void Fill(int start, byte[] labels)
        {
            byte[] cells = _window.Cells;
            ushort[] sources = _window.Sources;
            int stamp = ++_seenStamp;
            int head = 0, n = 0;
            _queue[n++] = start;
            _seen[start] = stamp;
            int minX = start % Side, maxX = minX, minY = start / Side, maxY = minY;
            bool open = false, lit = false;
            while (head < n)
            {
                int w = _queue[head++];
                int x = w % Side, y = w / Side;
                if (sources[(y >> 1) * (Side / 2) + (x >> 1)] != 0)
                {
                    lit = true;
                }

                for (int d = 0; d < 4; d++)
                {
                    int nx = x + (d == 0 ? 1 : d == 1 ? -1 : 0);
                    int ny = y + (d == 2 ? 1 : d == 3 ? -1 : 0);
                    if ((uint)nx >= Side || (uint)ny >= Side)
                    {
                        open = true; // не бывает при рамке ≤ 64 (окно шире), но край окна — «снаружи»
                        break;
                    }

                    int nw = ny * Side + nx;
                    byte f = cells[nw];
                    if ((f & CellWindow.Void) != 0)
                    {
                        open = true;
                        break;
                    }

                    if ((f & CellWindow.RoomWall) != 0 || _seen[nw] == stamp)
                    {
                        continue;
                    }

                    _seen[nw] = stamp;
                    if (nx < minX) minX = nx;
                    if (nx > maxX) maxX = nx;
                    if (ny < minY) minY = ny;
                    if (ny > maxY) maxY = ny;
                    if (maxX - minX >= MaxRoomCells || maxY - minY >= MaxRoomCells)
                    {
                        open = true;
                        break;
                    }

                    _queue[n++] = nw;
                }

                if (open)
                {
                    break;
                }
            }

            if (open)
            {
                return; // ячейки этой заливки — снаружи (метка 0); Visited не даст начать с них снова
            }

            byte label = lit ? LitRoom : Room;
            for (int i = 0; i < n; i++)
            {
                int w = _queue[i];
                int x = w % Side - N, y = w / Side - N;
                if ((uint)x < N && (uint)y < N)
                {
                    labels[y * N + x] = label;
                }
            }
        }
    }
}
