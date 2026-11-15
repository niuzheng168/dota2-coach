using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace DotaCoach.Desktop;

public record ProcessChoice(int pid, string name, string startedAt);

internal static class ProcessCatalog
{
    // Exclude our entire ancestor/descendant tree, so a parent such as Explorer cannot recapture the app.
    internal static IReadOnlyList<ProcessChoice> List()
    {
        var parents = Parents();
        var forbidden = new HashSet<int> { Environment.ProcessId };
        int cursor = Environment.ProcessId;
        while (parents.TryGetValue(cursor, out int parent) && parent > 0 && forbidden.Add(parent)) cursor = parent;
        using var self = Process.GetCurrentProcess();
        var choices = new List<ProcessChoice>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    cursor = process.Id;
                    var visited = new HashSet<int>();
                    bool ours = false;
                    while (visited.Add(cursor) && parents.TryGetValue(cursor, out int parent))
                    {
                        if (parent == Environment.ProcessId) { ours = true; break; }
                        cursor = parent;
                    }
                    if (ours || forbidden.Contains(process.Id) || process.SessionId != self.SessionId ||
                        process.ProcessName.Equals("msedgewebview2", StringComparison.OrdinalIgnoreCase)) continue;
                    choices.Add(new(process.Id, process.ProcessName + ".exe", process.StartTime.ToUniversalTime().Ticks.ToString()));
                }
                catch { /* Exited/protected processes cannot be selected. */ }
            }
        }
        return choices.OrderBy(p => p.name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.pid).Take(512).ToArray();
    }

    private static Dictionary<int, int> Parents()
    {
        var result = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == new IntPtr(-1)) throw new InvalidOperationException("无法枚举进程，请重试。");
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            if (Process32First(snapshot, ref entry))
                do { result[(int)entry.Pid] = (int)entry.ParentPid; } while (Process32Next(snapshot, ref entry));
        }
        finally { CloseHandle(snapshot); }
        return result;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, Pid;
        public UIntPtr DefaultHeap;
        public uint ModuleId, Threads, ParentPid;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
    }
    [DllImport("kernel32.dll")] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
