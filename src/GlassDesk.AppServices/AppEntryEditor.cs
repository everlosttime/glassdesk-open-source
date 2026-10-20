using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;

namespace GlassDesk.AppServices;

/// <summary>编辑一条共享应用库记录，并在保存失败时恢复原对象。</summary>
public sealed class AppEntryEditor(AppLibraryStore library, AppPaths paths, Action changed)
{
    public bool Edit(string id, string displayName, IReadOnlyList<string> aliases, string? iconFile)
    {
        var old = library.Find(id);
        var name = displayName.Trim();
        if (old is null || name.Length is < 1 or > 120) return false;
        var filteredAliases = aliases.Select(value => value.Trim()).Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string? staged = null;
        if (!string.IsNullOrWhiteSpace(iconFile))
        {
            var format = ImageFormatSniffer.Detect(iconFile);
            if (format is null) return false;
            var extension = ImageFormatSniffer.FileExtensionFor(format.Value);
            staged = Path.Combine(paths.IconCacheDirectory,
                AppPaths.ManagedCustomIconPrefix + id + "-" + Guid.NewGuid().ToString("n") + extension);
            Directory.CreateDirectory(paths.IconCacheDirectory);
            File.Copy(iconFile, staged);
        }
        var edited = new AppEntry
        {
            Id = old.Id, DisplayName = name, Kind = old.Kind, Target = old.Target,
            Arguments = old.Arguments, WorkingDirectory = old.WorkingDirectory,
            AppUserModelId = old.AppUserModelId, IconSource = old.IconSource,
            CustomIconPath = staged ?? old.CustomIconPath,
            SearchAliases = filteredAliases, PinyinIndex = null, PinyinInitials = null,
            IsTargetMissing = old.IsTargetMissing, AddedAtUtc = old.AddedAtUtc,
        };
        if (!library.Update(edited))
        {
            if (staged is not null) File.Delete(staged);
            return false;
        }
        try { library.Save(); }
        catch
        {
            library.Update(old);
            if (staged is not null) File.Delete(staged);
            throw;
        }
        if (staged is not null && old.CustomIconPath is { } previous && File.Exists(previous)
            && Path.GetFullPath(previous).StartsWith(
                Path.GetFullPath(paths.IconCacheDirectory) + Path.DirectorySeparatorChar + AppPaths.ManagedCustomIconPrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(previous); } catch (IOException) { }
        }
        changed();
        return true;
    }
}
