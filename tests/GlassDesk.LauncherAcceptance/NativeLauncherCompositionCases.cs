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
using GlassDesk.AppServices;
using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;
using GlassDesk.Launcher;
using GlassDesk.Platform.Displays;
using GlassDesk.Rendering;
using GlassDesk.UI.Controls;
using GlassDesk.UI.Theming;

internal static partial class Program
{
    private static int ObserveNativeLauncherComposition(string[] args)
    {
        const BindingFlags fields=BindingFlags.Instance|BindingFlags.NonPublic;
        var backend=Argument(args,"--backend")??"wpf";
        var host=Argument(args,"--host")??"hardware";
        var device=Argument(args,"--device")??"shared";
        if(backend is not ("wpf" or "native") || host is not ("hardware" or "software") || device is not ("shared" or "independent"))
            throw new ArgumentException("Invalid composition configuration.");
        if(backend=="wpf" && host!="hardware")throw new ArgumentException("Reference must use the hardware lens.");
        var report=Path.GetFullPath(Argument(args,"--report")??throw new ArgumentException("--report required"));
        var directory=Path.GetDirectoryName(report)!;
        if(Directory.Exists(directory))throw new IOException("Use a fresh composition directory.");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory,"phase.txt"),"startup-before-diagnostic");
        var priorDpi=MaterialMemoryNative.SetThreadDpiAwarenessContext(-4);
        if(host=="software")RenderOptions.ProcessRenderMode=RenderMode.SoftwareOnly;
        Environment.SetEnvironmentVariable("GLASSDESK_LAUNCHER_NATIVE_SHADOW","1");
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/GlassDesk.UI;component/Theming/LiquidGlassTheme.xaml")});
        using var theme=ThemeService.Initialize(app.Dispatcher);
        var data=new GlassDeskDataStore(new AppPaths(Path.Combine(directory,"Data")));data.LoadAll();
        data.Settings.Current.Appearance.GlassMaterial=GlassMaterialMode.AndroidLiquidGlass;
        data.Settings.Current.Appearance.UseSystemBackdrop=true;data.Settings.Current.Appearance.PinBarsToDesktop=false;
        data.Settings.Current.Launcher.FocusSearchOnOpen=false;
        theme.Update(data.Settings.Current.Appearance);
        var iconFile=Path.Combine(directory,"fixture-icon.png");
        var iconPixels=new byte[32*32*4];
        for(var i=0;i<32*32;i++){iconPixels[i*4]=40;iconPixels[i*4+1]=(byte)(80+i%32*4);iconPixels[i*4+2]=220;iconPixels[i*4+3]=255;}
        WriteCompositionPng(iconFile,new(32,32,iconPixels));
        for(var i=0;i<36;i++)data.Library.Add(new AppEntry{Id=$"composition-{i}",DisplayName=$"应用 {i:00}",
            Target=Path.Combine(directory,$"missing-{i}"),IsTargetMissing=true,CustomIconPath=i%2==0?iconFile:null});
        using var services=new AppServiceHost(data);
        var display=DisplayService.Primary()??throw new InvalidOperationException("No primary display.");
        var window=new LauncherWindow();window.BindPrimaryDisplay(new PrimaryDisplaySession(display));
        var surface=(LiquidGlassSurface)window.FindName("GlassSurface");
        surface.SetPerformanceSleeping(true);surface.Visibility=Visibility.Collapsed;
        window.AttachServices(services,true);window.ApplySettings(data.Settings.Current.Launcher);
        typeof(LauncherWindow).GetField("_keepOpenWhileModalCount",fields)!.SetValue(window,1);
        window.ShowActivated=false;
        var phases=new List<object>();string stage="construct";string? failure=null;
        NativeForegroundPresenter? renderer=null;NativeForegroundScene? scene=null,bodyMask=null;
        VisualDrawingSnapshot? snapshot=null;DirectDeviceStatistics? shadowDevice=null,foregroundDevice=null;
        object? resources=null,groupStats=null;CapturePixels? frame=null;byte[]? source=null;
        var valid=false;var hwnd=(nint)0;object? beforeContract=null,afterContract=null;
        try
        {
            Observe("constructed-hidden");stage="show-source";
            window.Show();window.UpdateLayout();Pump(TimeSpan.FromMilliseconds(400));
            surface.SetPerformanceSleeping(true);
            hwnd=new WindowInteropHelper(window).Handle;
            beforeContract=CompositionWindowContract(hwnd);
            var size=new Size(window.ActualWidth,window.ActualHeight);
            var panel=(Rect)typeof(LauncherWindow).GetField("_transitionPanelBoundsDip",fields)!.GetValue(window)!;
            var bounds=DisplayService.GetWindowPixelBounds(hwnd)??throw new InvalidOperationException("Host bounds unavailable.");
            const int sw=640;var sh=Math.Max(1,(int)Math.Round(sw*size.Height/size.Width));
            source=new byte[sw*sh*4];
            for(var y=0;y<sh;y++)for(var x=0;x<sw;x++)
            {var i=(y*sw+x)*4;source[i]=(byte)(35+x/4);source[i+1]=(byte)(25+y%428/3);source[i+2]=(byte)(50+((x/16+y/16)%2)*100);source[i+3]=255;}
            var root=(Grid)window.FindName("LauncherGuideRoot");
            var content=(UIElement)window.Content;
            if(backend=="wpf")
            {
                var filtered=new byte[source.Length];var scratch=new byte[source.Length];
                AndroidLiquidGlassRenderer.RenderTransitionBackdropInto(source,filtered,scratch,sw,sh,size,true,1,backgroundClarity:.5,launchpadOptics:true);
                var bitmap=BitmapSource.Create(sw,sh,96,96,PixelFormats.Bgra32,null,filtered,sw*4);bitmap.Freeze();
                var lens=new TransitionGlassLayer{Opacity=1};lens.SetBackdrop(bitmap);
                lens.SetPanel(panel,new Rect(size),80,1,true);root.Children.Insert(0,lens);
                window.UpdateLayout();
                if(!lens.UsesGpuLens)throw new InvalidOperationException("Reference hardware lens unavailable.");
            }
            else
            {
                stage="snapshot";
                snapshot=VisualDrawingSnapshot.Capture(window,new(){Exclude=v=>ReferenceEquals(v,surface),PresentationOpacityCarrier=content});
                stage="create-presenter";
                var shadow=(GlassShadow?)typeof(GlassWindow).GetField("_shadow",fields)!.GetValue(window);
                var shadowPresenter=shadow is null?null:(NativeDirectPresenter?)typeof(GlassShadow).GetField("_nativePresenter",fields)!.GetValue(shadow);
                if(shadowPresenter is null)throw new InvalidOperationException("Real native shadow must remain active; no shadow ablation permitted.");
                shadowDevice=shadowPresenter.DeviceStatistics;
                if(device=="shared")
                {
                    var sibling=shadowPresenter.CreateSibling(hwnd,bounds.Width,bounds.Height);
                    try{renderer=(NativeForegroundPresenter)Activator.CreateInstance(typeof(NativeForegroundPresenter),fields,null,
                        new object[]{sibling,bounds.Width,bounds.Height,0},null)!;}
                    catch{sibling.Dispose();throw;}
                }
                else renderer=new(hwnd,bounds.Width,bounds.Height);
                foregroundDevice=renderer.DeviceStatistics;
                if((device=="shared")!=(shadowDevice.Identity==foregroundDevice.Identity))throw new InvalidOperationException("Unexpected device sharing identity.");
                stage="compile-foreground";scene=renderer.Prepare(snapshot,size);
                // The real launcher has a Window clip as well as the content
                // clip. The native glass shader already supplies one coverage;
                // retain the additional Window coverage, including fractional AA.
                var maskVisual=new DrawingVisual();
                using(var dc=maskVisual.RenderOpen())dc.DrawGeometry(Brushes.White,null,window.Clip);
                bodyMask=renderer.Prepare(VisualDrawingSnapshot.Capture(maskVisual),size);
                var output=(NativeDirectPresenter)typeof(NativeForegroundPresenter).GetField("_presenter",fields)!.GetValue(renderer)!;
                stage="draw";output.UploadWallpaper(source,sw,sh);
                var clock=Stopwatch.StartNew();
                while(WaitForCompositionAdmission(renderer.FrameLatencyHandle)==258 && clock.Elapsed<TimeSpan.FromSeconds(3))Pump(TimeSpan.FromMilliseconds(2));
                output.Clear(Colors.Transparent);output.PushGroup();
                output.RenderGlass(new((float)size.Width,(float)size.Height,1,.5f,.16f,false,true),
                    new(size,panel,new Rect(size),80,1,Launchpad:true,CompactOutline:false));
                output.BeginGroupMask();renderer.Draw(bodyMask);output.PopGroup();
                renderer.Draw(scene);
                if(!renderer.Present())throw new InvalidOperationException("Composition frame deferred.");
                content.Opacity=0;
                resources=renderer.ResourceStatistics;groupStats=renderer.GroupStatistics;
            }
            stage="settle";Pump(TimeSpan.FromSeconds(2));Observe("shown-before-diagnostic-readback");
            stage="capture";
            File.WriteAllText(Path.Combine(directory,"phase.txt"),"diagnostic-readback");
            frame=WgcWindowCapture.Capture(hwnd,TimeSpan.FromSeconds(3))??throw new InvalidOperationException("Composed frame unavailable.");
            if(frame.Width!=bounds.Width || frame.Height!=bounds.Height)throw new InvalidOperationException("Output extent differs from HWND.");
            File.WriteAllBytes(Path.Combine(directory,"frame.bgra"),frame.Pixels);WriteCompositionPng(Path.Combine(directory,"frame.png"),frame);
            afterContract=CompositionWindowContract(new WindowInteropHelper(window).Handle);
            if(JsonSerializer.Serialize(beforeContract)!=JsonSerializer.Serialize(afterContract))throw new InvalidOperationException("HWND contract changed.");
            valid=true;
        }
        catch(Exception error){failure=error.ToString();}
        finally
        {
            bodyMask?.Dispose();scene?.Dispose();renderer?.Dispose();
            ((UIElement)window.Content).Opacity=1;
            window.Close();Pump(TimeSpan.FromSeconds(1));Observe("closed-after-diagnostic-readback");
            app.Shutdown();if(priorDpi!=0)MaterialMemoryNative.SetThreadDpiAwarenessContext(priorDpi);
            File.WriteAllText(report,JsonSerializer.Serialize(new{backend,host,device,valid,stage,failure,componentOnly=true,
                syntheticLibrary=true,syntheticBackground=true,fullProductPeakProven=false,animationsVerified=false,inputVerified=false,
                shadowIncludedInProcessMemory=true,shadowPixelsExcludedFromWindowCapture=true,forcedGc=false,phases,
                gcConserveMemory=Environment.GetEnvironmentVariable("DOTNET_GCConserveMemory")??"runtime-default",
                width=frame?.Width,height=frame?.Height,hwnd=(long)hwnd,beforeContract,afterContract,
                snapshotVisuals=snapshot?.VisualCount,snapshotDrawings=snapshot?.DrawingCount,snapshotGlyphs=snapshot?.GlyphCount,
                snapshotBitmapBytes=snapshot?.BitmapSourceBytes,resources,groupStats,shadowDevice,foregroundDevice,
                syntheticLibraryEntries=36,iconSha256=Convert.ToHexString(SHA256.HashData(iconPixels)),
                uiSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(LiquidGlassSurface).Assembly.Location))),
                launcherSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(LauncherWindow).Assembly.Location))),
                renderingSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(NativeForegroundPresenter).Assembly.Location))),
                sourceSha256=source is null?null:Convert.ToHexString(SHA256.HashData(source))},new JsonSerializerOptions{WriteIndented=true}));
        }
        return valid?0:1;
        void Observe(string phase)
        {
            using var process=Process.GetCurrentProcess();phases.Add(new{phase,process.PrivateMemorySize64,process.WorkingSet64,
                gcCommittedBytes=GC.GetGCMemoryInfo().TotalCommittedBytes});
        }
    }

    private static object CompositionWindowContract(nint window)=>new{handle=(long)window,
        style=(long)CompositionGetWindowLong(window,-16),extendedStyle=(long)CompositionGetWindowLong(window,-20),owner=(long)CompositionGetWindow(window,4)};
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] private static extern nint CompositionGetWindowLong(nint window,int index);
    [DllImport("user32.dll",EntryPoint="GetWindow")] private static extern nint CompositionGetWindow(nint window,uint command);
    [DllImport("kernel32.dll",EntryPoint="WaitForSingleObject")] private static extern uint WaitForCompositionAdmission(nint handle,uint milliseconds=0);
    private static void WriteCompositionPng(string path,CapturePixels frame)
    {
        var image=BitmapSource.Create(frame.Width,frame.Height,96,96,PixelFormats.Bgra32,null,frame.Pixels,frame.Width*4);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var stream=File.Create(path);encoder.Save(stream);
    }
}
