namespace GlassDesk.Launcher;

public partial class LauncherWindow
{
    private bool _backgroundWorkSuspended;
    private long _iconRequestGeneration;
    private bool _iconsNeedReapply;

    /// <summary>前台窗口变化时只更新可见启动台的桌面取样。</summary>
    public void RequestGlassRefresh()
    {
        if (IsVisible) GlassSurface.RequestBackdropRefresh();
    }

    public void SetPerformanceSleeping(bool sleeping)
    {
        GlassSurface.SetPerformanceSleeping(sleeping);
        if (!sleeping || IsVisible || Handle == 0) return;
        RetireIndependentShadowResources();
        RetireUnifiedTransitionSurfaces();
        CancelDrag(); StopPageSlide();
        _iconRequestGeneration++;
        // Keep bounded, frozen icon pixels across idle/game sleep. Retire the large
        // visual tree and layered surface below without forcing a cold icon decode.
        _retainedTilePages.Clear(); _retainedTileKey = null; _lastTilesRenderKey = null;
        TileGrid.Children.Clear(); GroupItems.Children.Clear();
        _iconsNeedReapply = true;
        // Layered HWNDs retain their last full-size presentation surface even after
        // Hide. Release that backing size; placement is rebuilt before the next Show.
        _transformHostBounds = default;
        _hasTransformPath = false; _containerMotion = null;
        Width = 1; Height = 1;
    }

    public void SetBackgroundWorkSuspended(bool suspended)
    {
        if (_backgroundWorkSuspended == suspended) return;
        _backgroundWorkSuspended = suspended;
        _iconRequestGeneration++;
        _iconLoader.SetSuspended(suspended);
        if (!suspended && _iconsNeedReapply)
        {
            // Completed callbacks discarded during sleep are reapplied once on resume.
            _iconsNeedReapply = false;
            _lastTilesRenderKey = null;
            _retainedTilePages.Clear();
            if (IsVisible && _services is not null) RenderTiles();
        }
        if (!suspended) PreloadIcons();
    }
}
