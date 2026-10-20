using GlassDesk.Platform;

internal enum DesktopSceneProvider { IsolatedSharedVisual }

internal static class DesktopSceneCaptureFactory
{
    // Version numbers never permit private capture in the presentation process.
    internal static DesktopSceneProvider SelectProvider(int build) => build switch
    {
        < 22000 => throw new PlatformNotSupportedException("This version requires Windows 11."),
        _ => DesktopSceneProvider.IsolatedSharedVisual
    };

    internal static IDesktopSceneCapture Create(ScreenRect region, bool diagnosticOnScreen, Action<CaptureTrace>? trace)
    {
        SelectProvider(WindowsCapabilities.BuildNumber);
        return new RecoveringDesktopSceneCapture(region, trace);
    }
}
