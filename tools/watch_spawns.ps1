# Наблюдатель за рождением процессов (C-022) — без прав администратора.
# Раз в -IntervalMs (по умолчанию 200 мс) снимает список процессов (Toolhelp32, то же, что Win32_Process, но
# без WMI и в ~100 раз быстрее) и для каждого нового процесса читает из его PEB (свой пользователь — ReadProcessMemory
# без админа) рабочий стол (RTL_USER_PROCESS_PARAMETERS.DesktopInfo), флаги STARTUPINFO и путь exe.
# conhost.exe приписывается своему родителю (консольному процессу, для которого он рождён).
# Использование:
#   powershell -NoProfile -File tools\watch_spawns.ps1 -DurationSec 120 -Filter tc022 -OutFile build\logs\spawns.tsv
#   (или -StopFile <путь>: наблюдатель выходит, когда файл появится)
# Сводка в конце: сколько новых процессов «своих» (путь exe или путь родителя conhost содержит -Filter) и на каком столе.
param(
    [int]$DurationSec = 120,
    [int]$IntervalMs = 200,
    [string]$Filter = '',
    [string]$OutFile = '',
    [string]$StopFile = ''
)

Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public sealed class SpawnRecord
{
    public DateTime Time; public int Pid; public int ParentPid; public string Name; public string Path;
    public string Desktop; public uint WindowFlags; public string ParentPath;
}

public static class SpawnWatcher
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PROCESSENTRY32W
    {
        public int dwSize; public int cntUsage; public int th32ProcessID; public IntPtr th32DefaultHeapID;
        public int th32ModuleID; public int cntThreads; public int th32ParentProcessID; public int pcPriClassBase;
        public int dwFlags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr ExitStatus, PebBaseAddress, AffinityMask, BasePriority, UniqueProcessId, InheritedFromUniqueProcessId;
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr h, ref PROCESSENTRY32W e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr h, ref PROCESSENTRY32W e);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageNameW(IntPtr h, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr read);
    [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(IntPtr h, int cls, ref PROCESS_BASIC_INFORMATION pbi, int size, out int len);

    const uint QUERY_LIMITED = 0x1000, VM_READ = 0x0010;

    static Dictionary<int, string> Snapshot(Dictionary<int, int> parents)
    {
        var map = new Dictionary<int, string>();
        IntPtr snap = CreateToolhelp32Snapshot(2, 0);
        if (snap == new IntPtr(-1)) return map;
        try
        {
            var e = new PROCESSENTRY32W(); e.dwSize = Marshal.SizeOf(typeof(PROCESSENTRY32W));
            for (bool ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e))
            {
                map[e.th32ProcessID] = e.szExeFile;
                parents[e.th32ProcessID] = e.th32ParentProcessID;
            }
        }
        finally { CloseHandle(snap); }
        return map;
    }

    static string ImagePath(int pid)
    {
        IntPtr h = OpenProcess(QUERY_LIMITED, false, pid);
        if (h == IntPtr.Zero) return "";
        try
        {
            var sb = new StringBuilder(1024); int n = sb.Capacity;
            return QueryFullProcessImageNameW(h, 0, sb, ref n) ? sb.ToString() : "";
        }
        finally { CloseHandle(h); }
    }

    static byte[] Read(IntPtr h, long addr, int n)
    {
        var b = new byte[n]; IntPtr got;
        return ReadProcessMemory(h, new IntPtr(addr), b, new IntPtr(n), out got) && got.ToInt64() == n ? b : null;
    }

    // x64: PEB+0x20 → ProcessParameters; там WindowFlags +0xA4, DesktopInfo (UNICODE_STRING) +0xC0.
    static void ReadParams(int pid, out string desktop, out uint flags)
    {
        desktop = "?"; flags = 0;
        IntPtr h = OpenProcess(QUERY_LIMITED | VM_READ, false, pid);
        if (h == IntPtr.Zero) { desktop = "?нет доступа"; return; }
        try
        {
            var pbi = new PROCESS_BASIC_INFORMATION(); int len;
            if (NtQueryInformationProcess(h, 0, ref pbi, Marshal.SizeOf(typeof(PROCESS_BASIC_INFORMATION)), out len) != 0) return;
            byte[] p = Read(h, pbi.PebBaseAddress.ToInt64() + 0x20, 8); if (p == null) return;
            long pp = BitConverter.ToInt64(p, 0);
            byte[] f = Read(h, pp + 0xA4, 4); if (f != null) flags = BitConverter.ToUInt32(f, 0);
            byte[] us = Read(h, pp + 0xC0, 16); if (us == null) return;
            int bytes = BitConverter.ToUInt16(us, 0); long buf = BitConverter.ToInt64(us, 8);
            if (bytes == 0) { desktop = ""; return; }
            byte[] s = Read(h, buf, bytes); desktop = s == null ? "?" : Encoding.Unicode.GetString(s);
        }
        finally { CloseHandle(h); }
    }

    public static List<SpawnRecord> Watch(int durationMs, int intervalMs, string stopFile, Action<SpawnRecord> onNew)
    {
        var parents = new Dictionary<int, int>();
        var known = Snapshot(parents);
        var paths = new Dictionary<int, string>();
        var list = new List<SpawnRecord>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < durationMs && (string.IsNullOrEmpty(stopFile) || !System.IO.File.Exists(stopFile)))
        {
            System.Threading.Thread.Sleep(intervalMs);
            var now = Snapshot(parents);
            foreach (var kv in now)
            {
                string old;
                if (known.TryGetValue(kv.Key, out old) && old == kv.Value) continue;
                var r = new SpawnRecord();
                r.Time = DateTime.Now; r.Pid = kv.Key; r.Name = kv.Value; r.ParentPid = parents[kv.Key];
                r.Path = ImagePath(kv.Key); paths[kv.Key] = r.Path;
                ReadParams(kv.Key, out r.Desktop, out r.WindowFlags);
                string pp;
                if (!paths.TryGetValue(r.ParentPid, out pp)) { pp = ImagePath(r.ParentPid); paths[r.ParentPid] = pp; }
                r.ParentPath = pp;
                list.Add(r);
                if (onNew != null) onNew(r);
            }
            known = now;
        }
        return list;
    }
}
"@

