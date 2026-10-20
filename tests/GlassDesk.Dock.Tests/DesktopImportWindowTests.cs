using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GlassDesk.AppServices;
using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;
using GlassDesk.Launcher;
using GlassDesk.Platform.Shell;

namespace GlassDesk.Dock.Tests;

public sealed class DesktopImportWindowTests
{
    [Fact]
    public void ImportPickerPreservesShortcutSourcesForTheirNamesArgumentsAndCustomIcons()
    {
        StaTest.Run(() =>
        {
            var factory = typeof(LauncherWindow).GetMethod("CreateImportFilePicker", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(factory);
            var picker = Assert.IsType<Microsoft.Win32.OpenFileDialog>(factory.Invoke(null, null));
            Assert.False(picker.DereferenceLinks);
            Assert.True(picker.Multiselect);
        });
    }

    [Fact]
    public void SelectionCanBeChangedIndividuallyOrAllAtOnceAndOnlyChosenItemsAreImported()
    {
        StaTest.Run(() =>
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "GlassDesk-desktop-selection-" + Guid.NewGuid().ToString("N")));
            LauncherWindow? launcher = null;
            DesktopImportWindow? dialog = null;
            try
            {
                Directory.CreateDirectory(root);
                var first = Path.Combine(root, "一.txt");
                var second = Path.Combine(root, "二.txt");
                File.WriteAllText(first, "one"); File.WriteAllText(second, "two");
                dialog = new DesktopImportWindow(new DesktopScanResult([
                    new DesktopItem(first, "一.txt", false, true),
                    new DesktopItem(second, "二.txt", false, false)], []));
                var add = (Button)dialog.FindName("AddSelectedButton");
                Assert.False(add.IsEnabled);
                Assert.Empty(dialog.SelectedPaths);

                ((Button)dialog.FindName("SelectAllButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Equal([first, second], dialog.SelectedPaths);
                Assert.True(add.IsEnabled);
                ((Button)dialog.FindName("ClearSelectionButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Empty(dialog.SelectedPaths);
                Assert.False(add.IsEnabled);

                var list = (ListBox)dialog.FindName("DesktopItemsList");
                ((CheckBox)list.Items[1]).IsChecked = true;
                Assert.Equal([second], dialog.SelectedPaths);
                Assert.True(add.IsEnabled);
                var data = new GlassDeskDataStore(new AppPaths(Path.Combine(root, "Data"))); data.LoadAll();
                using var services = new AppServiceHost(data);
                launcher = new LauncherWindow();
                launcher.AttachServices(services, true);
                typeof(LauncherWindow).GetMethod("ImportPaths", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(launcher, [dialog.SelectedPaths]);
                Assert.Equal(second, Assert.Single(services.Library.Items).Target);
                Assert.Single(LauncherLayoutRules.AppIds(data.Layouts.Launcher));
            }
            finally
            {
                dialog?.Close(); launcher?.Close();
                if (Path.GetDirectoryName(root) != Path.TrimEndingDirectorySeparator(Path.GetTempPath()))
                    throw new IOException("Unexpected cleanup boundary.");
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        });
    }

    [Fact]
    public void EmptyDesktopCannotBeConfirmed()
    {
        StaTest.Run(() =>
        {
            var dialog = new DesktopImportWindow(new DesktopScanResult([], []));
            try
            {
                ((Button)dialog.FindName("SelectAllButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.False(((Button)dialog.FindName("AddSelectedButton")).IsEnabled);
                Assert.Empty(dialog.SelectedPaths);
            }
            finally { dialog.Close(); }
        });
    }

    [Fact]
    public void LoadedRowsReceiveShellIconsWithoutImportingAnything()
    {
        StaTest.Run(() =>
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "notepad.exe");
            var dialog = new DesktopImportWindow(new DesktopScanResult([new DesktopItem(path, "记事本", false, true)], []));
            try
            {
                dialog.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                var list = (ListBox)dialog.FindName("DesktopItemsList");
                var row = (DockPanel)((CheckBox)list.Items[0]).Content;
                var icon = (Image)row.Children[0];
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (icon.Source is null && DateTime.UtcNow < deadline)
                {
                    var frame = new DispatcherFrame();
                    Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
                    Dispatcher.PushFrame(frame);
                    Thread.Sleep(10);
                }
                Assert.IsType<BitmapImage>(icon.Source);
                Assert.Empty(dialog.SelectedPaths);
            }
            finally { dialog.Close(); }
        });
    }

    [Theory]
    [InlineData(600, 560)]
    [InlineData(420, 320)]
    public void LongListsScrollWhileSelectionAndConfirmationStayVisible(double width, double height)
    {
        StaTest.Run(() =>
        {
            var items = Enumerable.Range(0, 60).Select(index => new DesktopItem($"C:\\sample\\{index}.lnk",
                index == 0 ? "中文应用与较长的名称 v2.0 · Remote Desktop Connection" : $"桌面项目 {index + 1}", false, index % 2 == 0)).ToArray();
            var dialog = new DesktopImportWindow(new DesktopScanResult(items, []));
            try
            {
                dialog.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri("pack://application:,,,/GlassDesk.UI;component/Theming/LiquidGlassTheme.xaml") });
                dialog.Width = width; dialog.Height = height;
                _ = new WindowInteropHelper(dialog).EnsureHandle();
                var content = (FrameworkElement)dialog.Content;
                content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
                var add = (Button)dialog.FindName("AddSelectedButton");
                var bounds = add.TransformToAncestor(content).TransformBounds(new Rect(add.RenderSize));
                Assert.InRange(bounds.Left, 0, width); Assert.InRange(bounds.Right, 0, width);
                Assert.InRange(bounds.Top, 0, height); Assert.InRange(bounds.Bottom, 0, height);
                var list = (ListBox)dialog.FindName("DesktopItemsList");
                Assert.InRange(list.ActualHeight, 20, height - 80);
                ((Button)dialog.FindName("SelectAllButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Equal(60, dialog.SelectedPaths.Count);
                content.UpdateLayout();

                var previewRoot = Environment.GetEnvironmentVariable("GLASSDESK_DESKTOP_IMPORT_PREVIEW");
                if (!string.IsNullOrWhiteSpace(previewRoot))
                {
                    Directory.CreateDirectory(previewRoot);
                    var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(content);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(previewRoot, $"desktop-import-{width:0}.png"));
                    encoder.Save(file);
                }
            }
            finally { dialog.Close(); }
        });
    }
}
