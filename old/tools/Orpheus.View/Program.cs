using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Orpheus.Core.Geometry;
using Orpheus.Core.World;
using Orpheus.Save;
using Orpheus.WorldGen;

namespace Orpheus.View
{
    /// <summary>
    /// Кадр мира из сохранения (промежуточные playable-сборки, docs/08 3.0): читает поколение
    /// (world.NNNNNN.save), генерирует землю по зерну и рисует BMP: чанк = пиксель (цвет пола
    /// в центре чанка), сущности — точки. Земля — по генератору (docs/07, 6.3: сохранение хранит
    /// только хэш базы и дельты чанков); дельты из сохранения на кадр не накладываются.
    /// </summary>
    internal static class Program
    {
        private const int ChunkTiles = Torus.ChunkSize; // тайлов в чанке
        private const int PosScale = 256; // WorldPos: 1/256 тайла

        private static int Main(string[] args)
        {
            string worldDir = "", outPath = "";
            long generation = -1;
            int maxPx = 1024;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"нет значения у {a}");
                switch (a)
                {
                    case "--world": worldDir = Next(); break;
                    case "--out": outPath = Next(); break;
                    case "--generation": generation = long.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--max-px": maxPx = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    default:
                        Console.Error.WriteLine($"неизвестный ключ: {a}");
                        return 2;
                }
            }

            if (worldDir == "" || outPath == "")
            {
                Console.Error.WriteLine("употребление: OrpheusView --world <папка> --out <файл.bmp> [--generation N] [--max-px 1024]");
                return 2;
            }

            string? file = PickGeneration(worldDir, generation);
            if (file == null)
            {
                Console.Error.WriteLine($"в {worldDir} нет поколений (world.NNNNNN.save)");
                return 2;
            }

            SaveFile save = SaveContainer.Read(File.ReadAllBytes(file));
            WorldHeader hdr = ReadWorld(save);
            List<EntityPos> entities = ReadEntities(save);

            // Земля — генератором по зерну (тот же код, что у сервера).
            var shape = new MapShape(hdr.Width, hdr.Height, hdr.Wraps);
            WorldModel world = new StubGenerator().Generate(shape, hdr.Seed, out _);

            int stride = Math.Max(1, (int)Math.Ceiling((double)Math.Max(shape.Width, shape.Height) / maxPx));
            int w = shape.Width / stride, h = shape.Height / stride;

            byte[] pixels = new byte[w * h];
            for (int py = 0; py < h; py++)
            {
                for (int px = 0; px < w; px++)
                {
                    pixels[py * w + px] = world.GetFloor(new TileCoord(px * stride, py * stride));
                }
            }