try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
$utf8 = New-Object System.Text.UTF8Encoding($false)
if ($OutFile -ne '') {
    [System.IO.File]::WriteAllText($OutFile, "time`tpid`tppid`tname`tdesktop`tflags`tpath`tparent`n", $utf8)
}
$log = {
    param($r)
    $line = "{0:HH:mm:ss.fff}`t{1}`t{2}`t{3}`t{4}`t0x{5:X}`t{6}`t{7}" -f $r.Time, $r.Pid, $r.ParentPid, $r.Name, $r.Desktop, $r.WindowFlags, $r.Path, $r.ParentPath
    if ($OutFile -ne '') { [System.IO.File]::AppendAllText($OutFile, $line + "`n", $utf8) }
}
$records = [SpawnWatcher]::Watch($DurationSec * 1000, $IntervalMs, $StopFile, [Action[SpawnRecord]]$log)

$mine = @($records | Where-Object {
    $Filter -eq '' -or $_.Path -like "*$Filter*" -or ($_.Name -eq 'conhost.exe' -and $_.ParentPath -like "*$Filter*")
})
"новых процессов всего: {0}; своих (фильтр '{1}'): {2}" -f $records.Count, $Filter, $mine.Count
$mine | Group-Object Name, Desktop | Sort-Object Count -Descending |
    ForEach-Object { "  {0,4}  {1}" -f $_.Count, $_.Name }
# conhost на столе Автора: главное число замера C-022.
"conhost на WinSta0\Default (свои): {0}" -f @($mine | Where-Object { $_.Name -eq 'conhost.exe' -and $_.Desktop -like '*\Default' }).Count
