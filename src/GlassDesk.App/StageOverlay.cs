using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using GlassDesk.Platform.Displays;

namespace GlassDesk.App;

internal record StageArt(string Title, BitmapSource? Preview, BitmapSource? Icon);
internal sealed record StageCard(nint Group, IReadOnlyList<(nint Handle, StageArt Art)> Members, bool CanMerge);

internal class StageOverlay : Window
{
    private readonly bool _clickThrough;

    public StageOverlay(bool clickThrough)
    {
        _clickThrough = clickThrough;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            var style = GetWindowLong(handle, -20) | 0x08000080; // NOACTIVATE + TOOLWINDOW
            if (_clickThrough) style |= 0x20; // Layered window passes pointer input through.
            _ = SetWindowLong(handle, -20, style);
            if (!_clickThrough) HwndSource.FromHwnd(handle)?.AddHook(StayInactive);
        };
    }

    public void Place(PixelRect bounds, DisplayInfo display)
    {
        Width = display.ToDips(bounds.Width);
        Height = display.ToDips(bounds.Height);
        var handle = new WindowInteropHelper(this).EnsureHandle();
        _ = DisplayService.MoveWindowToPixelRect(handle, bounds);
    }

    private static nint StayInactive(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != 0x0021) return 0; // WM_MOUSEACTIVATE
        handled = true;
        return 3; // MA_NOACTIVATE, still deliver the click.
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(nint window, int index);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(nint window, int index, int value);
}

internal sealed class StageRail : StageOverlay
{
    private const int CardWidth = 144;
    private const int CardHeight = 96;
    private const int CardPitch = 108;
    private const double HiddenOffset = -40;
    private static readonly SolidColorBrush HitBackground = CreateHitBackground();
    private readonly DisplayInfo _display;
    private readonly Action<nint> _select, _merge, _split, _deselect;
    private readonly Func<bool> _animate;
    private readonly StackPanel _stack = new();
    private readonly ScrollViewer _scroll;
    private readonly TranslateTransform _slide = new(HiddenOffset, 0);
    private readonly Dictionary<nint, CardBorder> _cards = [];
    private DoubleAnimation? _visibilityMotion;
    private PixelRect _bounds;
    private IReadOnlyList<StageCard> _entries = [];
    private nint _requested;
    private HashSet<nint> _movingPreviews = [];
    private IReadOnlyDictionary<nint, double>? _previewHandoffs;

    public StageRail(DisplayInfo display, Action<nint> select, Action<nint> merge, Action<nint> split, Action<nint> deselect,
        Func<bool> animate) : base(false)
    {
        _display = display;
        _select = select;
        _merge = merge;
        _split = split;
        _deselect = deselect;
        _animate = animate;
        _scroll = new ScrollViewer
        {
            Content = _stack, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            PanningMode = PanningMode.VerticalOnly, Focusable = false, RenderTransform = _slide, Opacity = 0,
        };
        Content = _scroll;
    }

    public PixelRect Bounds => _bounds;
    public PixelRect CardArea
    {
        get
        {
            var horizontal = _display.ToPixels(8);
            var vertical = Math.Min(_display.ToPixels((CardPitch - CardHeight) / 2), Math.Max(0, (_bounds.Height - 1) / 2));
            return new(_bounds.X + horizontal, _bounds.Y + vertical, Math.Max(0, _bounds.Width - 2 * horizontal),
                _bounds.Height - 2 * vertical);
        }
    }
    public bool IsRevealed { get; private set; }
    internal int PreviewCount => _entries.Sum(card => card.Members.Count(member => member.Art.Preview is not null));
    public bool HasEntries => _entries.Count != 0;
    public bool IsMenuOpen => _cards.Values.Any(card => card.ContextMenu?.IsOpen == true);

