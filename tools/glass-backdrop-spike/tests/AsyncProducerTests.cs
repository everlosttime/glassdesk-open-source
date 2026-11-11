using System.Diagnostics;

internal static class AsyncProducerTests
{
    internal static void Run()
    {
        using(var preciseWait=new NativeCadenceWait())
        using(var cancelled=new CancellationTokenSource())
        {
            var delay=Stopwatch.StartNew(); preciseWait.Wait(30,CancellationToken.None);
            if(delay.Elapsed.TotalMilliseconds<25) throw new Exception("deadline wait returned before its deadline");
            cancelled.Cancel();delay.Restart();preciseWait.Wait(1000,cancelled.Token);
            if(delay.Elapsed.TotalMilliseconds>200) throw new Exception("deadline wait did not honor cancellation");
        }
        using var state=new LatestBackdropState();
        state.Request(new PixelBounds(1,0,2,1),1,1);
        using var entered=new ManualResetEventSlim();
        using var release=new ManualResetEventSlim();
        var mainThread=Environment.CurrentManagedThreadId;
        using var producer=new AsyncBackdropProducer(state,()=>new GateCapture(entered,release,mainThread),TimeSpan.FromMilliseconds(10));
        try
        {
            if(!entered.Wait(TimeSpan.FromSeconds(2))) throw new Exception("producer did not start");
            for(var x=2;x<=7;x++) state.Request(new PixelBounds(x,0,x+1,1),1,1);
            if(state.TryRead(Stopwatch.GetTimestamp(),Stopwatch.Frequency,out _)) throw new Exception("in-flight frame published before completion");
            release.Set();
            BackdropFrame? frame=null;
            var watch=Stopwatch.StartNew();
            while(watch.Elapsed<TimeSpan.FromSeconds(2) && !state.TryRead(Stopwatch.GetTimestamp(),Stopwatch.Frequency,out frame)) Thread.Sleep(1);
            if(frame is null || frame.Request.Region.Left!=7 || frame.Pixels.Span[0]!=7)
                throw new Exception("request changes must skip intermediate regions and discard the old capture");
            if(!producer.Stop(TimeSpan.FromSeconds(2))) throw new Exception("producer did not stop cleanly");
            if(state.TryRead(Stopwatch.GetTimestamp(),Stopwatch.Frequency,out _)) throw new Exception("stopped producer leaked a usable frame");
        }
        finally { release.Set(); }
    }
    private sealed class GateCapture(ManualResetEventSlim entered,ManualResetEventSlim release,int mainThread):IBackdropCapture
    {
        public BackdropFrame Capture(BackdropRequest request,long sequence)
        {
            if(Environment.CurrentManagedThreadId==mainThread) throw new Exception("capture ran on consumer thread");
            var start=Stopwatch.GetTimestamp();
            if(sequence==1) { entered.Set(); if(!release.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException(); }
            return new(request,sequence,start,Stopwatch.GetTimestamp(),[(byte)request.Region.Left,0,0,255]);
        }
        public void Dispose() { }
    }
}
