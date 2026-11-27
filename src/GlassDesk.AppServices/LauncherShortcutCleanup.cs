using GlassDesk.Core.Models;

namespace GlassDesk.AppServices;

/// <summary>Checks paths off-thread and applies confirmed removals on the caller's host thread.</summary>
public sealed class LauncherShortcutCleanup
{
    public static TimeSpan CheckInterval { get; } = TimeSpan.FromMinutes(1);
    // The dispatcher timer starts before the initial scan and uses a different clock.
    private static readonly TimeSpan MinimumConfirmationAge = TimeSpan.FromSeconds(55);

    private sealed record Shortcut(string Id, string Target);

    private readonly AppServiceHost _host;
    private readonly TimeProvider _clock;
    private Dictionary<Shortcut, long> _missingSince = [];
    private int _generation;
    private bool _checking;

    internal LauncherShortcutCleanup(AppServiceHost host, TimeProvider? clock = null)
    {
        _host = host;
        _clock = clock ?? TimeProvider.System;
    }

    public void Reset()
    {
        _generation++;
        _missingSince.Clear();
    }

    public async Task<int> SweepAsync(CancellationToken cancellationToken = default)
    {
        if (!_host.LauncherSettings.AutoRemoveBrokenShortcutsEnabled)
        {
            Reset();
            return 0;
        }
        if (_checking) return 0;

        _checking = true;
        try
        {
            var generation = _generation;
            var now = _clock.GetTimestamp();
            var libraryVersion = _host.Library.Version;
            var layoutVersion = _host.Layouts.LauncherVersion;
            var candidates = LauncherLayoutRules.AppIds(_host.Layouts.Launcher).Distinct(StringComparer.Ordinal)
                .Select(_host.Library.Find)
                .OfType<AppEntry>()
                .Where(entry => entry.Kind != AppItemKind.Url && entry.IsUsable)
                .Select(entry => new Shortcut(entry.Id, entry.Target)).ToArray();
            var missing = await Task.Run(() => candidates.Where(shortcut =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return IsConfirmedMissingLocalPath(shortcut.Target);
            }).ToArray(), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var settings = _host.LauncherSettings;
            if (generation != _generation || !settings.AutoRemoveBrokenShortcutsEnabled
                || libraryVersion != _host.Library.Version || layoutVersion != _host.Layouts.LauncherVersion) return 0;

            _missingSince = missing.ToDictionary(shortcut => shortcut, shortcut => _missingSince.GetValueOrDefault(shortcut, now));
            var confirmed = _missingSince.Where(item => _clock.GetElapsedTime(item.Value, now) >= MinimumConfirmationAge)
                .Select(item => item.Key.Id).ToHashSet(StringComparer.Ordinal);
            if (confirmed.Count == 0) return 0;

            var capacity = Math.Clamp(settings.Rows, 2, 10) * Math.Clamp(settings.Columns, 2, 12);
            var draft = LauncherLayoutEditor.RemoveAppShortcuts(_host.Layouts.Launcher, confirmed, capacity);
            if (!_host.Layouts.CommitLauncher(draft, layoutVersion)) return 0;
            foreach (var shortcut in missing.Where(shortcut => confirmed.Contains(shortcut.Id))) _missingSince.Remove(shortcut);
            _host.OnLibraryChanged();
            return confirmed.Count;
        }
        finally { _checking = false; }
    }

    private static bool IsConfirmedMissingLocalPath(string target)
    {
        if (!Path.IsPathFullyQualified(target)) return false;
        try
        {
            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
            var root = Path.GetPathRoot(path);
            if (root is not { Length: 3 } || !root.EndsWith(":\\", StringComparison.Ordinal)) return false;
            var drive = new DriveInfo(root);
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady) return false;

            // Walk from the root so we never traverse a junction, mounted drive or cloud placeholder.
            var components = new Stack<string>();
            for (string? current = path; current is not null && current != root; current = Path.GetDirectoryName(current))
                components.Push(current);
            while (components.TryPop(out var component))
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(component); }
                catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
                {
                    return drive.DriveType == DriveType.Fixed && drive.IsReady;
                }
                if ((attributes & FileAttributes.ReparsePoint) != 0
                    || components.Count > 0 && (attributes & FileAttributes.Directory) == 0) return false;
            }
            return false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
