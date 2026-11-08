using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using Orpheus.Core.Geometry;
using Orpheus.Core.World;
using Orpheus.WorldGen;

namespace Orpheus.Tools.GenPreview
{
    /// <summary>
    /// gen_preview — PNG карты по снимку генерации (карточка 0-007, шаг 7). Пиксель = тайл.
    /// <para>
    /// Запуск: <c>dotnet run -c Release --project tools/gen_preview -- [--size 1024] [--seed N] [--base файл] [--out папка]</c>.
    /// По умолчанию папка — <c>build/gen_preview</c> (не в git). Без <c>--base</c> мир генерируется, снимок
    /// <c>world_&lt;size&gt;_&lt;seed&gt;.base</c> пишется в папку вывода, картинка рисуется уже по прочитанному снимку.
    /// </para>
    /// <para>
    /// Файлы: <c>map_&lt;size&gt;_&lt;seed&gt;.png</c> — карта как есть; <c>…_seam.png</c> — та же карта, сдвинутая на W/2 по обеим
    /// осям: оба шва тора проходят крестом через середину — бесшовность видна глазом. Жёлтая рамка — стартовая зона.
    /// </para>
    /// <para>Ключ <c>--macro</c> — макрокарта и поля генератора v2 (карточка 1-015), см. <see cref="MacroPreview"/>.</para>
    /// <para>Ключ <c>--world</c> — мир генератора v1 на тайлах (карточка 1-017), см. <see cref="WorldPreview"/>.</para>
    /// </summary>
    internal static class Program
    {
        // Цвета: луга, скалы (пол), дерево, скала-объект, чужой объект, рамка стартовой зоны.
        private static readonly byte[] Grass = { 0x6A, 0x9E, 0x45 };
        private static readonly byte[] Stone = { 0x8C, 0x88, 0x80 };
        private static readonly byte[] Tree = { 0x1E, 0x4A, 0x1C };
        private static readonly byte[] Rock = { 0x45, 0x42, 0x3E };
        private static readonly byte[] Other = { 0xFF, 0x00, 0xFF };
        private static readonly byte[] Zone = { 0xF2, 0xD0, 0x2A };

        private static int Main(string[] args)
        {
            if (Array.IndexOf(args, "--macro") >= 0)
            {
                return MacroPreview.Run(args, FindRoot());
            }

            if (Array.IndexOf(args, "--world") >= 0)
            {
                return WorldPreview.Run(args, FindRoot());
            }

            int size = 1024;
            ulong seed = 0x0E5_0007UL;
            string? basePath = null;
            string outDir = Path.Combine(FindRoot(), "build", "gen_preview");
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--size": size = int.Parse(args[++i]); break;
                    case "--seed": seed = ulong.Parse(args[++i]); break;
                    case "--base": basePath = args[++i]; break;
                    case "--out": outDir = args[++i]; break;
                    default:
                        Console.Error.WriteLine($"Неизвестный параметр {args[i]}. См. комментарий в tools/gen_preview/Program.cs.");
                        return 2;
                }
            }

            Directory.CreateDirectory(outDir);
            byte[] snapshot;
            if (basePath == null)
            {
                var sw = Stopwatch.StartNew();
                WorldModel generated = new StubGenerator().Generate(MapShape.World(size), seed, out GenStats stats);
                double genMs = sw.Elapsed.TotalMilliseconds;
                snapshot = WorldSnapshot.WriteBase(generated);
                long tiles = (long)size * size;
                Console.WriteLine($"{size}²: генерация {genMs:F0} мс (с JIT), снимок {snapshot.Length / 1048576.0:F2} МБ; " +
                    $"скалы {100.0 * stats.RockTiles / tiles:F2} %, деревьев {stats.Trees}, скал-объектов {stats.Rocks}");
                basePath = Path.Combine(outDir, $"world_{size}_{seed}.base");
                File.WriteAllBytes(basePath, snapshot);
            }
            else
            {
                snapshot = File.ReadAllBytes(basePath);
            }