    public void SetRevealed(bool revealed, bool animate = true)
    {
        animate = animate && _animate();
        if (IsRevealed == revealed && (animate || _visibilityMotion is null)) return;
        IsRevealed = revealed;

        var fromX = _slide.X;
        var fromOpacity = _scroll.Opacity;
        _slide.X = revealed ? 0 : HiddenOffset;
        _scroll.Opacity = revealed ? 1 : 0;
        if (!animate)
        {
            _visibilityMotion = null;
            _slide.BeginAnimation(TranslateTransform.XProperty, null);
            _scroll.BeginAnimation(OpacityProperty, null);
            if (revealed && !IsVisible) Show();
            else if (!revealed && IsVisible) Hide();
            return;
        }

        var duration = TimeSpan.FromMilliseconds(revealed ? 185 : 165);
        var slide = new DoubleAnimation(fromX, revealed ? 0 : HiddenOffset, duration)
        {
            DecelerationRatio = .75, FillBehavior = FillBehavior.Stop,
        };
        slide.Completed += (_, _) =>
        {
            if (_visibilityMotion != slide) return;
            _visibilityMotion = null;
            if (!revealed && IsVisible) Hide();
        };
        _visibilityMotion = slide;
        // Keep the native window on this monitor and move only the card visuals inside it.
        _slide.BeginAnimation(TranslateTransform.XProperty, slide);
        _scroll.BeginAnimation(OpacityProperty, new DoubleAnimation(fromOpacity, revealed ? 1 : 0, duration)
        {
            DecelerationRatio = .75, FillBehavior = FillBehavior.Stop,
        });
        if (revealed && !IsVisible) Show();
    }

    public void Update(IReadOnlyList<StageCard> entries, PixelRect? dock)
    {
        if (!SameCards(entries)) BuildCards(entries);
        if (entries.Count == 0) return;
        var area = _display.WorkArea;
        var padding = _display.ToPixels(10);
        var width = _display.ToPixels(CardWidth + 16);
        var top = area.Y + padding;
        var bottom = area.Bottom - padding;
        if (dock is { } avoid && avoid.Width > 0 && avoid.X < area.X + padding + width && avoid.Right > area.X)
        {
            var above = Math.Min(bottom, avoid.Y - padding);
            var below = Math.Max(top, avoid.Bottom + padding);
            if (above - top >= bottom - below) bottom = above;
            else top = below;
        }
        var left = area.X + padding;
        if (bottom - top < _display.ToPixels(CardPitch) && dock is { } occupied)
        {
            left = Math.Clamp(occupied.Right + padding, area.X + padding, Math.Max(area.X + padding, area.Right - width));
            top = area.Y + padding;
            bottom = area.Bottom - padding;
        }
        var height = Math.Clamp(_display.ToPixels(CardPitch * entries.Count), 1, Math.Max(1, bottom - top));
        var bounds = new PixelRect(left, top + (bottom - top - height) / 2, width, height);
        if (_bounds != bounds)
        {
            _bounds = bounds;
            Place(bounds, _display);
        }
    }

    public void Highlight(nint group)
    {
        if (_requested == group) return;
        _requested = group;
        foreach (var (key, card) in _cards)
            card.Opacity = key == group ? .70 : 1;
    }

    public void MaskMovingPreviews(IEnumerable<nint> handles)
    {
        if (_movingPreviews.SetEquals(handles)) return;
        _movingPreviews = handles.ToHashSet();
        foreach (var card in _cards.Values)
            card.Art.SetMovingPreviews(_movingPreviews);
    }

    public void SetPreviewHandoffs(IReadOnlyDictionary<nint, double>? handoffs)
    {
        _previewHandoffs = handoffs;
        foreach (var card in _cards.Values)
            card.Art.SetPreviewHandoffs(handoffs);
    }

