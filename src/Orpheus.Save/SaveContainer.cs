using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace Orpheus.Save
{
    /// <summary>Заголовок файла сохранения (docs/07, 6.3).</summary>
    public readonly struct SaveHeader
    {
        /// <summary>Версия формата сохранения (6.7).</summary>
        public readonly uint FormatVersion;

        /// <summary>Версия сборки, которая писала файл.</summary>
        public readonly uint BuildVersion;

        /// <summary>Номер сохранения (поколение, имя <c>world.NNNNNN.save</c>).</summary>
        public readonly ulong Number;

        /// <summary>Время мира — номер такта, на границе которого собрано сохранение.</summary>
        public readonly long WorldTick;

        /// <summary>Дата записи: миллисекунды Unix, UTC. Её даёт вызывающий: ядро настенных часов не читает.</summary>
        public readonly long DateUnixMs;

        public SaveHeader(uint formatVersion, uint buildVersion, ulong number, long worldTick, long dateUnixMs)
        {
            FormatVersion = formatVersion;
            BuildVersion = buildVersion;
            Number = number;
            WorldTick = worldTick;
            DateUnixMs = dateUnixMs;
        }
    }

    /// <summary>Раздел сохранения: id и байты.</summary>
    public readonly struct SaveSection
    {
        public readonly uint Id;
        public readonly byte[] Data;

        public SaveSection(uint id, byte[] data)
        {
            Id = id;
            Data = data ?? throw new ArgumentNullException(nameof(data));
        }
    }

    /// <summary>Строка таблицы разделов: id, смещение, длина, CRC32 — и сошёлся ли CRC при чтении.</summary>
    public readonly struct SaveSectionEntry
    {
        public readonly uint Id;
        public readonly int Offset;
        public readonly int Length;
        public readonly uint Crc;
        public readonly bool CrcOk;

        public SaveSectionEntry(uint id, int offset, int length, uint crc, bool crcOk)
        {
            Id = id;
            Offset = offset;
            Length = length;
            Crc = crc;
            CrcOk = crcOk;
        }
    }

    /// <summary>Итог проверки файла.</summary>
    public enum SaveCheckStatus
    {
        /// <summary>Целый: метка, версия, SHA-256 и все CRC сошлись.</summary>
        Ok = 0,

        /// <summary>Повреждён или недописан.</summary>
        Damaged = 1,

        /// <summary>Целый по виду, но формат новее сборки — не портить и не грузить (6.7).</summary>
        TooNew = 2,
    }

    /// <summary>Разобранный файл сохранения: заголовок, таблица разделов, итог проверки.</summary>
    public sealed class SaveFile
    {
        internal SaveFile(byte[] data)
        {
            Data = data;
        }

        /// <summary>Байты файла целиком.</summary>
        public byte[] Data { get; }

        public SaveCheckStatus Status { get; internal set; }

        /// <summary>Почему не целый (null — целый).</summary>
        public string? Error { get; internal set; }

        /// <summary>Заголовок (действителен, если метка и длина на месте).</summary>
        public SaveHeader Header { get; internal set; }

        /// <summary>SHA-256 сошёлся.</summary>
        public bool ShaOk { get; internal set; }

        /// <summary>Таблица разделов (пусто, если таблица не читается).</summary>
        public IReadOnlyList<SaveSectionEntry> Sections { get; internal set; } = Array.Empty<SaveSectionEntry>();

        public bool IsOk => Status == SaveCheckStatus.Ok;

        /// <summary>Байты раздела по id; нет — пусто (отсутствующий раздел = умолчания).</summary>
        public ReadOnlySpan<byte> Section(uint id)
        {
            for (int i = 0; i < Sections.Count; i++)
            {
                if (Sections[i].Id == id)
                {
                    return new ReadOnlySpan<byte>(Data, Sections[i].Offset, Sections[i].Length);
                }
            }

            return ReadOnlySpan<byte>.Empty;
        }

        /// <summary>Есть ли раздел.</summary>
        public bool HasSection(uint id)
        {
            for (int i = 0; i < Sections.Count; i++)
            {
                if (Sections[i].Id == id)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Целый — иначе <see cref="InvalidDataException"/> с причиной.</summary>
        public SaveFile EnsureOk()
        {
            if (!IsOk)
            {
                throw new InvalidDataException(Error);
            }

            return this;
        }
    }

    /// <summary>
    /// Контейнер сохранения (docs/07, 6.3). Байты, младший первым:
    /// <list type="bullet">
    /// <item>заголовок 44 байта: метка <c>ORPHSAVE</c> (8), версия формата (4), версия сборки (4), номер сохранения (8),
    /// время мира — такт (8), дата — мс Unix UTC (8), число разделов (4);</item>
    /// <item>таблица разделов по 16 байт: id (4), смещение от начала файла (4), длина (4), CRC32 раздела (4);</item>
    /// <item>разделы подряд в порядке таблицы;</item>
    /// <item>SHA-256 всего, что выше (32).</item>
    /// </list>
    /// Проверка: длина → метка → версия формата (новее сборки — <see cref="SaveCheckStatus.TooNew"/>, файл не трогают) →
    /// SHA-256 → таблица в границах → CRC32 каждого раздела. CRC нужен, чтобы сказать, <i>какой</i> раздел битый
    /// (<c>orpheus-save dump</c>); решает целостность SHA-256.
    /// </summary>
    public static class SaveContainer
    {
        /// <summary>Метка «ORPHSAVE».</summary>
        public static ReadOnlySpan<byte> Magic => new byte[] { (byte)'O', (byte)'R', (byte)'P', (byte)'H', (byte)'S', (byte)'A', (byte)'V', (byte)'E' };

        public const int HeaderBytes = 44;
        public const int TableEntryBytes = 16;
        public const int ShaBytes = 32;

        /// <summary>Наибольший файл сохранения: 256 МБ (4096² в акте III — 1,4 МБ, 6.3) — предел против мусора.</summary>
        public const int MaxFileBytes = 256 << 20;

        /// <summary>Наибольшее число разделов.</summary>
        public const int MaxSections = 1024;

        /// <summary>Собрать файл.</summary>
        public static byte[] Write(in SaveHeader header, IReadOnlyList<SaveSection> sections)
        {
            if (sections == null)
            {
                throw new ArgumentNullException(nameof(sections));
            }

            if (sections.Count > MaxSections)
            {
                throw new ArgumentException($"Разделов {sections.Count}, наибольшее {MaxSections}.", nameof(sections));
            }

            long total = HeaderBytes + (long)TableEntryBytes * sections.Count + ShaBytes;
            for (int i = 0; i < sections.Count; i++)
            {
                total += sections[i].Data.Length;
            }

            if (total > MaxFileBytes)
            {
                throw new ArgumentException($"Сохранение {total} байт — больше предела {MaxFileBytes}.", nameof(sections));
            }

            var file = new byte[total];
            Span<byte> s = file;
            Magic.CopyTo(s);
            Le.WriteUInt32(s.Slice(8), header.FormatVersion);
            Le.WriteUInt32(s.Slice(12), header.BuildVersion);
            Le.WriteUInt64(s.Slice(16), header.Number);
            Le.WriteUInt64(s.Slice(24), unchecked((ulong)header.WorldTick));
            Le.WriteUInt64(s.Slice(32), unchecked((ulong)header.DateUnixMs));
            Le.WriteUInt32(s.Slice(40), (uint)sections.Count);

            int table = HeaderBytes;
            int at = HeaderBytes + TableEntryBytes * sections.Count;
            for (int i = 0; i < sections.Count; i++)
            {
                byte[] data = sections[i].Data;
                Le.WriteUInt32(s.Slice(table), sections[i].Id);
                Le.WriteUInt32(s.Slice(table + 4), (uint)at);
                Le.WriteUInt32(s.Slice(table + 8), (uint)data.Length);
                Le.WriteUInt32(s.Slice(table + 12), Crc32.Compute(data));
                table += TableEntryBytes;
                data.CopyTo(s.Slice(at));
                at += data.Length;
            }

            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(file, 0, at);
                hash.CopyTo(s.Slice(at));
            }

            return file;
        }

        /// <summary>Разобрать и проверить файл. Не бросает на порче — итог в <see cref="SaveFile.Status"/>.</summary>
        public static SaveFile Read(byte[] data)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            var f = new SaveFile(data);
            ReadInto(f, data);
            return f;
        }

        private static void ReadInto(SaveFile f, byte[] data)
        {
            ReadOnlySpan<byte> s = data;
            if (data.Length < HeaderBytes + ShaBytes)
            {
                Fail(f, $"Файл {data.Length} байт — короче заголовка и SHA-256 ({HeaderBytes + ShaBytes}): недописан.");
                return;
            }

            if (!s.Slice(0, 8).SequenceEqual(Magic))
            {
                Fail(f, "Нет метки ORPHSAVE: это не сохранение Orpheus или начало файла испорчено.");
                return;
            }

            uint format = Le.ReadUInt32(s, 8);
            f.Header = new SaveHeader(
                format,
                Le.ReadUInt32(s, 12),
                Le.ReadUInt64(s, 16),
                unchecked((long)Le.ReadUInt64(s, 24)),
                unchecked((long)Le.ReadUInt64(s, 32)));

            int bodyEnd = data.Length - ShaBytes;
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(data, 0, bodyEnd);
                f.ShaOk = s.Slice(bodyEnd, ShaBytes).SequenceEqual(hash);
            }

            if (format > SaveModule.FormatVersion && f.ShaOk)
            {
                f.Status = SaveCheckStatus.TooNew;
                f.Error = $"Мир сохранён более новой версией игры (формат {format}, у тебя {SaveModule.FormatVersion}).";
                return;
            }

            if (format == 0 || format > SaveModule.FormatVersion)
            {
                Fail(f, $"Версия формата {format} — такой нет (сборка знает 1…{SaveModule.FormatVersion}); вероятно, заголовок испорчен.");
                return;
            }

            uint count = Le.ReadUInt32(s, 40);
            if (count > MaxSections || HeaderBytes + (long)TableEntryBytes * count > bodyEnd)
            {
                Fail(f, $"Таблица разделов на {count} строк не помещается в файл.");
                return;
            }

            int tableEnd = HeaderBytes + TableEntryBytes * (int)count;
            var entries = new SaveSectionEntry[count];
            string? sectionError = null;
            for (int i = 0; i < count; i++)
            {
                int t = HeaderBytes + TableEntryBytes * i;
                uint id = Le.ReadUInt32(s, t);
                uint offset = Le.ReadUInt32(s, t + 4);
                uint length = Le.ReadUInt32(s, t + 8);
                uint crc = Le.ReadUInt32(s, t + 12);
                if (offset < tableEnd || offset > bodyEnd || length > bodyEnd - offset)
                {
                    sectionError ??= $"Раздел {SaveSections.Name(id)}: смещение {offset}, длина {length} — за границами файла.";
                    entries[i] = new SaveSectionEntry(id, 0, 0, crc, false);
                    continue;
                }

                bool ok = Crc32.Compute(s.Slice((int)offset, (int)length)) == crc;
                if (!ok)
                {
                    sectionError ??= $"Раздел {SaveSections.Name(id)}: CRC32 не сошёлся.";
                }

                entries[i] = new SaveSectionEntry(id, (int)offset, (int)length, crc, ok);
            }

            f.Sections = entries;
            if (!f.ShaOk)
            {
                Fail(f, "SHA-256 не сошёлся: файл недописан или испорчен" + (sectionError != null ? ". " + sectionError : "."));
                return;
            }

            if (sectionError != null)
            {
                Fail(f, sectionError);
                return;
            }

            f.Status = SaveCheckStatus.Ok;
            f.Error = null;
        }

        private static void Fail(SaveFile f, string error)
        {
            f.Status = SaveCheckStatus.Damaged;
            f.Error = error;
        }
    }

    /// <summary>Целые в байтах, младший первым.</summary>
    internal static class Le
    {
        public static void WriteUInt32(Span<byte> d, uint v)
        {
            d[0] = (byte)v;
            d[1] = (byte)(v >> 8);
            d[2] = (byte)(v >> 16);
            d[3] = (byte)(v >> 24);
        }

        public static void WriteUInt64(Span<byte> d, ulong v)
        {
            WriteUInt32(d, (uint)v);
            WriteUInt32(d.Slice(4), (uint)(v >> 32));
        }

        public static uint ReadUInt32(ReadOnlySpan<byte> s, int at) =>
            (uint)(s[at] | (s[at + 1] << 8) | (s[at + 2] << 16) | (s[at + 3] << 24));

        public static ulong ReadUInt64(ReadOnlySpan<byte> s, int at) =>
            ReadUInt32(s, at) | ((ulong)ReadUInt32(s, at + 4) << 32);
    }
}
