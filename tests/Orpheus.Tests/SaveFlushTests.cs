using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
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

        // ---------- Лист шва: вызовы до ОС (0-020, замечание 1 проверки 0-016) ----------
        //
        // Тесты выше подменяют PcFileCalls наследником, который зовёт base — то есть сами листья шва
        // (две строки рядом с DllImport) они не проверяют: заглушка «=> true» или «flags & ~8» прошли бы.
        // Здесь листья проверены дважды: поведением настоящей ОС и телом метода (IL) — лист только передаёт
        // аргументы в extern-функцию kernel32 как есть.

        /// <summary>
        /// <c>FlushFileBuffers</c> доходит до ОС: на дескрипторе только для чтения ОС отказывает с
        /// <c>ERROR_ACCESS_DENIED</c> (нужен GENERIC_WRITE) — заглушка без вызова ОС вернула бы успех.
        /// </summary>
        [Fact]
        public void D00_Save_PcFileCalls_Flush_ReachesOs_ReadOnlyHandleDenied()
        {
            if (!IsWindows)
            {
                return;
            }

            string path = Path.Combine(_dir, "flush.bin");
            File.WriteAllBytes(path, Bytes(100, 7));

            using (var ro = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                Assert.False(PcFileCalls.Os.FlushFileBuffers(ro.SafeFileHandle, path), "сброс дескриптора только для чтения — ОС не позвана");
                Assert.Equal(ErrorAccessDenied, Marshal.GetLastPInvokeError());
            }

            using (var rw = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            {
                Assert.True(PcFileCalls.Os.FlushFileBuffers(rw.SafeFileHandle, path), $"сброс на запись: код {Marshal.GetLastPInvokeError()}");
            }
        }

        /// <summary>
        /// Флаги <c>MoveFileExW</c> доходят до ОС как есть: без <c>MOVEFILE_REPLACE_EXISTING</c> перенос на
        /// существующий файл — отказ <c>ERROR_ALREADY_EXISTS</c>, с ним — замена.
        /// </summary>
        [Fact]
        public void D00_Save_PcFileCalls_MoveFileEx_ReachesOs_ReplaceExistingHonoured()
        {
            if (!IsWindows)
            {
                return;
            }

            string from = Path.Combine(_dir, "move.tmp");
            string to = Path.Combine(_dir, "move.bin");
            File.WriteAllBytes(from, Bytes(64, 8));
            File.WriteAllBytes(to, Bytes(32, 9));

            Assert.False(PcFileCalls.Os.MoveFileEx(from, to, MoveFileWriteThrough), "перенос на существующий без REPLACE_EXISTING удался");
            Assert.Equal(ErrorAlreadyExists, Marshal.GetLastPInvokeError());
            Assert.Equal(Bytes(32, 9), File.ReadAllBytes(to));

            Assert.True(PcFileCalls.Os.MoveFileEx(from, to, MoveFileWriteThrough | MoveFileReplaceExisting), $"замена: код {Marshal.GetLastPInvokeError()}");
            Assert.Equal(Bytes(64, 8), File.ReadAllBytes(to));
            Assert.False(File.Exists(from));
        }

        /// <summary>
        /// Тело листьев шва — ровно «аргументы → extern kernel32 → результат», без правки по дороге.
        /// <c>MOVEFILE_WRITE_THROUGH</c> снаружи не увидеть (ОС ведёт себя так же, только без сброса журнала NTFS),
        /// поэтому снятие 0x8 в листе ловит сверка IL: лишняя инструкция (<c>ldc.i4 -9; and</c>, константа вместо
        /// вызова) — красный. Сама extern-функция сверяется по <see cref="DllImportAttribute"/>: kernel32, нужная
        /// точка входа, <c>SetLastError</c> (иначе код отказа теряется).
        /// </summary>
        [Fact]
        public void D00_Save_PcFileCalls_Leaves_PassArgumentsToKernel32AsIs()
        {
            AssertLeaf(nameof(PcFileCalls.MoveFileEx), new[] { typeof(string), typeof(string), typeof(uint) }, "MoveFileExW");
            AssertLeaf(nameof(PcFileCalls.FlushFileBuffers), new[] { typeof(SafeFileHandle), typeof(string) }, "FlushFileBuffers");
        }

        private const int ErrorAccessDenied = 5;
        private const int ErrorAlreadyExists = 183;

        /// <summary>
        /// Лист <paramref name="leaf"/> класса <see cref="PcFileCalls"/>: IL (без <c>nop</c> и отладочного
        /// <c>stloc/br/ldloc</c> вокруг возврата) — <c>ldarg.1 … ldarg.k</c>, <c>call</c> extern-функции
        /// <paramref name="entryPoint"/> с теми же типами параметров, <c>ret</c>.
        /// </summary>
        private static void AssertLeaf(string leaf, Type[] parameters, string entryPoint)
        {
            const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            MethodInfo method = typeof(PcFileCalls).GetMethod(leaf, Any, null, parameters, null)
                ?? throw new InvalidOperationException($"нет листа {leaf}");
            List<(OpCode Op, int Token)> il = DecodeIl(method);
            string dump = string.Join("; ", il.ConvertAll(i => i.Op.Name));

            // В extern идут все аргументы листа, кроме пути для сообщений (у FlushFileBuffers он последний и в ОС не нужен).
            Assert.True(il.Count >= 2 && il[il.Count - 2].Op == OpCodes.Call, $"{leaf}: нет call перед ret — {dump}");
            MethodBase target = method.Module.ResolveMethod(il[il.Count - 2].Token)
                ?? throw new InvalidOperationException($"{leaf}: call не разрешился");
            int args = target.GetParameters().Length;
            Assert.True(il.Count == args + 2, $"{leaf}: лишние инструкции — {dump}");
            for (int i = 0; i < args; i++)
            {
                Assert.True(il[i].Op == ArgOps[i + 1], $"{leaf}: аргумент {i + 1} не передан как есть — {dump}");
            }

            Assert.Equal(OpCodes.Ret, il[il.Count - 1].Op);

            Assert.True(target.IsStatic && (target.Attributes & MethodAttributes.PinvokeImpl) != 0, $"{leaf}: зовёт не extern, а {target.Name}");
            DllImportAttribute import = target.GetCustomAttribute<DllImportAttribute>()
                ?? throw new InvalidOperationException($"{target.Name}: нет DllImport");
            Assert.Equal("kernel32.dll", import.Value, ignoreCase: true);
            Assert.Equal(entryPoint, import.EntryPoint);
            Assert.True(import.SetLastError, $"{target.Name}: без SetLastError код отказа теряется");
            ParameterInfo[] p = target.GetParameters();
            for (int i = 0; i < args; i++)
            {
                Assert.Equal(parameters[i], p[i].ParameterType);
            }
        }

        private static readonly OpCode[] ArgOps = { OpCodes.Ldarg_0, OpCodes.Ldarg_1, OpCodes.Ldarg_2, OpCodes.Ldarg_3 };

        private static readonly Dictionary<short, OpCode> OpByValue = BuildOpTable();

        private static Dictionary<short, OpCode> BuildOpTable()
        {
            var table = new Dictionary<short, OpCode>();
            foreach (FieldInfo f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                var op = (OpCode)f.GetValue(null)!;
                table[op.Value] = op;
            }

            return table;
        }

        /// <summary>Инструкции тела: код и токен (для call), без <c>nop</c> и отладочной пары «результат в локальную и обратно».</summary>
        private static List<(OpCode Op, int Token)> DecodeIl(MethodInfo method)
        {
            byte[] body = method.GetMethodBody()?.GetILAsByteArray() ?? Array.Empty<byte>();
            var all = new List<(OpCode Op, int Token)>();
            for (int at = 0; at < body.Length;)
            {
                short value = body[at] == 0xFE ? (short)(0xFE00 | body[at + 1]) : body[at];
                at += body[at] == 0xFE ? 2 : 1;
                OpCode op = OpByValue[value];
                int size = OperandBytes(op.OperandType);
                int token = size == 4 ? BitConverter.ToInt32(body, at) : 0;
                Assert.True(op.OperandType != OperandType.InlineSwitch, $"{method.Name}: switch в листе");
                at += size;
                all.Add((op, token));
            }

            // Отладочная сборка: «stloc.0; br.s +0; ldloc.0» перед ret — то же, что сразу ret.
            var il = all.FindAll(i => i.Op != OpCodes.Nop);
            int n = il.Count;
            if (n >= 4 && il[n - 4].Op == OpCodes.Stloc_0 && il[n - 3].Op == OpCodes.Br_S && il[n - 2].Op == OpCodes.Ldloc_0 && il[n - 1].Op == OpCodes.Ret)
            {
                il.RemoveRange(n - 4, 3);
            }

            return il;
        }

        private static int OperandBytes(OperandType type)
        {
            switch (type)
            {
                case OperandType.InlineNone:
                    return 0;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar:
                    return 1;
                case OperandType.InlineVar:
                    return 2;
                case OperandType.InlineI8:
                case OperandType.InlineR:
                    return 8;
                default:
                    return 4;
            }
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
