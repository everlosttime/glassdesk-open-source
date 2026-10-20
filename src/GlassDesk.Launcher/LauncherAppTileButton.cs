using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using GlassDesk.UI.Theming;

namespace GlassDesk.Launcher;

internal sealed class LauncherAppTileButton : Button
{
    private readonly ScaleTransform _iconScale = new(1, 1);
    private readonly FrameworkElement _iconHost;
    private bool _folderHovered;
    internal FrameworkElement IconHost => _iconHost;
    internal void AnimateFolderArrival()
    {
        if (ThemeService.Current?.AnimationsEnabled == false) return;
        var animation = new DoubleAnimation(.93, 1, TimeSpan.FromMilliseconds(320))
        { EasingFunction = new BackEase { Amplitude = .5, EasingMode = EasingMode.EaseOut } };
        _iconScale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        _iconScale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }
    internal void SetFolderHovered(bool hovered)
    {
        _folderHovered = hovered;
        var value = hovered ? 1.045 : 1;
        if (ThemeService.Current?.AnimationsEnabled == false) { ResetIconScale(); return; }
        var animation = new DoubleAnimation(value, TimeSpan.FromMilliseconds(240))
        { EasingFunction = new BackEase { Amplitude = .22, EasingMode = EasingMode.EaseOut } };
        _iconScale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        _iconScale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    public LauncherAppTileButton(FrameworkElement iconHost)
    {
        _iconHost = iconHost;
        iconHost.RenderTransformOrigin = new Point(.5, .5);
        iconHost.RenderTransform = _iconScale;
        Unloaded += (_, _) => ResetIconScale();
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_iconHost is null) return;
        if (e.Property == IsVisibleProperty && e.NewValue is false)
            ResetIconScale();
        else if (e.Property == IsPressedProperty)
        {
            if (ThemeService.Current?.AnimationsEnabled == false)
            {
                ResetIconScale();
                return;
            }
            var motion = new DoubleAnimation(IsPressed ? .97 : 1,
                TimeSpan.FromMilliseconds(IsPressed ? 60 : 180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            if (!IsPressed)
                motion.Completed += (_, _) => { if (!IsPressed) ResetIconScale(); };
            _iconScale.BeginAnimation(ScaleTransform.ScaleXProperty, motion);
            _iconScale.BeginAnimation(ScaleTransform.ScaleYProperty, motion);
        }
    }

    private void ResetIconScale()
    {
        _iconScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _iconScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _iconScale.ScaleX = _iconScale.ScaleY = _folderHovered ? 1.045 : 1;
    }
}
