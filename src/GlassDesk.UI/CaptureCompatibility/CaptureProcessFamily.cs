using System.Collections.Concurrent;
using System.Diagnostics;

internal static class CaptureProcessFamily
{
    private static readonly ConcurrentDictionary<int, byte> Workers = new();
    private static readonly ConcurrentDictionary<int, byte> PendingRetirement = new();
    private static readonly object Gate = new();
    private static int[] _otherProductProcesses = [];
    private static long _refreshAt;
    internal static void Register(int pid) => Workers.TryAdd(pid, 0);
    internal static void Unregister(int pid) => Workers.TryRemove(pid, out _);
    internal static void Quarantine(int pid) => PendingRetirement.TryAdd(pid, 0);
    internal static void ConfirmRetirement(int pid) { PendingRetirement.TryRemove(pid, out _); Unregister(pid); }
    internal static void EnsureRetirementConfirmed()
    {
        if (!PendingRetirement.IsEmpty)
            throw new InvalidOperationException("Previous capture worker exit is still pending; new ownership is disabled.");
    }
    internal static int[] WorkerProcesses => Workers.Keys.Order().ToArray();
    internal static int PendingRetirementCount => PendingRetirement.Count;
    internal static int[] KnownProcesses => Workers.Keys.Concat(Volatile.Read(ref _otherProductProcesses))
        .Append(Environment.ProcessId).Distinct().Order().ToArray();
    internal static int[] Snapshot()
    {
        lock (Gate)
        {
            if (Stopwatch.GetTimestamp() >= _refreshAt)
            {
                _refreshAt = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
                var processes = Process.GetProcessesByName("GlassDesk");
                try { Volatile.Write(ref _otherProductProcesses, processes.Select(p => p.Id).ToArray()); }
                finally { foreach (var process in processes) process.Dispose(); }
            }
        }
        return KnownProcesses;
    }
}
