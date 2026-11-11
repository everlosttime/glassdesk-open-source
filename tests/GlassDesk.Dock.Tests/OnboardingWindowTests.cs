// Public 1.6.0 adaptation: information-bar-specific implementation is excluded.
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassDesk.Core.Storage;
using GlassDesk.Settings;

namespace GlassDesk.Dock.Tests;

public sealed class OnboardingWindowTests
{
    [Fact]
    public void TeachingStartsOnInteractiveDesktopWithFiveTasks()
    {
        StaTest.Run(() => WithWindow((w, _) =>
        {
            Assert.Null(w.FindName("ChapterList"));
            Assert.NotNull(w.FindName("SimulationSurface"));
            Assert.Equal("01 / 05", Text(w, "PageCounter"));
            Assert.True(Element(w, "DesktopExample").IsVisible);
            Assert.False(Button(w, "PreviousButton").IsEnabled);
        }));
    }
    [Fact]
    public void DockAddsOnlyOnDropAndRemovesThroughContextMenu()
    {
        StaTest.Run(() => WithWindow((w, s) =>
        {
            Drag(w, "desktop", Center(w, "SimulationSurface"));
            Assert.False(Element(w, "DockExample").IsVisible);
            Drag(w, "desktop", Center(w, "MockDock"));
            Assert.True(Element(w, "DockExample").IsVisible);
            Assert.Contains("完成", Text(w, "FeedbackText"));
            Click(w, "NextButton");
            var menu = Assert.IsType<ContextMenu>(((Button)Element(w, "DockExample")).ContextMenu);
            ((MenuItem)menu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.False(Element(w, "DockExample").IsVisible);
            Assert.Contains("完成", Text(w, "FeedbackText"));
            Assert.False(s.Current.OnboardingDismissed);
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(s.FilePath)!, "apps.json")));
        }));
    }
    [Fact]
    public void LauncherUsesAddButtonAndBottomCenterTrash()
    {
        StaTest.Run(() => WithWindow((w, _) =>
        {
            Click(w, "NextButton"); Click(w, "NextButton"); Click(w, "LauncherToggleButton");
            Drag(w, "desktop", Center(w, "MockLauncher"));
            Assert.False(Element(w, "LauncherExample").IsVisible);
            Click(w, "AddAppButton"); Assert.True(Element(w, "MockPicker").IsVisible);
            Click(w, "ConfirmAddButton"); Assert.True(Element(w, "LauncherExample").IsVisible);
            Click(w, "NextButton"); Click(w, "LauncherToggleButton");
            Invoke(w, "StartDrag", "launcher", Center(w, "LauncherExample")); w.UpdateLayout();
            var trash = Element(w, "TrashDropTarget"); var launcher = Element(w, "MockLauncher");
            Assert.True(trash.IsVisible);
            var bounds = trash.TransformToAncestor(launcher).TransformBounds(new Rect(trash.RenderSize));
            Assert.InRange(bounds.X + bounds.Width / 2, launcher.ActualWidth / 2 - 1, launcher.ActualWidth / 2 + 1);
            Assert.True(bounds.Bottom > launcher.ActualHeight - 20);
            Export(w, "launcher-trash.png");
            Invoke(w, "FinishDrag", Center(w, "TrashDropTarget"));
            Assert.False(Element(w, "LauncherExample").IsVisible);
            Assert.False(trash.IsVisible); Assert.Contains("完成", Text(w, "FeedbackText"));
        }));
    }
    [Fact]
    public void OutsideClickClosesLauncherAndInfoBarGearOpensMockSettings()
    {
        StaTest.Run(() => WithWindow((w, _) =>
        {
            for (var i = 0; i < 4; i++) Click(w, "NextButton");
            Click(w, "LauncherToggleButton"); Invoke(w, "DismissPopoversAt", new Point(10, 10));
            Assert.False(Element(w, "MockLauncher").IsVisible);
            Click(w, "MockTrayIcon"); Assert.True(Element(w, "MockTrayMenu").IsVisible);
            Export(w, "control-center.png");
            Click(w, "SettingsGearButton"); Assert.True(Element(w, "MockSettings").IsVisible);
            Assert.Contains("完成", Text(w, "FeedbackText"));
            Assert.Equal("完成教学", Button(w, "NextButton").Content); Export(w, "settings-completed.png");
        }));
    }
    [Theory]
    [InlineData(940, 740)]
    [InlineData(640, 540)]
    public void FiveTasksFitSmallerWindowAndCanRestartBackAndSkip(double width, double height)
    {
        StaTest.Run(() => WithWindow((w, s) =>
        {
            w.Width = width; w.Height = height;
            Drag(w, "desktop", Center(w, "MockDock")); Click(w, "RestartButton");
            Assert.False(Element(w, "DockExample").IsVisible);
            Click(w, "NextButton"); Click(w, "PreviousButton");
            for (var i = 0; i < 5; i++)
            {
                w.UpdateLayout(); var next = Button(w, "NextButton");
                var bounds = next.TransformToAncestor(w).TransformBounds(new Rect(next.RenderSize));
                Assert.True(bounds.Right <= w.ActualWidth && bounds.Bottom <= w.ActualHeight);
                Assert.Equal($"{i + 1:00} / 05", Text(w, "PageCounter")); Export(w, $"{(int)width}-task-{i + 1}.png");
                if (i < 4) Click(w, "NextButton");
            }
            Click(w, "SkipButton"); var reload = new SettingsStore(s.FilePath); reload.Load();
            Assert.True(reload.Current.OnboardingDismissed);
        }));
    }
    [Fact]
    public void EachShortcutHasAccessibleHelpWithoutRecordingAndTeachingCanReplay()
    {
        StaTest.Run(() =>
        {
            var w = new SettingsWindow(); var count = 0; w.OnboardingRequested += (_, _) => count++;
            w.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri("pack://application:,,,/GlassDesk.UI;component/Theming/LiquidGlassTheme.xaml") });
            try
            {
                w.Show(); w.NavigateToPage("快捷操作");
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.SystemIdle);
                w.UpdateLayout();
                string[] names = ["ToggleLauncher", "NextPage", "PreviousPage", "ToggleDesktopIcons", "ToggleDockRevealLock", "ToggleGameSleep"];
                foreach (var name in names)
                {
                    var help = Assert.IsAssignableFrom<Button>(w.FindName(name + "Help"));
                    Assert.False(string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetHelpText(help)));
                    var tooltip = Assert.IsType<ToolTip>(help.ToolTip);
                    Assert.True(ToolTipService.GetShowDuration(help) >= 20000);
                    help.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); Assert.True(tooltip.IsOpen);
                    Assert.False(((ShortcutRecorder)w.FindName(name + "HotkeyBox")).IsRecording);
                    if (name == "ToggleDockRevealLock")
                    {
                        Export(w, "shortcuts.png");
                        var directory = Environment.GetEnvironmentVariable("GLASSDESK_ONBOARDING_PREVIEW");
                        if (!string.IsNullOrWhiteSpace(directory))
                        {
                            tooltip.UpdateLayout();
                            var image = new RenderTargetBitmap((int)Math.Ceiling(tooltip.ActualWidth), (int)Math.Ceiling(tooltip.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                            image.Render(tooltip); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                            using var output = File.Create(Path.Combine(directory, "shortcut-tooltip.png")); encoder.Save(output);
                        }
                    }
                    tooltip.IsOpen = false;
                }
                Click(w, "ReplayOnboardingButton"); Assert.Equal(1, count);
            }
            finally { w.Close(); }
        });
    }
    [Fact]
    public void EscapeCancelsDragBeforeClosingAndRealSettingsRequiresExplicitFinalAction()
    {
        StaTest.Run(() => WithWindow((w, s) =>
        {
            Invoke(w, "StartDrag", "desktop", Center(w, "DesktopExample"));
            var input = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(w), 0, System.Windows.Input.Key.Escape)
                { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
            w.RaiseEvent(input);
            Assert.True(w.IsVisible); Assert.False(Element(w, "DragGhost").IsVisible);
            Assert.False(Element(w, "DockExample").IsVisible);
            string? request = null;
            w.SettingsPageRequested += (_, page) => { Assert.False(w.IsVisible); Assert.True(s.Current.OnboardingDismissed); request = page; };
            for (var i = 0; i < 4; i++) Click(w, "NextButton");
            Click(w, "MockTrayIcon"); Click(w, "SettingsGearButton");
            Assert.Null(request); Click(w, "OpenSettingsButton"); Assert.Equal("关于", request);
        }));
    }
    private static FrameworkElement Element(Window w, string name) => Assert.IsAssignableFrom<FrameworkElement>(w.FindName(name));
    private static Button Button(Window w, string name) => Assert.IsType<Button>(w.FindName(name));
    private static string Text(Window w, string name) => ((TextBlock)Element(w, name)).Text;
    private static void Click(Window w, string name) { Button(w, name).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); w.UpdateLayout(); }
    private static Point Center(Window w, string name)
    {
        var item = Element(w, name); return item.TranslatePoint(new Point(item.ActualWidth / 2, item.ActualHeight / 2), Element(w, "SimulationSurface"));
    }
    private static void Drag(Window w, string source, Point end) { Invoke(w, "StartDrag", source, new Point()); Invoke(w, "FinishDrag", end); w.UpdateLayout(); }
    private static void Invoke(Window w, string method, params object[] args)
    {
        var member = w.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic); Assert.NotNull(member); member.Invoke(w, args);
    }
    private static void Export(Window w, string file)
    {
        var directory = Environment.GetEnvironmentVariable("GLASSDESK_ONBOARDING_PREVIEW"); if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory); var bitmap = new RenderTargetBitmap((int)w.ActualWidth, (int)w.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(w);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(Path.Combine(directory, file)); encoder.Save(stream);
    }
    private static void WithWindow(Action<OnboardingWindow, SettingsStore> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "GlassDesk-Simulation-" + Guid.NewGuid().ToString("N"));
        var store = new SettingsStore(Path.Combine(root, "settings.json")); store.Load(); OnboardingWindow? w = null;
        try { w = new OnboardingWindow(store); w.Show(); w.UpdateLayout(); action(w, store); }
        finally { w?.Close(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