            WorldModel world = WorldSnapshot.LoadBase(snapshot);
            string name = $"map_{world.Shape.Width}_{world.Seed}";
            string plain = Path.Combine(outDir, name + ".png");
            string seam = Path.Combine(outDir, name + "_seam.png");
            int w = world.Shape.Width;
            int h = world.Shape.Height;
            WritePng(plain, Render(world, 0, 0), w, h);
            WritePng(seam, Render(world, w / 2, h / 2), w, h);
            Console.WriteLine($"снимок: {Path.GetFullPath(basePath)} (генератор v{world.GeneratorVersion}, зерно {world.Seed})");
            Console.WriteLine($"карта:  {Path.GetFullPath(plain)}");
            Console.WriteLine($"шов:    {Path.GetFullPath(seam)}");
            return 0;
        }

        // Пиксель (px, py) — тайл (px + shiftX, py + shiftY) по тору.
        private static byte[] Render(WorldModel world, int shiftX, int shiftY)
        {
            int w = world.Shape.Width;
            int h = world.Shape.Height;
            var rgb = new byte[w * h * 3];
            for (int py = 0; py < h; py++)
            {
                for (int px = 0; px < w; px++)
                {
                    int x = (px + shiftX) & (w - 1);
                    int y = (py + shiftY) & (h - 1);
                    var t = new TileCoord(x, y);
                    ushort obj = world.GetObject(t);
                    byte[] c = obj switch
                    {
                        ObjectId.Tree => Tree,
                        ObjectId.Rock => Rock,
                        ObjectId.Empty => world.GetFloor(t) == FloorId.Stone ? Stone : Grass,
                        _ => Other,
                    };
                    if (OnZoneBorder(world.Shape, x, y))
                    {
                        c = Zone;
                    }

                    int at = (py * w + px) * 3;
                    rgb[at] = c[0];
                    rgb[at + 1] = c[1];
                    rgb[at + 2] = c[2];
                }
            }

            return rgb;
        }

        private static bool OnZoneBorder(in MapShape shape, int x, int y) =>
            StubGenerator.InStartZone(shape, x, y)
            && (!StubGenerator.InStartZone(shape, x - 1, y) || !StubGenerator.InStartZone(shape, x + 1, y)
                || !StubGenerator.InStartZone(shape, x, y - 1) || !StubGenerator.InStartZone(shape, x, y + 1));

        private static string FindRoot()
        {
            string? dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "Orpheus.sln")))
            {
                dir = Path.GetDirectoryName(dir);
            }

            return dir ?? Directory.GetCurrentDirectory();
        }

        // ---------- PNG: RGB 8 бит, фильтр 0, zlib ----------

        internal static void WritePng(string path, byte[] rgb, int width, int height)
        {
            using var file = File.Create(path);
            file.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

            var ihdr = new byte[13];
            WriteBE(ihdr, 0, (uint)width);
            WriteBE(ihdr, 4, (uint)height);
            ihdr[8] = 8; // бит на канал
            ihdr[9] = 2; // RGB
            WriteChunk(file, "IHDR", ihdr);

            using var raw = new MemoryStream();
            using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
            {
                int stride = width * 3;
                for (int y = 0; y < height; y++)
                {
                    z.WriteByte(0);
                    z.Write(rgb, y * stride, stride);
                }
            }

            WriteChunk(file, "IDAT", raw.ToArray());
            WriteChunk(file, "IEND", Array.Empty<byte>());
        }

        private static void WriteChunk(Stream s, string type, byte[] data)
        {
            var head = new byte[8];
            WriteBE(head, 0, (uint)data.Length);
            Encoding.ASCII.GetBytes(type, 0, 4, head, 4);
            s.Write(head);
            s.Write(data);
            uint crc = Crc32(head.AsSpan(4, 4), 0xFFFFFFFFu);
            crc = Crc32(data, crc) ^ 0xFFFFFFFFu;
            var tail = new byte[4];
            WriteBE(tail, 0, crc);
            s.Write(tail);
        }

        private static readonly uint[] CrcTable = MakeCrcTable();

        private static uint[] MakeCrcTable()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }

                t[n] = c;
            }

            return t;
        }

        private static uint Crc32(ReadOnlySpan<byte> data, uint crc)
        {
            foreach (byte b in data)
            {
                crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
            }

            return crc;
        }

        private static void WriteBE(byte[] buf, int at, uint v)
        {
            buf[at] = (byte)(v >> 24);
            buf[at + 1] = (byte)(v >> 16);
            buf[at + 2] = (byte)(v >> 8);
            buf[at + 3] = (byte)v;
        }
    }
}
