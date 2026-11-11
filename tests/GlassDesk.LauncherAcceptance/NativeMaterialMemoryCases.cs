using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassDesk.Rendering;
using GlassDesk.UI.Controls;

internal static partial class Program
{
    // Material-only lower bound. No AppHost, user data, desktop sampling,
    // foreground, shadow, input policy migration or installed process changes.
    private static int ObserveNativeMaterialMemory(string[] args)
    {
        var backend=Argument(args,"--backend")??throw new ArgumentException("--backend required");
        if(backend is not ("wpf" or "native" or "native-wpf-host" or "native-wpf-empty" or "native-wpf-software"))throw new ArgumentException("Unknown backend.");
        var report=Path.GetFullPath(Argument(args,"--report")??throw new ArgumentException("--report required"));
        if(File.Exists(report))throw new IOException("Use a fresh report path.");
        var directory=Path.GetDirectoryName(report)!;Directory.CreateDirectory(directory);
        var priorDpi=MaterialMemoryNative.SetThreadDpiAwarenessContext(-4);
        if(backend=="native-wpf-software")
            System.Windows.Media.RenderOptions.ProcessRenderMode=RenderMode.SoftwareOnly;
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        const double dw=1460.8,dh=979.2;
        const int sw=640,sh=428;
        var hostSize=new Size(dw,dh);var panel=new Rect(54.4,62.4,1352,728.8);var back=new Rect(hostSize);
        var source=new byte[sw*sh*4];
        for(var y=0;y<sh;y++)for(var x=0;x<sw;x++)
        {
            var i=(y*sw+x)*4;
            source[i]=(byte)(35+x/4);source[i+1]=(byte)(25+y/3);
            source[i+2]=(byte)(50+((x/16+y/16)%2)*100);source[i+3]=255;
        }
        var phases=new List<object>();
        Window? window=null;nint rawWindow=0;NativeDirectPresenter? native=null;SafeHandle? nativeHandle=null;
        try
        {
            Observe("prepared-source");
            if(backend=="wpf")
            {
                var filtered=new byte[source.Length];var scratch=new byte[source.Length];
                AndroidLiquidGlassRenderer.RenderTransitionBackdropInto(source,filtered,scratch,sw,sh,hostSize,true,1,
                    backgroundClarity:.5,launchpadOptics:true);
                var bitmap=BitmapSource.Create(sw,sh,96,96,PixelFormats.Bgra32,null,filtered,sw*4);bitmap.Freeze();
                var lens=new TransitionGlassLayer{Opacity=1,Width=dw,Height=dh};
                lens.SetBackdrop(bitmap);lens.SetPanel(panel,back,80,1,true);
                var root=new Canvas{Width=dw,Height=dh,Clip=SquircleGlassGeometry.Create(panel,80)};root.Children.Add(lens);
                window=new Window{Width=dw,Height=dh,Left=0,Top=0,Content=root,WindowStyle=WindowStyle.None,
                    AllowsTransparency=true,Background=Brushes.Transparent,ShowInTaskbar=false,ShowActivated=false,
                    ResizeMode=ResizeMode.NoResize};
                window.Show();window.UpdateLayout();
                if(!lens.UsesGpuLens)throw new InvalidOperationException("Hardware WPF lens required.");
            }
            else
            {
                // Native HWND is an explicit lower-bound control, not a replacement
                // for the product HWND/owner/desktop/input policies.
                const int physicalWidth=1826,physicalHeight=1224;
                nint targetWindow;
                if(backend is "native-wpf-host" or "native-wpf-empty" or "native-wpf-software")
                {
                    window=new Window{Width=dw,Height=dh,Left=0,Top=0,WindowStyle=WindowStyle.None,
                        AllowsTransparency=true,Background=backend=="native-wpf-host"?Brushes.Transparent:null,ShowInTaskbar=false,ShowActivated=false,
                        ResizeMode=ResizeMode.NoResize};
                    window.Show();window.UpdateLayout();targetWindow=new WindowInteropHelper(window).Handle;
                }
                else
                {
                    rawWindow=MaterialMemoryNative.CreateWindowExW(0x08200080,"STATIC","GlassDesk owned material control",
                        0x80000000,0,0,physicalWidth,physicalHeight,0,0,0,0);
                    if(rawWindow==0)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                    MaterialMemoryNative.ShowWindow(rawWindow,4);targetWindow=rawWindow;
                }
                (native,nativeHandle)=CreateDiagnosticMaterialPresenter(targetWindow,physicalWidth,physicalHeight);
                native.UploadWallpaper(source,sw,sh);
                native.RenderGlass(new((float)dw,(float)dh,1,.5f,.16f,false,true),
                    new(hostSize,panel,back,80,1,Launchpad:true,CompactOutline:false));
                if(!native.Present())throw new InvalidOperationException("Initial material presentation deferred.");
            }
            Pump(TimeSpan.FromSeconds(2));Observe("shown-before-diagnostic-readback");
            var handle=rawWindow!=0?rawWindow:new WindowInteropHelper(window!).Handle;
            CapturePixels pixels;
            if(native is not null && rawWindow!=0)
            {
                // A no-redirection HWND is not a WGC source on this host. Use the
                // core's existing opt-in diagnostic readback after measuring RAM.
                native.RenderGlass(new((float)dw,(float)dh,1,.5f,.16f,false,true),
                    new(hostSize,panel,back,80,1,Launchpad:true,CompactOutline:false));
                var data=new byte[1826*1224*4];var pin=GCHandle.Alloc(data,GCHandleType.Pinned);
                var library=NativeLibrary.Load(Environment.GetEnvironmentVariable("GLASSDESK_NATIVE_RENDER_DLL")
                    ??Path.Combine(AppContext.BaseDirectory,"GlassDesk.Rendering.Native.dll"));
                try
                {
                    var read=Marshal.GetDelegateForFunctionPointer<MaterialReadPixels>(NativeLibrary.GetExport(library,"GD_PresenterReadPixels"));
                    Marshal.ThrowExceptionForHR(read(nativeHandle!.DangerousGetHandle(),pin.AddrOfPinnedObject(),(ulong)data.Length,1826*4));
                    pixels=new(1826,1224,data);
                }
                finally{NativeLibrary.Free(library);pin.Free();}
            }
            else pixels=WgcWindowCapture.Capture(handle,TimeSpan.FromSeconds(3))
                ??throw new InvalidOperationException("Owned WPF material capture missing.");
            if(pixels.Width!=1826 || pixels.Height!=1224)
                throw new InvalidOperationException($"Unexpected physical output {pixels.Width}x{pixels.Height}; this control requires 125% DPI.");
            File.WriteAllBytes(Path.Combine(directory,"frame.bgra"),pixels.Pixels);
            var nativeStatistics=native?.Statistics;
            var image=BitmapSource.Create(pixels.Width,pixels.Height,96,96,PixelFormats.Bgra32,null,pixels.Pixels,pixels.Width*4);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));
            using(var stream=File.Create(Path.Combine(directory,"frame.png")))encoder.Save(stream);
            native?.Dispose();native=null;
            if(window is not null){window.Close();window=null;}
            if(rawWindow!=0){MaterialMemoryNative.DestroyWindow(rawWindow);rawWindow=0;}
            Pump(TimeSpan.FromSeconds(2));Observe("closed-after-diagnostic-readback");
            File.WriteAllText(report,JsonSerializer.Serialize(new{backend,componentOnly=true,materialOnly=true,
                fullProductPeakProven=false,forcedGc=false,diagnosticReadbackAfterShownMeasurement=true,
                pixelSource=backend=="native"?"Explicit diagnostic core readback":"WGC of owned WPF window",
                wpfProcessRenderMode=System.Windows.Media.RenderOptions.ProcessRenderMode.ToString(),
                note="Synthetic material at launcher host size. Native control uses its own HWND; no foreground, shadow or capture worker.",
                sourceWidth=sw,sourceHeight=sh,pixels.Width,pixels.Height,nativeStatistics,phases,
                sourceSha256=Convert.ToHexString(SHA256.HashData(source)),
                uiSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(TransitionGlassLayer).Assembly.Location)))},
                new JsonSerializerOptions{WriteIndented=true}));
            return 0;
        }
        finally
        {
            native?.Dispose();window?.Close();if(rawWindow!=0)MaterialMemoryNative.DestroyWindow(rawWindow);
            app.Shutdown();if(priorDpi!=0)MaterialMemoryNative.SetThreadDpiAwarenessContext(priorDpi);
        }
        void Observe(string phase)
        {
            using var process=Process.GetCurrentProcess();
            phases.Add(new{phase,process.PrivateMemorySize64,process.WorkingSet64,
                gcCommittedBytes=GC.GetGCMemoryInfo().TotalCommittedBytes});
        }
    }

    private static (NativeDirectPresenter,SafeHandle) CreateDiagnosticMaterialPresenter(nint window,int width,int height)
    {
        // Keep readback out of the production managed API. This fixture opts in
        // through the same internal create info already used by native tests.
        const BindingFlags fields=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
        var methods=typeof(NativeDirectPresenter).Assembly.GetType("GlassDesk.Rendering.NativeMethods",true)!;
        var infoType=methods.GetNestedType("CreateInfo",BindingFlags.NonPublic)!;
        var info=Activator.CreateInstance(infoType)!;
        void Set(string name,object value)=>infoType.GetField(name,fields)!.SetValue(info,value);
        Set("Size",32u);Set("Version",1u);Set("Window",window);Set("Width",(uint)width);Set("Height",(uint)height);Set("Flags",1u);
        var values=new object?[]{info,null};
        var hr=(int)methods.GetMethod("PresenterCreate",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,values)!;
        var handle=(SafeHandle)values[1]!;
        if(hr<0){handle.Dispose();Marshal.ThrowExceptionForHR(hr);}
        try
        {
            var presenter=(NativeDirectPresenter)Activator.CreateInstance(typeof(NativeDirectPresenter),
                BindingFlags.Instance|BindingFlags.NonPublic,null,new object[]{handle},null)!;
            return(presenter,handle);
        }
        catch{handle.Dispose();throw;}
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MaterialReadPixels(nint presenter,nint pixels,ulong bytes,uint stride);

    private static class MaterialMemoryNative
    {
        [DllImport("user32.dll")] internal static extern nint SetThreadDpiAwarenessContext(nint context);
        [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] internal static extern nint CreateWindowExW(uint ex,string cls,string title,uint style,int x,int y,int w,int h,nint parent,nint menu,nint instance,nint parameter);
        [DllImport("user32.dll")] internal static extern bool ShowWindow(nint window,int command);
        [DllImport("user32.dll")] internal static extern bool DestroyWindow(nint window);
    }
}
