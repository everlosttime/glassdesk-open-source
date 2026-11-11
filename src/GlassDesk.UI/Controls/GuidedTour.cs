using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using System.Windows.Data;
using GlassDesk.UI.Theming;

namespace GlassDesk.UI.Controls;

public enum GuideInteraction { Explore, Focus, Text, ClearText, Value, Toggle, Selection, Click }

public sealed record GuidedTourStep(FrameworkElement Target, string Title, string Description,
    GuideInteraction Interaction = GuideInteraction.Explore, Action? Prepare = null);

/// <summary>A temporary, window-local lesson. The real target keeps receiving input.</summary>
public sealed class GuidedTour : IDisposable
{
    private readonly Window _owner;
    private readonly FrameworkElement _root;
    private readonly IReadOnlyList<GuidedTourStep> _steps;
    private readonly AdornerLayer _layer;
    private readonly CoachAdorner _adorner;
    private readonly bool[] _observed;
    private Action? _detach;
    private DispatcherOperation? _position;
    private int _index;
    public bool IsActive { get; private set; } = true;
    public bool IsCompleted { get; private set; }
    public event EventHandler? Closed;

    public GuidedTour(Window owner, FrameworkElement root, IReadOnlyList<GuidedTourStep> steps)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(steps);
        if (steps.Count == 0) throw new ArgumentException("A guide needs at least one step.", nameof(steps));
        _owner = owner; _root = root; _steps = steps.ToArray(); _observed = new bool[steps.Count];
        _layer = AdornerLayer.GetAdornerLayer(root) ?? throw new InvalidOperationException("The guide root needs an AdornerDecorator.");
        _adorner = new CoachAdorner(root, Previous, Next, Dispose);
        _layer.Add(_adorner);
        _owner.Closed += OnClosed;
        _owner.KeyDown += OnKeyDown;
        _owner.IsVisibleChanged += OnVisibility;
        _root.LayoutUpdated += OnLayout;
        ShowStep();
    }

    private void ShowStep()
    {
        _detach?.Invoke(); _detach = null; _position?.Abort();
        var step = _steps[_index];
        step.Prepare?.Invoke();
        _adorner.SetStep(step, _index, _steps.Count, _observed[_index]);
        // Section scrolling may already be queued. Bring this exact control into view
        // after that navigation, so long pages never leave the highlight offscreen.
        _position = _root.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            _position = null;
            if (!IsActive) return;
            step.Target.BringIntoView();
            _adorner.InvalidateMeasure();
            Observe(step);
        }));
    }

    private void Observe(GuidedTourStep step)
    {
        void Done() { if (!IsActive) return; _observed[_index] = true; _adorner.ShowFeedback(true); }
        switch (step.Interaction, step.Target)
        {
            case (GuideInteraction.Value, Slider slider):
                RoutedPropertyChangedEventHandler<double> value = (_, _) => Done();
                slider.ValueChanged += value; _detach = () => slider.ValueChanged -= value; break;
            case (GuideInteraction.Toggle, ToggleButton toggle):
                RoutedEventHandler changed = (_, _) => Done();
                toggle.Checked += changed; toggle.Unchecked += changed;
                _detach = () => { toggle.Checked -= changed; toggle.Unchecked -= changed; }; break;
            case (GuideInteraction.Selection, Selector selector):
                SelectionChangedEventHandler selected = (_, _) => Done();
                selector.SelectionChanged += selected; _detach = () => selector.SelectionChanged -= selected; break;
            case (GuideInteraction.Text or GuideInteraction.ClearText, TextBox box):
                TextChangedEventHandler text = (_, _) =>
                {
                    if (step.Interaction == GuideInteraction.Text ? !string.IsNullOrWhiteSpace(box.Text) : box.Text.Length == 0) Done();
                };
                box.TextChanged += text; _detach = () => box.TextChanged -= text; break;
            case (GuideInteraction.Click, ButtonBase button):
                RoutedEventHandler click = (_, _) => Done();
                button.Click += click; _detach = () => button.Click -= click; break;
            case (GuideInteraction.Focus, _):
                KeyboardFocusChangedEventHandler focus = (_, _) => Done();
                step.Target.GotKeyboardFocus += focus; _detach = () => step.Target.GotKeyboardFocus -= focus; break;
        }
    }

    private void Previous() { if (IsActive && _index > 0) { _index--; ShowStep(); } }
    private void Next()
    {
        if (!IsActive) return;
        if (_index + 1 == _steps.Count) { IsCompleted = true; Dispose(); }
        else { _index++; ShowStep(); }
    }
    private void OnLayout(object? sender, EventArgs e) => _adorner.Position();
    private void OnClosed(object? sender, EventArgs e) => Dispose();
    private void OnKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Escape && !e.Handled) { e.Handled = true; Dispose(); } }
    private void OnVisibility(object sender, DependencyPropertyChangedEventArgs e) { if (!_owner.IsVisible) Dispose(); }
    public void Dispose()
    {
        if (!IsActive) return;
        IsActive = false;
        _position?.Abort(); _position = null;
        _detach?.Invoke(); _detach = null;
        _root.LayoutUpdated -= OnLayout;
        _owner.Closed -= OnClosed; _owner.IsVisibleChanged -= OnVisibility;
        _owner.KeyDown -= OnKeyDown;
        _layer.Remove(_adorner);
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class CoachAdorner : Adorner
    {
        private readonly Canvas _canvas = new();
        private readonly Border _ring, _card;
        private readonly TextBlock _progress, _heading, _body, _feedback;
        private readonly Button _previous, _next;
        private FrameworkElement? _target;
        private bool _interactive;
        private readonly VisualCollection _visuals;

        internal CoachAdorner(FrameworkElement root, Action previous, Action next, Action close) : base(root)
        {
            _visuals = new VisualCollection(this) { _canvas };
            var accent = new SolidColorBrush(Color.FromRgb(77, 150, 255));
            _ring = new Border { BorderBrush = accent, BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(8),
                IsHitTestVisible = false, Effect = new DropShadowEffect { Color = Color.FromRgb(77, 150, 255), BlurRadius = 12, ShadowDepth = 0, Opacity = .35 } };
            _canvas.Children.Add(_ring);
            var content = new Grid();
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var header = new StackPanel();
            _progress = Text(11, .66); _heading = Text(19, 1); _heading.FontWeight = FontWeights.SemiBold;
            _heading.Margin = new Thickness(0, 7, 0, 10);
            _body = Text(13, .90); _body.LineHeight = 21;
            _feedback = Text(12, .8); _feedback.Margin = new Thickness(0, 12, 0, 12);
            AutomationProperties.SetAutomationId(_feedback, "guide-feedback");
            header.Children.Add(_progress); header.Children.Add(_heading); content.Children.Add(header);
            var body = new StackPanel(); body.Children.Add(_body); body.Children.Add(_feedback);
            var scroller = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            Grid.SetRow(scroller, 1); content.Children.Add(scroller);
            var footer = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
            var quit = Button("返回教学", "guide-close", close);
            _previous = Button("上一步", "guide-previous", previous); _next = Button("下一步", "guide-next", next);
            _next.Background = new SolidColorBrush(Color.FromRgb(43, 111, 225));
            _next.Foreground = Brushes.White;
            footer.Children.Add(quit); footer.Children.Add(_previous); footer.Children.Add(_next);
            Grid.SetRow(footer, 2); content.Children.Add(footer);
            _card = new Border { Padding = new Thickness(18), CornerRadius = new CornerRadius(18),
                Background = new SolidColorBrush(Color.FromRgb(20, 32, 53)), BorderBrush = new SolidColorBrush(Color.FromRgb(79, 113, 161)), BorderThickness = new Thickness(1),
                Child = content };
            TextElement.SetForeground(_card, Brushes.White);
            AutomationProperties.SetAutomationId(_card, "guide-card");
            AutomationProperties.SetName(_card, "操作引导");
            _canvas.Children.Add(_card);
        }
        private static TextBlock Text(double size, double opacity) => new() { FontSize = size, Opacity = opacity, TextWrapping = TextWrapping.Wrap };
        private static Button Button(string label, string id, Action action)
        {
            var button = new Button { Content = label, Padding = new Thickness(9, 7, 9, 7), Margin = new Thickness(4, 3, 0, 0),
                Foreground = Brushes.White, Background = new SolidColorBrush(Color.FromRgb(40, 57, 82)), BorderBrush = Brushes.Transparent, Cursor = Cursors.Hand,
                Template = RoundedButtonTemplate() };
            AutomationProperties.SetAutomationId(button, id); AutomationProperties.SetName(button, label);
            button.Click += (_, _) => action(); return button;
        }
        private static ControlTemplate RoundedButtonTemplate()
        {
            var border = new FrameworkElementFactory(typeof(Border), "Chrome");
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            foreach (var (property, name) in new[] { (Border.BackgroundProperty, "Background"), (Border.BorderBrushProperty, "BorderBrush"), (Border.PaddingProperty, "Padding") })
                border.SetBinding(property, new Binding(name) { RelativeSource = RelativeSource.TemplatedParent });
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            presenter.SetBinding(ContentPresenter.ContentProperty, new Binding("Content") { RelativeSource = RelativeSource.TemplatedParent });
            border.AppendChild(presenter);
            var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
            var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(OpacityProperty, .82, "Chrome")); template.Triggers.Add(hover);
            var focus = new Trigger { Property = IsKeyboardFocusedProperty, Value = true };
            focus.Setters.Add(new Setter(Border.BorderBrushProperty, Brushes.LightSkyBlue, "Chrome")); template.Triggers.Add(focus);
            return template;
        }
        internal void SetStep(GuidedTourStep step, int index, int count, bool observed)
        {
            _target = step.Target; _interactive = step.Interaction != GuideInteraction.Explore;
            _progress.Text = $"跟着做 · {index + 1:00} / {count:00}";
            _heading.Text = step.Title; _body.Text = step.Description;
            _previous.IsEnabled = index > 0; _next.Content = index + 1 == count ? "完成实操" : "下一步";
            ShowFeedback(observed); InvalidateMeasure();
        }
        internal void ShowFeedback(bool observed) => _feedback.Text = observed ? "✓ 已完成这一步的操作" : _interactive ? "可以亲手试一试，也可以直接下一步。" : "了解后点下一步；无需修改当前设置。";
        protected override int VisualChildrenCount => 1;
        protected override Visual GetVisualChild(int index) => _visuals[index];
        // Only the card's actual children capture clicks. The highlight, empty canvas
        // and adorner never swallow clicks intended for the real product controls.
        protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters) => null;
        protected override Size MeasureOverride(Size constraint)
        {
            var size = AdornedElement.RenderSize;
            _card.Width = Math.Min(324, Math.Max(160, size.Width - 20));
            _card.MaxHeight = CardHeightLimit(size);
            _canvas.Measure(size); return size;
        }
        protected override Size ArrangeOverride(Size finalSize) { _canvas.Arrange(new Rect(finalSize)); Position(); return finalSize; }
        internal void Position()
        {
            if (_target is null || !_target.IsVisible || AdornedElement.RenderSize.Width < 1) { _ring.Visibility = Visibility.Collapsed; return; }
            if (!AdornedElement.IsAncestorOf(_target)) { _ring.Visibility = Visibility.Collapsed; return; }
            var area = new Rect(AdornedElement.RenderSize);
            var limit = CardHeightLimit(area.Size);
            if (Math.Abs(_card.MaxHeight - limit) > .2) { _card.MaxHeight = limit; InvalidateMeasure(); return; }
            var target = _target.TransformToAncestor(AdornedElement).TransformBounds(new Rect(_target.RenderSize));
            target.Inflate(5, 5); target.Intersect(area);
            if (target.IsEmpty) { _ring.Visibility = Visibility.Collapsed; return; }
            _ring.Visibility = Visibility.Visible;
            Set(_ring, target.Left, target.Top); SetSize(_ring, target.Width, target.Height);
            var width = _card.DesiredSize.Width; var height = _card.DesiredSize.Height;
            if (width < 1 || height < 1) return;
            const double gap = 16;
            var candidates = new[] {
                new Point(target.Right + gap, target.Top), new Point(target.Left - width - gap, target.Top),
                new Point(target.Left, target.Bottom + gap), new Point(target.Left, target.Top - height - gap),
                new Point(area.Width - width - 10, area.Height - height - 10), new Point(10, 10) };
            var location = candidates.Select(point => new Point(Math.Clamp(point.X, 10, Math.Max(10, area.Width - width - 10)),
                    Math.Clamp(point.Y, 10, Math.Max(10, area.Height - height - 10))))
                .OrderBy(point => Overlap(new Rect(point, new Size(width, height)), target)).First();
            Set(_card, location.X, location.Y);
        }
        private double CardHeightLimit(Size area)
        {
            var normal = Math.Max(160, area.Height - 20);
            if (_target is null || !_target.IsVisible || !AdornedElement.IsAncestorOf(_target)) return normal;
            var bounds = _target.TransformToAncestor(AdornedElement).TransformBounds(new Rect(_target.RenderSize));
            bounds.Inflate(5, 5);
            if (area.Width - bounds.Right - 26 >= _card.Width || bounds.Left - 26 >= _card.Width) return normal;
            // With narrow windows, scroll the explanation inside a shorter card;
            // keep the title and navigation pinned and leave the entire target free.
            return Math.Clamp(Math.Max(bounds.Top - 26, area.Height - bounds.Bottom - 26), 160, normal);
        }
        private static double Overlap(Rect left, Rect right) { left.Intersect(right); return left.IsEmpty ? 0 : left.Width * left.Height; }
        private static void Set(FrameworkElement element, double x, double y)
        {
            if (double.IsNaN(Canvas.GetLeft(element)) || Math.Abs(Canvas.GetLeft(element) - x) > .2) Canvas.SetLeft(element, x);
            if (double.IsNaN(Canvas.GetTop(element)) || Math.Abs(Canvas.GetTop(element) - y) > .2) Canvas.SetTop(element, y);
        }
        private static void SetSize(FrameworkElement element, double width, double height)
        {
            if (double.IsNaN(element.Width) || Math.Abs(element.Width - width) > .2) element.Width = width;
            if (double.IsNaN(element.Height) || Math.Abs(element.Height - height) > .2) element.Height = height;
        }
    }
}
