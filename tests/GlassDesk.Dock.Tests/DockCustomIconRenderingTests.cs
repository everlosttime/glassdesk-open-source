using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GlassDesk.AppServices;
using GlassDesk.Core.Storage;
using GlassDesk.Dock;
using GlassDesk.Platform.Displays;
using GlassDesk.Settings;
using GlassDesk.UI.Settings;

namespace GlassDesk.Dock.Tests;

public sealed class DockCustomIconRenderingTests
{
    [Theory]
    [InlineData("entry:launcher", "Launcher", "LauncherButton")]
    [InlineData("entry:this-pc", "ThisPc", "ThisPcButton")]
    [InlineData("entry:desktop-icons", "DesktopIcons", "DesktopIconsButton")]
    public void BuiltinIconRendersPersistsThroughOtherDockEditsAndResets(string key, string prefix, string buttonName)
    {
        StaTest.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "GlassDesk-builtin-icon-" + Guid.NewGuid().ToString("N"));
            var data = new GlassDeskDataStore(new AppPaths(root)); data.LoadAll();
            using var services = new AppServiceHost(data);
            var host = DockSettingsHost.Initialize(Dispatcher.CurrentDispatcher); host.Update(data.Settings.Current.Dock);
            var dock = new DockWindow();
            var settings = new SettingsWindow();
            try
            {
                dock.BindPrimaryDisplay(new PrimaryDisplaySession(DisplayService.Primary()!));
                dock.AttachServices(services, host, () => { });
                var source = Path.Combine(root, "source.png");
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgra32, null,
                    Enumerable.Repeat((byte)255, 64).ToArray(), 16)));
                using (var stream = File.Create(source)) encoder.Save(stream);
                Assert.True(services.DockIcons.SetIcon(key, source));
                dock.RefreshTiles();
                var image = Assert.IsType<Image>(dock.FindName(prefix + "CustomIcon"));
                var original = Assert.IsAssignableFrom<FrameworkElement>(dock.FindName(prefix + "DefaultGlyph"));
                Assert.NotNull(image.Source);
                Assert.Equal(Visibility.Visible, image.Visibility);
                Assert.Equal(Visibility.Collapsed, original.Visibility);
                var path = services.DockIcons.GetIconPath(key);
                settings.AttachDataStore(data, null);
                Assert.IsType<Slider>(settings.FindName("DockOffsetXSlider")).Value = 12;
                Assert.Equal(path, services.DockIcons.GetIconPath(key));
                var button = Assert.IsType<Button>(dock.FindName(buttonName));
                button.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
                    { RoutedEvent = UIElement.MouseRightButtonUpEvent });
                Assert.Equal(2, button.ContextMenu.Items.Count);
                var reset = Assert.IsType<MenuItem>(button.ContextMenu.Items[1]);
                Assert.True(reset.IsEnabled);
                button.ContextMenu.IsOpen = false;
                reset.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Assert.Null(image.Source);
                Assert.Equal(Visibility.Visible, original.Visibility);
                Assert.True(File.Exists(source));
            }
            finally { settings.Close(); dock.Close(); Directory.Delete(root, true); }
        });
    }
}
