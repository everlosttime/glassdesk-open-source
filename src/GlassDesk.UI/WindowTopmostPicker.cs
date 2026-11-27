using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using GlassDesk.Platform;
using GlassDesk.Platform.Displays;

namespace GlassDesk.UI;

/// <summary>A temporary, per-monitor window picker. It never captures the desktop or forwards a click.</summary>
public sealed class WindowTopmostPicker : IDisposable
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly List<WindowTopmostPickerOverlay> _overlays = [];
    private readonly DispatcherTimer _refresh;
    private readonly DispatcherTimer _feedbackDeadline;
    private Action<WindowTopmostTarget>? _selected;
    private Func<WindowTopmostTarget, string?>? _describeTarget;
    private WindowTopmostTarget? _pressedTarget;
    private nint _previousForeground;
    private Window? _feedback;
    private bool _ending, _building, _disposed;

    public WindowTopmostPicker()
    {
        _refresh = new DispatcherTimer(DispatcherPriority.Input, _dispatcher) { Interval = TimeSpan.FromMilliseconds(33) };
        _refresh.Tick += OnRefresh;
        _feedbackDeadline = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromSeconds(2.5) };
        _feedbackDeadline.Tick += OnFeedbackDeadline;
    }

    public bool IsSelecting => _selected is not null;

    public void Begin(Action<WindowTopmostTarget> selected, Func<WindowTopmostTarget, string?>? describeTarget = null,
        string title = "窗口置顶", Func<DisplayInfo, bool>? includeDisplay = null)
    {
        _dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(selected);
        if (IsSelecting) { Cancel(); return; }
        CloseFeedback();
        var displays = DisplayService.Enumerate().Where(display => includeDisplay?.Invoke(display) != false).ToArray();
        if (displays.Length == 0) throw new InvalidOperationException("无法读取显示器，未进入窗口选择。");
        _previousForeground = WindowControl.GetForegroundWindow();
        _selected = selected;
        _describeTarget = describeTarget ?? (target => target.IsTopmost ? "点击取消置顶" : "点击置顶");
        _building = true;
        try
        {
            foreach (var display in displays)
            {
                var overlay = new WindowTopmostPickerOverlay(this, display, title);
                _overlays.Add(overlay);
                overlay.Place();
                overlay.Show();
                overlay.Place();
            }
            RefreshTarget();
            var point = DisplayService.GetCursorPixelPoint();
            var active = _overlays.FirstOrDefault(w => point is { } p && w.Display.Bounds.Contains(p.X, p.Y)) ?? _overlays[0];
            active.Activate();
            active.Focus();
            _refresh.Start();
        }
        catch
        {
            End(restoreFocus: true);
            throw;
        }
        finally { _building = false; }
    }

    public void Cancel()
    {
        _dispatcher.VerifyAccess();
        End(restoreFocus: true);
    }

    internal void PointerMoved() => RefreshTarget();

    internal void PointerPressed(WindowTopmostPickerOverlay overlay, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!IsSelecting) return;
        if (e.ChangedButton is MouseButton.Left or MouseButton.Right)
        {
            _pressedTarget = e.ChangedButton == MouseButton.Left ? TargetUnderPointer() : null;
            Mouse.Capture(overlay, CaptureMode.Element);
        }
    }

    internal void PointerReleased(MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!IsSelecting) return;
        if (e.ChangedButton == MouseButton.Right) { Cancel(); return; }
        if (e.ChangedButton != MouseButton.Left) return;
        var target = TargetUnderPointer();
        var pressed = _pressedTarget;
        _pressedTarget = null;
        Mouse.Capture(null);
        // A drag to another window, or a click on the desktop, does not commit.
        if (target is null || pressed is null || target.Handle != pressed.Handle
            || target.ProcessId != pressed.ProcessId || target.ProcessStarted != pressed.ProcessStarted) return;
        var selected = _selected;
        End(restoreFocus: true);
        selected?.Invoke(target);
    }

    internal void KeyPressed(KeyEventArgs e)
    {
        e.Handled = true;
        if (e.Key == Key.Escape) Cancel();
    }

    internal void OverlayDeactivated()
    {
        if (_building || _ending || !IsSelecting) return;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!IsSelecting || _building || _ending) return;
            var foreground = WindowControl.GetForegroundWindow();
            if (!_overlays.Any(w => w.Handle == foreground)) End(restoreFocus: false);
        }));
    }

    internal void OverlayClosing(WindowTopmostPickerOverlay overlay)
    {
        if (_ending || !IsSelecting) return;
        // The window is already closing (Alt+F4 or a native close request).
        // Remove it before cancellation closes the other monitor overlays.
        _overlays.Remove(overlay);
        Cancel();
    }

    private WindowTopmostTarget? TargetUnderPointer()
    {
        var point = DisplayService.GetCursorPixelPoint();
        return point is { } p ? FindTarget(p.X, p.Y).Target : null;
    }

    private void OnRefresh(object? sender, EventArgs e) => RefreshTarget();

    private void RefreshTarget()
    {
        if (!IsSelecting) return;
        var point = DisplayService.GetCursorPixelPoint();
        var (target, action) = point is { } p ? FindTarget(p.X, p.Y) : default;
        foreach (var overlay in _overlays)
            overlay.UpdateTarget(target, action, point is { } cursor && overlay.Display.Bounds.Contains(cursor.X, cursor.Y));
    }

    private (WindowTopmostTarget? Target, string? Action) FindTarget(int x, int y)
    {
        if (!_overlays.Any(overlay => overlay.Display.Bounds.Contains(x, y))) return default;
        var target = WindowTopmostWindows.FindAt(x, y);
        var action = target is null ? null : _describeTarget?.Invoke(target);
        return action is null ? default : (target, action);
    }

    private void End(bool restoreFocus)
    {
        if (_ending) return;
        _ending = true;
        try
        {
            _refresh.Stop();
            _selected = null;
            _describeTarget = null;
            _pressedTarget = null;
            var foreground = WindowControl.GetForegroundWindow();
            var restore = restoreFocus && _overlays.Any(w => w.Handle == foreground);
            if (Mouse.Captured is WindowTopmostPickerOverlay captured && _overlays.Contains(captured)) Mouse.Capture(null);
            foreach (var overlay in _overlays) overlay.Close();
            _overlays.Clear();
            if (restore && WindowControl.IsAlive(_previousForeground)) WindowActivation.TryBringToForeground(_previousForeground);
            _previousForeground = 0;
        }
        finally { _ending = false; }
    }

    public void ShowFeedback(string message, bool isError = false)
    {
        _dispatcher.VerifyAccess();
        if (_disposed || string.IsNullOrWhiteSpace(message)) return;
        CloseFeedback();
        var point = DisplayService.GetCursorPixelPoint();
        var display = point is { } p ? DisplayService.FromPixelPoint(p.X, p.Y) : DisplayService.Primary();
        if (display is null) return;
        var content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(242, 27, 32, 44)),
            BorderBrush = new SolidColorBrush(isError ? Color.FromRgb(255, 166, 142) : Color.FromRgb(111, 183, 255)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(13), Padding = new Thickness(18, 12, 18, 12),
            Child = new TextBlock { Text = message, Foreground = Brushes.White, FontSize = 14, TextWrapping = TextWrapping.Wrap, MaxWidth = 380 },
        };
        content.Measure(new Size(416, 160));
        var width = Math.Min(display.WorkArea.Width, display.ToPixels(content.DesiredSize.Width));
        var height = Math.Min(display.WorkArea.Height, display.ToPixels(content.DesiredSize.Height));
        _feedback = new Window
        {
            Title = "GlassDesk 窗口操作提示", Content = content, Background = Brushes.Transparent,
            AllowsTransparency = true, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, ShowActivated = false, Topmost = true, Focusable = false, IsHitTestVisible = false,
            Width = display.ToDips(width), Height = display.ToDips(height),
        };
        var handle = new WindowInteropHelper(_feedback).EnsureHandle();
        WindowStyles.MakeNoActivate(handle);
        WindowStyles.SetPointerTransparent(handle, true);
        _feedback.Show();
        DisplayService.MoveWindowToPixelRect(handle, new PixelRect(display.WorkArea.X + (display.WorkArea.Width - width) / 2,
            display.WorkArea.Y + display.ToPixels(24), width, height));
        _feedbackDeadline.Start();
    }

    private void OnFeedbackDeadline(object? sender, EventArgs e) => CloseFeedback();

    private void CloseFeedback()
    {
        _feedbackDeadline.Stop();
        _feedback?.Close();
        _feedback = null;
    }

    public void Dispose()
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        End(restoreFocus: true);
        CloseFeedback();
        _refresh.Tick -= OnRefresh;
        _feedbackDeadline.Tick -= OnFeedbackDeadline;
    }
}

/// <summary>Converts a complete physical window outline to a monitor's local drawing coordinates.</summary>
public static class WindowTopmostPickerLayout
{
    public static Rect ToDisplayRect(PixelRect bounds, DisplayInfo display) => new(
        display.ToDips(bounds.X - display.Bounds.X), display.ToDips(bounds.Y - display.Bounds.Y),
        display.ToDips(bounds.Width), display.ToDips(bounds.Height));
}