    public PixelRect Slot(nint group)
    {
        var index = 0;
        while (index < _entries.Count && _entries[index].Group != group) index++;
        if (index == _entries.Count) index = 0;
        var y = _bounds.Y + _display.ToPixels(Math.Max(0, index) * CardPitch + (CardPitch - CardHeight) / 2 - _scroll.VerticalOffset);
        return new(_bounds.X + _display.ToPixels(8),
            Math.Clamp(y, _bounds.Y, Math.Max(_bounds.Y, _bounds.Bottom - _display.ToPixels(CardHeight))),
            _display.ToPixels(CardWidth), _display.ToPixels(CardHeight));
    }

    private static SolidColorBrush CreateHitBackground()
    {
        // Layered HWNDs let alpha-zero pixels pass input to the desktop. Keep
        // each card's slot clickable even while its preview is being replaced.
        var brush = new SolidColorBrush(Color.FromArgb(2, 15, 20, 28));
        brush.Freeze();
        return brush;
    }

    private bool SameCards(IReadOnlyList<StageCard> entries) => entries.Count == _entries.Count
        && entries.Zip(_entries).All(pair => pair.First.Group == pair.Second.Group && pair.First.CanMerge == pair.Second.CanMerge
            && pair.First.Members.SequenceEqual(pair.Second.Members));

    private void BuildCards(IReadOnlyList<StageCard> entries)
    {
        _entries = entries;
        var keys = entries.Select(entry => entry.Group).ToHashSet();
        foreach (var key in _cards.Keys.Where(key => !keys.Contains(key)).ToArray())
        {
            _stack.Children.Remove(_cards[key]);
            _cards.Remove(key);
        }

        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (!_cards.TryGetValue(entry.Group, out var card))
            {
                card = new CardBorder(new StageCardArt(entry.Members, _movingPreviews)
                {
                    Width = CardWidth, Height = CardHeight, HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                }, _animate);
                var group = entry.Group;
                var pressed = false;
                card.MouseLeftButtonDown += (_, e) =>
                {
                    pressed = true;
                    e.Handled = true;
                };
                card.MouseLeave += (_, _) => pressed = false;
                card.MouseLeftButtonUp += (_, e) =>
                {
                    if (!pressed) return;
                    pressed = false;
                    e.Handled = true;
                    _select(group);
                };
                _cards.Add(group, card);
            }
            else card.Art.SetMembers(entry.Members);
            card.Art.SetPreviewHandoffs(_previewHandoffs);
            card.Opacity = _requested == entry.Group ? .70 : 1;
            var title = string.Join(Environment.NewLine, entry.Members.Select(member => member.Art.Title));
            System.Windows.Automation.AutomationProperties.SetName(card, title);
            var menu = new ContextMenu();
            if (entry.CanMerge)
            {
                var merge = new MenuItem { Header = "加入当前组" };
                merge.Click += (_, _) => _merge(entry.Group);
                menu.Items.Add(merge);
            }
            if (entry.Members.Count > 1)
            {
                var split = new MenuItem { Header = "移出窗口" };
                foreach (var member in entry.Members)
                {
                    var item = new MenuItem { Header = member.Art.Title };
                    item.Click += (_, _) => _split(member.Handle);
                    split.Items.Add(item);
                }
                menu.Items.Add(split);
            }
            var deselect = new MenuItem { Header = "移出台前调度" };
            if (entry.Members.Count == 1)
            {
                var handle = entry.Members[0].Handle;
                deselect.Click += (_, _) => _deselect(handle);
            }
            else
            {
                foreach (var member in entry.Members)
                {
                    var item = new MenuItem { Header = member.Art.Title };
                    item.Click += (_, _) => _deselect(member.Handle);
                    deselect.Items.Add(item);
                }
            }
            menu.Items.Add(deselect);
            card.ContextMenu = menu;
            if (index < _stack.Children.Count && _stack.Children[index] == card) continue;
            _stack.Children.Remove(card);
            _stack.Children.Insert(index, card);
        }
    }

    private sealed class CardBorder : Border
    {
        public CardBorder(StageCardArt art, Func<bool> animate)
        {
            Art = art;
            Height = CardPitch;
            Background = HitBackground;
            Cursor = Cursors.Hand;
            Child = art;
            MouseEnter += (_, _) => Art.SetHovered(true, animate());
            MouseLeave += (_, _) => Art.SetHovered(false, animate());
        }

        public StageCardArt Art { get; }
    }
}

