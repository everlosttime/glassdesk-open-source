using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassDesk.Rendering;

namespace GlassDesk.NativeRenderingProbe;

// Isolated ownership experiment. No AppHost, installed profile, background
// worker, input registration, forced GC or working-set trimming.
internal static class DirectDeviceProbe
{
    internal static int Run(string[] args,string output)
    {
        var modeIndex=Array.IndexOf(args,"--direct-device-mode");
        var mode=modeIndex+1<args.Length?args[modeIndex+1]:"";
        if(mode is not ("shared" or "independent"))return 64;
        var durationIndex=Array.IndexOf(args,"--direct-device-observe-ms");
        var duration=durationIndex>=0 && durationIndex+1<args.Length && int.TryParse(args[durationIndex+1],out var parsed)
            ?Math.Clamp(parsed,200,30000):3000;
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        var windows=new List<Window>();var presenters=new List<NativeDirectPresenter>();
        var samples=new List<object>();var watch=Stopwatch.StartNew();var exit=0;
        using var process=Process.GetCurrentProcess();
        var wallpaper=new byte[256*144*4];var icon=new byte[64*64*4];
        for(var i=0;i<wallpaper.Length;i+=4){wallpaper[i]=(byte)(i%256);wallpaper[i+1]=(byte)(i/1024%256);wallpaper[i+2]=180;wallpaper[i+3]=255;}
        for(var i=0;i<icon.Length;i+=4){icon[i]=40;icon[i+1]=180;icon[i+2]=220;icon[i+3]=255;}
        var bitmap=BitmapSource.Create(64,64,96,96,PixelFormats.Pbgra32,null,icon,256);bitmap.Freeze();
        var material=new StaticGlassMaterial(320,180,1,.5f,.16f,false,true);
        var glass=new DirectGlassScene(new(320,180),new(30,40,260,100),new(0,0,320,180),26,1);
        var shadow=new DirectShadowScene(new(320,180),new(30,40,260,100),26);
        DirectShadowLayer[] layers=[new(34,8,.15f,Colors.Black),new(10,3,.1f,Colors.Black)];
        void Sample(string phase)
        {
            if(!GetProcessMemoryInfo(process.Handle,out var memory,(uint)Marshal.SizeOf<MemoryCounters>()))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var devices=presenters.Select(p=>p.DeviceStatistics).ToArray();
            samples.Add(new{phase,elapsedMs=watch.Elapsed.TotalMilliseconds,privateCommitBytes=(ulong)memory.PrivateUsage,
                peakPrivateCommitBytes=(ulong)memory.PeakPagefileUsage,workingSetBytes=(ulong)memory.WorkingSetSize,
                peakWorkingSetBytes=(ulong)memory.PeakWorkingSetSize,livePresenters=presenters.Count,
                liveDevices=devices.Length>0?(uint?)devices[0].LiveDevices:null,devices});
        }
        void Draw()
        {
            for(var i=0;i<presenters.Count;i++)
            {
                var p=presenters[i];
                if(i%2==1)p.RenderShadow(shadow,layers);
                else{p.RenderGlass(material,glass);p.DrawImages(new(320,180),[new(1,new(55,65,48,48)),new(1,new(125,65,48,48),.6f)]);}
                p.Present(); // Submission only; this experiment makes no FPS claim.
            }
        }
        async Task Observe(string phase)
        {
            File.WriteAllText(Path.Combine(output,"phase.txt"),phase);
            var end=watch.ElapsedMilliseconds+duration;
            do{Draw();Sample(phase);await Task.Delay(100);}while(watch.ElapsedMilliseconds<end);
        }
        app.Startup+=async (_,_)=>
        {
            try
            {
                Sample("baseline");
                for(var i=0;i<4;i++)
                {
                    var window=new Window{Width=320,Height=180,Left=20+i*24,Top=20+i*24,WindowStyle=WindowStyle.None,
                        AllowsTransparency=true,Background=Brushes.Transparent,ShowActivated=false,ShowInTaskbar=false};
                    windows.Add(window);window.Show();window.UpdateLayout();
                }
                Sample("windows");
                foreach(var window in windows)
                {
                    var hwnd=new WindowInteropHelper(window).Handle;var scale=PresentationSource.FromVisual(window)!.CompositionTarget!.TransformToDevice;
                    var w=(int)Math.Round(320*scale.M11);var h=(int)Math.Round(180*scale.M22);
                    var presenter=mode=="shared" && presenters.Count>0?presenters[0].CreateSibling(hwnd,w,h):new NativeDirectPresenter(hwnd,w,h);
                    presenters.Add(presenter);Sample("devices");
                    presenter.UploadWallpaper(wallpaper,256,144);presenter.SetImage(1,bitmap);
                }
                await Observe("warm");
                for(var i=presenters.Count-1;i>0;i--){presenters[i].Dispose();presenters.RemoveAt(i);}
                await Observe("one-survivor");
                presenters[0].Dispose();presenters.Clear();
                await Observe("released");
                GetProcessMemoryInfo(process.Handle,out var final,(uint)Marshal.SizeOf<MemoryCounters>());
                File.WriteAllText(Path.Combine(output,"direct-device.json"),JsonSerializer.Serialize(new
                {
                    mode,processId=process.Id,processStartUtc=process.StartTime.ToUniversalTime(),osBuild=Environment.OSVersion.Version.Build,
                    wholeProductAcceptance=false,forcedGc=false,workingSetTrimmed=false,displayedFpsMeasured=false,
                    gpuMemorySource="External GPUProcessMemory counters; see gpu-memory.json when collected. Never add to CPU private commit.",
                    observedPerStageMs=duration,peakPrivateCommitBytes=(ulong)final.PeakPagefileUsage,
                    sourceHash=Convert.ToHexString(SHA256.HashData(wallpaper)),iconHash=Convert.ToHexString(SHA256.HashData(icon)),
                    scope="Four owned WPF HWNDs, two glass/image and two shadow scenes. Same build/content/geometry; no product host or Data. CPU samples every ~100 ms; OS process peak includes startup.",samples
                },new JsonSerializerOptions{WriteIndented=true}));
            }
            catch(Exception error){exit=1;File.WriteAllText(Path.Combine(output,"direct-device-error.txt"),error.ToString());}
            finally
            {
                foreach(var p in presenters)p.Dispose();
                foreach(var window in windows)window.Close();
                app.Shutdown(exit);
            }
        };
        app.Run();return exit;
    }

    [StructLayout(LayoutKind.Sequential)] private struct MemoryCounters
    {
        internal uint Size,PageFaultCount;
        internal nuint PeakWorkingSetSize,WorkingSetSize,QuotaPeakPagedPoolUsage,QuotaPagedPoolUsage,
            QuotaPeakNonPagedPoolUsage,QuotaNonPagedPoolUsage,PagefileUsage,PeakPagefileUsage,PrivateUsage;
    }
    [DllImport("kernel32.dll",EntryPoint="K32GetProcessMemoryInfo",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessMemoryInfo(nint process,out MemoryCounters counters,uint size);
}
