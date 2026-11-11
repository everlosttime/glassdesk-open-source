using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Win32.SafeHandles;

namespace GlassDesk.Rendering;

public sealed record PresentationClockStatistics(uint Numerator,uint Denominator,ulong Requests,ulong Callbacks,ulong Coalesced,bool Pending);

/// <summary>Dispatcher-owned, on-demand frame requests at a configured rational
/// cadence. Callbacks are not displayed FPS. With a latency handle the clock
/// acquires admission before delivery; otherwise the owner checks admission.</summary>
public sealed class NativePresentationClock:DispatcherObject,IDisposable
{
    private readonly Action _frame;
    private readonly Action<Exception> _fault;
    private readonly TimerHandle _timer;
    private readonly RegisteredWaitHandle _registration;
    private readonly TimerHandle? _admissionHandle;
    private RegisteredWaitHandle? _admissionWait;
    private readonly object _signalGate=new();
    private bool _admissionReady;
    private uint _numerator,_denominator;
    private ulong _requests,_callbacks,_coalesced,_frameIndex;
    private long _origin,_deadline;
    private bool _pending,_inFrame,_disposed;
    private int _dispatchQueued;

    public NativePresentationClock(uint numerator,uint denominator,Action frame,Action<Exception> fault)
        :this(numerator,denominator,0,frame,fault){}
    /// <summary>Consumes one frame-latency signal for each delivered callback.
    /// The callback owns that admission and must not wait on the handle again.
    /// The duplicated handle is registered only while a frame is requested.
    /// Stop/rate changes retain any acquired, undelivered admission for this
    /// same presenter; disposal ends that ownership.</summary>
    public NativePresentationClock(uint numerator,uint denominator,nint frameLatencyHandle,Action frame,Action<Exception> fault)
    {
        ArgumentNullException.ThrowIfNull(frame);ArgumentNullException.ThrowIfNull(fault);Validate(numerator,denominator);
        _numerator=numerator;_denominator=denominator;_frame=frame;_fault=fault;
        var handle=CreateWaitableTimerExW(0,null,2,0x00100002); // HIGH_RESOLUTION; modify + synchronize.
        if(handle.IsInvalid){handle.Dispose();throw new Win32Exception(Marshal.GetLastWin32Error());}
        _timer=new(handle);
        try
        {
            if(frameLatencyHandle!=0)
            {
                if(!DuplicateHandle(-1,frameLatencyHandle,-1,out var duplicate,0,false,2))
                {var error=Marshal.GetLastWin32Error();duplicate.Dispose();throw new Win32Exception(error);}
                _admissionHandle=new(duplicate);
            }
            _registration=ThreadPool.RegisterWaitForSingleObject(_timer,OnTimer,null,Timeout.Infinite,false);
        }
        catch{_admissionHandle?.Dispose();_timer.Dispose();throw;}
        Dispatcher.ShutdownStarted+=OnDispatcherShutdown;
    }
    private static void Validate(uint numerator,uint denominator)
    {
        if(numerator==0 || denominator==0 || numerator/(double)denominator is <1 or >2000)
            throw new ArgumentOutOfRangeException(nameof(numerator),"A known positive display cadence is required.");
    }
    private void Check(){VerifyAccess();ObjectDisposedException.ThrowIf(_disposed,this);}
    public PresentationClockStatistics Statistics
    {get{VerifyAccess();return new(_numerator,_denominator,_requests,_callbacks,_coalesced,_pending);}}
    public void SetRefreshRate(uint numerator,uint denominator)
    {
        Check();Validate(numerator,denominator);if(numerator==_numerator && denominator==_denominator)return;
        var requested=_pending;Stop();_numerator=numerator;_denominator=denominator;if(requested)RequestFrame();
    }
    /// <summary>Retains this callback's admission when no swapchain Present was attempted.</summary>
    public void KeepUnusedAdmission()
    {
        Check();
        if (!_inFrame) throw new InvalidOperationException("Admission belongs to the current frame callback.");
        if (_admissionHandle is not null) lock (_signalGate) _admissionReady = true;
    }
    public void RequestFrame()
    {
        Check();_requests++;
        if(_pending){_coalesced++;return;}
        var now=Stopwatch.GetTimestamp();
        if(!_inFrame){_origin=now;_frameIndex=0;_deadline=now;}
        else
        {
            // Anchor deadlines to the original rational cadence. Skip missed
            // periods instead of accumulating rounding error or catch-up work.
            var elapsed=Math.Max(0,now-_origin);
            var phase=(ulong)((Int128)elapsed*_numerator/((Int128)Stopwatch.Frequency*_denominator));
            _frameIndex=Math.Max(_frameIndex+1,phase+1);
            _deadline=checked(_origin+TicksForFrames(_frameIndex,Stopwatch.Frequency));
        }
        _pending=true;
        try{BeginAdmissionWait();Arm();}catch{_pending=false;CancelAdmissionWait();throw;}
    }
    private void BeginAdmissionWait()
    {
        if(_admissionHandle is null)return;
        lock(_signalGate){if(_admissionReady)return;}
        _admissionWait=ThreadPool.RegisterWaitForSingleObject(_admissionHandle,(_,_)=>
        {
            lock(_signalGate)
            {
                if(_disposed)return;
                // Even a canceled wait may already have consumed its signal.
                // It is still a real, unused admission on this same presenter.
                _admissionReady=true;
            }
            QueueDispatch();
        },null,Timeout.Infinite,true);
    }
    private void CancelAdmissionWait()
    {
        _admissionWait?.Unregister(null);_admissionWait=null;
    }
    private long TicksForFrames(ulong frames,long frequency)=>checked((long)((Int128)frames*frequency*_denominator/_numerator));
    private void Arm()
    {
        var ticks=Math.Max(0,_deadline-Stopwatch.GetTimestamp());
        var due=-Math.Max(1,(long)Math.Ceiling(ticks*(10000000d/Stopwatch.Frequency)));
        if(!SetWaitableTimerEx(_timer.SafeWaitHandle,ref due,0,0,0,0,0))throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    private void OnTimer(object? state,bool timedOut)
        =>QueueDispatch();
    private void QueueDispatch()
    {
        if(Volatile.Read(ref _disposed) || Interlocked.CompareExchange(ref _dispatchQueued,1,0)!=0)return;
        try{Dispatcher.BeginInvoke(DispatcherPriority.Render,new Action(OnDue));}
        catch(InvalidOperationException)when(Dispatcher.HasShutdownStarted){Interlocked.Exchange(ref _dispatchQueued,0);}
    }
    private void OnDue()
    {
        Interlocked.Exchange(ref _dispatchQueued,0);
        if(_disposed || !_pending)return;
        try
        {
            // A canceled/rearmed timer callback can already be queued. It may
            // service the latest demand, but cannot deliver it before its deadline.
            if(Stopwatch.GetTimestamp()<_deadline){Arm();return;}
            if(_admissionHandle is not null){lock(_signalGate){if(!_admissionReady)return;_admissionReady=false;}}
            CancelAdmissionWait();
            _pending=false;_callbacks++;_inFrame=true;
            try{_frame();}finally{_inFrame=false;}
        }
        catch(Exception error){if(!_disposed)Stop();_fault(error);}
    }
    public void Stop()
    {
        Check();_pending=false;_origin=0;_frameIndex=0;
        CancelAdmissionWait();
        if(!CancelWaitableTimer(_timer.SafeWaitHandle))throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    private void OnDispatcherShutdown(object? sender,EventArgs e)=>Dispose();
    public void Dispose()
    {
        VerifyAccess();if(_disposed)return;Volatile.Write(ref _disposed,true);_pending=false;
        Dispatcher.ShutdownStarted-=OnDispatcherShutdown;
        CancelAdmissionWait();CancelWaitableTimer(_timer.SafeWaitHandle);_registration.Unregister(null);_timer.Dispose();_admissionHandle?.Dispose();
    }
    private sealed class TimerHandle:WaitHandle{internal TimerHandle(SafeWaitHandle handle){SafeWaitHandle=handle;}}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern SafeWaitHandle CreateWaitableTimerExW(nint attributes,string? name,uint flags,uint access);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool SetWaitableTimerEx(SafeWaitHandle timer,ref long due,int period,nint completion,nint argument,nint wakeContext,uint tolerance);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool CancelWaitableTimer(SafeWaitHandle timer);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool DuplicateHandle(nint sourceProcess,nint source,nint targetProcess,out SafeWaitHandle target,uint access,[MarshalAs(UnmanagedType.Bool)]bool inherit,uint options);
}
