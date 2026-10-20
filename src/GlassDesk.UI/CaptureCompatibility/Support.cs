using System.Windows.Threading;
internal static class Program
{
    internal static void Pump(TimeSpan duration)
    {
        var frame=new DispatcherFrame();
        var timer=new DispatcherTimer(DispatcherPriority.Background) {Interval=duration};
        timer.Tick+=(_,_)=>frame.Continue=false;
        timer.Start();try {Dispatcher.PushFrame(frame);} finally {timer.Stop();}
    }
}
