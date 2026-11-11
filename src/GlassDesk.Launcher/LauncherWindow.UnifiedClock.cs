using System.Windows.Media;
using GlassDesk.Platform.Displays;
using GlassDesk.Rendering;

namespace GlassDesk.Launcher;

public partial class LauncherWindow
{
    private NativePresentationClock? _unifiedFrameClock;
    private bool _unifiedClockDriving;

    private bool StartUnifiedClock()
    {
        if (!_unifiedActive || _unifiedPresenter is null) return false;
        try
        {
            var rate = DisplayService.RefreshForWindow(Handle) ?? throw new NotSupportedException("Animation display refresh is unavailable.");
            _unifiedFrameClock ??= new NativePresentationClock(rate.Numerator, rate.Denominator,
                _unifiedPresenter.FrameLatencyHandle, OnUnifiedClockFrame, FailUnifiedTransition);
            _unifiedFrameClock.SetRefreshRate(rate.Numerator, rate.Denominator);
            _unifiedClockDriving = true;
            _unifiedFrameClock.RequestFrame();
            return true;
        }
        catch (Exception error) when (IsUnifiedFailure(error) || error is System.ComponentModel.Win32Exception)
        { _unifiedClockDriving = false; FailUnifiedTransition(error); return false; }
    }

    private void OnUnifiedClockFrame()
    {
        var clock = _unifiedFrameClock;
        if (clock is null || !_transformActive || !_unifiedClockDriving) return;
        OnContainerTransformRendering(this, EventArgs.Empty);
        if (ReferenceEquals(clock, _unifiedFrameClock) && _transformActive && _unifiedClockDriving)
        {
            if (_unifiedSourceWaitAt != 0) clock.KeepUnusedAdmission();
            clock.RequestFrame();
        }
    }

    private void StopUnifiedClock()
    {
        _unifiedFrameClock?.Stop(); _unifiedClockDriving = false;
    }

    private void DisposeUnifiedClock()
    {
        var driving = _unifiedClockDriving;
        _unifiedFrameClock?.Dispose(); _unifiedFrameClock = null; _unifiedClockDriving = false;
        if (driving && _transformActive)
        {
            _lastTransformRenderingTime = TimeSpan.MinValue;
            CompositionTarget.Rendering += OnContainerTransformRendering;
        }
    }
}