internal sealed class StageCardArt(IReadOnlyList<(nint Handle, StageArt Art)> members, IReadOnlySet<nint> movingPreviews) : FrameworkElement
{
    private static readonly DependencyProperty HoverProgressProperty = DependencyProperty.Register(
        "HoverProgress", typeof(double), typeof(StageCardArt), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    private IReadOnlyList<(nint Handle, StageArt Art)> _members = members;
    private IReadOnlySet<nint> _movingPreviews = movingPreviews;
    private IReadOnlyDictionary<nint, double>? _previewHandoffs;

    public void SetMembers(IReadOnlyList<(nint Handle, StageArt Art)> value)
    {
        _members = value;
        InvalidateVisual();
    }

    public void SetHovered(bool hovered, bool animate)
    {
        var from = (double)GetValue(HoverProgressProperty);
        var target = hovered ? 1d : 0d;
        SetValue(HoverProgressProperty, target);
        BeginAnimation(HoverProgressProperty, animate && from != target
            ? new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(hovered ? 110 : 150))
            {
                DecelerationRatio = .55, FillBehavior = FillBehavior.Stop,
            }
            : null);
    }

    public void SetMovingPreviews(IReadOnlySet<nint> handles)
    {
        _movingPreviews = handles;
        InvalidateVisual();
    }

    public void SetPreviewHandoffs(IReadOnlyDictionary<nint, double>? handoffs)
    {
        var relevant = handoffs is not null && _members.Any(member => handoffs.ContainsKey(member.Handle)) ? handoffs : null;
        if (_previewHandoffs is null && relevant is null) return;
        _previewHandoffs = relevant;
        InvalidateVisual();
    }

    public static double PreviewOpacity(int index) => .85 - Math.Min(index, 2) * .14;
    public static PixelRect PlaceMember(PixelRect slot, double aspect, int index, DisplayInfo display)
    {
        var rect = MemberRect(aspect, Math.Min(index, 2));
        return new(slot.X + display.ToPixels(rect.X), slot.Y + display.ToPixels(rect.Y),
            Math.Max(1, display.ToPixels(rect.Width)), Math.Max(1, display.ToPixels(rect.Height)));
    }

    private static Rect MemberRect(double aspect, int index)
    {
        var availableWidth = 126 - 2 * index;
        var height = Math.Min(76, availableWidth / aspect);
        var width = Math.Min(availableWidth, height * aspect);
        return new Rect(5 + index * 6 + (availableWidth - width) / 2, (85 - height) / 2 + index * 4, width, height);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var hover = (double)GetValue(HoverProgressProperty);
        var shade = .22 * (1 - hover);
        var edge = StageArtRenderer.PreviewEdge(hover);
        var count = Math.Min(3, _members.Count);
        for (var index = count - 1; index >= 0; index--)
        {
            var member = _members[index];
            var weight = 1.0;
            if (_movingPreviews.Contains(member.Handle)
                && (_previewHandoffs?.TryGetValue(member.Handle, out weight) != true || weight <= 0)) continue;
            var art = member.Art;
            var preview = art.Preview;
            var ratio = preview is null ? 1.65 : (double)preview.PixelWidth / preview.PixelHeight;
            var rect = MemberRect(ratio, index);
            StageArtRenderer.Draw(dc, art, rect, weight * PreviewOpacity(index), 6, 7, 1, edge, shade * weight);
        }
        for (var index = 0; index < count; index++)
        {
            var rect = new Rect(8 + index * 21, 73, 21, 21);
            dc.DrawRoundedRectangle(StageArtRenderer.Frame, null, rect, 5, 5);
            if (_members[index].Art.Icon is { } icon) dc.DrawImage(icon, new Rect(rect.X + 2, rect.Y + 2, 17, 17));
            else StageArtRenderer.DrawLabel(dc, _members[index].Art.Title[..1], rect, 10);
        }
    }
}

