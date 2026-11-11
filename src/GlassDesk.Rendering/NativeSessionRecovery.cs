using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace GlassDesk.Rendering;

/// <summary>Explicit probe opt-in. An unclean session disables the next native launch.</summary>
public sealed class NativeSessionRecovery : IDisposable
{
    private readonly string _path;
    private FileStream? _marker;
    private NativeSessionRecovery(string path, FileStream marker) { _path = path; _marker = marker; }
    public static NativeSessionRecovery? TryBegin(string stateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        var directory = Path.GetFullPath(stateDirectory);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "native-session-active.json");
        FileStream marker;
        try { marker = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read); }
        catch (IOException) when (File.Exists(path)) { return null; }
        try
        {
            JsonSerializer.Serialize(marker, new { processId = Environment.ProcessId, startedUtc = DateTimeOffset.UtcNow,
                processStartedUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime(), abi = 1 });
            marker.Flush(flushToDisk: true);
            return new(path, marker);
        }
        catch { marker.Dispose(); throw; } // An incomplete marker is deliberately conservative.
    }
    public void Dispose()
    {
        var marker = Interlocked.Exchange(ref _marker, null);
        if (marker is null) return;
        marker.Dispose();
        File.Delete(_path);
    }
}
