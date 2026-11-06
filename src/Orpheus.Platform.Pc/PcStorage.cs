using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Orpheus.Save;

namespace Orpheus.Platform.Pc
{
    /// <summary>Миг атомарной записи — для отладочного крючка приёмки 0.4 (<see cref="PcStorage.Probe"/>).</summary>
    public enum PcWriteStage
    {
        /// <summary><c>.tmp</c> создан, записано <c>offset</c> байт (зовётся до первого куска и после каждого).</summary>
        Writing = 0,

        /// <summary>Все байты отданы ОС, сброса на носитель ещё не было.</summary>
        Written = 1,

        /// <summary>Сброшено на носитель (FlushFileBuffers / fsync), переименования ещё не было.</summary>
        Flushed = 2,

        /// <summary>Переименовано со сбросом — файл на месте.</summary>
        Moved = 3,
    }

    /// <summary>
    /// Хранилище «ПК» (docs/07, 1.6 и 6.5). Атомарная запись <see cref="WriteAtomic"/>:
    /// <list type="number">
    /// <item><c>&lt;файл&gt;.tmp</c> — <see cref="FileStream"/> с <see cref="FileOptions.WriteThrough"/>, без буфера .NET;</item>
    /// <item>сброс на носитель: Windows — <c>FlushFileBuffers</c> (P/Invoke, не полагаемся на то, что
    /// <c>Flush(true)</c> в Mono/IL2CPP Unity делает то же), остальные — <c>fsync</c> (<c>Flush(true)</c> .NET);</item>
    /// <item>файл закрывается (Windows не переименует открытый без FILE_SHARE_DELETE);</item>
    /// <item>переименование: Windows — <c>MoveFileExW(MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)</c>;
    /// Linux/macOS — <c>rename</c>, затем <c>fsync</c> папки (<c>open(папка, O_RDONLY)</c> + <c>fsync</c> + <c>close</c>).</item>
    /// </list>
    /// <para>
    /// <b>Почему <c>MoveFileExW</c>, а не <c>ReplaceFileW</c>.</b> Поколение всегда пишется в новое имя
    /// (<c>world.000128.save</c>), заменять нечего — а <c>ReplaceFileW</c> требует существующий файл-цель.
    /// <c>ReplaceFileW</c> к тому же переносит атрибуты и ACL и имеет частичные отказы
    /// (ERROR_UNABLE_TO_MOVE_REPLACEMENT_2 — цель уже переименована в резервную копию, новый файл — ещё нет),
    /// и в документации у него нет обещания «не вернуть управление, пока переименование не на диске».
    /// У <c>MoveFileExW</c> с <c>MOVEFILE_WRITE_THROUGH</c> оно есть: функция не возвращается, пока перенос
    /// не сброшен на диск, — запись не остаётся в журнале NTFS в памяти (в этом и разница с <c>File.Move</c>,
    /// который флага не ставит). <c>MOVEFILE_REPLACE_EXISTING</c> — для файлов с постоянным именем
    /// (<c>server.json</c>, файл токена, 6.5 п. 6). Отдельный сброс папки на Windows не нужен: переименование
    /// — операция метаданных NTFS, и её сбрасывает сам <c>MOVEFILE_WRITE_THROUGH</c>; <c>FlushFileBuffers</c>
    /// на папку без прав администратора не открыть.
    /// </para>
    /// <para>
    /// Обрыв питания в любой миг: недописанный <c>.tmp</c> (SHA-256 не сойдётся — его отвергнет проверка при старте),
    /// целый <c>.tmp</c> (продвигается при старте), целый новый файл. Предыдущее поколение не трогается.
    /// Гарантия не действует, если для диска выключена очистка буфера кэша записей Windows (6.8) — тогда
    /// FlushFileBuffers до носителя не доходит; защита — три поколения и SHA-256.
    /// </para>
    /// </summary>
    public sealed class PcStorage : IPlatformStorage
    {
        /// <summary>
        /// Параметры <c>.tmp</c>: сквозь кэш ОС. Константа, а не литерал в вызове, — её же сверяет тест флагов
        /// (<c>D00_Save_PcStorage_*</c>) через шов <see cref="PcFileCalls"/>.
        /// </summary>
        internal const FileOptions TmpOptions = FileOptions.WriteThrough;

