using GlassDesk.Core.Models;
using GlassDesk.Platform.Shell;

namespace GlassDesk.AppServices;

public sealed record InternetShortcutIconRepair(string AppId, string Target, string? PreviousIconSource, string IconSource);

public sealed partial class LibraryMaintenance
{
    /// <summary>Snapshot on the host thread, then resolve and cache explicit local icons on a worker.</summary>
    public Task<IReadOnlyList<InternetShortcutIconRepair>> PrepareInternetShortcutIconRepairsAsync(CancellationToken token = default)
    {
        var snapshots = _library.Items.Where(entry => entry is { IsUsable: true }
            && string.IsNullOrWhiteSpace(entry.CustomIconPath)
            && Path.GetExtension(entry.Target).Equals(".url", StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(entry.IconSource)
                || Path.GetExtension(IconExtractor.SplitIconSource(entry.IconSource).Path).Equals(".url", StringComparison.OrdinalIgnoreCase)))
            .Select(entry => new InternetShortcutIconRepair(entry.Id, entry.Target, entry.IconSource, string.Empty)).ToArray();
        return Task.Run<IReadOnlyList<InternetShortcutIconRepair>>(() =>
        {
            var repairs = new List<InternetShortcutIconRepair>();
            foreach (var snapshot in snapshots)
            {
                token.ThrowIfCancellationRequested();
                var source = InternetShortcutReader.ReadIconSource(snapshot.Target);
                if (source is null || string.Equals(source, snapshot.PreviousIconSource, StringComparison.OrdinalIgnoreCase)) continue;
                // Never replace a working source with an icon that cannot actually be extracted.
                try
                {
                    if (_icons.EnsureFor(source) is not null) repairs.Add(snapshot with { IconSource = source });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                    or NotSupportedException or System.Runtime.InteropServices.ExternalException) { }
            }
            token.ThrowIfCancellationRequested();
            return repairs;
        }, token);
    }

    /// <summary>Commit on the host thread. Preserve user changes made while preparation was running.</summary>
    public int ApplyInternetShortcutIconRepairs(IReadOnlyList<InternetShortcutIconRepair> repairs)
    {
        ArgumentNullException.ThrowIfNull(repairs);
        var originals = new List<AppEntry>();
        foreach (var repair in repairs)
        {
            var current = _library.Find(repair.AppId);
            if (current is null || !string.IsNullOrWhiteSpace(current.CustomIconPath)
                || current.Target != repair.Target || current.IconSource != repair.PreviousIconSource) continue;
            var updated = LauncherAutoOrganizationService.Clone(current);
            updated.IconSource = repair.IconSource;
            if (_library.Update(updated)) originals.Add(current);
        }
        if (originals.Count == 0) return 0;
        try { _library.Save(); }
        catch { foreach (var original in originals) _library.Update(original); throw; }
        _observer?.OnLibraryChanged();
        return originals.Count;
    }
}
