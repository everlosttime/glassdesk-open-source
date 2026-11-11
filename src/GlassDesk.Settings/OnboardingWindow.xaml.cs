// Public 1.6.0 adaptation: information-bar-specific implementation is excluded.
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GlassDesk.Core.Storage;
using GlassDesk.UI.Controls;

namespace GlassDesk.Settings;

/// <summary>Isolated desktop practice. All icons and gestures are local to this window.</summary>
public partial class OnboardingWindow : GlassWindow
{
    private readonly SettingsStore _settings;
    private int _task;
    private bool _completed;
    private string? _pendingSource;
    private string? _dragSource;
    private Point _pressPosition;
    private long _pressTimestamp;
    private static readonly SolidColorBrush Accent = FrozenBrush(Color.FromRgb(139, 190, 255));
    private static readonly SolidColorBrush Quiet = FrozenBrush(Color.FromRgb(82, 100, 129));
    private static SolidColorBrush FrozenBrush(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }
    private static readonly string[] Titles = ["把快捷方式添加到 Dock", "从 Dock 移除快捷方式", "通过右上角添加到启动台", "拖到下方红色垃圾桶移除", "从托盘菜单打开设置"];
    private static readonly string[] Instructions = [
        "按住桌面左侧的“示例快捷方式”，拖到底部 Dock 的 ＋ 位置，再松开鼠标。",
        "在 Dock 的蓝色示例图标上点击鼠标右键，再选择“从 Dock 移除”。",
        "点击 Dock 的九宫格打开启动台，再点右上角“＋ 添加”，添加所选示例。",
        "点击九宫格打开启动台。长按“示例应用”并拖到底部中央的红色垃圾桶，再松开鼠标。",
        "点击右上角的模拟托盘入口，再选择“设置”。"];
    protected override bool UseOpaquePanel => true;
    public event EventHandler<string>? SettingsPageRequested;
    public void PositionOnPrimary() => CenterOnPrimaryWorkArea();

    public OnboardingWindow(SettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        InitializeComponent();
        MaxWidth = Math.Max(320, SystemParameters.WorkArea.Width - 24);
        MaxHeight = Math.Max(300, SystemParameters.WorkArea.Height - 24);
        MinWidth = Math.Min(MinWidth, MaxWidth); MinHeight = Math.Min(MinHeight, MaxHeight);
        PreviewKeyDown += OnPracticeKeyDown;
        Deactivated += (_, _) => { CancelDrag(); HidePopovers(); };
        RenderTask();
    }

