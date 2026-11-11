using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GlassDesk.UI.CaptureCompatibility;

internal static class WallpaperFanoutRecoveryReport
{
    internal static int Hold(string name,string ready)
    {
        using var gate=Mutex.OpenExisting(name+"-gate");
        if(!gate.WaitOne(5000))return 1;
        File.WriteAllText(ready,"locked");
        Thread.Sleep(Timeout.Infinite);return 1;
    }
    internal static int Run(string path)
    {
        if(File.Exists(path))throw new IOException("Use a fresh recovery report.");
        Environment.SetEnvironmentVariable("GLASSDESK_DIAGNOSTIC_SHARED_WALLPAPER_SOURCE","1");
        Environment.SetEnvironmentVariable(SharedWallpaperBuffer.FrameFanoutEnvironmentVariable,"1");
        var events=new List<CaptureTrace>();var exe=Environment.ProcessPath!;
        var region=new ScreenRect(900,1100,640,240);var crop=new PixelBounds(0,0,640,240);
        using var a=new RecoveringDesktopSceneCapture(region,DesktopSceneProvider.IsolatedDocumentedScene,events.Add,exe);
        using var b=new IsolatedDesktopSceneCapture(region,true,events.Add,exe);
        var target=new byte[160*60*4];
        CapturePixels Read(IDesktopSceneCapture capture,long barrier=0)
        {
            var watch=Stopwatch.StartNew();
            while(watch.Elapsed<TimeSpan.FromSeconds(5))
            {
                var frame=capture.TryReadSampled(crop,160,60,target,TimeSpan.FromMilliseconds(30),sourceNotBeforeTicks:barrier);
                if(frame is not null)return frame;
                global::Program.Pump(TimeSpan.FromMilliseconds(10));
            }
            throw new TimeoutException("No real capture frame during recovery diagnostic.");
        }
        _=Read(a);_=Read(b);
        var source=events.FirstOrDefault(t=>t.Stage=="WallpaperBuffer" && t.Reason=="SharedStorageCreated");
        if(source.Width<=0)throw new InvalidOperationException("This explicit recovery diagnostic requires an actual live wallpaper source.");
        var oldScope=CaptureProcessFamily.WallpaperSourceScope;
        using var retained=new SharedWallpaperBuffer(oldScope,(nint)source.WindowHandle,source.Width,source.Height);
        var ready=path+".mutex-ready";
        var start=new ProcessStartInfo(exe){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden};
        foreach(var argument in new[]{"--abandon-shared-wallpaper-mutex",retained.Name,ready})start.ArgumentList.Add(argument);
        using var holder=Process.Start(start)??throw new InvalidOperationException("Cannot create the owned abandonment fixture.");
        string? firstError=null,lateError=null;long resumedSourceTicks=0;string? newScope=null;var valid=false;
        try
        {
            var clock=Stopwatch.StartNew();while(!File.Exists(ready) && clock.Elapsed<TimeSpan.FromSeconds(5))Thread.Sleep(10);
            if(!File.Exists(ready))throw new TimeoutException("Abandonment fixture did not acquire the real source mutex.");
            holder.Kill();if(!holder.WaitForExit(3000))throw new TimeoutException("Abandonment fixture exit unconfirmed.");
            var barrier=Stopwatch.GetTimestamp();
            resumedSourceTicks=Read(a,barrier).SourceQpcTicks;
            firstError=events.Any(t=>t.Stage=="BackdropProvider" && t.Reason=="SharedSourceRecreated")
                ? SharedWallpaperBufferException.ProtocolErrorCode : null;
            newScope=CaptureProcessFamily.WallpaperSourceScope;
            try{_=Read(b);}catch(InvalidOperationException e){lateError=e.Message;}
            valid=firstError?.Contains(SharedWallpaperBufferException.ProtocolErrorCode)==true
                && lateError?.Contains(SharedWallpaperBufferException.ProtocolErrorCode)==true
                && oldScope!=newScope && CaptureProcessFamily.WallpaperSourceScope==newScope
                && resumedSourceTicks>=barrier
                && !events.Any(t=>t.Stage=="BackdropProvider" && t.Reason=="DocumentedFallback");
        }
        finally
        {
            if(!holder.HasExited){holder.Kill();holder.WaitForExit(3000);}
            File.WriteAllText(path,JsonSerializer.Serialize(new{valid,componentOnly=true,oldScopeHash=Hash(oldScope),
                newScopeHash=newScope is null?null:Hash(newScope),firstError,lateError,resumedSourceTicks,
                oldBackingRetainedDuringRecovery=true,controllerRetriedWithoutStaticFallback=true,source.Width,source.Height,
                note="An actual Recovering provider and a peer Documented helper; an owned child abandons their real source mutex. The production controller resumes on a new scope while the old backing stays open. No installed process mutation."},new JsonSerializerOptions{WriteIndented=true}));
        }
        return valid?0:1;
    }
    private static string Hash(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
