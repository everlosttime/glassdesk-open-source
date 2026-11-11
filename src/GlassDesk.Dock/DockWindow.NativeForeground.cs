using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using GlassDesk.Rendering;

namespace GlassDesk.Dock;

public sealed record DockForegroundExperimentStatus(bool Active,ulong SubmittedFrames,ulong DeferredFrames,string? Failure,
    ForegroundResourceStatistics? Resources,ForegroundCompileStatistics? Compiled,RetainedSourceStatistics? Source);

public partial class DockWindow
{
    private NativeForegroundPresenter? _experimentalForeground;
    private NativeForegroundScene? _experimentalForegroundScene;
    private RetainedVisualDrawingSource? _experimentalFrameSource;
    private UIElement? _experimentalContent;
    private int _experimentalWidth,_experimentalHeight;
    private ulong _experimentalSubmitted,_experimentalDeferred;
    private string? _experimentalForegroundFailure;
    public DockForegroundExperimentStatus NativeForegroundExperimentStatus
    {
        get{VerifyAccess();return new(_experimentalForeground is not null,_experimentalSubmitted,_experimentalDeferred,
            _experimentalForegroundFailure,_experimentalForeground?.ResourceStatistics,_experimentalForeground?.CompileStatistics,_experimentalFrameSource?.Statistics);}
    }

    /// <summary>Explicit diagnostic entry only; no production backend/cadence is
    /// selected. Call after layout/render settles; the owner drives each frame.</summary>
    public bool StartNativeForegroundExperiment()
    {
        VerifyAccess();ObjectDisposedException.ThrowIf(_foregroundSourceClosed,this);
        if(_experimentalForeground is not null)return true;
        _experimentalForegroundFailure=null;_experimentalSubmitted=0;_experimentalDeferred=0;
        try
        {
            if(!IsSurfacePresented || Content is not UIElement{IsVisible:true} content || ActualWidth<1 || ActualHeight<1)
                throw new InvalidOperationException("Show and lay out the diagnostic Dock before starting native foreground.");
            var dpi=VisualTreeHelper.GetDpi(this);_experimentalWidth=(int)Math.Round(ActualWidth*dpi.DpiScaleX);_experimentalHeight=(int)Math.Round(ActualHeight*dpi.DpiScaleY);
            _experimentalContent=content;content.IsVisibleChanged+=OnExperimentalContentVisibilityChanged;
            // Material and foreground share application opacity/masks. Preflight
            // the complete frame; unsupported live/effect materials restore WPF.
            _experimentalFrameSource=new(this,new(){Exclude=visual=>ReferenceEquals(visual,NativeForegroundInputCarrier),PresentationOpacityCarrier=ForegroundPresentationCarrier});
            _experimentalFrameSource.Track(_tileStrip,()=>_tileStrip.ForegroundDrawingVersion);
            _experimentalForeground=new(Handle,_experimentalWidth,_experimentalHeight,64);
            if(TryPresentNativeForegroundExperiment())return true;
            StopNativeForegroundExperiment();return false;
        }
        catch(Exception error){_experimentalForegroundFailure=error.GetType().Name+": "+error.Message;StopNativeForegroundExperiment();return false;}
    }

    /// <summary>Nonblocking admission; true means submitted, never displayed FPS.
    /// At most current/candidate scenes coexist. Failure restores the legacy tree.</summary>
    public bool TryPresentNativeForegroundExperiment()
        =>TryPresentNativeForegroundFrame(null);
    private bool TryPresentNativeForegroundFrame(Action? advance,bool admitted=false)
    {
        VerifyAccess();ObjectDisposedException.ThrowIf(_foregroundSourceClosed,this);
        var presenter=_experimentalForeground;if(presenter is null)return false;
        NativeForegroundScene? candidate=null;
        try
        {
            if(!admitted)
            {
                var ready=WaitForSingleObject(presenter.FrameLatencyHandle,0);
                if(ready==258){_experimentalDeferred++;return false;}
                if(ready!=0)throw new InvalidOperationException("Native foreground latency handle is unavailable.");
            }
            if(advance is not null){UpdateLayout();advance();UpdateLayout();}
            var dpi=VisualTreeHelper.GetDpi(this);var width=(int)Math.Round(ActualWidth*dpi.DpiScaleX);var height=(int)Math.Round(ActualHeight*dpi.DpiScaleY);
            if(width!=_experimentalWidth || height!=_experimentalHeight)
            {
                presenter.Resize(width,height);_experimentalWidth=width;_experimentalHeight=height;
                _experimentalForegroundScene?.Dispose();_experimentalForegroundScene=null;
            }
            candidate=presenter.Prepare(_experimentalFrameSource!.Capture(),new(ActualWidth,ActualHeight));
            presenter.Clear(Colors.Transparent);presenter.Draw(candidate);
            if(!presenter.Present()){candidate.Dispose();candidate=null;_experimentalDeferred++;return false;}
            var retired=_experimentalForegroundScene;_experimentalForegroundScene=candidate;candidate=null;retired?.Dispose();
            ForegroundPresentationCarrier.SetCurrentValue(OpacityProperty,0d);
            NativeForegroundInputCarrier.SetCurrentValue(VisibilityProperty,Visibility.Visible);
            _experimentalSubmitted++;return true;
        }
        catch(Exception error)
        {
            _experimentalForegroundFailure=error.GetType().Name+": "+error.Message;
            try{candidate?.Dispose();}catch(Exception cleanup){_experimentalForegroundFailure+="; "+cleanup.Message;}
            StopNativeForegroundExperiment();return false;
        }
    }

    public void StopNativeForegroundExperiment()
    {
        VerifyAccess();
        StopNativeForegroundClock();
        if(_experimentalContent is { } content)content.IsVisibleChanged-=OnExperimentalContentVisibilityChanged;
        _experimentalContent=null;
        ForegroundPresentationCarrier.SetCurrentValue(OpacityProperty,1d);
        NativeForegroundInputCarrier.SetCurrentValue(VisibilityProperty,Visibility.Collapsed);
        var scene=_experimentalForegroundScene;var presenter=_experimentalForeground;
        _experimentalForegroundScene=null;_experimentalForeground=null;
        try{scene?.Dispose();}catch(Exception error){_experimentalForegroundFailure??=error.GetType().Name+": "+error.Message;}
        try{presenter?.Dispose();}catch(Exception error){_experimentalForegroundFailure??=error.GetType().Name+": "+error.Message;}
        _experimentalFrameSource?.Dispose();_experimentalFrameSource=null;
        ReleaseForegroundSource();
    }
    private void OnExperimentalContentVisibilityChanged(object sender,DependencyPropertyChangedEventArgs e)
    {if(e.NewValue is false)StopNativeForegroundExperiment();}
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if(e.Property==OpacityProperty && (double)e.NewValue<=0 && _experimentalForeground is not null)StopNativeForegroundExperiment();
    }
    [DllImport("kernel32.dll")]private static extern uint WaitForSingleObject(nint handle,uint milliseconds);
}