    private void RenderTask()
    {
        CancelDrag(); HidePopovers();
        _completed = false;
        FeedbackText.Foreground = new SolidColorBrush(Color.FromRgb(151, 177, 211));
        PageCounter.Text = $"{_task + 1:00} / 05";
        PageHeading.Text = Titles[_task]; InstructionText.Text = Instructions[_task];
        FeedbackText.Text = _task == 3 ? "长按后拖动；点击启动台外面会收起它。移除入口不会删除原文件。" : "放心练习：不会改动你的真实图标、文件或布局。";
        PreviousButton.IsEnabled = _task > 0;
        NextButton.Content = "跳过此项";
        OpenSettingsButton.Visibility = Visibility.Collapsed;
        DesktopExample.Visibility = _task == 0 ? Visibility.Visible : Visibility.Hidden;
        DesktopExampleGroup.Visibility = _task == 0 ? Visibility.Visible : Visibility.Hidden;
        DockExample.Visibility = _task == 1 ? Visibility.Visible : Visibility.Collapsed;
        DockDropHint.Visibility = _task == 0 ? Visibility.Visible : Visibility.Collapsed;
        LauncherExampleGroup.Visibility = _task == 3 ? Visibility.Visible : Visibility.Collapsed;
        LauncherExample.Visibility = _task == 3 ? Visibility.Visible : Visibility.Collapsed;
        MockDock.BorderBrush = _task <= 1 ? Accent : Quiet;
        LauncherToggleButton.BorderBrush = _task is 2 or 3 ? Accent : Quiet;
        AddAppButton.BorderBrush = _task == 2 ? Accent : Quiet;
        MockTrayIcon.BorderBrush = _task == 4 ? Accent : Quiet;
        SettingsGearButton.BorderBrush = _task == 4 ? Accent : Quiet;
    }
    private void Complete(string message)
    {
        _completed = true; FeedbackText.Text = "✓ 完成！" + message;
        FeedbackText.Foreground = new SolidColorBrush(Color.FromRgb(147, 223, 186));
        NextButton.Content = _task == 4 ? "完成教学" : "下一项";
        OpenSettingsButton.Visibility = _task == 4 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void OnNextClick(object sender, RoutedEventArgs e)
    {
        if (_task == 4) { Close(); return; }
        _task++; RenderTask();
    }
    private void OnPreviousClick(object sender, RoutedEventArgs e) { if (_task > 0) { _task--; RenderTask(); } }
    private void OnRestartClick(object sender, RoutedEventArgs e) => RenderTask();
    private void OnSkipClick(object sender, RoutedEventArgs e) => Close();
    private void OnRealSettingsClick(object sender, RoutedEventArgs e) { Close(); SettingsPageRequested?.Invoke(this, "关于"); }
    private void OnLauncherClick(object sender, RoutedEventArgs e)
    {
        var show = MockLauncher.Visibility != Visibility.Visible;
        HidePopovers(); MockLauncher.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show && _task == 2 && !_completed) FeedbackText.Text = "找到右上角“＋ 添加”。启动台外面不能拖入添加，点击外面会收起启动台。";
    }
    private void OnAddAppClick(object sender, RoutedEventArgs e)
    {
        if (_task != 2 || _completed) { FeedbackText.Text = "本项先练习指示的操作；可以点击“重练本项”重新开始。"; return; }
        MockPicker.Visibility = Visibility.Visible;
    }
    private void OnConfirmAddClick(object sender, RoutedEventArgs e)
    {
        if (_task != 2 || MockPicker.Visibility != Visibility.Visible) return;
        MockPicker.Visibility = Visibility.Collapsed;
        LauncherExampleGroup.Visibility = Visibility.Visible; LauncherExample.Visibility = Visibility.Visible;
        Complete("已通过右上角添加按钮添加入口。实际使用时可选择文件、文件夹、网站或从桌面批量添加。");
    }
    private void OnRemoveDockClick(object sender, RoutedEventArgs e)
    {
        if (_task != 1 || DockExample.Visibility != Visibility.Visible) return;
        DockExample.Visibility = Visibility.Collapsed;
        Complete("仅移除了 Dock 入口，原应用和文件仍然保留。");
    }
    private void OnTrayClick(object sender, RoutedEventArgs e)
    {
        var show = MockTrayMenu.Visibility != Visibility.Visible;
        HidePopovers(); MockTrayMenu.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show && _task == 4 && !_completed) FeedbackText.Text = "托盘菜单已展开。选择“设置”，打开 GlassDesk 设置。";
    }
    private void OnSettingsGearClick(object sender, RoutedEventArgs e)
    {
        if (MockTrayMenu.Visibility != Visibility.Visible) return;
        HidePopovers(); MockSettings.Visibility = Visibility.Visible;
        if (_task == 4) Complete("已学会从托盘菜单打开设置。你可以完成教学，或主动打开真实设置。");
    }
    private void HidePopovers()
    {
        MockPicker.Visibility = MockLauncher.Visibility = MockTrayMenu.Visibility = MockSettings.Visibility = Visibility.Collapsed;
    }
    private bool Contains(FrameworkElement element, Point point)
    {
        if (element.Visibility != Visibility.Visible) return false;
        var bounds = element.TransformToAncestor(SimulationSurface).TransformBounds(new Rect(element.RenderSize));
        return bounds.Contains(point);
    }
    private void DismissPopoversAt(Point position)
    {
        if (!Contains(MockLauncher, position) && !Contains(LauncherToggleButton, position))
            MockPicker.Visibility = MockLauncher.Visibility = Visibility.Collapsed;
        if (!Contains(MockTrayMenu, position) && !Contains(MockTrayIcon, position))
            MockTrayMenu.Visibility = Visibility.Collapsed;
        if (!Contains(MockSettings, position)) MockSettings.Visibility = Visibility.Collapsed;
    }
    private void OnSurfaceMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_dragSource is null) DismissPopoversAt(e.GetPosition(SimulationSurface));
    }
    private void OnItemMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string source } || e.ChangedButton != MouseButton.Left) return;
        if (!CanDrag(source)) return;
        _pendingSource = source; _pressPosition = e.GetPosition(SimulationSurface); _pressTimestamp = Stopwatch.GetTimestamp();
        SimulationSurface.CaptureMouse(); e.Handled = true;
    }
    private bool CanDrag(string source) => !_completed && ((source == "desktop" && _task == 0) ||
        (source == "launcher" && _task == 3 && MockLauncher.Visibility == Visibility.Visible && LauncherExample.Visibility == Visibility.Visible));
    private void OnSurfaceMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) { CancelDrag(); return; }
        var position = e.GetPosition(SimulationSurface);
        if (_dragSource is null && _pendingSource is not null && (position - _pressPosition).Length >= 6)
        {
            if (_pendingSource == "launcher" && Stopwatch.GetElapsedTime(_pressTimestamp).TotalMilliseconds < 350)
            { FeedbackText.Text = "先按住图标片刻，再拖向下方中央的红色垃圾桶。"; return; }
            StartDrag(_pendingSource, position);
        }
        if (_dragSource is null) return;
        Canvas.SetLeft(DragGhost, position.X - 28); Canvas.SetTop(DragGhost, position.Y - 28);
        if (_dragSource == "launcher") TrashDropTarget.Background = Contains(TrashDropTarget, position)
            ? new SolidColorBrush(Color.FromArgb(120, 239, 68, 68)) : new SolidColorBrush(Color.FromArgb(34, 239, 68, 68));
    }
    private void StartDrag(string source, Point position)
    {
        if (!CanDrag(source)) return;
        _dragSource = source; DragGhost.Visibility = Visibility.Visible;
        Canvas.SetLeft(DragGhost, position.X - 28); Canvas.SetTop(DragGhost, position.Y - 28);
        if (source == "launcher") TrashDropTarget.Visibility = Visibility.Visible;
        SimulationSurface.UpdateLayout();
    }
    private void FinishDrag(Point position)
    {
        var source = _dragSource;
        if (source == "desktop" && Contains(MockDock, position))
        {
            DockExample.Visibility = Visibility.Visible; DockDropHint.Visibility = Visibility.Collapsed;
            Complete("快捷方式已加入模拟 Dock。以后可从这里快速打开应用。");
        }
        else if (source == "launcher" && Contains(TrashDropTarget, position))
        {
            LauncherExample.Visibility = LauncherExampleGroup.Visibility = Visibility.Collapsed;
            Complete("启动台入口已移除；原应用与文件不会被删除。");
        }
        else if (source is not null) FeedbackText.Text = source == "desktop"
            ? "还没放入 Dock。把图标拖到底部 Dock 内，再松开鼠标。" : "还没放入垃圾桶。请拖到底部中央的红色垃圾桶后松开。";
        CancelDrag();
    }
    private void OnSurfaceMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || (_pendingSource is null && _dragSource is null)) return;
        FinishDrag(e.GetPosition(SimulationSurface)); e.Handled = true;
    }
    private void CancelDrag()
    {
        _pendingSource = _dragSource = null;
        DragGhost.Visibility = TrashDropTarget.Visibility = Visibility.Collapsed;
        if (SimulationSurface.IsMouseCaptured) SimulationSurface.ReleaseMouseCapture();
    }
    private void OnLostMouseCapture(object sender, MouseEventArgs e) { if (_pendingSource is not null || _dragSource is not null) CancelDrag(); }
    private void OnPracticeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        if (_pendingSource is not null || _dragSource is not null) { CancelDrag(); return; }
        if (MockPicker.IsVisible) MockPicker.Visibility = Visibility.Collapsed;
        else if (MockLauncher.IsVisible || MockTrayMenu.IsVisible || MockSettings.IsVisible) HidePopovers();
        else Close();
    }
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e); if (e.Cancel) return;
        CancelDrag();
        if (_settings.Current.OnboardingDismissed == true) return;
        try { _settings.DismissOnboarding(); }
        catch (StorageException)
        {
            MessageBox.Show("新手教学的完成状态未能保存，下次启动可能再次显示。你仍可正常使用 GlassDesk。", "GlassDesk", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
    private void OnDragArea(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not TextBlock || e.ChangedButton != MouseButton.Left || Mouse.LeftButton != MouseButtonState.Pressed) return;
        e.Handled = true; DragMove();
    }
}
