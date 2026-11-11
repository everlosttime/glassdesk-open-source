using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using GlassDesk.Platform.Displays;

namespace GlassDesk.UI.Controls;

public sealed partial class LiquidGlassSurface
{
    internal bool ReuseSceneQueriesWithinTurn { get; set; }
    internal bool ValidateSceneDuringPaint { get; set; } = true;
    private long _lastSceneRecovery = -1;

    private bool NeedsPaintSceneVerification()
    {
        if (ValidateSceneDuringPaint || _backdropScene is null || _lastSceneRecovery != BackdropRecovery.Generation) return true;
        var stamp = ReadMaterialStamp();
        return _materialStamp is not null && _materialStamp != stamp
            || _compatibilityStamp is not null && _compatibilityStamp != stamp;
    }
    private GlassBackdropScene? _turnScene;
    private (nint Handle, PixelRect? Bounds, DpiScale Dpi, long Recovery, long Lifecycle, bool DesktopOnly) _turnSceneKey;
    private DispatcherOperation? _turnSceneReset;
    private long _sceneQueries, _sceneQueryReuses;

    private void ClearTurnSceneSnapshot()
    {
        _turnSceneReset?.Abort(); _turnSceneReset = null;
        _turnScene = null;
    }

    private GlassBackdropScene? ReadSceneForCurrentTurn(bool fresh)
    {
        if (!ReuseSceneQueriesWithinTurn || fresh)
        {
            ClearTurnSceneSnapshot(); ++_sceneQueries;
            return ReadBackdropScene();
        }
        var owner = Window.GetWindow(this);
        var handle = owner is null ? 0 : new WindowInteropHelper(owner).Handle;
        var bounds = owner is { IsVisible: false } && _revealHost is { } hidden
            ? hidden : DisplayService.GetWindowPixelBounds(handle);
        var key = (handle, bounds, VisualTreeHelper.GetDpi(this), BackdropRecovery.Generation, _lifecycleGeneration, DesktopOnlyBackdrop);
        if (_turnScene is not null && _turnSceneKey.Equals(key))
        {
            ++_sceneQueryReuses;
            return _turnScene;
        }
        ++_sceneQueries;
        var scene = ReadBackdropScene();
        if (scene is null) { ClearTurnSceneSnapshot(); return null; }
        _turnScene = scene; _turnSceneKey = key;
        // Share only the verification already performed by this uninterrupted UI
        // work item. Clear at Send priority before any queued render/input/worker
        // continuation. No wall-clock TTL or reuse across dispatcher work items.
        _turnSceneReset ??= Dispatcher.BeginInvoke(DispatcherPriority.Send, (Action)ClearTurnSceneSnapshot);
        return scene;
    }
}
