using GlassDesk.Core.Storage;

namespace GlassDesk.AppServices;

/// <summary>Transactional managed icons for pinned applications and the three built-in Dock entries.</summary>
public sealed class DockIconCustomization(GlassDeskDataStore data, Action changed)
{
    public static bool IsBuiltin(string id) => id is "entry:launcher" or "entry:this-pc" or "entry:desktop-icons" or "entry:nearby";

    public string? GetIconPath(string id) => id switch
    {
        "entry:launcher" => data.Settings.Current.Dock.LauncherCustomIconPath,
        "entry:this-pc" => data.Settings.Current.Dock.ThisPcCustomIconPath,
        "entry:desktop-icons" => data.Settings.Current.Dock.DesktopIconsCustomIconPath,
        "entry:nearby" => data.Settings.Current.Dock.NearbyCustomIconPath,
        _ => data.Library.Find(id)?.CustomIconPath,
    };

    public bool SetIcon(string id, string? source)
    {
        var builtin = IsBuiltin(id);
        var entry = builtin ? null : data.Library.Find(id);
        if (!builtin && entry is null) return false;
        var previous = GetIconPath(id);
        string? staged = null;
        if (!string.IsNullOrWhiteSpace(source))
        {
            var format = ImageFormatSniffer.Detect(source);
            if (format is null) return false;
            // A fresh name keeps a failed save from overwriting the previous icon's pixels.
            staged = Path.Combine(data.Paths.IconCacheDirectory,
                AppPaths.ManagedCustomIconPrefix + Guid.NewGuid().ToString("N") + ImageFormatSniffer.FileExtensionFor(format.Value));
            try
            {
                Directory.CreateDirectory(data.Paths.IconCacheDirectory);
                File.Copy(source, staged);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                DeleteManaged(staged);
                return false;
            }
        }

        void Assign(string? value)
        {
            switch (id)
            {
                case "entry:launcher": data.Settings.Current.Dock.LauncherCustomIconPath = value; break;
                case "entry:this-pc": data.Settings.Current.Dock.ThisPcCustomIconPath = value; break;
                case "entry:desktop-icons": data.Settings.Current.Dock.DesktopIconsCustomIconPath = value; break;
                case "entry:nearby": data.Settings.Current.Dock.NearbyCustomIconPath = value; break;
                default: entry!.CustomIconPath = value; data.Library.Update(entry); break;
            }
        }

        try
        {
            Assign(staged);
            if (builtin) data.Settings.Save(); else data.Library.Save();
        }
        catch (Exception error) when (error is StorageException or IOException or UnauthorizedAccessException)
        {
            Assign(previous);
            DeleteManaged(staged);
            return false;
        }
        DeleteManaged(previous);
        changed();
        return true;
    }

    private void DeleteManaged(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFullPath(data.Paths.IconCacheDirectory), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(path).StartsWith(AppPaths.ManagedCustomIconPrefix, StringComparison.OrdinalIgnoreCase)) File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
    }
}
