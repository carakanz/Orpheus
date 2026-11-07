using System;
using Orpheus.Core.Geometry;
using Orpheus.Core.Rng;
using Orpheus.Core.World;

namespace Orpheus.WorldGen
{
    /// <summary>
    /// Генератор-заглушка (docs/08, 3.0; docs/01, 2 — только этап полей): тор W × H, два поля — луга и скалы,
    /// деревья и скалы как объекты, чистая стартовая зона в центре. Только целые числа; случайность — хэш
    /// SplitMix64 от зерна, номера этапа и координат, без потоков с состоянием, поэтому порядок обхода
    /// на результат не влияет.
    /// <para>Проходы (один поток):</para>
    /// <list type="number">
    /// <item>шум скал по строкам → массив ushort и гистограмма;</item>
    /// <item>порог по гистограмме: доля скал — <see cref="RockSharePercent"/> % (доля, а не порог шума, как в `01` 2);</item>
    /// <item>пол тайла и «кандидат в объект»: дерево на лугу по шуму плотности, скала на скалах и изредка на лугу;</item>
    /// <item>прореживание: кандидат остаётся, если его приоритет (хэш тайла) больше, чем у всех кандидатов
    /// в 8 соседях по тору, — два объекта никогда не стоят рядом (расстояние ≥ 1 тайл), через шов тоже;</item>
    /// <item>запись в чанки как содержимое генерации (ревизия 0).</item>
    /// </list>
    /// </summary>
    public sealed class StubGenerator : IWorldGenerator
    {
        /// <summary>Версия генератора-заглушки (растёт при любом изменении результата, docs/07, 3.4).</summary>
        public const int GeneratorVersion = 1;

        // ---------- Числа (все — прикидка, подбираются по картинке gen_preview) ----------

        /// <summary>Доля поля «скалы», % площади мира *(прикидка)*. Остальное — луга.</summary>
        public const int RockSharePercent = 25;

        /// <summary>Стартовая зона: квадрат 32 × 32 тайла вокруг центра (W/2, H/2) — без скал и объектов.</summary>
        public const int StartZoneSize = 32;

        /// <summary>Шум скал: ячейка базовой октавы и число октав (128, 64, 32, 16).</summary>
        public const int RockCell = 128;
        public const int RockOctaves = 4;

        /// <summary>Шум плотности деревьев: ячейка и октавы (64, 32, 16).</summary>
        public const int DensityCell = 64;
        public const int DensityOctaves = 3;

        /// <summary>Шансы кандидата — из 65 536. Дерево на лугу: база + наклон по плотности выше порога.</summary>
        public const int TreeChanceBase = 700;
        public const int TreeDensityFrom = 30000;
        public const int TreeChanceSlope = 2;
        public const int TreeChanceMax = 40000;

        /// <summary>Скала-объект на лугу и на поле скал — из 65 536.</summary>
        public const int MeadowRockChance = 350;
        public const int RockFieldChance = 36000;

        // ---------- Номера этапов и полей для зёрен (`01` 2: seed_этапа = hash(зерно, номер этапа)) ----------

        /// <summary>Этап 2 «Поля».</summary>
        public const int StageFields = 2;

        /// <summary>Этап 11 «Чанки» (объекты на тайлах).</summary>
        public const int StageTiles = 11;

        public const int FieldRock = 1;
        public const int FieldDensity = 2;

        private const byte CandNone = 0;
        private const byte CandTree = 1;
        private const byte CandRock = 2;

        /// <inheritdoc />
        public int Version => GeneratorVersion;

        /// <summary>Шум поля скал мира (для тестов бесшовности и картинки).</summary>
        public static TorusNoise CreateRockNoise(MapShape shape, ulong seed) =>
            new TorusNoise(SplitMix64.Hash(StageFields, FieldRock, seed), shape.Width, shape.Height, RockCell, RockOctaves);

        /// <summary>Шум плотности деревьев.</summary>
        public static TorusNoise CreateDensityNoise(MapShape shape, ulong seed) =>
            new TorusNoise(SplitMix64.Hash(StageFields, FieldDensity, seed), shape.Width, shape.Height, DensityCell, DensityOctaves);

        /// <summary>Тайл в стартовой зоне (координаты — уже свёрнутые, 0…W−1).</summary>
        public static bool InStartZone(in MapShape shape, int x, int y)
        {
            int x0 = (shape.Width >> 1) - (StartZoneSize >> 1);
            int y0 = (shape.Height >> 1) - (StartZoneSize >> 1);
            return (uint)(x - x0) < StartZoneSize && (uint)(y - y0) < StartZoneSize;
        }

        /// <inheritdoc />
        public WorldModel Generate(MapShape shape, ulong seed) => Generate(shape, seed, out _);

