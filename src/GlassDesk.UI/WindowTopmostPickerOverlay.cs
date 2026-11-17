using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using GlassDesk.Platform;
using GlassDesk.Platform.Displays;

namespace GlassDesk.UI;

internal sealed class WindowTopmostPickerOverlay : Window
{
    private readonly WindowTopmostPicker _picker;
    private readonly PickerDrawing _drawing;

    public WindowTopmostPickerOverlay(WindowTopmostPicker picker, DisplayInfo display)
    {
        _picker = picker;
        Display = display;
        Title = "GlassDesk 窗口置顶选择";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Cursor = Cursors.Cross;
        Width = display.ToDips(display.Bounds.Width);
        Height = display.ToDips(display.Bounds.Height);
        _drawing = new PickerDrawing(display);
        Content = _drawing;
        PreviewMouseDown += (_, e) => _picker.PointerPressed(this, e);
        PreviewMouseUp += (_, e) => _picker.PointerReleased(e);
        PreviewMouseMove += (_, _) => _picker.PointerMoved();
        PreviewKeyDown += (_, e) => _picker.KeyPressed(e);
        Deactivated += (_, _) => _picker.OverlayDeactivated();
        Closing += (_, _) => _picker.OverlayClosing(this);
        DpiChanged += (_, _) => _drawing.InvalidateVisual();
    }

    public DisplayInfo Display { get; }
    public nint Handle => new WindowInteropHelper(this).Handle;

    public void Place()
    {
        var handle = new WindowInteropHelper(this).EnsureHandle();
        if (!DisplayService.MoveWindowToPixelRect(handle, Display.Bounds))
            throw new InvalidOperationException("无法显示窗口选择区域，请重试。");
    }

    public void UpdateTarget(WindowTopmostTarget? target, bool showLabel) => _drawing.Update(target, showLabel);

    private sealed class PickerDrawing(DisplayInfo display) : FrameworkElement
    {
        private static readonly Brush Shade = FrozenBrush(Color.FromArgb(62, 9, 15, 25));
        private static readonly Brush Panel = FrozenBrush(Color.FromArgb(242, 25, 31, 43));
        private static readonly Brush Accent = FrozenBrush(Color.FromRgb(99, 181, 255));
        private static readonly Brush Muted = FrozenBrush(Color.FromRgb(203, 215, 231));
        private static readonly Typeface Face = new("Microsoft YaHei UI");
        private WindowTopmostTarget? _target;
        private bool _showLabel;

        public void Update(WindowTopmostTarget? target, bool showLabel)
        {
            if (_target == target && _showLabel == showLabel) return;
            _target = target;
            _showLabel = showLabel;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            if (ActualWidth <= 0 || ActualHeight <= 0) return;
            var screen = new Rect(0, 0, ActualWidth, ActualHeight);
            dc.PushClip(new RectangleGeometry(screen));
            if (_target is { } target)
            {
                var rect = WindowTopmostPickerLayout.ToDisplayRect(target.Bounds, display);
                dc.DrawGeometry(Shade, null, new CombinedGeometry(GeometryCombineMode.Exclude,
                    new RectangleGeometry(screen), new RectangleGeometry(rect)));
                dc.DrawRectangle(FrozenBrush(Color.FromArgb(12, 99, 181, 255)), new Pen(Accent, 2), rect);
                if (_showLabel)
                {
                    var title = Text(string.IsNullOrWhiteSpace(target.Title) ? "应用窗口" : target.Title, 14, Brushes.White);
                    title.MaxTextWidth = Math.Max(40, Math.Min(380, ActualWidth - 60));
                    title.MaxTextHeight = 24;
                    title.Trimming = TextTrimming.CharacterEllipsis;
                    var action = Text(target.IsTopmost ? "点击取消置顶" : "点击置顶", 12, Accent);
                    var width = Math.Min(ActualWidth - 24, Math.Max(title.Width, action.Width) + 30);
                    var x = Math.Clamp(rect.Left + 12, 12, Math.Max(12, ActualWidth - width - 12));
                    var y = rect.Top >= 100 ? rect.Top - 74 : Math.Max(76, rect.Top + 14);
                    y = Math.Clamp(y, 12, Math.Max(12, ActualHeight - 78));
                    dc.DrawRoundedRectangle(Panel, new Pen(FrozenBrush(Color.FromArgb(112, 139, 191, 237)), 1), new Rect(x, y, width, 62), 11, 11);
                    dc.DrawText(title, new Point(x + 15, y + 9));
                    dc.DrawText(action, new Point(x + 15, y + 34));
                }
            }
            else dc.DrawRectangle(Shade, null, screen);
            var instruction = Text("选择窗口置顶  ·  Esc / 右键取消", 12, Muted);
            var instructionWidth = Math.Min(ActualWidth - 24, instruction.Width + 32);
            var instructionX = (ActualWidth - instructionWidth) / 2;
            dc.DrawRoundedRectangle(Panel, new Pen(FrozenBrush(Color.FromArgb(70, 203, 220, 239)), 1),
                new Rect(instructionX, 22, instructionWidth, 37), 12, 12);
            dc.DrawText(instruction, new Point(instructionX + 16, 31));
            dc.Pop();
        }

        private FormattedText Text(string text, double size, Brush brush) => new(text, CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight, Face, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

        private static SolidColorBrush FrozenBrush(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}
