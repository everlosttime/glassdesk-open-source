using System.Reflection;
using System.Text.Json;
using System.Windows;
using GlassDesk.Launcher;
using GlassDesk.Platform;
using GlassDesk.Platform.Displays;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock.Tests;

public sealed class LauncherNativeShadowTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    public void ReleaseUsesTheTestedLauncherShadowByDefaultAndRetainsLegacyOverride(string? selection, bool expected)
    {
        StaTest.Run(() =>
        {
            var previous = Environment.GetEnvironmentVariable("GLASSDESK_LAUNCHER_NATIVE_SHADOW");
            Environment.SetEnvironmentVariable("GLASSDESK_LAUNCHER_NATIVE_SHADOW", selection);
            var window = new LauncherWindow();
            try
            {
                var option = typeof(LauncherWindow).GetProperty("UseNativeIndependentShadow", BindingFlags.Instance | BindingFlags.NonPublic)!;
                Assert.Equal(expected, option.GetValue(window));
            }
            finally { window.Close(); Environment.SetEnvironmentVariable("GLASSDESK_LAUNCHER_NATIVE_SHADOW", previous); }
        });
    }

    [Fact]
    public void HiddenLauncherResetMustNotPublishAnExpandedNativeOutline()
    {
        StaTest.Run(() =>
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var previous = Environment.GetEnvironmentVariable("GLASSDESK_LAUNCHER_NATIVE_SHADOW");
            Environment.SetEnvironmentVariable("GLASSDESK_LAUNCHER_NATIVE_SHADOW", "1");
            var window = new LauncherWindow();
            try
            {
                var surface = (LiquidGlassSurface)window.FindName("GlassSurface");
                var scene = new GlassBackdropScene(); surface.ReadBackdropScene = () => scene;
                surface.CaptureRevealBackdrop = (_, w, h) => Enumerable.Repeat((byte)80, w * h * 4).ToArray();
                var display = DisplayService.Primary()!;
                window.BindPrimaryDisplay(new PrimaryDisplaySession(display));
                var anchor = new PixelRect(display.WorkArea.X + display.WorkArea.Width / 2 - 24, display.WorkArea.Bottom - 84, 48, 48);
                window.ContainerAnchorProvider = () => anchor;
                window.ShowWithTransition(anchor);
                typeof(LauncherWindow).GetMethod("CompleteContainerTransform", flags)!.Invoke(window, [1d]);
                var shadow = (GlassShadow)typeof(GlassWindow).GetField("_shadow", flags)!.GetValue(window)!;
                ulong Submitted() => JsonSerializer.SerializeToElement(shadow.InspectPerformance()).GetProperty("submitted").GetUInt64();
                Assert.True(Submitted() > 0);
                // The production closing path preserves the expanded host, then
                // Hide synchronously resets the launcher before shadow visibility
                // listeners run. That reset must never present the full outline.
                typeof(LauncherWindow).GetField("_preserveExpandedHostOnHide", flags)!.SetValue(window, true);
                var before = Submitted();
                window.Hide();
                Assert.Equal(before, Submitted());
                Assert.False(shadow.IsVisible);
                var hidden = JsonSerializer.SerializeToElement(shadow.InspectPerformance());
                Assert.False(hidden.GetProperty("pending").GetBoolean());
                Assert.False(hidden.GetProperty("retryScheduled").GetBoolean());
                window.PrewarmNextRevealMaterial();
                Assert.True(Submitted() > before);
                Assert.False(shadow.IsVisible); // Explicit prewarm may draw, never show.
                var prepared = Submitted();
                window.ShowWithTransition(anchor);
                Assert.True(Submitted() > prepared);
                Assert.True(shadow.IsVisible);
            }
            finally { window.Close(); Environment.SetEnvironmentVariable("GLASSDESK_LAUNCHER_NATIVE_SHADOW", previous); }
        });
    }

    [Fact]
    public void NativeShadowKeepsTheIndependentInputTransparentWindowAndRestoresFullDetail()
    {
        StaTest.Run(() =>
        {
            var previous = Environment.GetEnvironmentVariable("GLASSDESK_LAUNCHER_NATIVE_SHADOW");
            Environment.SetEnvironmentVariable("GLASSDESK_LAUNCHER_NATIVE_SHADOW", "1");
            var window = new LauncherWindow();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            try
            {
                var surface = (LiquidGlassSurface)window.FindName("GlassSurface");
                var scene = new GlassBackdropScene(); surface.ReadBackdropScene = () => scene;
                surface.CaptureRevealBackdrop = (_, width, height) => Enumerable.Repeat((byte)80, width * height * 4).ToArray();
                var display = DisplayService.Primary()!;
                window.BindPrimaryDisplay(new PrimaryDisplaySession(display));
                window.ShowWithTransition(new PixelRect(display.WorkArea.X + display.WorkArea.Width / 2 - 24, display.WorkArea.Bottom - 84, 48, 48));
                var shadow = (GlassShadow)typeof(GlassWindow).GetField("_shadow", flags)!.GetValue(window)!;
                Assert.True(WindowShadowStyles.IsInputTransparent(shadow.Handle), "Shadow HWND input policy changed: " + shadow.Handle);
                var inspect = typeof(GlassShadow).GetMethod("InspectPerformance", flags);
                Assert.NotNull(inspect);
                var metrics = JsonSerializer.SerializeToElement(inspect.Invoke(shadow, null));
                Assert.True(metrics.GetProperty("nativeActive").GetBoolean(), metrics.ToString());
                Assert.True(metrics.GetProperty("submitted").GetUInt64() > 0);
                var handle = shadow.Handle;
                typeof(LauncherWindow).GetMethod("CompleteContainerTransform", flags)!.Invoke(window, [1d]);
                metrics = JsonSerializer.SerializeToElement(inspect.Invoke(shadow, null));
                Assert.Equal(128u, metrics.GetProperty("contours").GetUInt32());
                Assert.Equal(handle, shadow.Handle);
                var host = (Window)typeof(GlassShadow).GetField("_window", flags)!.GetValue(shadow)!;
                Assert.Equal(1, host.Opacity);
                var submitted = metrics.GetProperty("submitted").GetUInt64();
                window.Opacity = .75;
                metrics = JsonSerializer.SerializeToElement(inspect.Invoke(shadow, null));
                Assert.True(metrics.GetProperty("submitted").GetUInt64() > submitted, "Native shadow opacity is baked into its pixels and must be republished.");
                var appearance = LiquidGlassDebugState.FinishingParameters;
                try
                {
                    submitted = metrics.GetProperty("submitted").GetUInt64();
                    LiquidGlassDebugState.FinishingParameters = appearance with { AmbientShadowOpacity = .12 };
                    metrics = JsonSerializer.SerializeToElement(inspect.Invoke(shadow, null));
                    Assert.True(metrics.GetProperty("submitted").GetUInt64() > submitted, "Settled native shadow must react to live appearance changes.");
                }
                finally { LiquidGlassDebugState.FinishingParameters = appearance; }
                window.Hide();
                Assert.False(shadow.IsVisible);
                window.SetPerformanceSleeping(true);
                metrics = JsonSerializer.SerializeToElement(inspect.Invoke(shadow, null));
                Assert.False(metrics.GetProperty("nativeActive").GetBoolean());
                Assert.False(metrics.GetProperty("retryScheduled").GetBoolean());
            }
            finally { window.Close(); Environment.SetEnvironmentVariable("GLASSDESK_LAUNCHER_NATIVE_SHADOW", previous); }
        });
    }
}
