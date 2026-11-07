using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Orpheus.Platform.Pc;
using Xunit;

namespace Orpheus.Tests
{
    /// <summary>
    /// Сброс на носитель под тестом (0-016, замечание 3 проверки 0-009; `07` 6.5 п. 1–2, 6.10 п. 9).
    /// <para>
    /// Убийство процесса (<see cref="SaveKillTests"/>) не отличает кэш ОС от носителя: файл, отданный ОС, переживает
    /// снятие процесса и без сброса. Обрыв питания — нет. Поэтому гарантию «сохранение дошло до диска» держит этот
    /// тест: <see cref="PcStorage"/> пишет через записывающий шов (<c>PcFileCalls</c>), и тест сверяет каждый
    /// системный вызов атомарной записи:
    /// </para>
    /// <list type="number">
    /// <item><c>.tmp</c> открыт с <see cref="FileOptions.WriteThrough"/> — и в аргументе, и по самому дескриптору
    /// (<c>NtQueryInformationFile</c>, <c>FileModeInformation</c>, флаг <c>FILE_WRITE_THROUGH</c>);</item>
    /// <item><c>FlushFileBuffers</c> позван на дескрипторе <c>.tmp</c>, когда все байты уже записаны;</item>
    /// <item>затем <c>.tmp</c> закрыт и <c>MoveFileExW</c> позван с <c>MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH</c>
    /// (0x1 | 0x8; числа — литералами, чтобы правка констант в <see cref="PcStorage"/> тоже краснила тест).</item>
    /// </list>
    /// <para>Удаление любого из трёх краснит быстрый CI. Ручная приёмка 6.10 п. 9 (Hyper-V, <c>Stop-VM -TurnOff</c>) — отдельно, перед выпуском.</para>
    /// <para>POSIX-ветки (<c>fsync</c>, <c>rename</c>, сброс папки) тест не касается: CI идёт на Windows; на других ОС тест пуст.</para>
    /// </summary>
    public sealed class SaveFlushTests : IDisposable
    {
        private const uint MoveFileReplaceExisting = 0x1;
        private const uint MoveFileWriteThrough = 0x8;

        private readonly string _dir;

