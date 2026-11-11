using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Runtime.InteropServices;
using GlassDesk.Platform.Displays;

internal static class CaptureWorkerMemoryReport
{
    internal static int Run(string outputDirectory,bool guarded=false,bool documented=false,bool pair=false,bool desktopOnly=false)
    {
        var output=Path.GetFullPath(outputDirectory);
        if(Directory.Exists(output))throw new IOException("Use a fresh capture memory directory.");
        Directory.CreateDirectory(output);
        var display=DisplayService.Primary()??throw new InvalidOperationException("No primary display.");
        var bounds=display.Bounds;var region=new ScreenRect(bounds.X,bounds.Y,bounds.Width,bounds.Height);
        int w=Math.Min(1600,bounds.Width),h=Math.Min(256,bounds.Height);
        var left=(bounds.Width-w)/2;var top=Math.Max(0,bounds.Height-h-16);
        var crop=new PixelBounds(left,top,left+w,top+h);
        var requestedScreenCrop=new ScreenRect(bounds.X+left,bounds.Y+top,w,h);
        if(guarded)
        {
            var x=Math.Max(0,left-64);var y=Math.Max(0,top-64);
            region=new(bounds.X+x,bounds.Y+y,Math.Min(bounds.Width,left+w+64)-x,Math.Min(bounds.Height,top+h+64)-y);
            crop=new(left-x,top-y,left-x+w,top-y+h);
        }
        if(desktopOnly)
        {
            crop=new(0,0,region.Width,region.Height);
            requestedScreenCrop=region;
        }
        var cases=new List<(ScreenRect Region,PixelBounds Crop,int Width,int Height)>
            {(region,crop,desktopOnly?740:640,desktopOnly?416:100)};
        if(pair)cases.Add((new(bounds.Right-640,bounds.Y,640,240),new(64,32,576,208),320,110));
        var traces=new System.Collections.Concurrent.ConcurrentQueue<CaptureTrace>();
        var samples=new List<object>();var latencies=new System.Collections.Concurrent.ConcurrentQueue<double>();var owners=new HashSet<int>();
        var helperResults=new object[cases.Count];var helperLatest=new long[cases.Count];
        var phase="starting";var timer=Stopwatch.StartNew();var frames=0;var reads=0;var counterErrors=0;long sourceTicks=0;string? failure=null;
        using var stop=new ManualResetEventSlim();
        var observer=new Thread(()=>
        {
            while(!stop.IsSet)
            {
                var ids=CaptureProcessFamily.WorkerProcesses.Prepend(Environment.ProcessId).ToArray();
                var rows=new List<object>();long total=0;ulong privateWorkingSet=0;
                foreach(var id in ids)
                {
                    try
                    {
                        using var p=Process.GetProcessById(id);p.Refresh();
                        if(p.HasExited)continue;owners.Add(id);var bytes=p.PrivateMemorySize64;total+=bytes;
                        var counters=new MemoryCounters{Size=(uint)Marshal.SizeOf<MemoryCounters>()};
                        if(!GetProcessMemoryInfo(p.Handle,ref counters,counters.Size))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                        privateWorkingSet+=(ulong)counters.PrivateWorkingSet;
                        rows.Add(new{id,privateBytes=bytes,privateWorkingSetBytes=(ulong)counters.PrivateWorkingSet,osPeakPagefileBytes=p.PeakPagedMemorySize64});
                    }
                    catch(ArgumentException){}catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){Interlocked.Increment(ref counterErrors);}
                }
                samples.Add(new{seconds=timer.Elapsed.TotalSeconds,phase=Volatile.Read(ref phase),privateGroupBytes=total,privateWorkingSetGroupBytes=privateWorkingSet,processes=rows});
                stop.Wait(20);
            }
        }){IsBackground=true,Name="Capture component memory observer"};
        observer.Start();
        try
        {
            var captures=new List<IsolatedDesktopSceneCapture>();
            try
            {
                foreach(var item in cases)
                {
                    Action<CaptureTrace> trace=t=>{if(traces.Count<512)traces.Enqueue(t);};
                    captures.Add(desktopOnly
                        ? new(item.Region,GlassDesk.UI.Controls.GlassBackdropSource.CompatibleScene,trace,
                            executable:null,desktopOnly:true,documentedDesktop:true)
                        : new(item.Region,documented,trace:trace));
                }
                Volatile.Write(ref phase,"sampling");
                var tasks=cases.Select((item,index)=>Task.Run(()=>
                {
                    var target=new byte[item.Width*item.Height*4];var clock=Stopwatch.StartNew();var localLatencies=new List<double>();var localReads=0;var localFrames=0;
                    while(clock.Elapsed<TimeSpan.FromSeconds(4))
                    {
                        var read=Stopwatch.StartNew();
                        var frame=captures[index].TryReadSampled(item.Crop,item.Width,item.Height,target,TimeSpan.FromMilliseconds(30));Interlocked.Increment(ref reads);localReads++;
                        latencies.Enqueue(read.Elapsed.TotalMilliseconds);localLatencies.Add(read.Elapsed.TotalMilliseconds);
                        if(frame is not null){Interlocked.Increment(ref frames);localFrames++;helperLatest[index]=Math.Max(helperLatest[index],frame.SourceQpcTicks);}
                        Thread.Sleep(10);
                    }
                    helperResults[index]=new{index,region=item.Region,crop=item.Crop,width=item.Width,height=item.Height,reads=localReads,frames=localFrames,latencies=localLatencies};
                })).ToArray();
                Task.WaitAll(tasks);sourceTicks=helperLatest.Max();
                Volatile.Write(ref phase,"retained");Thread.Sleep(150);
            }
            finally{foreach(var capture in captures)capture.Dispose();}
            Volatile.Write(ref phase,"closed");Thread.Sleep(100);
        }
        catch(Exception e){failure=e.ToString();}
        finally{stop.Set();observer.Join();}
        var ui=typeof(GlassDesk.UI.BackdropCaptureWorker).Assembly.Location;
        var valid=failure is null && counterErrors==0 && frames>0 && owners.Count==cases.Count+1 && helperLatest.All(t=>t>0);
        File.WriteAllText(Path.Combine(output,"report.json"),JsonSerializer.Serialize(new
        {
            valid,failure,counterErrors,componentOnly=true,fullProductPeakProven=false,
            note="One real isolated capture helper and its diagnostic parent. No installed process mutation, forced GC or working-set trimming. Source desktop is uncontrolled; pixel parity is tested separately with owned windows.",
            gcConserveMemory=Environment.GetEnvironmentVariable("DOTNET_GCConserveMemory"),
            provider=documented?"documented":"private-live",desktopOnly,diagnosticSoftwareWallpaper=Environment.GetEnvironmentVariable("GLASSDESK_DIAGNOSTIC_SOFTWARE_WALLPAPER")=="1",
            diagnosticSharedWallpaperSource=Environment.GetEnvironmentVariable("GLASSDESK_DIAGNOSTIC_SHARED_WALLPAPER_SOURCE")=="1",
            sharedWallpaperFrames=Environment.GetEnvironmentVariable(GlassDesk.UI.CaptureCompatibility.SharedWallpaperBuffer.FrameFanoutEnvironmentVariable)=="1",workerCount=cases.Count,helperResults,
            protocol=typeof(GlassDesk.UI.BackdropCaptureWorker).GetField("ProtocolVersion",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!.GetRawConstantValue(),
            sourceGeometry=guarded?"guarded-request":"full-monitor-stress",requestedScreenCrop,
            region,crop,width=cases[0].Width,height=cases[0].Height,reads,frames,sourceTicks,processIds=owners.Order().ToArray(),
            uiPath=ui,uiSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(ui))),
            probeSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(CaptureWorkerMemoryReport).Assembly.Location))),
            latencies=latencies.ToArray(),traces=traces.ToArray(),samples
        },new JsonSerializerOptions{WriteIndented=true}));
        return valid?0:1;
    }

    // PROCESS_MEMORY_COUNTERS_EX2, supported by the Windows 11 measurement host.
    // https://learn.microsoft.com/windows/win32/api/psapi/ns-psapi-process_memory_counters_ex2
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryCounters
    {
        internal uint Size, PageFaultCount;
        internal nuint PeakWorkingSet, WorkingSet, PeakPagedPool, PagedPool,
            PeakNonPagedPool, NonPagedPool, Pagefile, PeakPagefile, PrivateUsage, PrivateWorkingSet;
        internal ulong SharedCommit;
    }
    [DllImport("psapi.dll",SetLastError=true)]
    private static extern bool GetProcessMemoryInfo(nint process,ref MemoryCounters counters,uint size);
}
