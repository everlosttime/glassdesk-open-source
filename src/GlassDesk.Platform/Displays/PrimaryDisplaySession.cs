namespace GlassDesk.Platform.Displays;

/// <summary>
/// The display selected by Windows as Primary when the process starts. Its device identity
/// is retained for diagnostics. The host can explicitly recover the shared session after
/// cancelling transitions and retiring background resources when Windows changes Primary.
/// </summary>
public sealed class PrimaryDisplaySession
{
    private DisplayInfo _display;
    private int _requiresRestart;

    public string StartupDeviceName { get; }

    public DisplayInfo Display => Volatile.Read(ref _display);

    public bool IsAvailable => Volatile.Read(ref _requiresRestart) == 0;

    public bool RequiresRestart => !IsAvailable;

    public PrimaryDisplaySession(DisplayInfo startupPrimary)
    {
        ArgumentNullException.ThrowIfNull(startupPrimary);
        if (!startupPrimary.IsPrimary)
            throw new ArgumentException("The display session must be captured from the current Primary Monitor.", nameof(startupPrimary));

        _display = startupPrimary;
        StartupDeviceName = startupPrimary.DeviceName;
    }

    /// <summary>
    /// Refreshes metrics only while the startup device remains Windows Primary. A Primary
    /// identity change permanently invalidates this session so the process must restart.
    /// </summary>
    public bool TryRefreshPrimary(DisplayInfo? currentPrimary)
    {
        if (RequiresRestart) return false;
        if (currentPrimary is null || !currentPrimary.IsPrimary
            || !string.Equals(currentPrimary.DeviceName, Display.DeviceName, StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Exchange(ref _requiresRestart, 1);
            return false;
        }

        Volatile.Write(ref _display, currentPrimary);
        return true;
    }

    /// <summary>Called by the host on its dispatcher after suspending old display interaction.</summary>
    public bool TryRebindPrimary(DisplayInfo? currentPrimary)
    {
        if (currentPrimary is null || !currentPrimary.IsPrimary)
        {
            Interlocked.Exchange(ref _requiresRestart, 1);
            return false;
        }
        Volatile.Write(ref _display, currentPrimary);
        Interlocked.Exchange(ref _requiresRestart, 0);
        return true;
    }

    public bool ContainsPrimaryPoint(int x, int y) => Display.Bounds.Contains(x, y);

    public PixelRect ClipToPrimaryBounds(PixelRect region) => Clip(region, Display.Bounds);

    public PixelRect ClampWindowToWorkArea(PixelRect requested)
    {
        var area = Display.WorkArea;
        var width = Math.Max(1, Math.Min(requested.Width, area.Width));
        var height = Math.Max(1, Math.Min(requested.Height, area.Height));
        var x = Math.Clamp(requested.X, area.X, area.Right - width);
        var y = Math.Clamp(requested.Y, area.Y, area.Bottom - height);
        return new PixelRect(x, y, width, height);
    }

    public bool ContainsInWorkArea(PixelRect rect)
    {
        var area = Display.WorkArea;
        return rect.Width > 0 && rect.Height > 0
            && rect.X >= area.X && rect.Y >= area.Y
            && rect.Right <= area.Right && rect.Bottom <= area.Bottom;
    }

    private static PixelRect Clip(PixelRect rect, PixelRect bounds)
    {
        var left = Math.Max(rect.X, bounds.X);
        var top = Math.Max(rect.Y, bounds.Y);
        var right = Math.Min(rect.Right, bounds.Right);
        var bottom = Math.Min(rect.Bottom, bounds.Bottom);
        if (right <= left || bottom <= top)
            return new PixelRect(Math.Clamp(left, bounds.X, bounds.Right), Math.Clamp(top, bounds.Y, bounds.Bottom), 0, 0);
        return new PixelRect(left, top, right - left, bottom - top);
    }
}
