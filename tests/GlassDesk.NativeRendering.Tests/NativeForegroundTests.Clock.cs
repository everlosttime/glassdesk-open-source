using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Models;
using GlassDesk.Dock;
using GlassDesk.UI.Controls;
using GlassDesk.UI.Theming;

namespace GlassDesk.NativeRendering.Tests;

public sealed partial class NativeForegroundTests
{
    [Fact]
    public void ActualDockNativeClockAdvancesRealHoverSettlesIdleAndRestoresLegacyScheduling()=>RunDockClockSta(()=>
    {
        var start=typeof(DockWindow).GetMethod("StartNativeForegroundAnimationExperiment");Assert.NotNull(start);
        using var theme=ThemeService.Initialize(Dispatcher.CurrentDispatcher);
        theme.Update(new AppearanceSettings{PinBarsToDesktop=false,GlassMaterial=GlassMaterialMode.PlainGlass});
        var dock=new DockWindow();
        ClockCallbackObservation? observation=null;
        try
        {
            ((LiquidGlassSurface)dock.FindName("GlassSurface")).IsBackdropActive=()=>false;
            dock.Show();dock.UpdateLayout();global::Program.Pump(TimeSpan.FromMilliseconds(80));
            var captureDirectory=Environment.GetEnvironmentVariable("GLASSDESK_NATIVE_CLOCK_CAPTURE_DIR");
            if(captureDirectory is not null)
            {
                System.IO.Directory.CreateDirectory(captureDirectory);
                System.IO.File.WriteAllText(System.IO.Path.Combine(captureDirectory,"owned-fixture-ready.json"),System.Text.Json.JsonSerializer.Serialize(new
                {pid=Environment.ProcessId,qpcFrequency=System.Diagnostics.Stopwatch.Frequency,window=(long)new System.Windows.Interop.WindowInteropHelper(dock).Handle}));
                var wait=System.Diagnostics.Stopwatch.StartNew();
                while(!System.IO.File.Exists(System.IO.Path.Combine(captureDirectory,"capture-start")) && wait.Elapsed<TimeSpan.FromSeconds(90))
                    global::Program.Pump(TimeSpan.FromMilliseconds(20));
                Assert.True(wait.Elapsed<TimeSpan.FromSeconds(90),"The owned presentation collector did not signal start.");
            }
            Assert.True(start.Invoke(dock,null) is true,dock.NativeForegroundExperimentStatus.Failure);
            if(captureDirectory is not null)observation=new(dock);
            var hover=typeof(DockWindow).GetMethod("SetEntryHovered",BindingFlags.Instance|BindingFlags.NonPublic)!;
            var scale=(ScaleTransform)typeof(DockWindow).GetField("_launcherHoverScale",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(dock)!;
            var rate=dock.NativeForegroundDisplayRefresh;var markers=new List<object>();
            void Mark(string name)=>markers.Add(new{name,qpc=System.Diagnostics.Stopwatch.GetTimestamp(),status=dock.NativeForegroundExperimentStatus,submitted=dock.NativeForegroundExperimentStatus.SubmittedFrames,scale=scale.ScaleX});
            Mark("enter-start");
            hover.Invoke(dock,[dock.FindName("LauncherButton"),true]);
            global::Program.Pump(TimeSpan.FromMilliseconds(650));
            Mark("enter-settled");
            Assert.Equal(DockSettings.FixedMagnificationPeak,scale.ScaleX);Assert.True(dock.NativeForegroundExperimentStatus.SubmittedFrames>3);
            var settled=dock.NativeForegroundExperimentStatus.SubmittedFrames;global::Program.Pump(TimeSpan.FromMilliseconds(100));
            Mark("idle-end");
            Assert.Equal(settled,dock.NativeForegroundExperimentStatus.SubmittedFrames);
            Mark("exit-start");hover.Invoke(dock,[dock.FindName("LauncherButton"),false]);global::Program.Pump(TimeSpan.FromMilliseconds(650));Assert.Equal(1,scale.ScaleX);Mark("exit-settled");
            if(captureDirectory is not null)
            {
                Mark("sustained-start");
                for(var cycle=0;cycle<24;cycle++)
                {
                    hover.Invoke(dock,[dock.FindName("LauncherButton"),cycle%2==0]);global::Program.Pump(TimeSpan.FromMilliseconds(160));
                }
                Mark("sustained-end");global::Program.Pump(TimeSpan.FromMilliseconds(650));Assert.Equal(1,scale.ScaleX);
            }
            var timeline=System.Text.Json.JsonSerializer.Serialize(new
            {pid=Environment.ProcessId,qpcFrequency=System.Diagnostics.Stopwatch.Frequency,rate,markers,clock=dock.NativeForegroundClockStatistics,displayedFpsMeasured=false,wholeProductAcceptance=false,material="plain",fixture="actual Dock; built-in entries; hover/return/idle"});
            System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Api.LibraryPath)!,"dock-clock-timeline.json"),timeline);
            if(captureDirectory is not null)System.IO.File.WriteAllText(System.IO.Path.Combine(captureDirectory,"timeline.json"),timeline);
            if(captureDirectory is not null)System.IO.File.WriteAllText(System.IO.Path.Combine(captureDirectory,"callback-observations.json"),System.Text.Json.JsonSerializer.Serialize(observation!.Frames));
            observation?.Dispose();observation=null;
            hover.Invoke(dock,[dock.FindName("LauncherButton"),true]);global::Program.Pump(TimeSpan.FromMilliseconds(40));
            dock.StopNativeForegroundExperiment();Assert.False(dock.NativeForegroundExperimentStatus.Active);
            global::Program.Pump(TimeSpan.FromMilliseconds(650));Assert.Equal(DockSettings.FixedMagnificationPeak,scale.ScaleX);
            Assert.True(start.Invoke(dock,null) is true,dock.NativeForegroundExperimentStatus.Failure);
            hover.Invoke(dock,[dock.FindName("LauncherButton"),false]);dock.Close();global::Program.Pump(TimeSpan.FromMilliseconds(60));
            Assert.False(dock.NativeForegroundExperimentStatus.Active);
        }
        finally{observation?.Dispose();dock.Close();}
    });
    // Test-only wall timing of the real dispatcher operation. It includes the
    // unchanged production path and distinguishes successful/deferred callbacks;
    // neither this timing nor a submission count establishes displayed FPS.
    private sealed class ClockCallbackObservation:IDisposable
    {
        private readonly DockWindow _dock;
        private readonly Dictionary<DispatcherOperation,(long Qpc,ulong Callbacks,ulong Submitted,ulong Deferred)> _pending=[];
        internal readonly List<object> Frames=new(2048);
        internal ClockCallbackObservation(DockWindow dock)
        {
            _dock=dock;dock.Dispatcher.Hooks.OperationStarted+=Started;
            dock.Dispatcher.Hooks.OperationCompleted+=Completed;dock.Dispatcher.Hooks.OperationAborted+=Aborted;
        }
        private void Started(object? sender,DispatcherHookEventArgs e)
        {
            if(e.Operation.Priority!=DispatcherPriority.Render || Frames.Count>=8192)return;
            var status=_dock.NativeForegroundExperimentStatus;
            _pending[e.Operation]=(System.Diagnostics.Stopwatch.GetTimestamp(),_dock.NativeForegroundClockStatistics?.Callbacks??0,status.SubmittedFrames,status.DeferredFrames);
        }
        private void Completed(object? sender,DispatcherHookEventArgs e)
        {
            if(!_pending.Remove(e.Operation,out var begin))return;
            var end=System.Diagnostics.Stopwatch.GetTimestamp();var callbacks=_dock.NativeForegroundClockStatistics?.Callbacks??0;
            if(callbacks<=begin.Callbacks)return;
            var status=_dock.NativeForegroundExperimentStatus;
            Frames.Add(new{start=begin.Qpc,end,callbacks=callbacks-begin.Callbacks,submitted=status.SubmittedFrames-begin.Submitted,deferred=status.DeferredFrames-begin.Deferred});
        }
        private void Aborted(object? sender,DispatcherHookEventArgs e)=>_pending.Remove(e.Operation);
        public void Dispose()
        {
            _dock.Dispatcher.Hooks.OperationStarted-=Started;_dock.Dispatcher.Hooks.OperationCompleted-=Completed;
            _dock.Dispatcher.Hooks.OperationAborted-=Aborted;_pending.Clear();
        }
    }
    private static void RunDockClockSta(Action action)
    {
        if(Environment.GetEnvironmentVariable("GLASSDESK_NATIVE_CLOCK_CAPTURE_DIR") is null){NativeEngineTests.OnSta(action);return;}
        // UAC consent time is outside animation timing. This extra allowance is
        // confined to the explicitly enabled diagnostic capture handshake.
        Exception? error=null;var thread=new Thread(()=>{try{action();}catch(Exception failure){error=failure;}});
        thread.SetApartmentState(ApartmentState.STA);thread.Start();Assert.True(thread.Join(TimeSpan.FromSeconds(115)),"Owned capture fixture exceeded its deadline.");
        if(error is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
}
