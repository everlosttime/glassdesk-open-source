using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using GlassDesk.Rendering;

namespace GlassDesk.UI.Controls;

public sealed partial class GlassShadow
{
    private readonly bool _nativeRequested;
    private NativeDirectPresenter? _nativePresenter;
    private DirectShadowLayer[]? _nativeLayers;
    private bool _nativeFailed, _nativeReducedDetail, _preparingNative, _nativePending;
    private int _nativeWidth, _nativeHeight, _nativeError, _nativeErrorStage;
    private DispatcherTimer? _nativeRetry;
    private double _lastNativeMilliseconds;
    private string? _nativeFailure;

    internal void PrewarmNative()
    {
        if (!_nativeRequested || _nativeFailed || _disposed || _owner.IsVisible) return;
        _preparingNative = true;
        try { Update(); }
        finally { _preparingNative = false; }
    }

    internal void RetireNative()
    {
        _owner.Dispatcher.VerifyAccess();
        _nativeRetry?.Stop(); _nativePending = false;
        _nativePresenter?.Dispose(); _nativePresenter = null;
        _nativeWidth = _nativeHeight = 0;
    }

    internal object InspectPerformance()
    {
        _owner.Dispatcher.VerifyAccess();
        var statistics = _nativePresenter?.Statistics;
        return new { nativeRequested = _nativeRequested, nativeActive = _nativePresenter is not null,
            nativeFailed = _nativeFailed, errorCode = _nativeError, errorStage = _nativeErrorStage, submitted = statistics?.Submitted ?? 0,
            failure = _nativeFailure,
            wouldBlock = statistics?.WouldBlock ?? 0, contours = _nativePresenter?.ShadowStatistics.ContourCount ?? 0,
            targetBytes = _nativePresenter?.ShadowStatistics.TargetBytes ?? 0,
            lastRenderMilliseconds = _lastNativeMilliseconds, pending = _nativePending,
            retryScheduled = _nativeRetry?.IsEnabled == true };
    }

    private bool TryRenderNative(Rect panel, double radius)
    {
        if (!_nativeRequested || _nativeFailed) return false;
        var started = Stopwatch.GetTimestamp();
        var stage = 0;
        try
        {
            if (BackdropRecovery.RequiresBasicRendering) throw new NotSupportedException("Basic shadow fallback requested.");
            var width = Math.Max(1, (int)Math.Round(_shadowSize.Width * _shadowScale));
            var height = Math.Max(1, (int)Math.Round(_shadowSize.Height * _shadowScale));
            stage = 1;
            _nativePresenter ??= new NativeDirectPresenter(Handle, width, height);
            if (_nativeWidth != width || _nativeHeight != height)
            {
                stage = 2;
                if (_nativeWidth != 0) _nativePresenter.Resize(width, height);
                _nativeWidth = width; _nativeHeight = height;
            }
            Span<DirectShadowLayer> layers = _nativeLayers ??= new DirectShadowLayer[4];
            var count = CopyDirectLayers(layers, LiquidGlassDebugState.View, LiquidGlassDebugState.FinishingParameters, _ownerScale / _shadowScale);
            stage = 3;
            _nativePresenter.RenderShadow(new(_shadowSize, panel, (float)radius,
                (float)(_owner.Opacity * _transitionStrength),
                _transitionPanel is not null && _owner.UsesSquirclePanelGeometry, _nativeReducedDetail), layers[..count]);
            stage = 4;
            _nativePending = !_nativePresenter.Present();
            SetOpacity(_owner.Opacity * _transitionStrength);
            if (_nativePending && _window.IsVisible)
            {
                _nativeRetry ??= CreateRetry();
                _nativeRetry.Start();
            }
            else _nativeRetry?.Stop();
            return true;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException
            or COMException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            _nativeError = error.HResult; _nativeErrorStage = stage; _nativeFailed = true;
            _nativeFailure = error.ToString();
            RetireNative();
            _drawing.SetReducedDetail(_nativeReducedDetail);
            SetOpacity(_owner.Opacity * _transitionStrength);
            Trace.WriteLine("Launcher native shadow reverted: " + error.GetType().Name);
            return false;
        }
        finally { _lastNativeMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds; }
    }

    private DispatcherTimer CreateRetry()
    {
        // Only a not-yet-submitted latest state can schedule a retry. No animation
        // time is advanced here, and a hidden/settled submitted shadow has no ticks.
        var timer = new DispatcherTimer(DispatcherPriority.Render, _owner.Dispatcher)
            { Interval = TimeSpan.FromMilliseconds(1000 / Math.Clamp(
                GlassDesk.Platform.Displays.DisplayService.RefreshForWindow(new System.Windows.Interop.WindowInteropHelper(_owner).Handle)?.Hertz ?? 120, 1, 1000)) };
        timer.Tick += (_, _) =>
        {
            if (_disposed || !_nativePending || !_owner.IsVisible || !_window.IsVisible)
            { timer.Stop(); return; }
            UpdatePanelGeometry();
        };
        return timer;
    }
}
