using System.Windows.Threading;
internal static class Program
{
    // Process queued dispatcher work without waiting for a timer tick.
    internal static void PumpPending() => Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.Background);

    internal static void Pump(TimeSpan duration)
    {
        var frame=new DispatcherFrame();
        var timer=new DispatcherTimer(DispatcherPriority.Background) {Interval=duration};
        timer.Tick+=(_,_)=>frame.Continue=false;
        timer.Start();try {Dispatcher.PushFrame(frame);} finally {timer.Stop();}
    }
}
