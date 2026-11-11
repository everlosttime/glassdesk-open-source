using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GlassDesk.Core.Storage;
using GlassDesk.Settings;
using GlassDesk.AppServices;
using GlassDesk.Core.Models;
using GlassDesk.Launcher;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock.Tests;

public sealed class GuidedOnboardingTests
{
    [Theory]
    [InlineData("外观")]
    [InlineData("Dock")]
    [InlineData("信息栏")]
    [InlineData("快捷操作")]
    [InlineData("性能")]
    [InlineData("备份恢复")]
    [InlineData("关于")]
    public void EverySettingsPracticeCanCompleteWithoutEditingOrOpeningDialogs(string page)
    {
        StaTest.Run(() => WithSettings((window, data) =>
        {
            data.Settings.Save();
            var before = File.ReadAllBytes(data.Settings.FilePath);
            using var tour = Begin(window, page); Pump(window);
            var steps = 0;
            while (Descendants(window).OfType<Border>().Any(x => AutomationProperties.GetAutomationId(x) == "guide-card"))
            {
                Assert.True(++steps <= 6);
                Click(window, "guide-next"); Pump(window);
            }
            Assert.Equal(before, File.ReadAllBytes(data.Settings.FilePath));
            Assert.Empty(window.OwnedWindows.Cast<Window>());
            Assert.Empty(data.Library.Items);
        }));
    }