        private readonly PcFileCalls _calls;

        public PcStorage()
            : this(PcFileCalls.Os)
        {
        }

        /// <summary>Только для тестов: системные вызовы записи через подменённый <paramref name="calls"/>.</summary>
        internal PcStorage(PcFileCalls calls)
        {
            _calls = calls;
        }

        /// <summary>Кусок записи при включённом крючке — чтобы обрыв приходился на разные смещения файла.</summary>
        public const int ProbePieceBytes = 512;

        private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        /// <summary>
        /// Отладочный крючок (только приёмка 0.4): зовётся в каждый миг записи — путь файла, стадия, записано байт,
        /// всего байт. Обычной игре не нужен: null.
        /// </summary>
        public Action<string, PcWriteStage, long, long>? Probe { get; set; }

        public bool FileExists(string path) => File.Exists(path);

        public bool DirectoryExists(string path) => Directory.Exists(path);

        public void CreateDirectory(string path) => Directory.CreateDirectory(path);

        public string[] ListFiles(string directory)
        {
            if (!Directory.Exists(directory))
            {
                return Array.Empty<string>();
            }

            string[] files = Directory.GetFiles(directory);
            for (int i = 0; i < files.Length; i++)
            {
                files[i] = Path.GetFileName(files[i]);
            }

            Array.Sort(files, StringComparer.Ordinal);
            return files;
        }

        public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);

        public void WriteAtomic(string path, ReadOnlySpan<byte> data)
        {
            string full = Path.GetFullPath(path);
            string tmp = full + WorldFolder.TmpSuffix;
            Action<string, PcWriteStage, long, long>? probe = Probe;

            // bufferSize 1 — без буфера .NET: байты сразу идут в ОС, а WriteThrough — сквозь кэш ОС.
            using (FileStream fs = _calls.CreateTmp(tmp, TmpOptions))
            {
                if (probe == null)
                {
                    fs.Write(data);
                }
                else
                {
                    probe(full, PcWriteStage.Writing, 0, data.Length);
                    for (int at = 0; at < data.Length; at += ProbePieceBytes)
                    {
                        int n = Math.Min(ProbePieceBytes, data.Length - at);
                        fs.Write(data.Slice(at, n));
                        probe(full, PcWriteStage.Writing, at + n, data.Length);
                    }

                    probe(full, PcWriteStage.Written, data.Length, data.Length);
                }

                FlushToDisk(fs, tmp);
            }

            probe?.Invoke(full, PcWriteStage.Flushed, data.Length, data.Length);
            Rename(tmp, full, replaceExisting: true);
            probe?.Invoke(full, PcWriteStage.Moved, data.Length, data.Length);
        }

        public void MoveDurable(string from, string to, bool replaceExisting) =>
            Rename(Path.GetFullPath(from), Path.GetFullPath(to), replaceExisting);

        public void Delete(string path) => File.Delete(path);

