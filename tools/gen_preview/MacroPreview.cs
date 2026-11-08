using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Orpheus.WorldGen;

namespace Orpheus.Tools.GenPreview
{
    /// <summary>
    /// Макрокарта и поля генератора v2, этапы 1–2 (карточка 1-015, шаг 5): PNG полей, бинарный дамп для сверки
    /// с моделью Python (<c>tools/gen_preview/compare_fields.py</c>), время и хэш макрокарты.
    /// <para>
    /// Запуск: <c>dotnet run -c Release --project tools/gen_preview -- --macro [--size 1024] [--seeds 1-30] [--set slice|full]
    /// [--dump] [--png N] [--out папка]</c>. Папка по умолчанию — <c>build/gen_preview/macro</c> (не в git).
    /// </para>
    /// <para>
    /// Файлы: <c>fields_&lt;W&gt;_&lt;зерно&gt;.bin</c> (с <c>--dump</c>) — заголовок <c>OFLD</c>, W, зерно, набор, M; поля h, m, c, край
    /// (u16, M·M); смещения осей (i32, M ×2); d, dH, dC, dA (i32, M·M); байт зоны (M·M); биом этапа 3 (M·M, номера <see cref="Biome"/>; с 1-016). PNG для первых N зёрен:
    /// <c>h_…</c>, <c>m_…</c>, <c>c_…</c>, <c>edge_…</c>, <c>zones_…</c>, <c>biome_…</c> (цвета модели) и те же со сдвигом на W/2 (<c>…_seam.png</c>) — шов крестом посередине.
    /// </para>
    /// </summary>
    internal static class MacroPreview
    {
        public static int Run(string[] args, string root)
        {
            int size = 1024;
            int seedFrom = 1;
            int seedTo = 30;
            BiomeSet set = BiomeSet.Slice;
            bool dump = false;
            int pngCount = 1;
            string outDir = Path.Combine(root, "build", "gen_preview", "macro");
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--macro": break;
                    case "--size": size = int.Parse(args[++i]); break;
                    case "--seeds":
                        string[] r = args[++i].Split('-');
                        seedFrom = int.Parse(r[0]);
                        seedTo = r.Length > 1 ? int.Parse(r[1]) : seedFrom;
                        break;
                    case "--set": set = args[++i] == "full" ? BiomeSet.Full : BiomeSet.Slice; break;
                    case "--dump": dump = true; break;
                    case "--png": pngCount = int.Parse(args[++i]); break;
                    case "--out": outDir = args[++i]; break;
                    default:
                        Console.Error.WriteLine($"Неизвестный параметр {args[i]}. См. комментарий в tools/gen_preview/MacroPreview.cs.");
                        return 2;
                }
            }

            Directory.CreateDirectory(outDir);
            var times = new List<double>();
            double first = 0;
            for (int s = seedFrom; s <= seedTo; s++)
            {
                var sw = Stopwatch.StartNew();
                MacroWorld world = MacroGenerator.Generate(size, (ulong)s, set);
                double ms = sw.Elapsed.TotalMilliseconds;
                if (s == seedFrom)
                {
                    first = ms;
                }
                else
                {
                    times.Add(ms);
                }

                if (dump)
                {
                    File.WriteAllBytes(Path.Combine(outDir, $"fields_{size}_{s}.bin"), Dump(world));
                }

                if (s - seedFrom < pngCount)
                {
                    WriteImages(world, outDir, size, s);
                }

                if (s == seedFrom)
                {
                    Console.WriteLine($"зерно {s}: хэш макрокарты {world.Map.HashHex()}, полей {world.Fields.HashHex()}");
                    int[] cnt = BiomeStage.CountCells(world.Map);
                    int n = world.Map.Size * world.Map.Size;
                    var parts = new List<string>();
                    for (int b = 0; b <= Biome.Count; b++)
                    {
                        if (cnt[b] > 0)
                        {
                            parts.Add($"{(b == Biome.Count ? "нет" : b.ToString())}: {100.0 * cnt[b] / n:F2} %");
                        }
                    }

                    Console.WriteLine($"зерно {s}: доли биомов {string.Join(", ", parts)}");
                }
            }