internal static class StageArtRenderer
{
    public static readonly Brush Frame = Frozen(234, 26, 31, 42);
    public static readonly Brush Shadow = Frozen(84, 7, 12, 22);
    private static readonly Color RestingEdgeColor = Color.FromArgb(128, 139, 166, 196);
    private static readonly Color HoveredEdgeColor = Color.FromArgb(248, 195, 225, 255);
    private static readonly Pen RestingEdge = CreateEdge(RestingEdgeColor);
    private static readonly Pen HoveredEdge = CreateEdge(HoveredEdgeColor);
    private static readonly Typeface Face = new("Microsoft YaHei UI");

    public static Pen PreviewEdge(double hover) => hover switch
    {
        <= 0 => RestingEdge,
        >= 1 => HoveredEdge,
        _ => CreateEdge(RestingEdgeColor + (HoveredEdgeColor - RestingEdgeColor) * (float)hover),
    };

    private static Pen CreateEdge(Color color)
    {
        var pen = new Pen(new SolidColorBrush(color), 1);
        pen.Freeze();
        return pen;
    }

    public static void Draw(DrawingContext dc, StageArt art, Rect bounds, double opacity, double skew, double radius, double shadow,
        Pen edgePen, double shade)
    {
        if (bounds.IsEmpty || bounds.Width < 1 || bounds.Height < 1) return;
        dc.PushOpacity(Math.Clamp(opacity, 0, 1));
        dc.PushTransform(new SkewTransform(0, -skew, bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2));
        if (shadow > 0)
        {
            dc.PushOpacity(shadow);
            for (var step = 4; step >= 1; step--)
            {
                var outer = new Rect(bounds.X - step, bounds.Y + step, bounds.Width + 2 * step, bounds.Height + step);
                dc.DrawRoundedRectangle(Shadow, null, outer, radius + step, radius + step);
            }
            dc.Pop();
        }
        dc.DrawRoundedRectangle(Frame, null, bounds, radius, radius);
        dc.PushClip(new RectangleGeometry(bounds, radius, radius));
        if (art.Preview is { } preview) dc.DrawImage(preview, bounds);
        else
        {
            if (art.Icon is { } icon)
            {
                var edge = Math.Min(38, bounds.Height * .45);
                dc.DrawImage(icon, new Rect(bounds.X + (bounds.Width - edge) / 2, bounds.Y + bounds.Height * .12, edge, edge));
            }
            DrawLabel(dc, art.Title, new Rect(bounds.X, bounds.Y + bounds.Height * .60, bounds.Width, bounds.Height * .35), 10);
        }
        if (shade > 0)
        {
            dc.PushOpacity(shade);
            dc.DrawRectangle(Brushes.Black, null, bounds);
            dc.Pop();
        }
        dc.Pop();
        dc.DrawRoundedRectangle(null, edgePen, bounds, radius, radius);
        dc.Pop();
        dc.Pop();
    }

    public static void DrawLabel(DrawingContext dc, string title, Rect rect, double size)
    {
        if (rect.Width < 8 || rect.Height < 9) return;
        var text = new FormattedText(title, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, size, Brushes.White, 1)
        {
            TextAlignment = TextAlignment.Center, MaxTextWidth = rect.Width, MaxTextHeight = rect.Height,
            MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis
        };
        dc.DrawText(text, new Point(rect.X, rect.Y));
    }

