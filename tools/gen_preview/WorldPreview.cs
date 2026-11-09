using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Orpheus.Core.Geometry;
using Orpheus.Core.Sim;
using Orpheus.Core.World;
using Orpheus.WorldGen;

namespace Orpheus.Tools.GenPreview
{
    /// <summary>
    /// Мир генератора v1 на тайлах (карточка 1-017): замеры, числа приёмки и картинки.
    /// <para>
    /// Запуск: <c>dotnet run -c Release --project tools/gen_preview -- --world [--size 1024] [--seeds 1-30] [--png 3]
    /// [--png-dir work/screens] [--dump] [--out папка]</c>.
    /// </para>
    /// <para>
    /// В stdout — строка CSV на зерно (время этапов, доли биомов тайлов, плотности, медь в 600, связность, хэши) и сводка.
    /// <c>--dump</c> — макрокарта биомов после этапа 7 (<c>world_&lt;W&gt;_&lt;зерно&gt;.bin</c>: байт на ячейку) для
    /// <c>compare_world.py</c>. <c>--png N</c> — картинки первых N зёрен <c>1-017_&lt;W&gt;_&lt;зерно&gt;.png</c>: слева весь мир
    /// (2 × 2 тайла в пиксель, преграда побеждает), справа — 256 × 256 тайлов у Очага в масштабе 2.
    /// </para>
    /// </summary>
    internal static class WorldPreview
    {
        public static int Run(string[] args, string root)
        {
            int size = 1024;
            int seedFrom = 1;
            int seedTo = 30;
            int pngCount = 0;
            bool dump = false;
            string outDir = Path.Combine(root, "build", "gen_preview", "world");
            string pngDir = Path.Combine(root, "work", "screens");
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--world": break;
                    case "--size": size = int.Parse(args[++i]); break;
                    case "--seeds":
                        string[] r = args[++i].Split('-');
                        seedFrom = int.Parse(r[0]);
                        seedTo = r.Length > 1 ? int.Parse(r[1]) : seedFrom;
                        break;
                    case "--png": pngCount = int.Parse(args[++i]); break;
                    case "--png-dir": pngDir = args[++i]; break;
                    case "--dump": dump = true; break;
                    case "--out": outDir = args[++i]; break;
                    default:
                        Console.Error.WriteLine($"Неизвестный параметр {args[i]}. См. комментарий в tools/gen_preview/WorldPreview.cs.");
                        return 2;
                }
            }

            Console.OutputEncoding = Encoding.UTF8;
            Directory.CreateDirectory(outDir);
            Directory.CreateDirectory(pngDir);
            var ci = CultureInfo.InvariantCulture;
            Console.WriteLine("seed,ms_macro,ms_tiles,ms_model,ms_total,meadow_pct,forest_pct,mount_pct,cave_pct,meadow_trees_k,forest_trees_k," +
                              "mount_rock_pct,cave_rock_pct,granite_of_rock_pct,copper600,copper_stamp,forest_stamp,reach_pct,joined,carved,macro_hash,gen_hash");
            var totals = new List<double>();
            var meadowTrees = new List<double>();
            var copper = new List<int>();
            var reach = new List<double>();
            for (int s = seedFrom; s <= seedTo; s++)
            {
                var sw = Stopwatch.StartNew();
                MacroWorld macro = MacroGenerator.Generate(size, (ulong)s, BiomeSet.Slice);
                double msMacro = sw.Elapsed.TotalMilliseconds;
                TileWorld t = GeneratorV1.BuildTiles(macro);
                double msTiles = sw.Elapsed.TotalMilliseconds - msMacro;
                WorldModel model = GeneratorV1.ToModel(t);
                double msTotal = sw.Elapsed.TotalMilliseconds;
                double msModel = msTotal - msMacro - msTiles;

                var st = WorldStats.Of(t);
                double reachShare = ConnectStage.ReachableOpenShare(t, out _, out _);
                int cu = HearthStage.CountCopper(t);
                if (s > seedFrom || seedFrom == seedTo)
                {
                    totals.Add(msTotal);
                }

                meadowTrees.Add(st.PerThousand(Biome.Meadow, ObjectId.Tree));
                copper.Add(cu);
                reach.Add(reachShare);
                string genHash = StateHash.Generation(model).ToString("x16", ci);
                Console.WriteLine(string.Join(",",
                    s.ToString(ci), msMacro.ToString("F1", ci), msTiles.ToString("F1", ci), msModel.ToString("F1", ci), msTotal.ToString("F1", ci),
                    st.Share(Biome.Meadow).ToString("F2", ci), st.Share(Biome.Forest).ToString("F2", ci),
                    st.Share(Biome.Mountain).ToString("F2", ci), st.Share(Biome.Cave).ToString("F2", ci),
                    st.PerThousand(Biome.Meadow, ObjectId.Tree).ToString("F2", ci), st.PerThousand(Biome.Forest, ObjectId.Tree).ToString("F2", ci),
                    st.MassPercent(Biome.Mountain).ToString("F2", ci), st.MassPercent(Biome.Cave).ToString("F2", ci),
                    st.GraniteOfMass().ToString("F2", ci), cu.ToString(ci), t.CopperStamp.HasValue ? "1" : "0", t.ForestStamped ? "1" : "0",
                    (100 * reachShare).ToString("F3", ci), t.ComponentsJoined.ToString(ci), t.TilesCarved.ToString(ci),
                    macro.Map.HashHex().Substring(0, 16), genHash));
                if (dump)
                {
                    File.WriteAllBytes(Path.Combine(outDir, $"world_{size}_{s}.bin"), macro.Map.Biome);
                }

                if (s - seedFrom < pngCount)
                {
                    string p = Path.Combine(pngDir, $"1-017_{size}_{s}.png");
                    WriteImage(t, p);
                    Console.Error.WriteLine($"картинка: {Path.GetFullPath(p)}");
                }
            }

