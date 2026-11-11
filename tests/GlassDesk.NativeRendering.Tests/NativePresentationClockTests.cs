using System.Reflection;
using System.Windows.Threading;
using GlassDesk.Rendering;

namespace GlassDesk.NativeRendering.Tests;

public sealed class NativePresentationClockTests
{
    [Fact]
    public void ExternalAnimationOwnerIgnoresAnAlreadyQueuedWpfRenderingCallback()=>NativeEngineTests.OnSta(()=>
    {
        var type=typeof(GlassDesk.Dock.DockWindow).Assembly.GetType("GlassDesk.Dock.DockRenderLoop")!;
        var flags=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
        var advances=0;var requests=0;
        Func<double,bool> advance=_=>{advances++;return true;};
        var loop=Activator.CreateInstance(type,flags,null,[advance],null)!;
        void Call(string name,params object?[] args)=>type.GetMethod(name,flags)!.Invoke(loop,args);
        Call("UseExternalFrames",(Action)(()=>requests++));Call("Start");Assert.Equal(1,requests);
        Call("OnRendering",null,EventArgs.Empty);Assert.Equal(0,advances);
        Call("AdvanceExternalFrame");Assert.Equal(1,advances);Call("Stop");Call("UseExternalFrames",new object?[]{null});
    });
    private static NativePresentationClock Clock(uint numerator,uint denominator,Action frame,Action<Exception> fault)
    {
        var type=typeof(NativeDirectPresenter).Assembly.GetType("GlassDesk.Rendering.NativePresentationClock");Assert.NotNull(type);
        return (NativePresentationClock)Activator.CreateInstance(type,[numerator,denominator,frame,fault])!;
    }
    private static void Pump(int milliseconds)=>global::Program.Pump(TimeSpan.FromMilliseconds(milliseconds));
    private static NativePresentationClock AdmissionClock(AutoResetEvent available,Action frame)
    {
        var constructor=typeof(NativePresentationClock).GetConstructor([typeof(uint),typeof(uint),typeof(nint),typeof(Action),typeof(Action<Exception>)]);
        Assert.NotNull(constructor);
        return (NativePresentationClock)constructor.Invoke([240u,1u,available.SafeWaitHandle.DangerousGetHandle(),frame,(Action<Exception>)(error=>throw error)]);
    }
    [Fact]
    public void PresenterBackpressureDoesNotPollOrAdvanceUntilEachAdmissionSignal()=>NativeEngineTests.OnSta(()=>
    {
        using var available=new AutoResetEvent(false);var calls=0;NativePresentationClock? clock=null;
        using var lifetime=clock=AdmissionClock(available,()=>{calls++;if(calls<3)clock!.RequestFrame();});
        clock.RequestFrame();Pump(40);Assert.Equal(0,calls);Assert.True(clock.Statistics.Pending);
        available.Set();Pump(40);Assert.Equal(1,calls);Assert.Equal(1ul,clock.Statistics.Callbacks);
        Pump(40);Assert.Equal(1,calls); // No retry callbacks consume UI time while busy.
        available.Set();Pump(40);Assert.Equal(2,calls);
        clock.Stop();available.Set();Pump(40);Assert.Equal(2,calls);
        clock.RequestFrame();Pump(40);Assert.Equal(3,calls);Assert.False(clock.Statistics.Pending);
    });
    [Fact]
    public void AdmissionWaitIsIdleWithoutDemandAndDisposeKeepsOwnersHandleUsable()=>NativeEngineTests.OnSta(()=>
    {
        using var available=new AutoResetEvent(false);var calls=0;
        var clock=AdmissionClock(available,()=>calls++);
        available.Set();Pump(40);Assert.Equal(0,calls);Assert.True(available.WaitOne(0));
        clock.RequestFrame();Pump(20);clock.Dispose();available.Set();Pump(40);Assert.Equal(0,calls);
        Assert.True(available.WaitOne(0)); // The duplicated wait is canceled; ownership stays with the presenter.
    });
    [Fact]
    public void AFrameWithoutPresentCanKeepAdmissionButTheNextPresentNeedsANewSignal()=>NativeEngineTests.OnSta(()=>
    {
        using var available=new AutoResetEvent(false);var calls=0;NativePresentationClock? clock=null;
        using var lifetime=clock=AdmissionClock(available,()=>
        {
            calls++;
            if(calls==1)clock!.KeepUnusedAdmission();
            if(calls<3)clock!.RequestFrame();
        });
        Assert.Throws<InvalidOperationException>(()=>clock.KeepUnusedAdmission());
        clock.RequestFrame();available.Set();Pump(60);
        Assert.Equal(2,calls);Assert.True(clock.Statistics.Pending);
        Pump(40);Assert.Equal(2,calls);
        available.Set();Pump(40);Assert.Equal(3,calls);Assert.False(clock.Statistics.Pending);
    });
    [Fact]
    public void RefreshChangeRetainsAdmissionAlreadyConsumedBeforeDispatcherDelivery()=>NativeEngineTests.OnSta(()=>
    {
        using var available=new AutoResetEvent(false);var calls=0;
        using var clock=AdmissionClock(available,()=>calls++);
        clock.RequestFrame();available.Set();
        // Hold the UI dispatcher while the registered wait acquires admission.
        var acquired=typeof(NativePresentationClock).GetField("_admissionReady",BindingFlags.Instance|BindingFlags.NonPublic)!;
        Assert.True(SpinWait.SpinUntil(()=>(bool)acquired.GetValue(clock)!,TimeSpan.FromSeconds(2)),"Fixture did not acquire admission before the rate change.");
        Assert.Equal(0,calls);
        clock.SetRefreshRate(144,1);Pump(60);
        Assert.Equal(1,calls);Assert.False(clock.Statistics.Pending);
    });
    [Fact]
    public void ClockStaysIdleCoalescesRequestsAndCancelsAfterDispose()=>NativeEngineTests.OnSta(()=>
    {
        var callbacks=0;var failures=new List<Exception>();var thread=Environment.CurrentManagedThreadId;
        var clock=Clock(240,1,()=>{Assert.Equal(thread,Environment.CurrentManagedThreadId);callbacks++;},failures.Add);
        using var lifetime=(IDisposable)clock;
        Pump(40);Assert.Equal(0,callbacks);
        for(var i=0;i<40;i++)clock.RequestFrame();
        Pump(60);Assert.Equal(1,callbacks);Assert.False((bool)clock.Statistics.Pending);Assert.Equal(39ul,(ulong)clock.Statistics.Coalesced);
        Pump(40);Assert.Equal(1,callbacks);
        clock.RequestFrame();lifetime.Dispose();Pump(60);Assert.Equal(1,callbacks);Assert.Empty(failures);
        Assert.Throws<ObjectDisposedException>(()=>clock.RequestFrame());
    });
    [Fact]
    public void ClockRunsOnlyWhileFrameCallbackRequestsAnotherFrame()=>NativeEngineTests.OnSta(()=>
    {
        var callbacks=0;var failures=new List<Exception>();NativePresentationClock? clock=null;
        Action frame=()=>{callbacks++;if(callbacks<10)clock!.RequestFrame();};
        clock=Clock(240,1,frame,failures.Add);using var lifetime=(IDisposable)clock;
        clock.RequestFrame();Pump(250);Assert.Equal(10,callbacks);Assert.False((bool)clock.Statistics.Pending);
        Pump(40);Assert.Equal(10,callbacks);Assert.Empty(failures);
        Assert.Equal(10ul,(ulong)clock.Statistics.Callbacks); // Callback count, not displayed FPS.
    });
    [Fact]
    public void ClockFaultStopsDemandAndCanBeDisposedFromTheFrameCallback()=>NativeEngineTests.OnSta(()=>
    {
        var failures=new List<Exception>();var expected=new InvalidOperationException("owned clock fault");var calls=0;
        var clock=Clock(240,1,()=>{calls++;throw expected;},failures.Add);
        using(var lifetime=(IDisposable)clock){clock.RequestFrame();Pump(60);Assert.Equal(1,calls);Assert.Same(expected,Assert.Single(failures));Assert.False((bool)clock.Statistics.Pending);}
        NativePresentationClock? closing=null;closing=Clock(240,1,()=>closing!.Dispose(),failures.Add);
        closing.RequestFrame();Pump(60);Assert.False((bool)closing.Statistics.Pending);
    });
    [Fact]
    public void ClockCanCancelAndChangeCadenceWithoutDeliveringAnOldQueuedFrame()=>NativeEngineTests.OnSta(()=>
    {
        var calls=0;var failures=new List<Exception>();var clock=Clock(60000,1001,()=>calls++,failures.Add);
        using var lifetime=(IDisposable)clock;
        clock.RequestFrame();clock.Stop();Pump(40);Assert.Equal(0,calls);
        clock.SetRefreshRate(240u,1u);clock.RequestFrame();Pump(50);Assert.Equal(1,calls);
        Assert.Equal(240u,(uint)clock.Statistics.Numerator);Assert.Equal(1u,(uint)clock.Statistics.Denominator);Assert.Empty(failures);
    });
    [Theory]
    [InlineData(240u,1u,240ul,10000000L)]
    [InlineData(60000u,1001u,60000ul,10010000000L)]
    public void RationalCadenceDoesNotAccumulateRoundedIntervalDrift(uint numerator,uint denominator,ulong frames,long expected)=>NativeEngineTests.OnSta(()=>
    {
        var clock=Clock(numerator,denominator,()=>{},_=>{});using var lifetime=(IDisposable)clock;
        var method=((object)clock).GetType().GetMethod("TicksForFrames",BindingFlags.Instance|BindingFlags.NonPublic);Assert.NotNull(method);
        Assert.Equal(expected,(long)method.Invoke((object)clock,[frames,10000000L])!);
    });
}
