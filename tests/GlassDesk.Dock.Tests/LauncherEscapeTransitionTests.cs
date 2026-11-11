using System.Reflection;
using System.Windows;
using System.Windows.Input;
using GlassDesk.Launcher;
using GlassDesk.Platform.Displays;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock.Tests;

public sealed class LauncherEscapeTransitionTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("_imeComposing")]
    [InlineData("_logicalIconDrag")]
    [InlineData("_keepOpenWhileModalCount")]
    public void EscapeUsesTheExistingCloseMotionAndHonorsInteractionGuards(string? guard)
    {
        StaTest.Run(() =>
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var window = new LauncherWindow();
            try
            {
                var surface = (LiquidGlassSurface)window.FindName("GlassSurface");
                surface.ReadBackdropScene = () => new GlassBackdropScene();
                surface.CaptureRevealBackdrop = (_, w, h) => Enumerable.Repeat((byte)80, w * h * 4).ToArray();
                var display = DisplayService.Primary()!;
                window.ShowWithTransition(new PixelRect(display.WorkArea.X + 60, display.WorkArea.Bottom - 84, 48, 48));
                if (guard is not null) typeof(LauncherWindow).GetField(guard, flags)!.SetValue(window,
                    guard == "_keepOpenWhileModalCount" ? (object)1 : true);
                var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Escape)
                    { RoutedEvent = Keyboard.KeyDownEvent };
                window.RaiseEvent(key);
                Assert.Equal(guard is null ? 0d : 1d, (double)typeof(LauncherWindow).GetField("_transformEndTimeline", flags)!.GetValue(window)!);
                Assert.Equal(guard is null, key.Handled);
                Assert.True(window.IsVisible); // Closing still animates, rather than hiding synchronously.
            }
            finally { window.Close(); }
        });
    }
}
