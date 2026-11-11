namespace GlassDesk.Platform;

internal static class MediaSourceFilter
{
    internal static bool IsExcluded(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return false;
        // Match application identity, never a song title containing "wallpaper".
        var identity = source.Trim().Replace('/', '\\');
        identity = identity[(identity.LastIndexOf('\\') + 1)..].Split('!', 2)[0];
        if (identity.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) identity = identity[..^4];
        return identity.Equals("wallpaper32", StringComparison.OrdinalIgnoreCase)
            || identity.Equals("wallpaper64", StringComparison.OrdinalIgnoreCase)
            || identity.Equals("wallpaperservice32", StringComparison.OrdinalIgnoreCase)
            || identity.Equals("wallpaperservice64", StringComparison.OrdinalIgnoreCase)
            || identity.Equals("webwallpaper32", StringComparison.OrdinalIgnoreCase)
            || identity.Equals("webwallpaper64", StringComparison.OrdinalIgnoreCase)
            || identity.Equals("WallpaperEngine", StringComparison.OrdinalIgnoreCase)
            || identity.Equals("Wallpaper Engine", StringComparison.OrdinalIgnoreCase);
    }
}