        public SaveFlushTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "orpheus-flush-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_dir, true);
            }
            catch (IOException)
            {
            }
        }

        private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        [Fact]
        public void D00_Save_PcStorage_WriteAtomic_WriteThrough_FlushBeforeMove_MoveWriteThrough()
        {
            if (!IsWindows)
            {
                return;
            }

            byte[] data = Bytes(5000, 1);
            string path = Path.Combine(_dir, "world.000001.save");
            var calls = new RecordingCalls();

            new PcStorage(calls).WriteAtomic(path, data);

            AssertAtomicWrite(calls.Log, path, data.Length);
            Assert.Equal(data, File.ReadAllBytes(path));
            Assert.False(File.Exists(path + ".tmp"));
        }

        /// <summary>Тот же порядок с отладочным крючком приёмки 0.4 (запись кусками по 512 байт).</summary>
        [Fact]
        public void D00_Save_PcStorage_WriteAtomic_WithProbe_SameCallsAndFlags()
        {
            if (!IsWindows)
            {
                return;
            }

            byte[] data = Bytes(PcStorage.ProbePieceBytes * 3 + 17, 2);
            string path = Path.Combine(_dir, "world.000002.save");
            var calls = new RecordingCalls();
            var stages = new List<PcWriteStage>();
            var storage = new PcStorage(calls) { Probe = (_, stage, _, _) => stages.Add(stage) };

            storage.WriteAtomic(path, data);

            AssertAtomicWrite(calls.Log, path, data.Length);
            Assert.Equal(data, File.ReadAllBytes(path));
            Assert.Equal(PcWriteStage.Flushed, stages[stages.Count - 2]);
            Assert.Equal(PcWriteStage.Moved, stages[stages.Count - 1]);
        }

        /// <summary>Файл с постоянным именем (<c>server.json</c>, 6.5 п. 6): замена существующего — те же флаги.</summary>
        [Fact]
        public void D00_Save_PcStorage_WriteAtomic_ReplacesExisting_SameCallsAndFlags()
        {
            if (!IsWindows)
            {
                return;
            }

            string path = Path.Combine(_dir, "server.json");
            File.WriteAllBytes(path, Bytes(100, 3));
            byte[] data = Bytes(300, 4);
            var calls = new RecordingCalls();

            new PcStorage(calls).WriteAtomic(path, data);

            AssertAtomicWrite(calls.Log, path, data.Length);
            Assert.Equal(data, File.ReadAllBytes(path));
        }

        /// <summary>Перенос со сбросом (продвижение целого <c>.tmp</c>, перенос битого в <c>backups/damaged</c>).</summary>
        [Theory]
        [InlineData(false, MoveFileWriteThrough)]
        [InlineData(true, MoveFileWriteThrough | MoveFileReplaceExisting)]
        public void D00_Save_PcStorage_MoveDurable_MoveWriteThrough(bool replaceExisting, uint flags)
        {
            if (!IsWindows)
            {
                return;
            }

            string from = Path.Combine(_dir, "world.000003.save.tmp");
            string to = Path.Combine(_dir, "world.000003.save");
            File.WriteAllBytes(from, Bytes(64, 5));
            var calls = new RecordingCalls();

            new PcStorage(calls).MoveDurable(from, to, replaceExisting);

            Assert.Equal(new[] { $"move {from} -> {to} flags=0x{flags:X} closed=True" }, calls.Log);
            Assert.True(File.Exists(to));
            Assert.False(File.Exists(from));
        }

        /// <summary>Отказ <c>FlushFileBuffers</c> — исключение, а не молчаливое переименование несброшенного файла.</summary>
        [Fact]
        public void D00_Save_PcStorage_FlushFails_Throws_NoRename()
        {
            if (!IsWindows)
            {
                return;
            }

            string path = Path.Combine(_dir, "world.000004.save");
            var calls = new RecordingCalls { FailFlush = true };

            IOException e = Assert.Throws<IOException>(() => new PcStorage(calls).WriteAtomic(path, Bytes(10, 6)));
            Assert.Contains("FlushFileBuffers", e.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(calls.Log, l => l.StartsWith("move", StringComparison.Ordinal));
            Assert.False(File.Exists(path));
        }

        // ---------- Сверка ----------

        private static void AssertAtomicWrite(List<string> log, string path, int length)
        {
            string full = Path.GetFullPath(path);
            string tmp = full + ".tmp";
            Assert.Equal(
                new[]
                {
                    $"create {tmp} options={FileOptions.WriteThrough}",
                    $"flush {tmp} length={length} handleWriteThrough=True",
                    $"move {tmp} -> {full} flags=0x{MoveFileReplaceExisting | MoveFileWriteThrough:X} closed=True",
                },
                log);
        }

        private static byte[] Bytes(int n, byte salt)
        {
            var b = new byte[n];
            for (int i = 0; i < n; i++)
            {
                b[i] = (byte)(i * 31 + salt);
            }

            return b;
        }

        // ---------- Записывающий шов ----------

        /// <summary>Настоящие вызовы ОС плюс журнал: что позвано, с какими флагами, в каком состоянии файла.</summary>
        private sealed class RecordingCalls : PcFileCalls
        {
            public List<string> Log { get; } = new List<string>();

            public bool FailFlush { get; set; }

            public override FileStream CreateTmp(string path, FileOptions options)
            {
                Log.Add($"create {path} options={options}");
                return base.CreateTmp(path, options);
            }

            public override bool FlushFileBuffers(SafeFileHandle file, string path)
            {
                // Сброс — когда байты уже отданы ОС; дескриптор обязан быть сквозным (FILE_WRITE_THROUGH).
                Log.Add($"flush {path} length={RandomAccess.GetLength(file)} handleWriteThrough={HandleWriteThrough(file)}");
                if (FailFlush)
                {
                    Marshal.SetLastPInvokeError(5); // ERROR_ACCESS_DENIED
                    return false;
                }

                return base.FlushFileBuffers(file, path);
            }

            public override bool MoveFileEx(string from, string to, uint flags)
            {
                Log.Add($"move {from} -> {to} flags=0x{flags:X} closed={IsClosed(from)}");
                return base.MoveFileEx(from, to, flags);
            }

            /// <summary>Файл закрыт писателем: открыть без совместного доступа удаётся.</summary>
            private static bool IsClosed(string path)
            {
                try
                {
                    using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                    {
                        return true;
                    }
                }
                catch (IOException)
                {
                    return false;
                }
            }
        }

        // ---------- Режим дескриптора ----------

        private const int FileModeInformation = 16;
        private const uint FileWriteThroughMode = 0x2; // FILE_WRITE_THROUGH

        [StructLayout(LayoutKind.Sequential)]
        private struct IoStatusBlock
        {
            public IntPtr Status;
            public IntPtr Information;
        }

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationFile(SafeFileHandle file, out IoStatusBlock status, out uint mode, uint length, int informationClass);

        private static bool HandleWriteThrough(SafeFileHandle file)
        {
            int status = NtQueryInformationFile(file, out _, out uint mode, sizeof(uint), FileModeInformation);
            Assert.True(status == 0, $"NtQueryInformationFile: 0x{status:X8}");
            return (mode & FileWriteThroughMode) != 0;
        }
    }
}