    private static SolidColorBrush Frozen(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}

internal sealed class StageMotionOverlay : StageOverlay
{
    private readonly Dictionary<nint, MotionVisual> _motions = [];
    private readonly StageDrawing _drawing = new();
    private readonly DisplayInfo _display;
    private TimeSpan? _lastRenderTime;
    private bool _rendering, _prepared;
    private StageRail? _handoffRail;
    private readonly Dictionary<nint, double> _railWeights = [];

    public StageMotionOverlay(DisplayInfo display) : base(true)
    {
        _display = display;
        Content = _drawing;
    }

    public bool HasMotion(nint handle) => _motions.ContainsKey(handle);
    public bool HasMotions => _motions.Count > 0;
    public IEnumerable<nint> MovingHandles => _motions.Keys;
    public bool ClosingSettled => _motions.Values.All(motion => motion.Opening || motion.Track.IsSettled);
    public bool CanRestoreNativeWindows => _motions.Values.All(motion => !motion.Opening || motion.Track.CanRestoreNativeWindow);
    public bool HandoffComplete => _motions.Values.All(motion => motion.Track.HandoffComplete);

    public BitmapSource? PreviewFor(nint handle) => _motions.GetValueOrDefault(handle)?.Preview;

    public void Target(nint handle, StageArt art, PixelRect start, PixelRect end, bool opening, bool hold, double cardOpacity)
    {
        if (!_motions.TryGetValue(handle, out var motion))
        {
            motion = new MotionVisual(art, Local(start), opening);
            _motions.Add(handle, motion);
            _drawing.Place(motion.Visual, opening);
        }
        else if (hold)
        {
            motion.Track.Resume();
            if (IsVisible) EnsureRendering();
            return;
        }
        if (motion.Opening != opening) _drawing.Place(motion.Visual, opening);
        motion.SetDestination(Local(end), opening, hold, cardOpacity);
        if (IsVisible) EnsureRendering();
    }

    public void Prepare()
    {
        if (_prepared) return;
        Place(_display.Bounds, _display);
        _prepared = true;
    }

    public void Start()
    {
        if (!HasMotions) return;
        if (!IsVisible)
        {
            Prepare();
            Show();
        }
        EnsureRendering();
    }

    public void BeginOpeningHandoff()
    {
        foreach (var motion in _motions.Values)
            if (motion.Opening) motion.Track.BeginHandoff();
        if (IsVisible) EnsureRendering();
    }

    public void BeginClosingHandoff(StageRail rail)
    {
        foreach (var (handle, motion) in _motions)
        {
            if (motion.Opening) continue;
            motion.Track.BeginHandoff();
            _railWeights.Add(handle, 0);
        }
        if (_railWeights.Count == 0) return;
        _handoffRail = rail;
        rail.SetPreviewHandoffs(_railWeights);
        if (IsVisible) EnsureRendering();
    }

    public void CancelClosingHandoff()
    {
        _handoffRail?.SetPreviewHandoffs(null);
        _handoffRail = null;
        _railWeights.Clear();
    }

    public void Reset()
    {
        StopRendering();
        CancelClosingHandoff();
        _motions.Clear();
        _drawing.Clear();
        Hide();
    }

    private Rect Local(PixelRect rect) => new(_display.ToDips(rect.X - _display.Bounds.X),
        _display.ToDips(rect.Y - _display.Bounds.Y), _display.ToDips(rect.Width), _display.ToDips(rect.Height));

    private void EnsureRendering()
    {
        if (_rendering) return;
        _rendering = true;
        CompositionTarget.Rendering += Render;
    }

    private void StopRendering()
    {
        if (!_rendering) return;
        _rendering = false;
        _lastRenderTime = null;
        CompositionTarget.Rendering -= Render;
    }

