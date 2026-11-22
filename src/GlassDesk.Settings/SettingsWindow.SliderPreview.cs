using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace GlassDesk.Settings;

public partial class SettingsWindow
{
    private Slider? _draggedSlider;
    private DispatcherOperation? _sliderPreview;
    private Action? _commitSliderDrag;

    private void InitializeSliderPreviews()
    {
        Slider[] sliders =
        [
            DockTransparencySlider, LauncherTransparencySlider, StatusBarTransparencySlider,
            ControlCenterTransparencySlider, DockBuiltinIconBrightnessSlider, LauncherTextBrightnessSlider,
            StatusBarTextBrightnessSlider, DockScaleSlider, DockSpacingSlider, DockFrameLengthSlider,
            DockOffsetXSlider, DockOffsetYSlider, StatusBarScaleSlider, StatusBarOffsetXSlider,
        ];
        foreach (var slider in sliders)
        {
            slider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) =>
            {
                FinishSliderDrag();
                _draggedSlider = slider;
            }), handledEventsToo: true);
            slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => FinishSliderDrag()),
                handledEventsToo: true);
        }
        IsVisibleChanged += (_, _) => { if (!IsVisible) FinishSliderDrag(); };
        Closing += (_, _) => FinishSliderDrag();
    }

    private bool QueueSliderPreview(object sender, Action preview, Action commit)
    {
        if (!ReferenceEquals(_draggedSlider, sender)) return false;
        _commitSliderDrag = commit;
        _sliderPreview ??= Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            _sliderPreview = null;
            preview();
        }));
        return true;
    }

    private void FinishSliderDrag()
    {
        _draggedSlider = null;
        _sliderPreview?.Abort();
        _sliderPreview = null;
        var commit = _commitSliderDrag;
        _commitSliderDrag = null;
        commit?.Invoke();
    }
}
