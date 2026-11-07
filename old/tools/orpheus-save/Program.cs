using System;
using System.Globalization;
using System.IO;
using System.Text;
using Orpheus.Core.World;
using Orpheus.Save;

namespace Orpheus.Tools.SaveTool
{
    /// <summary>
    /// <c>orpheus-save dump &lt;файл&gt;</c> — сохранение текстом (docs/07, 6.3: JSON в ядре нет, смотреть
    /// сохранение — этим инструментом). Печатает заголовок, проверку SHA-256, таблицу разделов с CRC32 и поля
    /// записей с именами по схеме (<see cref="SaveSections.Schema"/>); незнакомые поля — номером и типом.
    /// Понимает и снимок генерации <c>world.base</c> (заголовок и проверка).
    /// Код выхода: 0 — файл целый; 1 — повреждён или новее сборки; 2 — ошибка ключей или чтения.
    /// </summary>
    public static class Program
    {
        public const string Usage = "orpheus-save dump <файл> [--all]   (--all: все байты полей, а не первые 32)";

        public static int Main(string[] args)
        {
            try
            {
                Console.OutputEncoding = new UTF8Encoding(false);
            }
            catch (IOException)
            {
            }

            if (args.Length < 2 || args[0] != "dump")
            {
                Console.Error.WriteLine(Usage);
                return 2;
            }

            bool all = Array.IndexOf(args, "--all") >= 0;
            byte[] data;
            try
            {
                data = File.ReadAllBytes(args[1]);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Не прочитать {args[1]}: {e.Message}");
                return 2;
            }

            return SaveDump.Write(Path.GetFileName(args[1]), data, Console.Out, all) ? 0 : 1;
        }
    }

    /// <summary>Текстовый вывод сохранения (отдельно от <c>Main</c> — его зовут тесты).</summary>
    public static class SaveDump
    {
        private const int BytesShown = 32;

        /// <summary>Напечатать файл; true — целый.</summary>
        public static bool Write(string name, byte[] data, TextWriter o, bool allBytes = false)
        {
            o.WriteLine($"файл: {name}, {data.Length} байт");
            if (data.Length >= 4 && data[0] == 'O' && data[1] == 'R' && data[2] == 'P' && data[3] == 'W')
            {
                return WriteSnapshot(data, o);
            }

            SaveFile f = SaveContainer.Read(data);
            o.WriteLine(f.Status switch
            {
                SaveCheckStatus.Ok => "состояние: целый",
                SaveCheckStatus.TooNew => "состояние: НОВЕЕ СБОРКИ — " + f.Error,
                _ => "состояние: ПОВРЕЖДЁН — " + f.Error,
            });

            if (data.Length < SaveContainer.HeaderBytes || !f.Data.AsSpan(0, 8).SequenceEqual(SaveContainer.Magic))
            {
                return false;
            }

            SaveHeader h = f.Header;
            string date = DateTimeOffset.FromUnixTimeMilliseconds(h.DateUnixMs).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            long seconds = h.WorldTick / 30;
            o.WriteLine($"заголовок: ORPHSAVE, формат {h.FormatVersion}, сборка {h.BuildVersion}, номер {h.Number}, " +
                        $"такт {h.WorldTick} ({seconds / 3600} ч {seconds / 60 % 60} мин {seconds % 60} с мира), дата {date}");
            o.WriteLine($"SHA-256: {(f.ShaOk ? "сошёлся" : "НЕ СОШЁЛСЯ")} ({Hex(data.AsSpan(data.Length - SaveContainer.ShaBytes), SaveContainer.ShaBytes)})");
            o.WriteLine($"разделы: {f.Sections.Count}");
            for (int i = 0; i < f.Sections.Count; i++)
            {
                SaveSectionEntry s = f.Sections[i];
                o.WriteLine($"  [{SaveSections.Name(s.Id)}] id {s.Id}, смещение {s.Offset}, длина {s.Length}, CRC32 0x{s.Crc:x8} — {(s.CrcOk ? "сошёлся" : "НЕ СОШЁЛСЯ")}");
                if (!s.CrcOk)
                {
                    continue;
                }

                try
                {
                    WriteRecords(new RecordReader(data.AsSpan(s.Offset, s.Length)), SaveSections.Schema(s.Id), o, "    ", allBytes);
                }
                catch (InvalidDataException e)
                {
                    o.WriteLine($"    ЗАПИСИ НЕ РАЗОБРАТЬ: {e.Message}");
                }
            }

            return f.IsOk;
        }