    private void Render(object? sender, EventArgs args)
    {
        if (args is not RenderingEventArgs frame || _lastRenderTime == frame.RenderingTime) return;
        var elapsed = _lastRenderTime is { } last ? (frame.RenderingTime - last).TotalSeconds : 0;
        _lastRenderTime = frame.RenderingTime;
        var needsFrames = false;
        foreach (var (handle, motion) in _motions)
        {
            if (!motion.Track.NeedsFrames) continue;
            motion.Track.Advance(elapsed);
            motion.ApplyPose();
            if (_railWeights.ContainsKey(handle)) _railWeights[handle] = motion.RailPreviewWeight;
            needsFrames |= motion.Track.NeedsFrames;
        }
        _handoffRail?.SetPreviewHandoffs(_railWeights);
        if (!needsFrames) StopRendering();
    }

    private sealed class StageDrawing : FrameworkElement
    {
        private readonly ContainerVisual _outgoing = new();
        private readonly ContainerVisual _incoming = new();
        private readonly VisualCollection _children;

        public StageDrawing() => _children = new VisualCollection(this) { _outgoing, _incoming };
        protected override int VisualChildrenCount => _children.Count;
        protected override Visual GetVisualChild(int index) => _children[index];

        public void Place(ContainerVisual visual, bool opening)
        {
            if (VisualTreeHelper.GetParent(visual) is ContainerVisual previous)
                previous.Children.Remove(visual);
            (opening ? _incoming : _outgoing).Children.Add(visual);
        }

        public void Clear()
        {
            _outgoing.Children.Clear();
            _incoming.Children.Clear();
        }
    }

    private sealed class MotionVisual
    {
        private static readonly Rect Unit = new(0, 0, 1, 1);
        private readonly MatrixTransform _transform = new();
        private readonly RectangleGeometry _clip = new(Unit);
        private readonly DrawingVisual _shadow = new();
        private double _cardOpacity = .85;

        public MotionVisual(StageArt art, Rect start, bool opening)
        {
            Opening = opening;
            Preview = art.Preview!;
            Track = new StageMotion(start, onStage: !opening);
            var image = new DrawingVisual { Clip = _clip };
            using (var dc = image.RenderOpen())
            {
                dc.DrawRectangle(StageArtRenderer.Frame, null, Unit);
                dc.DrawImage(Preview, Unit);
            }
            using (var dc = _shadow.RenderOpen())
            {
                for (var layer = 4; layer > 0; layer--)
                {
                    var inset = layer * .008;
                    dc.DrawRoundedRectangle(StageArtRenderer.Shadow, null,
                        new Rect(-inset, inset / 2, 1 + 2 * inset, 1 + inset), .05, .08);
                }
            }
            Visual = new ContainerVisual { Transform = _transform };
            Visual.Children.Add(_shadow);
            Visual.Children.Add(image);
        }

        public ContainerVisual Visual { get; }
        public BitmapSource Preview { get; }
        public StageMotion Track { get; }
        public double RailPreviewWeight => StageMotion.RailPreviewWeight(_cardOpacity, Visual.Opacity);
        public bool Opening { get; private set; }

        public void SetDestination(Rect end, bool opening, bool hold, double cardOpacity)
        {
            Opening = opening;
            _cardOpacity = cardOpacity;
            if (!hold) Track.Retarget(end, opening);
            ApplyPose();
        }

        public void ApplyPose()
        {
            var pose = Track.Pose;
            var bounds = pose.Bounds;
            var tilt = -Math.Tan(pose.Depth * 6 * Math.PI / 180);
            _transform.Matrix = new Matrix(bounds.Width, bounds.Width * tilt, 0, bounds.Height,
                bounds.X, bounds.Y - bounds.Width * tilt / 2);
            Visual.Opacity = (1 - (1 - _cardOpacity) * pose.Depth) * pose.HandoffOpacity;
            _shadow.Opacity = pose.Depth;
            _clip.RadiusX = Math.Min(.5, 7 * pose.Depth / bounds.Width);
            _clip.RadiusY = Math.Min(.5, 7 * pose.Depth / bounds.Height);
        }
    }
}