        /// <summary>Мир и сводка генерации.</summary>
        public WorldModel Generate(MapShape shape, ulong seed, out GenStats stats)
        {
            if (!shape.Wraps)
            {
                throw new ArgumentException("Генератор-заглушка строит только мир-тор.", nameof(shape));
            }

            int w = shape.Width;
            int h = shape.Height;
            int n = w * h;

            // 1. Шум скал и гистограмма.
            TorusNoise rockNoise = CreateRockNoise(shape, seed);
            var rock = new ushort[n];
            var hist = new int[rockNoise.MaxValue + 1];
            var row = new int[w];
            var scratch = new int[Math.Max(rockNoise.ScratchLength, CreateDensityNoise(shape, seed).ScratchLength)];
            for (int y = 0; y < h; y++)
            {
                rockNoise.FillRow(y, row, scratch);
                int at = y * w;
                for (int x = 0; x < w; x++)
                {
                    int v = row[x];
                    rock[at + x] = (ushort)v;
                    hist[v]++;
                }
            }

            // 2. Порог: сверху вниз, пока не набрана доля.
            long target = (long)n * RockSharePercent / 100;
            long acc = 0;
            int threshold = hist.Length;
            while (threshold > 0 && acc < target)
            {
                threshold--;
                acc += hist[threshold];
            }

            // 3. Пол и кандидаты.
            TorusNoise density = CreateDensityNoise(shape, seed);
            ulong tileSeed = SplitMix64.Hash(StageTiles, 0, seed);
            var floor = new byte[n];
            var cand = new byte[n];
            int densityMax = density.MaxValue;
            int rockTiles = 0;
            for (int y = 0; y < h; y++)
            {
                density.FillRow(y, row, scratch);
                int at = y * w;
                for (int x = 0; x < w; x++)
                {
                    int i = at + x;
                    if (InStartZone(shape, x, y))
                    {
                        floor[i] = FloorId.Grass;
                        continue;
                    }

                    ulong hash = SplitMix64.Hash(x, y, tileSeed);
                    int lo = (int)(hash & 0xFFFF);
                    if (rock[i] >= threshold)
                    {
                        floor[i] = FloorId.Stone;
                        rockTiles++;
                        if (lo < RockFieldChance)
                        {
                            cand[i] = CandRock;
                        }
                    }
                    else
                    {
                        floor[i] = FloorId.Grass;
                        int d = (int)(((long)row[x] << 16) / (densityMax + 1));
                        int treeChance = TreeChanceBase + Clamp((d - TreeDensityFrom) * TreeChanceSlope, 0, TreeChanceMax);
                        if (lo < treeChance)
                        {
                            cand[i] = CandTree;
                        }
                        else if ((int)((hash >> 16) & 0xFFFF) < MeadowRockChance)
                        {
                            cand[i] = CandRock;
                        }
                    }
                }
            }

            // 4–5. Прореживание и запись в чанки.
            var model = new WorldModel(shape, seed, GeneratorVersion);
            model.CreateAllChunks();
            int trees = 0;
            int rocks = 0;
            int size = Torus.ChunkSize;
            for (int cy = 0; cy < shape.ChunksY; cy++)
            {
                for (int cx = 0; cx < shape.ChunksX; cx++)
                {
                    Chunk chunk = model.FindChunk(new ChunkCoord(cx, cy))!;
                    for (int ly = 0; ly < size; ly++)
                    {
                        int y = (cy << Torus.ChunkBits) + ly;
                        for (int lx = 0; lx < size; lx++)
                        {
                            int x = (cx << Torus.ChunkBits) + lx;
                            int i = y * w + x;
                            ushort obj = ObjectId.Empty;
                            byte c = cand[i];
                            if (c != CandNone && WinsNeighbours(cand, w, h, x, y, tileSeed))
                            {
                                if (c == CandTree)
                                {
                                    obj = ObjectId.Tree;
                                    trees++;
                                }
                                else
                                {
                                    obj = ObjectId.Rock;
                                    rocks++;
                                }
                            }

                            chunk.SetBaseTile(Chunk.IndexOf(lx, ly), floor[i], obj);
                        }
                    }
                }
            }

            stats = new GenStats(rockTiles, trees, rocks, threshold, rockNoise.MaxValue);
            return model;
        }

        // Приоритет кандидата — старшие 32 бита хэша тайла; равенство — по индексу тайла (полный порядок).
        private static bool WinsNeighbours(byte[] cand, int w, int h, int x, int y, ulong tileSeed)
        {
            uint mine = Priority(x, y, tileSeed);
            int myIndex = y * w + x;
            for (int dy = -1; dy <= 1; dy++)
            {
                int ny = (y + dy) & (h - 1);
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0)
                    {
                        continue;
                    }

                    int nx = (x + dx) & (w - 1);
                    int ni = ny * w + nx;
                    if (cand[ni] == CandNone)
                    {
                        continue;
                    }

                    uint other = Priority(nx, ny, tileSeed);
                    if (other > mine || (other == mine && ni < myIndex))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static uint Priority(int x, int y, ulong tileSeed) => (uint)(SplitMix64.Hash(x, y, tileSeed) >> 32);

        private static int Clamp(int v, int min, int max) => v < min ? min : (v > max ? max : v);
    }

    /// <summary>Сводка генерации: для тестов, картинки и лога.</summary>
    public readonly struct GenStats
    {
        public GenStats(int rockTiles, int trees, int rocks, int rockThreshold, int rockNoiseMax)
        {
            RockTiles = rockTiles;
            Trees = trees;
            Rocks = rocks;
            RockThreshold = rockThreshold;
            RockNoiseMax = rockNoiseMax;
        }

        /// <summary>Тайлов поля «скалы».</summary>
        public int RockTiles { get; }

        /// <summary>Деревьев-объектов.</summary>
        public int Trees { get; }

        /// <summary>Скал-объектов.</summary>
        public int Rocks { get; }

        /// <summary>Порог шума скал (значение ≥ порога — скалы).</summary>
        public int RockThreshold { get; }

        /// <summary>Наибольшее значение шума скал.</summary>
        public int RockNoiseMax { get; }
    }
}
