using GlassDesk.Platform;

internal enum DesktopSceneProvider { IsolatedSharedVisual, IsolatedDocumentedScene }

internal static class DesktopSceneCaptureFactory
{
    // Version numbers never permit private capture in the presentation process.
    internal static DesktopSceneProvider SelectProvider(int build) => build switch
    {
        < 19041 => throw new PlatformNotSupportedException("This version requires Windows 10 2004 or later."),
        < 22000 => DesktopSceneProvider.IsolatedDocumentedScene,
        _ => DesktopSceneProvider.IsolatedSharedVisual
    };

    internal static IDesktopSceneCapture Create(ScreenRect region, bool diagnosticOnScreen, Action<CaptureTrace>? trace)
        => Create(region, diagnosticOnScreen, trace, null);

    internal static IDesktopSceneCapture Create(ScreenRect region, bool diagnosticOnScreen,
        Action<CaptureTrace>? trace, string? executable)
    {
        // A Shell desktop thumbnail already includes its owned popup group before
        // per-window exclusions. Keep that group out of its own glass background.
        var ownedDesktop = DesktopWindowAttachment.HasAttachedWindows;
        var provider = ownedDesktop ? DesktopSceneProvider.IsolatedDocumentedScene
            : SelectProvider(WindowsCapabilities.BuildNumber);
        if (ownedDesktop) trace?.Invoke(new("BackdropProvider", "DesktopOwnedDocumentedScene", System.Diagnostics.Stopwatch.GetTimestamp()));
        return new RecoveringDesktopSceneCapture(region, provider, trace, executable);
    }

    internal static IDesktopSceneCapture CreateDesktopOnly(ScreenRect region, Action<CaptureTrace>? trace,
        string? executable = null) => new RecoveringDesktopSceneCapture(region,
            () => new IsolatedDesktopSceneCapture(region, GlassDesk.UI.Controls.GlassBackdropSource.CompatibleScene,
                trace, executable, desktopOnly: true, documentedDesktop: DesktopWindowAttachment.HasAttachedWindows),
            () => new RecoveringDesktopSceneCapture(region,
                () => new IsolatedDesktopSceneCapture(region, GlassDesk.UI.Controls.GlassBackdropSource.CompatibleScene,
                    trace, executable, desktopOnly: true, documentedDesktop: true),
                () => new IsolatedDesktopSceneCapture(region, GlassDesk.UI.Controls.GlassBackdropSource.Wallpaper,
                    trace, executable), System.Diagnostics.Stopwatch.GetTimestamp, trace),
            System.Diagnostics.Stopwatch.GetTimestamp, trace);
}
