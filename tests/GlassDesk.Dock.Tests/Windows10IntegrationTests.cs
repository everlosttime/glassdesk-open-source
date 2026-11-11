using System.Reflection;
using GlassDesk.Core.Models.Settings;

namespace GlassDesk.Dock.Tests;

public sealed class Windows10IntegrationTests
{
    [Fact]
    public void DesktopClassChecksDoNotAllocatePerEnumeratedWindow()
    {
        var isHost = typeof(DocumentedDesktopSceneCapture)
            .GetMethod("IsDesktopHost", BindingFlags.Static | BindingFlags.NonPublic)!
            .CreateDelegate<Func<nint, bool>>();
        var window = GetDesktopWindow();
        Assert.NotEqual(IntPtr.Zero, window);
        for (var i = 0; i < 128; i++) Assert.False(isHost(window));
        var before = GC.GetAllocatedBytesForCurrentThread();
        var desktopHosts = 0;
        for (var i = 0; i < 1024; i++) if (isHost(window)) desktopHosts++;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, desktopHosts);
        Assert.True(allocated == 0, $"Allocated {allocated:N0} bytes for 1,024 desktop class checks.");
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetDesktopWindow();

    [Theory]
    [InlineData(19041, "IsolatedDocumentedScene")]
    [InlineData(19045, "IsolatedDocumentedScene")]
    [InlineData(21999, "IsolatedDocumentedScene")]
    [InlineData(22000, "IsolatedSharedVisual")]
    [InlineData(26100, "IsolatedSharedVisual")]
    [InlineData(40000, "IsolatedSharedVisual")]
    public void ProviderSelectionKeepsWindows10OutOfPrivateDwm(int build, string expected)
    {
        Assert.Equal(expected, DesktopSceneCaptureFactory.SelectProvider(build).ToString());
    }

    [Theory]
    [InlineData(17763)]
    [InlineData(19040)]
    public void UnsupportedBuildCannotStartCapture(int build)
    {
        Assert.Throws<PlatformNotSupportedException>(() => DesktopSceneCaptureFactory.SelectProvider(build));
    }

    [Fact]
    public void ExistingAppearanceProfilesEnableLiveBackdropWithoutLosingInformationBarBrightness()
    {
        var appearance = System.Text.Json.JsonSerializer.Deserialize<AppearanceSettings>(
            "{\"StatusBarTextBrightness\":0.4,\"DockGlassTransparency\":0.7}")!;
        var live = typeof(AppearanceSettings).GetProperty("LiveBackdrop");
        Assert.NotNull(live);
        Assert.Equal(true, live.GetValue(appearance));
        Assert.Equal(.4, appearance.StatusBarTextBrightness);
        Assert.Equal(.7, appearance.DockGlassTransparency);
    }
}
