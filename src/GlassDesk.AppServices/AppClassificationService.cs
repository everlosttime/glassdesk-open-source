using GlassDesk.Core.Models;

namespace GlassDesk.AppServices;

/// <summary>Classifies resolved desktop item types without inspecting executable product metadata.</summary>
public static class AppClassificationService
{
    public static LauncherClassificationResult Classify(AppEntry entry, string? description = null, string? protocolTarget = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (protocolTarget is null && entry.AutomaticSourceKey?.StartsWith("protocol:", StringComparison.Ordinal) == true)
            protocolTarget = entry.AutomaticSourceKey[9..];
        var resolved = entry;
        if (entry.Kind == AppItemKind.Shortcut && entry.Target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            resolved = AppEntryFactory.TryCreateFromPath(entry.Target, entry.DisplayName).Entry ?? entry;
        return LauncherAppClassifier.Classify(new()
        {
            Kind = resolved.Kind, DisplayName = entry.DisplayName, Target = protocolTarget ?? resolved.Target,
            Arguments = resolved.Arguments, AppUserModelId = resolved.AppUserModelId,
            Description = description,
        });
    }
}