            totals.Sort();
            meadowTrees.Sort();
            copper.Sort();
            reach.Sort();
            Console.Error.WriteLine(
                $"{size}², зёрен {seedTo - seedFrom + 1}: время мира (без первого, с JIT) медиана {Med(totals):F0} мс, худшее {totals[^1]:F0} мс; " +
                $"деревья лугов на 1000 — {meadowTrees[0]:F2}…{meadowTrees[^1]:F2}; медь в 600 — мин {copper[0]}; " +
                $"достижимо лугов и леса — мин {100 * reach[0]:F3} %");
            return 0;
        }

        private static double Med(List<double> v) => v.Count == 0 ? 0 : v[v.Count / 2];

        // ---------- Картинка ----------

        private static byte[] Color(byte floor, ushort obj, byte biome)
        {
            switch (obj)
            {
                case ObjectId.Tree: return biome == Biome.Forest ? new byte[] { 0x14, 0x3C, 0x16 } : new byte[] { 0x1E, 0x5A, 0x1C };
                case ObjectId.Bush: return new byte[] { 0x8E, 0x3A, 0x6A };
                case ObjectId.Rock: return new byte[] { 0x50, 0x4C, 0x48 };
                case ObjectId.Granite: return new byte[] { 0x2A, 0x2A, 0x36 };
                case ObjectId.Ore: return new byte[] { 0xE0, 0x7A, 0x20 };
                case ObjectId.Empty: break;
                default: return new byte[] { 0xFF, 0x00, 0xFF };
            }

            if (floor == FloorId.ForestFloor)
            {
                return new byte[] { 0x4E, 0x7A, 0x38 };
            }

            if (floor == FloorId.Stone)
            {
                return biome == Biome.Cave ? new byte[] { 0x78, 0x70, 0x84 } : new byte[] { 0x9C, 0x98, 0x90 };
            }

            return new byte[] { 0x8C, 0xC0, 0x5A };
        }

        private static void WriteImage(TileWorld t, string path)
        {
            int w = t.WorldSize;
            int half = 512;
            int pad = 8;
            int width = 2 * half + pad;
            var rgb = new byte[width * half * 3];
            for (int i = 0; i < rgb.Length; i++)
            {
                rgb[i] = 255;
            }

            // Слева — весь мир в 512 × 512: блок k × k тайлов, преграда побеждает (иначе деревья лугов не видны).
            int k = w / half;
            for (int py = 0; py < half; py++)
            {
                for (int px = 0; px < half; px++)
                {
                    int pick = (py * k * w) + (px * k);
                    for (int dy = 0; dy < k; dy++)
                    {
                        for (int dx = 0; dx < k; dx++)
                        {
                            int j = ((py * k) + dy) * w + (px * k) + dx;
                            if (t.Objects[j] == ObjectId.Ore || (t.Objects[j] != ObjectId.Empty && t.Objects[pick] != ObjectId.Ore && t.Objects[pick] == ObjectId.Empty))
                            {
                                pick = j;
                            }
                        }
                    }

                    Put(rgb, width, px, py, Color(t.Floor[pick], t.Objects[pick], t.Biome[pick]));
                }
            }

            // Кольца 150 / 200 / 600 от Очага — чёрные точки на левой половине.
            int c = w / 2;
            foreach (int r in new[] { HearthStage.MeadowRadius, HearthStage.ForestWithin, HearthStage.CopperWithin })
            {
                for (int a = 0; a < 2000; a++)
                {
                    double ang = a * 2 * Math.PI / 2000;
                    int x = (int)Math.Round((c + r * Math.Cos(ang)) / k);
                    int y = (int)Math.Round((c + r * Math.Sin(ang)) / k);
                    if (x >= 0 && x < half && y >= 0 && y < half)
                    {
                        Put(rgb, width, x, y, new byte[] { 0, 0, 0 });
                    }
                }
            }

            // Справа — 256 × 256 тайлов вокруг Очага, тайл = 2 × 2 пикселя.
            int crop = half / 2;
            for (int py = 0; py < half; py++)
            {
                for (int px = 0; px < half; px++)
                {
                    int x = (c - (crop / 2) + (px / 2)) & (w - 1);
                    int y = (c - (crop / 2) + (py / 2)) & (w - 1);
                    int j = y * w + x;
                    byte[] col = Color(t.Floor[j], t.Objects[j], t.Biome[j]);
                    if (TileWorld.InStartZone(w, x, y) && (!TileWorld.InStartZone(w, x - 1, y) || !TileWorld.InStartZone(w, x + 1, y)
                        || !TileWorld.InStartZone(w, x, y - 1) || !TileWorld.InStartZone(w, x, y + 1)))
                    {
                        col = new byte[] { 0xF2, 0xD0, 0x2A };
                    }

                    Put(rgb, width, half + pad + px, py, col);
                }
            }

            Program.WritePng(path, rgb, width, half);
        }

        private static void Put(byte[] rgb, int width, int x, int y, byte[] c)
        {
            int at = (y * width + x) * 3;
            rgb[at] = c[0];
            rgb[at + 1] = c[1];
            rgb[at + 2] = c[2];
        }
    }

    /// <summary>Сводка тайлов мира v1: тайлы биомов, объекты по биомам.</summary>
    internal sealed class WorldStats
    {
        private readonly long[] _tiles = new long[Biome.Count];
        private readonly long[,] _objects = new long[Biome.Count, ObjectId.MaxKnownId + 1];
        private long _n;

        public static WorldStats Of(TileWorld t)
        {
            var s = new WorldStats();
            for (int i = 0; i < t.Biome.Length; i++)
            {
                byte b = t.Biome[i];
                s._tiles[b]++;
                ushort o = t.Objects[i];
                if (o <= ObjectId.MaxKnownId)
                {
                    s._objects[b, o]++;
                }
            }

            s._n = t.Biome.Length;
            return s;
        }

        public double Share(byte biome) => 100.0 * _tiles[biome] / _n;

        public double PerThousand(byte biome, ushort obj) => _tiles[biome] == 0 ? 0 : 1000.0 * _objects[biome, obj] / _tiles[biome];

        public double MassPercent(byte biome) =>
            _tiles[biome] == 0 ? 0 : 100.0 * (_objects[biome, ObjectId.Rock] + _objects[biome, ObjectId.Granite]) / _tiles[biome];

        public double GraniteOfMass()
        {
            long g = _objects[Biome.Mountain, ObjectId.Granite] + _objects[Biome.Cave, ObjectId.Granite];
            long r = _objects[Biome.Mountain, ObjectId.Rock] + _objects[Biome.Cave, ObjectId.Rock] + g;
            return r == 0 ? 0 : 100.0 * g / r;
        }
    }
}