        private static void WriteRecords(RecordReader r, RecordSchema? schema, TextWriter o, string indent, bool allBytes)
        {
            while (r.Next())
            {
                string? fieldName = schema?.NameOf(r.Field);
                string label = fieldName != null ? $"{r.Field} {fieldName}" : $"{r.Field} (незнакомое)";
                switch (r.Type)
                {
                    case RecordType.UInt:
                        o.WriteLine($"{indent}{label}: {r.UInt()}");
                        break;
                    case RecordType.Int:
                        o.WriteLine($"{indent}{label}: {r.Int()}");
                        break;
                    case RecordType.Text:
                        o.WriteLine($"{indent}{label}: «{r.Text()}»");
                        break;
                    case RecordType.Bytes:
                        ReadOnlySpan<byte> b = r.Bytes();
                        o.WriteLine($"{indent}{label}: {b.Length} байт {Hex(b, allBytes ? b.Length : BytesShown)}");
                        break;
                    case RecordType.Record:
                        o.WriteLine($"{indent}{label}: запись, {r.Length} байт");
                        WriteRecords(r.Record(), schema?.NestedOf(r.Field), o, indent + "  ", allBytes);
                        break;
                    default:
                        o.WriteLine($"{indent}{label}: тип {(int)r.Type} (незнакомый), {r.Length} байт");
                        break;
                }
            }
        }

        private static bool WriteSnapshot(byte[] data, TextWriter o)
        {
            o.WriteLine("вид: снимок мира ORPW (world.base или изменения)");
            if (data.Length < WorldSnapshot.HeaderBytes + WorldSnapshot.HashBytes)
            {
                o.WriteLine("состояние: ПОВРЕЖДЁН — короче заголовка");
                return false;
            }

            o.WriteLine($"заголовок: формат {data[4]}, вид {(data[5] == WorldSnapshot.KindBase ? "снимок генерации" : "изменения")}, " +
                        $"генератор v{BitConverter.ToUInt32(data, 6)}, зерно {BitConverter.ToUInt64(data, 10)}, " +
                        $"{BitConverter.ToUInt32(data, 18)}×{BitConverter.ToUInt32(data, 22)}{(data[26] != 0 ? " тор" : "")}, " +
                        $"чанков {BitConverter.ToUInt32(data, 27)}");
            o.WriteLine($"SHA-256 (записан): {Hex(WorldSnapshot.StoredHash(data), WorldSnapshot.HashBytes)}");
            if (data[5] != WorldSnapshot.KindBase)
            {
                o.WriteLine("состояние: проверка изменений — только вместе со снимком генерации");
                return true;
            }

            try
            {
                WorldSnapshot.LoadBase(data);
                o.WriteLine("состояние: целый (SHA-256 заголовка и содержимого сошёлся)");
                return true;
            }
            catch (InvalidDataException e)
            {
                o.WriteLine("состояние: ПОВРЕЖДЁН — " + e.Message);
                return false;
            }
        }

        private static string Hex(ReadOnlySpan<byte> b, int max)
        {
            var sb = new StringBuilder();
            int n = Math.Min(b.Length, max);
            for (int i = 0; i < n; i++)
            {
                sb.Append(b[i].ToString("x2", CultureInfo.InvariantCulture));
            }

            if (n < b.Length)
            {
                sb.Append('…');
            }

            return sb.ToString();
        }
    }
}
