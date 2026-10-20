using System.Diagnostics;

internal interface IBackdropCapture : IDisposable
{
    BackdropFrame Capture(BackdropRequest request,long sequence);
}
internal interface ICadenceWait : IDisposable { void Wait(double milliseconds,CancellationToken cancellation); }

// Single persistent capture thread; no queued frames and no dispatcher waits for native painting.
internal sealed class AsyncBackdropProducer : IDisposable
{
    private readonly LatestBackdropState state;
    private readonly Func<IBackdropCapture> createCapture;
    private readonly TimeSpan interval;
    private readonly Func<ICadenceWait>? createWait;
    private readonly CancellationTokenSource cancel = new();
    private readonly Thread thread;
    private readonly object gate = new();
    private readonly List<double> captureMs = [];
    private readonly List<double> intervalsMs = [];
    private readonly List<double> requestedWaitMs = [],actualWaitMs = [];
    private int published,discarded;
    private string? failure;
    internal AsyncBackdropProducer(LatestBackdropState state,Func<IBackdropCapture> createCapture,TimeSpan interval,Func<ICadenceWait>? createWait=null)
    {
        if (interval<=TimeSpan.Zero) throw new ArgumentException("Invalid cadence.");
        this.state=state; this.createCapture=createCapture; this.interval=interval;
        this.createWait=createWait;
        thread=new Thread(Run) { IsBackground=true,Name="Isolated backdrop capture" };
        thread.Start();
    }
    private void Run()
    {
        try
        {
            using var capture=createCapture();
            using var wait=createWait?.Invoke();
            long sequence=0,previousStart=0;
            var signals=new[] {cancel.Token.WaitHandle,state.Changed};
            while(!cancel.IsCancellationRequested)
            {
                var request=state.CurrentRequest;
                if(request is null) { WaitHandle.WaitAny(signals); continue; }
                var start=Stopwatch.GetTimestamp();
                var frame=capture.Capture(request,++sequence);
                var end=Stopwatch.GetTimestamp();
                lock(gate)
                {
                    captureMs.Add((end-start)*1000d/Stopwatch.Frequency);
                    if(previousStart!=0) intervalsMs.Add((start-previousStart)*1000d/Stopwatch.Frequency);
                    previousStart=start;
                    if(state.Publish(frame)) published++; else discarded++;
                }
                var remaining=interval.TotalMilliseconds-(Stopwatch.GetTimestamp()-start)*1000d/Stopwatch.Frequency;
                if(remaining>0)
                {
                    var waitStart=Stopwatch.GetTimestamp();
                    if(wait is not null) wait.Wait(remaining,cancel.Token);
                    else cancel.Token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(remaining));
                    lock(gate) {requestedWaitMs.Add(remaining);actualWaitMs.Add((Stopwatch.GetTimestamp()-waitStart)*1000d/Stopwatch.Frequency);}
                }
            }
        }
        catch(Exception error) { lock(gate) failure=error.Message; state.Dispose(); }
        finally {state.RetireWaitHandle();}
    }
    internal bool Stop(TimeSpan timeout)
    {
        state.Dispose(); cancel.Cancel();
        return thread.Join(timeout);
    }
    internal object Statistics()
    {
        lock(gate) return new { published,discarded,failure,capture=DiagnosticMath.Distribution(captureMs),
            captureIntervals=DiagnosticMath.Distribution(intervalsMs),requestedWait=DiagnosticMath.Distribution(requestedWaitMs),actualWait=DiagnosticMath.Distribution(actualWaitMs) };
    }
    public void Dispose()
    {
        // A blocked PrintWindow cannot be cancelled safely in-process. The isolated harness
        // records unsuccessful joins and terminates with its producer still background-only.
        if(Stop(TimeSpan.FromSeconds(1))) cancel.Dispose();
    }
}
