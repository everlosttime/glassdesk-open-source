using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GlassDesk.AppServices;
using GlassDesk.Core.Models;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Storage;
using GlassDesk.Launcher;
using GlassDesk.Platform;
using GlassDesk.Platform.Displays;
using GlassDesk.Settings;
using GlassDesk.UI.Controls;
using GlassDesk.UI.Theming;

namespace GlassDesk.Dock.Tests;

public sealed class PrimaryDisplayDownloadPreviewTests
{
    [Fact]
    public void PrimaryDisplayPreviewsPreserveForegroundAndRenderSingleQr()
    {
        StaTest.Run(() =>
        {
            var display = DisplayService.Primary() ?? throw new InvalidOperationException("Primary display is unavailable.");
            var session = new PrimaryDisplaySession(display);
            var foreground = WindowControl.GetForegroundWindow();
            var root = Path.Combine(Path.GetTempPath(), "GlassDesk-download-preview-" + Guid.NewGuid().ToString("N"));
            var evidence = Path.Combine(AppContext.BaseDirectory, "download-entry-preview");
            Directory.CreateDirectory(evidence);
            using var theme = ThemeService.Initialize(Dispatcher.CurrentDispatcher);
            theme.Update(new AppearanceSettings { GlassMaterial = GlassMaterialMode.PlainGlass });
            var data = new GlassDeskDataStore(new AppPaths(root));
            data.LoadAll();
            for (var i = 1; i <= 8; i++)
                data.Library.Add(new AppEntry { Id = "feedback-" + i, DisplayName = "Feedback " + i, Target = "missing-" + i });
            using var services = new AppServiceHost(data);
            var launcher = new LauncherWindow { ShowActivated = false };
            var qr = new DownloadEntryWindow { ShowActivated = false };
            try
            {
                launcher.BindPrimaryDisplay(session);
                launcher.ApplySettings(new LauncherSettings { Rows = 2, Columns = 2, FocusSearchOnOpen = false });
                ((LiquidGlassSurface)launcher.FindName("GlassSurface")).SetPerformanceSleeping(true);
                launcher.AttachServices(services, true);
                PrepareWithoutInput(launcher);
                launcher.PositionOnWorkingArea();
                launcher.Show();
                launcher.UpdateLayout();
                Assert.False(launcher.IsActive);
                Assert.True(session.ContainsInWorkArea(DisplayService.GetWindowPixelBounds(new WindowInteropHelper(launcher).Handle)!.Value));
                Assert.Equal(foreground, WindowControl.GetForegroundWindow());
                Assert.Null(((ScrollViewer)launcher.FindName("PageScroll")).FocusVisualStyle);
                launcher.NavigatePage(1);
                launcher.UpdateLayout();
                Assert.Equal("2 / 2", ((TextBlock)launcher.FindName("PageText")).Text);
                ((TextBox)launcher.FindName("SearchBox")).Text = "Feedback 1";
                Assert.Equal("feedback-1", Assert.Single(((UniformGrid)launcher.FindName("TileGrid")).Children.OfType<Button>()).Tag);
                ((TextBox)launcher.FindName("SearchBox")).Clear();
                Save(launcher, Path.Combine(evidence, "launcher.png"), display.Dpi);
                launcher.Close();

                qr.BindPrimaryDisplay(session);
                PrepareWithoutInput(qr);
                qr.Show();
                qr.UpdateLayout();
                Assert.False(qr.IsActive);
                var bounds = DisplayService.GetWindowPixelBounds(new WindowInteropHelper(qr).Handle)!.Value;
                Assert.True(session.ContainsInWorkArea(bounds));
                Assert.Equal(foreground, WindowControl.GetForegroundWindow());
                Save(qr, Path.Combine(evidence, "website-qr-window.png"), display.Dpi);
                File.WriteAllText(Path.Combine(evidence, "primary-display.json"), JsonSerializer.Serialize(new
                {
                    display.DeviceName, display.IsPrimary, display.Bounds, display.WorkArea, display.Dpi,
                    portrait = display.Bounds.Height > display.Bounds.Width,
                    qrBounds = bounds, foregroundPreserved = foreground == WindowControl.GetForegroundWindow(),
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            finally { launcher.Close(); qr.Close(); services.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }
        });
    }

    private static void PrepareWithoutInput(Window window)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        Assert.True(WindowStyles.MakeNoActivate(handle));
        WindowStyles.SetPointerTransparent(handle, true);
        Assert.True(WindowStyles.IsNoActivate(handle));
        Assert.True(WindowStyles.IsPointerTransparent(handle));
    }

    private static void Save(Window window, string path, uint dpi)
    {
        var content = (FrameworkElement)window.Content;
        content.UpdateLayout();
        var scale = dpi / 96d;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth * scale),
            (int)Math.Ceiling(content.ActualHeight * scale), dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
