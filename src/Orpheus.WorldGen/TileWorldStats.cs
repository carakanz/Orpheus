using Orpheus.Core.World;

namespace Orpheus.WorldGen
{
    /// <summary>Числа приёмки мира на тайлах (E1-2): считают тесты на 30 зёрнах и <c>tools/gen_preview</c> — одним кодом.</summary>
    public sealed class TileWorldStats
    {
        /// <summary>Радиус у Очага без воды и обрывов (план 1.2).</summary>
        public const int StartRadius = 150;

        public long Tiles;
        public long Water;
        public long Sand;
        public long Cliff;

        /// <summary>Тайлов обрыва, у которых среди 8 соседей не больше 4 обрывов (линия, а не масса).</summary>
        public long CliffThin;

        /// <summary>Тайлы биома без воды: луг, лес, горы, пещеры — и объекты на них.</summary>
        public long MeadowLand;
        public long MeadowTrees;
        public long MeadowDry;
        public long ForestLand;
        public long ForestTrees;
        public long MountainLand;
        public long MountainBoulders;
        public long CaveLand;
        public long CaveMass;

        /// <summary>Воды и обрывов в <see cref="StartRadius"/> от Очага (должно быть 0).</summary>
        public long WaterNearStart;
        public long CliffNearStart;

        /// <summary>Доля проходимых тайлов, достижимых от Очага без копания.</summary>
        public double Reach;

        public double WaterPercent => 100.0 * Water / Tiles;

        public double CliffPercent => 100.0 * Cliff / Tiles;

        public double CliffThinPercent => Cliff == 0 ? 100.0 : 100.0 * CliffThin / Cliff;

        public double ForestTreePercent => ForestLand == 0 ? 0 : 100.0 * ForestTrees / ForestLand;

        public double MeadowTreePercent => MeadowLand == 0 ? 0 : 100.0 * MeadowTrees / MeadowLand;

        public double MeadowDryPercent => MeadowLand == 0 ? 0 : 100.0 * MeadowDry / MeadowLand;

        public double MountainBoulderPercent => MountainLand == 0 ? 0 : 100.0 * MountainBoulders / MountainLand;

        public double CaveMassPercent => CaveLand == 0 ? 0 : 100.0 * CaveMass / CaveLand;

        public static TileWorldStats Of(TileWorld t)
        {
            int w = t.WorldSize;
            int mask = w - 1;
            int half = w >> 1;
            var s = new TileWorldStats { Tiles = (long)w * w, Reach = ConnectStage.ReachableShare(t) };
            long r2 = (long)StartRadius * StartRadius;
            for (int y = 0; y < w; y++)
            {
                long dy = IntMath.Wrap(y - half, w);
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w) + x;
                    long dx = IntMath.Wrap(x - half, w);
                    bool near = (dx * dx) + (dy * dy) <= r2;
                    byte floor = t.Floor[i];
                    ushort obj = t.Objects[i];
                    if (floor == FloorId.Water)
                    {
                        s.Water++;
                        s.WaterNearStart += near ? 1 : 0;
                        continue;
                    }

                    s.Sand += floor == FloorId.Sand ? 1 : 0;
                    if (obj == ObjectId.Cliff)
                    {
                        s.Cliff++;
                        s.CliffNearStart += near ? 1 : 0;
                        int around = 0;
                        for (int k = 0; k < 9; k++)
                        {
                            if (k != 4 && t.Objects[(((y + (k / 3) - 1) & mask) * w) + ((x + (k % 3) - 1) & mask)] == ObjectId.Cliff)
                            {
                                around++;
                            }
                        }

                        s.CliffThin += around <= 4 ? 1 : 0;
                    }

                    bool boulder = obj == ObjectId.Rock || obj == ObjectId.Granite;
                    switch (t.Biome[i])
                    {
                        case Biome.Meadow:
                            s.MeadowLand++;
                            s.MeadowTrees += obj == ObjectId.Tree ? 1 : 0;
                            s.MeadowDry += floor == FloorId.DryGrass ? 1 : 0;
                            break;
                        case Biome.Forest:
                            s.ForestLand++;
                            s.ForestTrees += obj == ObjectId.Tree ? 1 : 0;
                            break;
                        case Biome.Mountain:
                            s.MountainLand++;
                            s.MountainBoulders += boulder ? 1 : 0;
                            break;
                        case Biome.Cave:
                            s.CaveLand++;
                            s.CaveMass += boulder ? 1 : 0;
                            break;
                    }
                }
            }

            return s;
        }
    }
}
