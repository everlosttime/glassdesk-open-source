using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Threading;

internal static class AsyncBackdropBenchmark
{
    internal static int Run(string output)
    {
        var runs=new List<object>(); var passed=true; string? failure=null;
        try
        {
            DesktopReferenceCapture.EnsurePhysicalCoordinates();
            var shell=GetShellWindow(); var root=DesktopReferenceCapture.Bounds(shell);
            var rw=Math.Min(root.Width,1260); var rh=Math.Min(root.Height,980);
            var region=new PixelBounds(root.Left+(root.Width-rw)/2,root.Top+(root.Height-rh)/2,
                root.Left+(root.Width+rw)/2,root.Top+(root.Height+rh)/2);
            var scale=Math.Min(1,Math.Sqrt(320_000d/(region.Width*region.Height)));
            var width=Math.Max(1,(int)Math.Floor(region.Width*scale));
            var height=Math.Max(1,(int)Math.Floor(region.Height*scale));
            foreach(var cadence in new[] {33,40})
            {
                using var state=new LatestBackdropState();
                state.Request(region,width,height);
                using var producer=new AsyncBackdropProducer(state,()=>DesktopReferenceCapture.CreateBackdropCapture(shell),TimeSpan.FromMilliseconds(cadence),()=>new NativeCadenceWait());
                var warmup=Stopwatch.StartNew();
                while(warmup.Elapsed<TimeSpan.FromSeconds(2) && !state.TryRead(Stopwatch.GetTimestamp(),Stopwatch.Frequency,out _))
                    Program.Pump(TimeSpan.FromMilliseconds(10));
                if(!state.TryRead(Stopwatch.GetTimestamp(),Stopwatch.Frequency,out _)) throw new InvalidOperationException("Background producer did not provide an initial frame.");
                var age=new List<double>(); var lookup=new List<double>(); var consumeIntervals=new List<double>(); var heartbeat=new List<double>();
                var misses=0; var repeated=0; var newFrames=0; long lastSequence=-1,lastRead=0,lastHeartbeat=0;
                using var own=Process.GetCurrentProcess(); var ownCpu=own.TotalProcessorTime;
                var gc=Enumerable.Range(0,3).Select(GC.CollectionCount).ToArray();
                var peakPrivate=own.PrivateMemorySize64; var watch=Stopwatch.StartNew();
                var readTimer=new DispatcherTimer(DispatcherPriority.Background) {Interval=TimeSpan.FromMilliseconds(cadence)};
                readTimer.Tick+=(_,_)=>
                {
                    var now=Stopwatch.GetTimestamp();
                    if(lastRead!=0) consumeIntervals.Add((now-lastRead)*1000d/Stopwatch.Frequency);
                    lastRead=now;
                    var ok=state.TryRead(now,Stopwatch.Frequency*120/1000,out var frame);
                    lookup.Add((Stopwatch.GetTimestamp()-now)*1000d/Stopwatch.Frequency);
                    if(!ok || frame is null) { misses++; return; }
                    age.Add((now-frame.EndTicks)*1000d/Stopwatch.Frequency);
                    if(frame.Sequence==lastSequence) repeated++; else {newFrames++; lastSequence=frame.Sequence;}
                };
                var pulse=new DispatcherTimer(DispatcherPriority.Normal) {Interval=TimeSpan.FromMilliseconds(16)};
                pulse.Tick+=(_,_)=>
                {
                    var now=Stopwatch.GetTimestamp();
                    if(lastHeartbeat!=0) heartbeat.Add((now-lastHeartbeat)*1000d/Stopwatch.Frequency);
                    lastHeartbeat=now;
                };
                try
                {
                    var frame=new DispatcherFrame();
                    var endTimer=new DispatcherTimer(DispatcherPriority.Normal) {Interval=TimeSpan.FromSeconds(30)};
                    endTimer.Tick+=(_,_)=>frame.Continue=false;
                    var memoryTimer=new DispatcherTimer(DispatcherPriority.Background) {Interval=TimeSpan.FromSeconds(1)};
                    memoryTimer.Tick+=(_,_)=>{own.Refresh();peakPrivate=Math.Max(peakPrivate,own.PrivateMemorySize64);};
                    try {readTimer.Start();pulse.Start();endTimer.Start();memoryTimer.Start();Dispatcher.PushFrame(frame);}
                    finally {endTimer.Stop();memoryTimer.Stop();}
                }
                finally { readTimer.Stop(); pulse.Stop(); }
                var elapsed=watch.Elapsed.TotalSeconds;
                var cleanStop=producer.Stop(TimeSpan.FromSeconds(2));
                own.Refresh();
                var producerStats=producer.Statistics();
                var stats=JsonSerializer.SerializeToElement(producerStats);
                var captureP95=stats.GetProperty("capture").GetProperty("p95Ms").GetDouble();
                var ageP95=Percentile(age,.95); var lookupP95=Percentile(lookup,.95);
                var produced=stats.GetProperty("published").GetInt32();
                var intervalP95=stats.GetProperty("captureIntervals").GetProperty("p95Ms").GetDouble();
                var okRun=cleanStop && stats.GetProperty("failure").ValueKind==JsonValueKind.Null &&
                    captureP95<cadence && intervalP95<cadence*1.25 && ageP95<2*cadence && lookupP95<1 && misses==0 && produced>=30_000d/cadence*.8;
                passed&=okRun;
                runs.Add(new { passed=okRun,cadenceMs=cadence,physicalRegion=region,width,height,elapsedSeconds=elapsed,
                    consumerReads=lookup.Count,newFrames,repeatedFrames=repeated,unavailableFrames=misses,newFramesObservedPerSecond=newFrames/elapsed,
                    sourceAge=DiagnosticMath.Distribution(age),consumerLookup=DiagnosticMath.Distribution(lookup),
                    consumerIntervals=DiagnosticMath.Distribution(consumeIntervals),dispatcherHeartbeat=DiagnosticMath.Distribution(heartbeat),
                    producer=producerStats,cleanStop,probeCpuMs=(own.TotalProcessorTime-ownCpu).TotalMilliseconds,
                    peakPrivateMiB=peakPrivate/1024d/1024,gcCollections=Enumerable.Range(0,3).Select(i=>GC.CollectionCount(i)-gc[i]).ToArray() });
                Console.WriteLine($"{cadence}ms: acquisition P95 {captureP95:F2}ms, source age P95 {ageP95:F2}ms, lookup P95 {lookupP95:F4}ms, new frames {newFrames}, passed {okRun}");
                if(!cleanStop) throw new InvalidOperationException("Native paint did not stop; further runs cancelled.");
            }
        }
        catch(Exception error) {passed=false; failure=error.Message;}
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output,JsonSerializer.Serialize(new { passed,failure,runs,
            note="Independent source test, 30 seconds per unchanged 33/40ms material cadence, <=320k sampled pixels; native paint still full shell. Persistent DIBs, immutable latest-only regional snapshots and an own-thread high-resolution waitable timer (no global timer setting changed). Real dispatcher message loop, no polling Sleep. Freshness limit 120ms; acquisition P95 below cadence, producer interval P95 below 1.25 cadences, age P95 below two cadences, lookup P95 below 1ms, no unavailable consumer frames after warmup, at least 80% target published frames, clean shutdown required. Consumer timer rate is reported separately. Dispatcher heartbeat is diagnostic, NOT presented FPS. No glass processing or full scene reconstruction measured; product remains unchanged." },new JsonSerializerOptions {WriteIndented=true}));
        return passed ? 0 : 1;
    }
    private static double Percentile(List<double> values,double p) {var v=values.Order().ToArray();return v.Length==0 ? double.PositiveInfinity : v[Math.Clamp((int)Math.Ceiling(v.Length*p)-1,0,v.Length-1)];}
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
}
