using System.Diagnostics;
using System.IO;
using System.Text.Json;
using GlassDesk.Core.Storage;
using GlassDesk.Platform;
using GlassDesk.Platform.Displays;

namespace GlassDesk.App;

internal sealed class StageParking(string journal)
{
    private readonly Dictionary<nint, Entry> _entries = [];
    private readonly HashSet<nint> _restoring = [];
    private Task _journalWrite = Task.CompletedTask;
    private sealed record Entry(StageWindowIdentity Identity, StageWindowPlacement Placement, int Token);

    public bool Contains(nint handle) => _entries.ContainsKey(handle);

    public void Recover()
    {
        try
        {
            if (!File.Exists(journal)) return;
            foreach (var entry in JsonSerializer.Deserialize<Entry[]>(File.ReadAllText(journal)) ?? [])
            {
                if (entry.Token <= 0 || !StageWindows.HasParkingMark(entry.Identity, entry.Token)) continue;
                _entries[entry.Identity.Hwnd] = entry;
                if (StageWindows.IsMinimized(entry.Identity.Hwnd)) Restore(entry.Identity.Hwnd);
                else Forget(entry.Identity.Hwnd);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            Trace.TraceWarning("Stage Manager recovery: {0}", error.Message);
        }
    }

    public async Task ParkAsync(IEnumerable<StageWindow> windows, Func<StageWindowIdentity, bool>? stillApplicable = null,
        CancellationToken cancellation = default)
    {
        if (cancellation.IsCancellationRequested) return;
        var pending = new List<Entry>();
        foreach (var window in windows)
        {
            if (window.Minimized || _entries.ContainsKey(window.Handle)) continue;
            var token = Random.Shared.Next(1, int.MaxValue);
            if (!StageWindows.MarkParked(window.Identity, token)) continue;
            var entry = new Entry(window.Identity, window.Placement, token);
            pending.Add(entry);
            _entries[window.Handle] = entry;
        }
        if (pending.Count == 0) return;
        var previous = _journalWrite;
        var entries = _entries.Values.ToArray();
        var written = Task.Run(async () =>
        {
            await previous.ConfigureAwait(false);
            return Save(entries);
        });
        _journalWrite = written;

        // The disk operation may outlast a switch request. Check both the latest
        // intent and the identity of each reservation before minimizing a window.
        var saved = await written;
        var applicable = saved && !cancellation.IsCancellationRequested;
        foreach (var entry in pending)
        {
            var handle = entry.Identity.Hwnd;
            if (_entries.GetValueOrDefault(handle) != entry) continue;
            if (!applicable || stillApplicable?.Invoke(entry.Identity) == false
                || StageWindows.IsMinimized(handle) || !StageWindows.Minimize(entry.Identity))
                Forget(handle);
        }
    }

    public void Restore(nint handle, bool placementFallback = false)
    {
        if (!_entries.TryGetValue(handle, out var entry)) return;
        if (!StageWindows.HasParkingMark(entry.Identity, entry.Token))
        {
            Forget(handle);
            return;
        }
        var posted = placementFallback
            ? StageWindows.RestorePlacement(entry.Identity, entry.Placement)
            : StageWindows.Restore(entry.Identity, entry.Placement);
        if (posted) _restoring.Add(handle);
    }

    public void RestoreAll()
    {
        foreach (var handle in _entries.Keys.ToArray()) Restore(handle);
    }

    // Clear only after Windows has confirmed the restore or the window is gone.
    public void Forget(nint handle)
    {
        _restoring.Remove(handle);
        if (_entries.Remove(handle, out var entry))
            StageWindows.ClearParkingMark(entry.Identity, entry.Token);
    }

    public void ObserveRestored()
    {
        foreach (var handle in _restoring.ToArray())
            if (!StageWindows.IsMinimized(handle) && DisplayService.GetWindowPixelBounds(handle) is { } bounds
                && (bounds.X, bounds.Y) != (-32000, -32000))
                Forget(handle);
    }

    public void RemoveClosed()
    {
        foreach (var entry in _entries.Values.ToArray())
            if (!StageWindows.HasParkingMark(entry.Identity, entry.Token)) Forget(entry.Identity.Hwnd);
    }

    private bool Save(Entry[] entries)
    {
        try
        {
            AtomicFile.WriteAllText(journal, JsonSerializer.Serialize(entries));
            return true;
        }
        catch (Exception error) when (error is StorageException or IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Stage Manager journal: {0}", error.Message);
            return false;
        }
    }
}