            // Сущности: WorldPos (1/256 тайла) → пиксель кадра.
            byte Mark(byte b) => b == FloorId.Water ? (byte)90 : b == FloorId.Lava ? (byte)91 : (byte)0;
            foreach (EntityPos e in entities)
            {
                int px = e.X / PosScale / stride;
                int py = e.Y / PosScale / stride;
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int qx = px + dx, qy = py + dy;
                        if (qx < 0 || qx >= w || qy < 0 || qy >= h)
                        {
                            continue;
                        }

                        int at = qy * w + qx;
                        pixels[at] = Mark(pixels[at]);
                    }
                }
            }

            byte[] bmp = ToBmp(pixels, w, h);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
            File.WriteAllBytes(outPath, bmp);

            Console.WriteLine($"кадр: файл {Path.GetFileName(file)}, мир «{hdr.Name}», seed {hdr.Seed}, " +
                $"{shape.Width}×{shape.Height} тайлов (шаг {stride}, кадр {w}×{h}), такт {hdr.Tick}, " +
                $"сущностей {entities.Count} → {outPath}");
            return 0;
        }

        private static string? PickGeneration(string dir, long number)
        {
            string path = number >= 0 ? Path.Combine(dir, WorldFolder.GenerationFileName((ulong)number)) : "";
            if (number >= 0)
            {
                return File.Exists(path) ? path : null;
            }

            string? best = null;
            ulong bestNumber = 0;
            foreach (string f in Directory.EnumerateFiles(dir, "world.*.save"))
            {
                if (WorldFolder.TryParseGeneration(Path.GetFileName(f), out ulong n, out bool tmp) && !tmp && (best == null || n > bestNumber))
                {
                    best = f;
                    bestNumber = n;
                }
            }

            return best;
        }

        private sealed record WorldHeader(string Name, ulong Seed, int Width, int Height, bool Wraps, long Tick);

        private static WorldHeader ReadWorld(SaveFile save)
        {
            ReadOnlySpan<byte> data = save.Section(SaveSections.World);
            string name = "";
            ulong seed = 0;
            int width = 0, height = 0;
            bool wraps = true;
            var r = new RecordReader(data);
            while (r.Next())
            {
                switch (r.Field)
                {
                    case SaveSections.WorldField.Name: name = Encoding.UTF8.GetString(r.Raw); break;
                    case SaveSections.WorldField.Seed: seed = r.UInt(); break;
                    case SaveSections.WorldField.Width: width = r.UIntAsInt(); break;
                    case SaveSections.WorldField.Height: height = r.UIntAsInt(); break;
                    case SaveSections.WorldField.Wraps: wraps = r.Bool(); break;
                }
            }

            if (width <= 0 || height <= 0)
            {
                throw new InvalidDataException($"раздел МИР: размеры {width}×{height}");
            }

            long tick = save.Header.WorldTick;
            return new WorldHeader(name, seed, width, height, wraps, tick);
        }

        private sealed record EntityPos(int X, int Y, int Id);

        private static List<EntityPos> ReadEntities(SaveFile save)
        {
            var list = new List<EntityPos>();
            if (!save.HasSection(SaveSections.Creatures))
            {
                return list;
            }

            var r = new RecordReader(save.Section(SaveSections.Creatures));
            while (r.Next())
            {
                if (r.Field != SaveSections.CreaturesField.Entity || r.Type != RecordType.Record)
                {
                    continue;
                }

                int id = 0, x = 0, y = 0;
                var e = new RecordReader(r.Raw);
                while (e.Next())
                {
                    switch (e.Field)
                    {
                        case SaveSections.EntityField.Id: id = e.UIntAsInt(); break;
                        case SaveSections.EntityField.X: x = e.IntAsInt(); break;
                        case SaveSections.EntityField.Y: y = e.IntAsInt(); break;
                    }
                }

                list.Add(new EntityPos(x, y, id));
            }

            return list;
        }

        /// <summary>BMP 24 бит, bottom-up; цвет пола → RGB, точка сущности — красный крест.</summary>
        private static byte[] ToBmp(byte[] cells, int w, int h)
        {
            int rowBytes = w * 3;
            int pad = (4 - rowBytes % 4) % 4;
            int size = 54 + (rowBytes + pad) * h;
            var b = new byte[size];
            b[0] = (byte)'B';
            b[1] = (byte)'M';
            Write32(b, 2, (uint)size);
            Write32(b, 10, 54);
            Write32(b, 14, 40);
            Write32(b, 18, (uint)w);
            Write32(b, 22, (uint)h);
            b[26] = 1;
            b[28] = 24;

            for (int y = 0; y < h; y++)
            {
                int row = 54 + (h - 1 - y) * (rowBytes + pad);
                for (int x = 0; x < w; x++)
                {
                    (byte r, byte g, byte bl) = Color(cells[y * w + x]);
                    int at = row + x * 3;
                    b[at] = bl;
                    b[at + 1] = g;
                    b[at + 2] = r;
                }
            }

            return b;
        }

        private static (byte, byte, byte) Color(byte floor) => floor switch
        {
            90 => (235, 60, 60),
            91 => (255, 130, 40),
            FloorId.Empty => (40, 40, 40),
            FloorId.Grass => (58, 125, 61),
            FloorId.Dirt => (125, 96, 63),
            FloorId.Stone => (110, 110, 116),
            FloorId.Sand => (196, 178, 128),
            FloorId.Snow => (225, 228, 232),
            FloorId.Water => (38, 74, 158),
            FloorId.Lava => (198, 74, 30),
            FloorId.WoodFloor => (150, 111, 68),
            FloorId.StoneFloor => (140, 140, 146),
            FloorId.Platform => (96, 82, 60),
            _ => (200, 60, 200),
        };

        private static void Write32(byte[] b, int at, uint v)
        {
            b[at] = (byte)v;
            b[at + 1] = (byte)(v >> 8);
            b[at + 2] = (byte)(v >> 16);
            b[at + 3] = (byte)(v >> 24);
        }
    }
}
