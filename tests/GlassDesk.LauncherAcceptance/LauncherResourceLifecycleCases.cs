using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using GlassDesk.AppServices;
using GlassDesk.Core.Storage;
using GlassDesk.Launcher;
using GlassDesk.Platform;
using GlassDesk.Platform.Displays;
using GlassDesk.UI.Controls;
using GlassDesk.UI.Theming;

internal static partial class Program
{
    private sealed record LauncherLifetimeObservation(List<object> Observations,WeakReference Window,WeakReference Surface,
        object SurfaceSnapshot,bool ValidMaterial);

    // Component observation, not an AppHost.Start or whole-product acceptance.
    private static int ObserveLauncherResourceLifecycle(string[] args)
    {
        var root=Path.GetFullPath(Argument(args,"--data-dir")??throw new ArgumentException("Copied --data-dir required"));
        var report=Path.GetFullPath(Argument(args,"--report")??throw new ArgumentException("--report required"));
        var captureExe=Path.GetFullPath(Argument(args,"--capture-exe")??throw new ArgumentException("--capture-exe required"));
        var ablation=Argument(args,"--diagnostic-ablation")??"none";
        if(ablation is not ("none" or "shadow" or "lens"))throw new ArgumentException("Unknown diagnostic ablation.");
        var capturePlacement=Argument(args,"--diagnostic-capture-placement")??"isolated";
        if(capturePlacement is not ("isolated" or "in-process-wallpaper"))throw new ArgumentException("Unknown capture placement.");
        if(File.Exists(report))throw new IOException("Use a fresh observation report.");
        if(!File.Exists(Path.Combine(root,".launcher-resource-test-root")) || File.ReadAllText(Path.Combine(root,".launcher-resource-test-root")).Trim()!=root)
            throw new InvalidOperationException("Copied data must contain its exact absolute test-root marker.");
        if(!File.Exists(captureExe))throw new FileNotFoundException("Capture worker entry point missing",captureExe);
        var output=Path.GetDirectoryName(report)!;Directory.CreateDirectory(output);
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        app.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/GlassDesk.UI;component/Theming/LiquidGlassTheme.xaml")});
        using var theme=ThemeService.Initialize(app.Dispatcher);
        var data=new GlassDeskDataStore(new AppPaths(root));data.LoadAll();
        if(data.Settings.Current.Appearance.UseSystemBackdrop || data.Settings.Current.Appearance.PinBarsToDesktop)
            throw new InvalidOperationException("This observation requires the explicit static-wallpaper, desktop-only-off profile.");
        theme.Update(data.Settings.Current.Appearance);
        using var services=new AppServiceHost(data);
        var primary=DisplayService.Primary()??throw new InvalidOperationException("No primary display");
        try
        {
            // No strong UI locals or callback closures from this non-inlined
            // frame may survive into the graph-collection diagnosis below.
            var result=ExerciseLauncherLifecycle(app,data,services,primary,captureExe,output,ablation,capturePlacement);
            if(args.Contains("--diagnostic-gc-after-close"))
            {
                Pump(TimeSpan.FromMilliseconds(200));
                GC.Collect(2,GCCollectionMode.Forced,true,true);GC.WaitForPendingFinalizers();
                Pump(TimeSpan.FromMilliseconds(200));
                GC.Collect(2,GCCollectionMode.Forced,true,true);GC.WaitForPendingFinalizers();
                Pump(TimeSpan.FromSeconds(3));
                WriteLifecyclePhase(output,"released-graph-after-diagnostic-gc");Pump(TimeSpan.FromSeconds(2));
                using var process=Process.GetCurrentProcess();
                result.Observations.Add(new{phase="released-graph-after-diagnostic-gc",utc=DateTimeOffset.UtcNow,
                    processId=Environment.ProcessId,process.PrivateMemorySize64,process.WorkingSet64,
                    gcCommittedBytes=GC.GetGCMemoryInfo().TotalCommittedBytes,surface=result.SurfaceSnapshot});
            }
            if(args.Contains("--hold-for-diagnostic-dump"))
            {
                File.WriteAllText(Path.Combine(output,"dump.ready"),Environment.ProcessId.ToString());
                if(!PumpUntil(()=>File.Exists(Path.Combine(output,"dump.done")),TimeSpan.FromSeconds(45)))
                    throw new TimeoutException("The owned diagnostic dump observer did not complete.");
            }
            File.WriteAllText(report,JsonSerializer.Serialize(new{componentObservationOnly=true,fullProductMemoryProven=false,
                diagnosticAblation=ablation,qualityChangedForDiagnostic=ablation!="none",
                diagnosticCapturePlacement=capturePlacement,productionCapturePlacementUnchanged=true,
                validShownMaterial=result.ValidMaterial,forcedGc=args.Contains("--diagnostic-gc-after-close"),diagnosticGcExcludedFromAcceptance=true,
                graphCollectionTestOutsideCreatorFrame=true,releasedWindowCollected=!result.Window.IsAlive,releasedSurfaceCollected=!result.Surface.IsAlive,
                conserveMemory=Environment.GetEnvironmentVariable("DOTNET_GCConserveMemory"),
                material=data.Settings.Current.Appearance.GlassMaterial.ToString(),observations=result.Observations},new JsonSerializerOptions{WriteIndented=true}));
            return result.ValidMaterial?0:1;
        }
        finally{app.Shutdown();}
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static LauncherLifetimeObservation ExerciseLauncherLifecycle(Application app,GlassDeskDataStore data,AppServiceHost services,
        DisplayInfo primary,string captureExe,string output,string ablation,string capturePlacement)
    {
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var observations=new List<object>();
        var window=new LauncherWindow();window.BindPrimaryDisplay(new PrimaryDisplaySession(primary));
        window.AttachServices(services,true);window.ApplySettings(data.Settings.Current.Launcher);
        var anchor=new PixelRect(primary.Bounds.X+primary.Bounds.Width/2-24,primary.Bounds.Bottom-120,48,64);
        window.ContainerAnchorProvider=()=>anchor;
        var surface=(LiquidGlassSurface)window.FindName("GlassSurface");
        var background=(GlassBackgroundLayer)typeof(LiquidGlassSurface).GetField("_background",flags)!.GetValue(surface)!;
        var feed=(SharedBackdropFeed)typeof(GlassBackgroundLayer).GetField("_compatible",flags)!.GetValue(background)!;
        Func<ScreenRect,bool,Action<CaptureTrace>?,IDesktopSceneCapture> create=(region,_,trace)=>new RecoveringDesktopSceneCapture(region,trace,captureExe);
        typeof(SharedBackdropFeed).GetField("_create",flags)!.SetValue(feed,create);
        var wallpaper=(SharedBackdropFeed)typeof(GlassBackgroundLayer).GetField("_wallpaperFeed",flags)!.GetValue(background)!;
        Func<ScreenRect,bool,Action<CaptureTrace>?,IDesktopSceneCapture> createWallpaper=(region,_,trace)=>
            capturePlacement=="in-process-wallpaper" ? new WallpaperSceneCapture(region)
                : new IsolatedDesktopSceneCapture(region,GlassBackdropSource.Wallpaper,trace,captureExe);
        typeof(SharedBackdropFeed).GetField("_create",flags)!.SetValue(wallpaper,createWallpaper);
        var closed=false;
        try
        {
            if(ablation=="shadow")
            {
                _=new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
                var shadow=typeof(GlassWindow).GetField("_shadow",flags)!;
                ((IDisposable?)shadow.GetValue(window))?.Dispose();shadow.SetValue(window,null);
            }
            if(ablation=="lens")
                for(var i=0;i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(surface);i++)
                {
                    if(System.Windows.Media.VisualTreeHelper.GetChild(surface,i) is TransitionGlassLayer layer)
                        layer.DiagnosticDisableLens=true;
                }
            Observe("created-hidden");
            window.PrewarmNextRevealMaterial();Observe("prewarmed-hidden");
            window.ShowWithTransition(anchor);window.Activate();Pump(TimeSpan.FromSeconds(2));Observe("shown");
            var state=surface.InspectPerformance();
            var valid=surface.BackdropSource is not (GlassBackdropSource.Basic or GlassBackdropSource.Transparent)
                && state.GetType().GetProperty("materialReady")!.GetValue(state) is true;
            window.CloseWithTransition();PumpUntil(()=>!window.IsVisible,TimeSpan.FromSeconds(4));Observe("warm-hidden");
            window.SetPerformanceSleeping(true);window.SetBackgroundWorkSuspended(true);Observe("retired-hidden");
            window.Close();closed=true;Observe("closed");
            return new(observations,new(window),new(surface),surface.InspectPerformance(),valid);
        }
        finally
        {
            if(!closed)window.Close();
            if(ReferenceEquals(app.MainWindow,window))app.MainWindow=null!;
        }

        void Observe(string phase)
        {
            Pump(TimeSpan.FromSeconds(3));WriteLifecyclePhase(output,phase);Pump(TimeSpan.FromSeconds(2));
            using var process=Process.GetCurrentProcess();
            // Registered workers belong to this fixture. Snapshot sequentially at
            // settled phases; these samples do not prove cold/interaction peaks.
            var group=new List<object>();long groupCommit=0;var groupComplete=true;
            foreach(var id in CaptureProcessFamily.WorkerProcesses.Append(Environment.ProcessId).Distinct())
            {
                try
                {
                    using var member=Process.GetProcessById(id);
                    var commit=member.PrivateMemorySize64;groupCommit+=commit;
                    group.Add(new{processId=id,privateCommitBytes=commit});
                }
                catch(ArgumentException){groupComplete=false;}
                catch(InvalidOperationException){groupComplete=false;}
            }
            var hwnd=closed?0:new System.Windows.Interop.WindowInteropHelper(window).Handle;
            observations.Add(new{phase,utc=DateTimeOffset.UtcNow,processId=Environment.ProcessId,process.PrivateMemorySize64,process.WorkingSet64,
                gcCommittedBytes=GC.GetGCMemoryInfo().TotalCommittedBytes,window.IsVisible,window.Width,window.Height,
                processGroupPrivateCommitBytes=groupCommit,processGroupSnapshotComplete=groupComplete,processGroupMembers=group,
                hwnd=hwnd.ToInt64(),pixelBounds=hwnd==0?default:DisplayService.GetWindowPixelBounds(hwnd),surface=surface.InspectPerformance()});
        }
    }

    private static void WriteLifecyclePhase(string output,string phase)=>
        File.WriteAllText(Path.Combine(output,"phase.json"),JsonSerializer.Serialize(new{phase,pid=Environment.ProcessId}));
}