        public IDisposable? TryLock(string path)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            }
            catch (IOException)
            {
                return null;
            }
        }

        // ---------- Сброс и переименование ----------

        private void FlushToDisk(FileStream fs, string path)
        {
            fs.Flush(false);
            if (IsWindows)
            {
                if (!_calls.FlushFileBuffers(fs.SafeFileHandle, path))
                {
                    throw Error("FlushFileBuffers", path, Marshal.GetLastWin32Error());
                }
            }
            else
            {
                fs.Flush(true); // fsync
            }
        }

        private void Rename(string from, string to, bool replaceExisting)
        {
            if (IsWindows)
            {
                uint flags = MoveFileWriteThrough | (replaceExisting ? MoveFileReplaceExisting : 0u);
                if (!_calls.MoveFileEx(from, to, flags))
                {
                    throw Error("MoveFileExW", from + " → " + to, Marshal.GetLastWin32Error());
                }

                return;
            }

            if (!replaceExisting && File.Exists(to))
            {
                throw new IOException($"Уже есть: {to}.");
            }

            if (rename(from, to) != 0)
            {
                throw Error("rename", from + " → " + to, Marshal.GetLastWin32Error());
            }

            FsyncDirectory(Path.GetDirectoryName(to)!);
            string? fromDir = Path.GetDirectoryName(from);
            if (fromDir != null && !string.Equals(fromDir, Path.GetDirectoryName(to), StringComparison.Ordinal))
            {
                FsyncDirectory(fromDir);
            }
        }

        /// <summary>fsync папки после rename (Linux: иначе новая запись каталога может не дожить до носителя).</summary>
        private static void FsyncDirectory(string dir)
        {
            int fd = open(dir, ORdOnly);
            if (fd < 0)
            {
                throw Error("open(папка)", dir, Marshal.GetLastWin32Error());
            }

            try
            {
                if (fsync(fd) != 0)
                {
                    throw Error("fsync(папка)", dir, Marshal.GetLastWin32Error());
                }
            }
            finally
            {
                close(fd);
            }
        }

        private static IOException Error(string call, string what, int code) =>
            new IOException($"{call} не удался: {what} — {new Win32Exception(code).Message} (код {code}).", code);

        // ---------- Windows ----------

        internal const uint MoveFileReplaceExisting = 0x1;
        internal const uint MoveFileWriteThrough = 0x8;

        // ---------- POSIX ----------

        // O_RDONLY = 0 везде; O_DIRECTORY не ставим — его значение разное на Linux x64/arm64 и macOS,
        // а папка и так открывается на чтение.
        private const int ORdOnly = 0;

        [DllImport("libc", SetLastError = true)]
        private static extern int rename([MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath, [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath);

        [DllImport("libc", SetLastError = true)]
        private static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

        [DllImport("libc", SetLastError = true)]
        private static extern int fsync(int fd);

        [DllImport("libc", SetLastError = true)]
        private static extern int close(int fd);
    }

    /// <summary>
    /// Системные вызовы атомарной записи на Windows — шов для теста флагов и порядка (0-016, замечание 3 проверки
    /// 0-009). Убийство процесса не отличает кэш ОС от носителя, поэтому гарантию «дошло до диска» держит не
    /// обрыв, а тест: он подменяет этот класс записывающим наследником и сверяет, что <c>.tmp</c> открыт с
    /// <see cref="FileOptions.WriteThrough"/>, <c>FlushFileBuffers</c> позван до <c>MoveFileExW</c>, в флагах
    /// <c>MOVEFILE_WRITE_THROUGH</c>. В игре — <see cref="Os"/>: один виртуальный вызов на файл, против записи
    /// на носитель — ничто. POSIX-ветка (<c>fsync</c>, <c>rename</c>) шва не имеет: CI идёт на Windows.
    /// </summary>
    internal class PcFileCalls
    {
        internal static readonly PcFileCalls Os = new PcFileCalls();

        /// <summary><c>.tmp</c> на запись, без буфера .NET (bufferSize 1): байты сразу идут в ОС.</summary>
        public virtual FileStream CreateTmp(string path, FileOptions options) =>
            new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1, options);

        public virtual bool FlushFileBuffers(SafeFileHandle file, string path) => NativeFlushFileBuffers(file);

        public virtual bool MoveFileEx(string from, string to, uint flags) => NativeMoveFileExW(from, to, flags);

        [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool NativeMoveFileExW(string existingFileName, string newFileName, uint flags);

        [DllImport("kernel32.dll", EntryPoint = "FlushFileBuffers", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool NativeFlushFileBuffers(SafeFileHandle file);
    }
}
