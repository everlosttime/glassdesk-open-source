using System.IO;
using System.Text;
using Microsoft.Win32;

namespace GlassDesk.UI.Controls;

internal sealed class WindowsWallpaperCache
{
    private static readonly Encoding IdentityEncoding = new UnicodeEncoding(false, false, true);
    private readonly string _cachePath;
    private readonly Func<(byte[]? Identity, string? Wallpaper)> _readIdentity;

    internal WindowsWallpaperCache()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Windows", "Themes", "TranscodedWallpaper"), ReadIdentity) { }

    internal WindowsWallpaperCache(string cachePath, Func<(byte[]? Identity, string? Wallpaper)> readIdentity)
    {
        _cachePath = cachePath;
        _readIdentity = readIdentity;
    }

    internal string? Resolve(string originalPath, int monitorCount)
    {
        if (monitorCount != 1 || string.IsNullOrWhiteSpace(originalPath)) return null;
        try
        {
            var original = Normalize(originalPath);
            if (original is null) return null;
            var (identity, wallpaper) = _readIdentity();
            if (identity is null || identity.Length is < 26 or > 65_536 || (identity.Length - 24) % 2 != 0)
                return null;
            var text = IdentityEncoding.GetString(identity, 24, identity.Length - 24);
            var terminator = text.IndexOf('\0');
            if (terminator <= 0) return null;
            var cachedOriginal = Normalize(text[..terminator]);
            if (!string.Equals(original, cachedOriginal, StringComparison.OrdinalIgnoreCase)) return null;
            if (!string.IsNullOrEmpty(wallpaper))
            {
                var registered = Normalize(wallpaper);
                if (!string.Equals(original, registered, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(Normalize(_cachePath), registered, StringComparison.OrdinalIgnoreCase)) return null;
            }
            var cache = new FileInfo(_cachePath);
            return cache.Exists && cache.Length is > 0 and <= 33_554_432 ? cache.FullName : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static string? Normalize(string path)
        => Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : null;

    private static (byte[]? Identity, string? Wallpaper) ReadIdentity()
    {
        using var desktop = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", false);
        return (desktop?.GetValue("TranscodedImageCache") as byte[], desktop?.GetValue("Wallpaper") as string);
    }
}
