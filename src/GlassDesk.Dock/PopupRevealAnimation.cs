using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using GlassDesk.UI.Theming;
using GlassDesk.UI.Controls;
using GlassDesk.Platform;
using System.Windows.Interop;

namespace GlassDesk.Dock;

/// <summary>Fade the panel/shadow together while its content settles into place.</summary>
internal sealed class PopupRevealAnimation
{
    private readonly GlassWindow _window;
    private readonly TranslateTransform _shift = new();
    private readonly ThemeService? _theme;
    private long _generation;
    private bool _hasPresented;

    internal PopupRevealAnimation(GlassWindow window, FrameworkElement content)
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
        _window.PresentationOpacity = 0;
        _shift.Y = -8;
        if (_hasPresented)
            LayeredWindowPresentation.ClearHiddenPixels(new WindowInteropHelper(_window).Handle);
    }

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_window.IsVisible) _hasPresented = true;
        if (!_window.IsVisible || !AnimationsEnabled) { Finish(); return; }
        var generation = ++_generation;
        var duration = TimeSpan.FromMilliseconds(200);
        var fade = new DoubleAnimation(0, 1, duration)
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        fade.Completed += (_, _) => { if (generation == _generation) Finish(); };
        _window.BeginAnimation(GlassWindow.PresentationOpacityProperty, fade);
        _shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-8, 0, duration)
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        // Keep the prepared base values concealed until the first clock tick.
        // Finish commits the endpoints while clocks are active, then removes them.
    }

    private void Finish()
    {
        ++_generation;
        _window.PresentationOpacity = 1;
        _shift.Y = 0;
        _window.BeginAnimation(GlassWindow.PresentationOpacityProperty, null);
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
