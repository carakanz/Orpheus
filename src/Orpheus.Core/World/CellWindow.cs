using System;
using Orpheus.Core.Geometry;

namespace Orpheus.Core.World
{
    /// <summary>
    /// Окно 3 × 3 чанка на сетке ячеек 0,5 тайла (C-017): что в каждой ячейке мешает — для комнат (<see cref="RoomIndex"/>)
    /// и видимости источников света (<see cref="LightSight"/>). Окно 192 × 192 ячейки, центральный чанк — ячейки 64…127.
    /// Целые числа, порядок обхода фиксирован — детерминированно (комнаты читает ядро).
    /// </summary>
    public sealed class CellWindow
    {
        /// <summary>Сторона окна в ячейках (3 чанка × 64).</summary>
        public const int Side = 3 * CellsPerChunk;

        /// <summary>Ячеек на сторону чанка.</summary>
        public const int CellsPerChunk = Torus.ChunkSize * 2;

        /// <summary>Ячейка — стена или дверь (любая: открытая дверь комнату не размыкает).</summary>
        public const byte RoomWall = 1;

        /// <summary>Ячейка не пропускает свет: стена, закрытая дверь, скала (обрыв).</summary>
        public const byte LightWall = 2;

        /// <summary>За краем плоской карты или чанка нет (ещё не пришёл) — «снаружи».</summary>
        public const byte Void = 4;

        /// <summary>Флаги ячеек окна: индекс wy × Side + wx.</summary>
        public readonly byte[] Cells = new byte[Side * Side];

        /// <summary>Тайлы-источники света окна: индекс (wy / 2) × (Side / 2) + wx / 2; 0 — нет, иначе id объекта.</summary>
        public readonly ushort[] Sources = new ushort[(Side / 2) * (Side / 2)];

        /// <summary>Есть ли в окне ячейки-стены для комнат / для света, источники.</summary>
        public bool AnyRoomWall { get; private set; }

        public bool AnyLightWall { get; private set; }

        public int SourceCount { get; private set; }

        /// <summary>Левый верхний тайл окна (мир, без заворота).</summary>
        public TileCoord Corner { get; private set; }

        /// <summary>Заполнить окно вокруг чанка <paramref name="center"/>.</summary>
        public void Load(WorldModel world, ChunkCoord center)
        {
            MapShape map = world.Shape;
            Array.Clear(Cells, 0, Cells.Length);
            Array.Clear(Sources, 0, Sources.Length);
            AnyRoomWall = false;
            AnyLightWall = false;
            SourceCount = 0;
            TileCoord cc = Torus.CornerOf(center);
            Corner = new TileCoord(cc.X - Torus.ChunkSize, cc.Y - Torus.ChunkSize);
            const int half = Side / 2;
            for (int dy = 0; dy < 3; dy++)
            {
                for (int dx = 0; dx < 3; dx++)
                {
                    var c = new ChunkCoord(center.X + dx - 1, center.Y + dy - 1);
                    bool inside = map.Wraps || ((uint)c.X < (uint)map.ChunksX && (uint)c.Y < (uint)map.ChunksY);
                    Chunk? chunk = inside ? world.FindChunk(Torus.Wrap(map, c)) : null;
                    TileCoord corner = Torus.CornerOf(c);
                    for (int ly = 0; ly < Torus.ChunkSize; ly++)
                    {
                        for (int lx = 0; lx < Torus.ChunkSize; lx++)
                        {
                            int tx = dx * Torus.ChunkSize + lx, ty = dy * Torus.ChunkSize + ly;
                            int w0 = (ty * 2) * Side + tx * 2;
                            bool off = chunk == null ||
                                (!map.Wraps && ((uint)(corner.X + lx) >= (uint)map.Width || (uint)(corner.Y + ly) >= (uint)map.Height));
                            if (off)
                            {
                                Cells[w0] = Cells[w0 + 1] = Cells[w0 + Side] = Cells[w0 + Side + 1] = Void;
                                continue;
                            }

                            int index = Chunk.IndexOf(lx, ly);
                            ushort obj = chunk!.GetObject(index);
                            if (obj == ObjectId.Empty)
                            {
                                continue;
                            }

                            ushort id = ObjectId.IdOf(obj);
                            if (id == ObjectId.Torch || id == ObjectId.Campfire)
                            {
                                Sources[ty * half + tx] = id;
                                SourceCount++;
                                continue;
                            }

                            if (id == ObjectId.Cliff)
                            {
                                Cells[w0] = Cells[w0 + 1] = Cells[w0 + Side] = Cells[w0 + Side + 1] = LightWall;
                                AnyLightWall = true;
                                continue;
                            }

                            if (id != ObjectId.StoneWall && id != ObjectId.WoodWall && id != ObjectId.Door)
                            {
                                continue;
                            }

                            bool has = chunk.TryGetBuild(index, out BuildRecord rec);
                            byte mask = BuildCells.MaskOf(obj, has, rec);
                            byte flags = id == ObjectId.Door && has && rec.IsDoorOpen ? RoomWall : (byte)(RoomWall | LightWall);
                            for (int q = 0; q < 4; q++)
                            {
                                if ((mask & (1 << q)) != 0)
                                {
                                    Cells[w0 + (q >> 1) * Side + (q & 1)] = flags;
                                }
                            }

                            AnyRoomWall = true;
                            AnyLightWall |= (flags & LightWall) != 0;
                        }
                    }
                }
            }
        }

        /// <summary>Штамп окна 3 × 3: ревизии чанков и их место (как у <see cref="LightIndex"/>); правка любого тайла окна меняет его.</summary>
        public static long StampOf(WorldModel world, ChunkCoord center)
        {
            MapShape map = world.Shape;
            long h = -7012172313846466560L;
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    var c = new ChunkCoord(center.X + dx, center.Y + dy);
                    bool inside = map.Wraps || ((uint)c.X < (uint)map.ChunksX && (uint)c.Y < (uint)map.ChunksY);
                    Chunk? chunk = inside ? world.FindChunk(Torus.Wrap(map, c)) : null;
                    h = (h ^ ((chunk == null ? -1L : chunk.Revision) * 1000003 + c.X)) * 1099511628211L;
                    h = (h ^ c.Y) * 1099511628211L;
                }
            }

            return h;
        }
    }
}