    [Fact]
    public void LauncherPracticeUsesRealSearchAndRestoresItsViewWithoutWritingLayout()
    {
        StaTest.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "GlassDesk-launcher-guide-" + Guid.NewGuid().ToString("N"));
            var data = new GlassDeskDataStore(new AppPaths(root)); data.LoadAll();
            data.Library.Add(new AppEntry { Id = "a", DisplayName = "工具", Target = "missing-a" });
            data.Library.Add(new AppEntry { Id = "b", DisplayName = "Other", Target = "missing-b" });
            data.Library.Save();
            using var services = new AppServiceHost(data);
            var window = new LauncherWindow { ShowActivated = false };
            try
            {
                ((LiquidGlassSurface)window.FindName("GlassSurface")).SetPerformanceSleeping(true);
                window.AttachServices(services, true); window.Show(); Pump(window);
                var search = (TextBox)window.FindName("SearchBox"); search.Text = "Other";
                var before = File.ReadAllBytes(data.Layouts.LauncherFilePath);
                var method = typeof(LauncherWindow).GetMethod("BeginOnboardingGuide"); Assert.NotNull(method);
                using var tour = Assert.IsAssignableFrom<IDisposable>(method.Invoke(window, null)); Pump(window);
                var status = Element<TextBlock>(window, "guide-feedback"); var initial = status.Text;
                search.Text = "gj";
                Assert.NotEqual(initial, status.Text);
                var grid = (UniformGrid)window.FindName("TileGrid");
                Assert.Equal("a", Assert.Single(grid.Children.OfType<Button>()).Tag);
                Click(window, "guide-next"); Pump(window);
                initial = status.Text; search.Clear(); Assert.NotEqual(initial, status.Text);
                Click(window, "guide-next"); Pump(window);
                Assert.False(((Button)window.FindName("NextPage")).IsEnabled);
                Click(window, "guide-next"); Pump(window);
                Click(window, "guide-next"); Pump(window);
                Assert.Equal("Other", search.Text);
                Assert.Equal(before, File.ReadAllBytes(data.Layouts.LauncherFilePath));
                Assert.Equal(0, typeof(LauncherWindow).GetField("_keepOpenWhileModalCount", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
            }
            finally { window.Close(); services.Dispose(); Directory.Delete(root, true); }
        });
    }

    [Fact]
    public void SettingsGuideObservesRealChangesWithoutChangingSettingsItself()
    {
        StaTest.Run(() => WithSettings((window, data) =>
        {
            data.Settings.Save();
            var before = File.ReadAllBytes(data.Settings.FilePath);
            using var tour = Begin(window, "外观");
            Pump(window);
            Assert.Equal(before, File.ReadAllBytes(data.Settings.FilePath));
            Assert.NotNull(Element<Border>(window, "guide-card"));
            Click(window, "guide-next"); Pump(window);
            var status = Element<TextBlock>(window, "guide-feedback");
            var previous = status.Text;
            var slider = (Slider)window.FindName("DockTransparencySlider");
            slider.Value = slider.Value < .8 ? .85 : .65;
            Assert.NotEqual(previous, status.Text);
            Click(window, "guide-previous"); Pump(window);
            Click(window, "guide-close"); Pump(window);
            Assert.DoesNotContain(Descendants(window).OfType<Border>(), x => AutomationProperties.GetAutomationId(x) == "guide-card");
        }));
    }

    [Theory]
    [InlineData(980, 760)]
    [InlineData(650, 460)]
    public void CoachFitsTheWindowAndLeavesHighlightedControlClickable(double width, double height)
    {
        StaTest.Run(() => WithSettings((window, data) =>
        {
            window.MinWidth = 500; window.Width = width; window.Height = height;
            using var tour = Begin(window, "外观"); Pump(window);
            Click(window, "guide-next"); Pump(window);
            var slider = (Slider)window.FindName("DockTransparencySlider");
            var point = slider.TranslatePoint(new Point(slider.ActualWidth / 2, slider.ActualHeight / 2), window);
            var hit = window.InputHitTest(point) as DependencyObject;
            Assert.True(IsWithin(hit, slider), $"Highlighted target was covered by {hit?.GetType().Name}.");
            var card = Element<Border>(window, "guide-card");
            var bounds = card.TransformToAncestor(window).TransformBounds(new Rect(card.RenderSize));
            var overlap = bounds;
            overlap.Intersect(slider.TransformToAncestor(window).TransformBounds(new Rect(slider.RenderSize)));
            Assert.True(overlap.IsEmpty || overlap.Width * overlap.Height < .1, $"Coach overlaps the target by {overlap}.");
            Assert.InRange(bounds.Left, 0, window.ActualWidth);
            Assert.InRange(bounds.Top, 0, window.ActualHeight);
            Assert.True(bounds.Right <= window.ActualWidth + 1 && bounds.Bottom <= window.ActualHeight + 1);
            Export(window, $"guide-settings-{(int)width}.png");
            window.Width += 40; window.Height += 20; Pump(window);
            bounds = card.TransformToAncestor(window).TransformBounds(new Rect(card.RenderSize));
            Assert.True(bounds.Right <= window.ActualWidth + 1 && bounds.Bottom <= window.ActualHeight + 1);
        }));
    }

    [Fact]
    public void HiddenWindowReleasesGuideAndOrdinaryNavigationStillWorks()
    {
        StaTest.Run(() => WithSettings((window, data) =>
        {
            using var tour = Begin(window, "备份恢复"); Pump(window);
            window.Hide(); Pump(window);
            window.Show(); Pump(window);
            Assert.DoesNotContain(Descendants(window).OfType<Border>(), x => AutomationProperties.GetAutomationId(x) == "guide-card");
            window.NavigateToPage("关于"); Pump(window);
            Assert.Equal("关于", ((ListBox)window.FindName("SettingsPageList")).SelectedItem);
        }));
    }

    private static IDisposable Begin(SettingsWindow window, string page)
    {
        var method = typeof(SettingsWindow).GetMethod("BeginOnboardingGuide", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(method);
        return Assert.IsAssignableFrom<IDisposable>(method.Invoke(window, [page]));
    }

    private static void WithSettings(Action<SettingsWindow, GlassDeskDataStore> run)
    {
        var root = Path.Combine(Path.GetTempPath(), "GlassDesk-guidance-" + Guid.NewGuid().ToString("N"));
        var data = new GlassDeskDataStore(new AppPaths(root)); data.LoadAll();
        var window = new SettingsWindow { ShowActivated = false };
        try { window.AttachDataStore(data, null); window.Show(); Pump(window); run(window, data); }
        finally { window.Close(); Directory.Delete(root, true); }
    }

    private static void Pump(Window window)
    {
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.SystemIdle);
        window.UpdateLayout();
    }
    private static void Export(Window window, string file)
    {
        var root = Environment.GetEnvironmentVariable("GLASSDESK_ONBOARDING_PREVIEW");
        if (string.IsNullOrEmpty(root)) return;
        Directory.CreateDirectory(root);
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(root, file)); encoder.Save(stream);
    }
    private static T Element<T>(DependencyObject root, string id) where T : DependencyObject
        => Assert.Single(Descendants(root).OfType<T>(), x => AutomationProperties.GetAutomationId(x) == id);
    private static void Click(Window window, string id) => Element<Button>(window, id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static bool IsWithin(DependencyObject? value, DependencyObject ancestor)
    {
        for (; value is not null; value = VisualTreeHelper.GetParent(value)) if (ReferenceEquals(value, ancestor)) return true;
        return false;
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
