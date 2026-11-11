using System.Windows;
using System.Windows.Threading;
using GlassDesk.Platform.Displays;
using GlassDesk.Rendering;
using Microsoft.Win32;

namespace GlassDesk.Dock;

public partial class DockWindow
{
    private NativePresentationClock? _experimentalFrameClock;
    private DisplayRefreshInfo? _experimentalDisplayRefresh;
    private DispatcherOperation? _experimentalDisplayChange;
    public PresentationClockStatistics? NativeForegroundClockStatistics=>_experimentalFrameClock?.Statistics;
    public DisplayRefreshInfo? NativeForegroundDisplayRefresh=>_experimentalDisplayRefresh;

    /// <summary>Explicit diagnostic animation owner. Uses the real hover/press
    /// curves, but does not select the product backend or prove displayed FPS.
    /// Other content producers call RequestNativeForegroundFrame after changes.</summary>
    public bool StartNativeForegroundAnimationExperiment()
    {
        VerifyAccess();if(_experimentalFrameClock is not null)return true;
        if(!StartNativeForegroundExperiment())return false;
        try
        {
            var rate=DisplayService.RefreshForWindow(Handle)??throw new NotSupportedException("The containing display refresh is unknown.");
            _experimentalDisplayRefresh=rate;
            _experimentalFrameClock=new(rate.Numerator,rate.Denominator,_experimentalForeground!.FrameLatencyHandle,OnNativeForegroundFrame,FailNativeForegroundClock);
            _entryHoverLoop?.UseExternalFrames(RequestNativeForegroundFrame);_tileStrip.UseForegroundFrames(RequestNativeForegroundFrame);
            LocationChanged+=OnNativeForegroundDisplayChanged;DpiChanged+=OnNativeForegroundDpiChanged;
            SystemEvents.DisplaySettingsChanged+=OnNativeForegroundDisplayChanged;
            _experimentalFrameClock.RequestFrame();return true;
        }
        catch(Exception error){FailNativeForegroundClock(error);return false;}
    }
    public void RequestNativeForegroundFrame(){VerifyAccess();_experimentalFrameClock?.RequestFrame();}
    private void OnNativeForegroundFrame()
    {
        var clock=_experimentalFrameClock;if(clock is null)return;
        var submitted=TryPresentNativeForegroundFrame(()=>
        {
            _entryHoverLoop?.AdvanceExternalFrame();_tileStrip.AdvanceForegroundFrame();
        },admitted:true);
        if(!ReferenceEquals(clock,_experimentalFrameClock))return;
        // Admission is checked before animation advances. A busy presenter keeps
        // the original per-loop elapsed timestamp; no invisible simulation step.
        if(!submitted || _entryHoverLoop?.IsActive==true || _tileStrip.IsForegroundAnimationActive)clock.RequestFrame();
    }
    private void FailNativeForegroundClock(Exception error)
    {
        _experimentalForegroundFailure=error.GetType().Name+": "+error.Message;StopNativeForegroundExperiment();
    }
    private void OnNativeForegroundDpiChanged(object sender,DpiChangedEventArgs e)=>OnNativeForegroundDisplayChanged(sender,e);
    private void OnNativeForegroundDisplayChanged(object? sender,EventArgs e)
    {
        if(_experimentalFrameClock is null)return;
        if(_experimentalDisplayChange is {Status:DispatcherOperationStatus.Pending})return;
        _experimentalDisplayChange=Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>
        {
            _experimentalDisplayChange=null;var clock=_experimentalFrameClock;if(clock is null)return;
            try
            {
                var rate=DisplayService.RefreshForWindow(Handle)??throw new NotSupportedException("The containing display refresh became unknown.");
                _experimentalDisplayRefresh=rate;clock.SetRefreshRate(rate.Numerator,rate.Denominator);clock.RequestFrame();
            }
            catch(Exception error){FailNativeForegroundClock(error);}
        }));
    }
    private void StopNativeForegroundClock()
    {
        if(_experimentalFrameClock is null && _experimentalDisplayChange is null && _experimentalDisplayRefresh is null)return;
        _experimentalDisplayChange?.Abort();_experimentalDisplayChange=null;
        LocationChanged-=OnNativeForegroundDisplayChanged;DpiChanged-=OnNativeForegroundDpiChanged;
        SystemEvents.DisplaySettingsChanged-=OnNativeForegroundDisplayChanged;
        var clock=_experimentalFrameClock;_experimentalFrameClock=null;clock?.Dispose();_experimentalDisplayRefresh=null;
        _entryHoverLoop?.UseExternalFrames(null);_tileStrip.UseForegroundFrames(null);
    }
}
