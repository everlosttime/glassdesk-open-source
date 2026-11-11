/// <summary>Bounds a native readback without translating PrintWindow's window-local coordinates.</summary>
internal static class WallpaperReadbackExtent
{
    internal static ScreenRect LimitToPrimary(ScreenRect source,ScreenRect primary,ScreenRect requested)
    {
        // Other displays and partially covering wallpaper windows retain their
        // original path. Every pixel used by a primary-screen crop must exist.
        if(!Contains(primary,requested) || !Contains(source,primary))return source;
        // PrintWindow can ignore DC origins. Retain the source's leading rows /
        // columns, and truncate only past the primary's right and bottom edges.
        // In a right-hand secondary layout this excludes its pixels entirely.
        var width=(long)primary.X+primary.Width-source.X;
        var height=(long)primary.Y+primary.Height-source.Y;
        return new(source.X,source.Y,(int)width,(int)height);
    }

    private static bool Contains(ScreenRect outer,ScreenRect inner)
        =>outer.Width>0 && outer.Height>0 && inner.Width>0 && inner.Height>0
            && inner.X>=outer.X && inner.Y>=outer.Y
            && (long)inner.X+inner.Width<=(long)outer.X+outer.Width
            && (long)inner.Y+inner.Height<=(long)outer.Y+outer.Height;
}
