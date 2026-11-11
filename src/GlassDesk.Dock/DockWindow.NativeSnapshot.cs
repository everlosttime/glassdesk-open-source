using GlassDesk.Rendering;

namespace GlassDesk.Dock;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design","CA1001",Justification="WPF Window lifetime is Close/OnClosed; OnClosed disposes native foreground resources and the live source before base.OnClosed.")]
public partial class DockWindow
{
    private RetainedVisualDrawingSource? _retainedForegroundSource;
    private bool _foregroundSourceClosed;
    public RetainedSourceStatistics? RetainedForegroundStatistics=>_retainedForegroundSource?.Statistics;
    /// <summary>Opt-in live-source capture. Existing full capture remains the oracle;
    /// no backend, input alpha carrier, HWND or desktop policy is changed.</summary>
    public VisualDrawingSnapshot CaptureRetainedForegroundSnapshot()
    {
        VerifyAccess();ObjectDisposedException.ThrowIf(_foregroundSourceClosed,this);
        if(_retainedForegroundSource is null)
        {
            _retainedForegroundSource=new(this,ForegroundSnapshotOptions());
            _retainedForegroundSource.Track(_tileStrip,()=>_tileStrip.ForegroundDrawingVersion);
        }
        return _retainedForegroundSource.Capture();
    }
    private VisualSnapshotOptions ForegroundSnapshotOptions()=>new()
    {
        Exclude=visual=>ReferenceEquals(visual,GlassSurface) || ReferenceEquals(visual,NativeForegroundInputCarrier),
        PresentationOpacityCarrier=ForegroundPresentationCarrier
    };
    private void ReleaseForegroundSource(){_retainedForegroundSource?.Dispose();_retainedForegroundSource=null;}
    private void CloseForegroundSource()
    {_foregroundSourceClosed=true;ReleaseForegroundSource();}

    /// <summary>
    /// Opt-in source capture for the staged native foreground renderer. Uses the
    /// production content and independent drag overlay in their real paint order;
    /// excludes only the separately rendered glass material. Does not hide the
    /// WPF tree, select a backend, change input, or retain this Window.
    /// </summary>
    public VisualDrawingSnapshot CaptureForegroundSnapshot()
    {
        VerifyAccess();
        return VisualDrawingSnapshot.Capture(this,ForegroundSnapshotOptions());
    }
}