            times.Sort();
            string rest = times.Count == 0 ? "" :
                $"; остальные {times.Count}: медиана {times[times.Count / 2]:F1} мс, худшее {times[times.Count - 1]:F1} мс";
            Console.WriteLine($"{size}² ({set}), этапы 1–3: первое зерно {first:F1} мс (с JIT){rest}");
            Console.WriteLine($"папка: {Path.GetFullPath(outDir)}");
            return 0;
        }

        private static byte[] Dump(MacroWorld world)
        {
            MacroMap map = world.Map;
            int m = map.Size;
            int n = m * m;
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write(new[] { (byte)'O', (byte)'F', (byte)'L', (byte)'D' });
            bw.Write(map.WorldSize);
            bw.Write(map.Seed);
            bw.Write((int)map.Set);
            bw.Write(m);
            foreach (ushort[] f in new[] { world.Fields.H, world.Fields.M, world.Fields.C, world.Fields.Edge })
            {
                for (int i = 0; i < n; i++)
                {
                    bw.Write(f[i]);
                }
            }

            foreach (int[] a in new[] { world.Zones.ShiftHot, world.Zones.ShiftCold })
            {
                for (int i = 0; i < m; i++)
                {
                    bw.Write(a[i]);
                }
            }

            foreach (int[] a in new[] { world.Zones.D, world.Zones.DHot, world.Zones.DCold, world.Zones.DGate })
            {
                for (int i = 0; i < n; i++)
                {
                    bw.Write(a[i]);
                }
            }

            bw.Write(map.Zone);
            bw.Write(map.Biome);
            bw.Flush();
            return ms.ToArray();
        }

        private static void WriteImages(MacroWorld world, string dir, int size, int seed)
        {
            int m = world.Map.Size;
            int scale = Math.Max(1, 512 / m);
            void Both(string name, Func<int, byte[]> color)
            {
                Program.WritePng(Path.Combine(dir, $"{name}_{size}_{seed}.png"), Render(m, scale, 0, color), m * scale, m * scale);
                Program.WritePng(Path.Combine(dir, $"{name}_{size}_{seed}_seam.png"), Render(m, scale, m / 2, color), m * scale, m * scale);
            }

            Both("h", i => Gray(world.Fields.H[i]));
            Both("m", i => Gray(world.Fields.M[i]));
            Both("c", i => Gray(world.Fields.C[i]));
            Both("edge", i => Gray(world.Fields.Edge[i]));
            Both("zones", i => ZoneColor(world.Map.Zone[i]));
            Both("biome", i => BiomeColor(world.Map.Biome[i]));
        }

        // Цвета биомов — как COL в worldgen_model.py; не размечено — пурпурный.
        private static readonly byte[][] BiomeColors =
        {
            new byte[] { 150, 200, 90 }, new byte[] { 40, 110, 50 }, new byte[] { 20, 150, 90 }, new byte[] { 110, 100, 60 },
            new byte[] { 130, 130, 130 }, new byte[] { 60, 50, 70 }, new byte[] { 230, 205, 120 }, new byte[] { 235, 235, 235 },
            new byte[] { 200, 215, 225 }, new byte[] { 150, 200, 245 }, new byte[] { 210, 60, 20 }, new byte[] { 60, 110, 200 },
            new byte[] { 90, 90, 90 },
        };

        private static byte[] BiomeColor(byte b) => b < BiomeColors.Length ? BiomeColors[b] : new byte[] { 255, 0, 255 };

        private static byte[] Gray(ushort v)
        {
            byte g = (byte)(v >> 8);
            return new[] { g, g, g };
        }

        // Кольца — оттенки серого; жаркая зона — красная, холодная — синяя, Перекрёсток — пурпурный, Предвратие — жёлтое.
        private static byte[] ZoneColor(byte z)
        {
            int ring = z & MacroMap.RingMask;
            byte g = (byte)(230 - 50 * ring);
            byte r = g, gg = g, b = g;
            if ((z & MacroMap.ZoneHot) != 0)
            {
                r = 230;
                gg = (byte)(gg / 2);
                b = (byte)(b / 2);
            }

            if ((z & MacroMap.ZoneCold) != 0)
            {
                b = 230;
                r = (byte)(r / 2);
            }

            if ((z & MacroMap.ZoneCrossroads) != 0)
            {
                r = 200;
                gg = 40;
                b = 200;
            }

            if ((z & MacroMap.ZonePregate) != 0)
            {
                r = 255;
                gg = 220;
                b = 0;
            }

            return new[] { r, gg, b };
        }

        private static byte[] Render(int m, int scale, int shift, Func<int, byte[]> color)
        {
            int px = m * scale;
            var rgb = new byte[px * px * 3];
            for (int y = 0; y < px; y++)
            {
                int cy = ((y / scale) + shift) & (m - 1);
                for (int x = 0; x < px; x++)
                {
                    int cx = ((x / scale) + shift) & (m - 1);
                    byte[] c = color(cy * m + cx);
                    int at = (y * px + x) * 3;
                    rgb[at] = c[0];
                    rgb[at + 1] = c[1];
                    rgb[at + 2] = c[2];
                }
            }

            return rgb;
        }
    }
}
