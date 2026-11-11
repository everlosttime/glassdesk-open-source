using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using GlassDesk.UI.Theming;

namespace GlassDesk.Dock;

/// <summary>Fade the panel/shadow together while its content settles into place.</summary>
internal sealed class PopupRevealAnimation
{
    private readonly Window _window;
    private readonly TranslateTransform _shift = new();
    private readonly ThemeService? _theme;
    private long _generation;

    internal PopupRevealAnimation(Window window, FrameworkElement content)
    {
        _window = window;
        content.RenderTransform = _shift;
        _theme = ThemeService.Current;
        if (_theme is not null) _theme.Changed += OnMotionPreferenceChanged;
        SystemParameters.StaticPropertyChanged += OnSystemPreferenceChanged;
        window.IsVisibleChanged += OnVisibilityChanged;
        window.Closed += OnClosed;
    }

    private static bool AnimationsEnabled => ThemeService.Current?.AnimationsEnabled != false
        && SystemParameters.ClientAreaAnimation;

    internal void Prepare()
    {
        Finish();
        if (!AnimationsEnabled) return;
        _window.Opacity = 0;
        _shift.Y = -8;
    }

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!_window.IsVisible || !AnimationsEnabled) { Finish(); return; }
        var generation = ++_generation;
        var duration = TimeSpan.FromMilliseconds(200);
        var fade = new DoubleAnimation(0, 1, duration)
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        fade.Completed += (_, _) => { if (generation == _generation) Finish(); };
        _window.BeginAnimation(UIElement.OpacityProperty, fade);
        _shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-8, 0, duration)
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        // Keep the prepared base values concealed until the first clock tick.
        // Finish commits the endpoints while clocks are active, then removes them.
    }

    private void Finish()
    {
        ++_generation;
        _window.Opacity = 1;
        _shift.Y = 0;
        _window.BeginAnimation(UIElement.OpacityProperty, null);
        _shift.BeginAnimation(TranslateTransform.YProperty, null);
    }

    private void OnMotionPreferenceChanged(object? sender, EventArgs e)
    { if (!AnimationsEnabled) Finish(); }
    private void OnSystemPreferenceChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation) && !AnimationsEnabled) Finish(); }
    private void OnClosed(object? sender, EventArgs e)
    {
        Finish();
        if (_theme is not null) _theme.Changed -= OnMotionPreferenceChanged;
        SystemParameters.StaticPropertyChanged -= OnSystemPreferenceChanged;
        _window.IsVisibleChanged -= OnVisibilityChanged;
        _window.Closed -= OnClosed;
    }
}
