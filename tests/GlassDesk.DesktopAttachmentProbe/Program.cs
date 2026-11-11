using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using GlassDesk.Platform;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "recovery") return HostRecovery(args[1]);
        if (args.Length != 3 || !uint.TryParse(args[1], out var process)) return 2;
        return args[0] switch { "exercise" => Exercise(process,args[2]), "monitor" => Monitor(process,args[2]), _ => 2 };
    }
    private sealed record Bar(nint Handle,string Name);
    private static List<Bar> Bars(uint process)
    {
        var result=new List<Bar>();
        EnumWindows((window,_) => {
            GetWindowThreadProcessId(window,out var owner);
            var title=new StringBuilder(128);GetWindowText(window,title,title.Capacity);
            if(owner==process && title.ToString() is "GlassDesk Dock" or "GlassDesk 信息栏")result.Add(new(window,title.ToString()));
            return true;
        },0);
        if(result.Count!=2)throw new InvalidOperationException($"Expected two bars for PID {process}; found {result.Count}.");
        return result;
    }
    private static bool ValidateDesktop(List<Bar> bars)
    {
        var desktop=DesktopWindowAttachment.FindDesktopHost();
        return bars.All(bar=>GetWindow(bar.Handle,4)==desktop && IsWindowVisible(bar.Handle) && !IsIconic(bar.Handle)
            && (GetWindowLong(bar.Handle,-20)&8)==0 && Above(bar.Handle,desktop) && GetForegroundWindow()!=bar.Handle);
    }
    private static int Exercise(uint process,string output)
    {
        var cover=new Window {Title="GlassDesk controlled desktop group acceptance",Width=480,Height=320};
        var observations=new List<object>();var passed=true;
        try
        {
            cover.Show();var coverHandle=new WindowInteropHelper(cover).Handle;Pump(200);
            var bars=Bars(process);
            for(var cycle=0;cycle<3;cycle++)
            {
                cover.WindowState=WindowState.Minimized;
                SetWindowPos(GetShellWindow(),0,0,0,0,0,0x13);Pump(350);
                var desktop=ValidateDesktop(bars)&&IsIconic(coverHandle);
                observations.Add(new {cycle,phase="desktop",passed=desktop});passed &= desktop;
                cover.WindowState=WindowState.Normal;
                SetWindowPos(coverHandle,0,0,0,0,0,0x13);cover.Activate();Pump(350);
                var application=bars.All(bar=>Above(coverHandle,bar.Handle));
                observations.Add(new {cycle,phase="application",passed=application});passed &= application;
            }
        }
        finally {cover.Close();}
        File.WriteAllText(output,JsonSerializer.Serialize(new {passed,trigger="ControlledNativeDesktopRaise",keyboardWinDVerified=false,observations},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"Controlled installed desktop group: {passed}");return passed?0:1;
    }
    private static int Monitor(uint process,string output)
    {
        var watch=System.Diagnostics.Stopwatch.StartNew();var observations=new List<object>();
        var desktopSeen=false;var applicationSeen=false;var passed=true;string? previous=null;
        while(watch.Elapsed.TotalSeconds<120 && !(desktopSeen&&applicationSeen))
        {
            Pump(150);var foreground=GetForegroundWindow();
            var className=new StringBuilder(128);GetClassName(foreground,className,className.Capacity);
            var desktop=className.ToString() is "Progman" or "WorkerW";
            GetWindowThreadProcessId(foreground,out var foregroundProcess);
            var phase=desktop?"desktop":desktopSeen&&foregroundProcess!=process&&IsWindowVisible(foreground)&&!IsIconic(foreground)?"application":null;
            if(phase is null || phase==previous)continue;
            Pump(400);var bars=Bars(process);
            var valid=phase=="desktop"?ValidateDesktop(bars):bars.All(bar=>Above(GetForegroundWindow(),bar.Handle));
            observations.Add(new {phase,passed=valid,atMilliseconds=watch.ElapsedMilliseconds,foreground=(long)GetForegroundWindow()});
            passed &= valid;previous=phase;
            if(phase=="desktop")desktopSeen=true;else applicationSeen=true;
        }
        passed &= desktopSeen&&applicationSeen;
        File.WriteAllText(output,JsonSerializer.Serialize(new{passed,trigger="UserControlledDesktopSwitch",desktopSeen,applicationSeen,observations},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"User controlled desktop observation: {passed}");return passed?0:1;
    }
    private static int HostRecovery(string output)
    {
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        var type=typeof(GlassDesk.App.Startup.CommandLineOptions).Assembly.GetType("GlassDesk.App.AppHost")!;
        var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Public;
        var recover=type.GetMethod("RecoverDesktopBars",flags);
        if(recover is null){Console.WriteLine("RED: AppHost has no desktop bar reconstruction path.");return 1;}
        var root=Path.Combine(Path.GetTempPath(),"GlassDesk-owner-recovery-"+Guid.NewGuid().ToString("N"));
        var paths=new GlassDesk.Core.Storage.AppPaths(root);var data=new GlassDesk.Core.Storage.GlassDeskDataStore(paths);data.LoadAll();
        data.Settings.Current.Appearance.PinBarsToDesktop=true;data.Settings.Current.Appearance.ReduceMotion=true;
        data.Settings.Current.Appearance.GlassMaterial=GlassDesk.Core.Models.GlassMaterialMode.PlainGlass;
        data.Settings.Current.Dock.AutoHide=false;data.Settings.Current.Dock.ShowStatusBar=true;
        data.Settings.Current.Display.SuppressTriggersWhenFullscreen=false;
        data.Settings.Save();var settingsBefore=File.ReadAllBytes(paths.SettingsFile);
        using var theme=GlassDesk.UI.Theming.ThemeService.Initialize(Dispatcher.CurrentDispatcher);theme.Update(data.Settings.Current.Appearance);
        var services=new GlassDesk.AppServices.AppServiceHost(data);
        var host=Activator.CreateInstance(type,flags,null,new object[]{app,new GlassDesk.App.Startup.CommandLineOptions()},null)!;
        void Set(string name,object value)=>type.GetField(name,flags)!.SetValue(host,value);
        object? Get(string name)=>type.GetField(name,flags)!.GetValue(host);
        void Call(string name,params object[] args)=>type.GetMethod(name,flags)!.Invoke(host,args);
        var settings=GlassDesk.UI.Settings.DockSettingsHost.Initialize(Dispatcher.CurrentDispatcher);settings.Update(data.Settings.Current.Dock);
        Set("_data",data);Set("_services",services);Set("_primaryDisplaySession",new GlassDesk.Platform.Displays.PrimaryDisplaySession(GlassDesk.Platform.Displays.DisplayService.Primary()!));Set("_dockSettings",settings);
        Set("_launcher",new GlassDesk.Launcher.LauncherWindow{Icon=System.Windows.Media.Imaging.BitmapSource.Create(1,1,96,96,System.Windows.Media.PixelFormats.Bgra32,null,new byte[4],4)});Set("_settings",new GlassDesk.Settings.SettingsWindow());
        try
        {
            Call("CreateDesktopBars");Call("InitializePerformanceCoordinator");Call("SetManualGameSleep",true);Pump(100);
            var coordinator=Get("_performance");var first=(GlassDesk.Dock.DockWindow)Get("_dock")!;var firstStatus=(Window)Get("_statusBar")!;
            var firstHandle=new WindowInteropHelper(first).Handle;
            var closing=(System.ComponentModel.CancelEventHandler)type.GetMethod("OnWindowClosing",flags)!.CreateDelegate(typeof(System.ComponentModel.CancelEventHandler),host);
            first.Closing-=closing;first.Close();Pump(400);Call("RecoverDesktopBars");
            var next=(GlassDesk.Dock.DockWindow)Get("_dock")!;var status=(GlassDesk.Dock.StatusBarWindow)Get("_statusBar")!;
            var passed=!ReferenceEquals(first,next)&&!ReferenceEquals(firstStatus,status)
                &&ReferenceEquals(coordinator,Get("_performance"))&&ReferenceEquals(services,Get("_services"))&&ReferenceEquals(data,Get("_data"))
                && (bool)type.GetProperty("RuntimeManualGameSleep",flags)!.GetValue(host)!
                && next.IsPerformanceSleeping&&!next.IsSurfacePresented&&!status.IsSurfacePresented
                && !next.SuppressPassiveTriggersWhenFullscreen
                &&GlassDesk.Platform.WindowControl.IsAlive(new WindowInteropHelper(next).Handle)
                &&File.ReadAllBytes(paths.SettingsFile).SequenceEqual(settingsBefore);
            foreach(var pinned in new[]{false,true})
            {
                var previous=Get("_dock");data.Settings.Current.Appearance.PinBarsToDesktop=pinned;theme.Update(data.Settings.Current.Appearance);
                Call("OnThemeChanged",theme,EventArgs.Empty);Pump(400);Call("RecoverDesktopBars");
                next=(GlassDesk.Dock.DockWindow)Get("_dock")!;status=(GlassDesk.Dock.StatusBarWindow)Get("_statusBar")!;
                passed &= !ReferenceEquals(previous,next)&&ReferenceEquals(coordinator,Get("_performance"))
                    &&next.IsPerformanceSleeping&&!next.IsSurfacePresented&&!status.IsSurfacePresented
                    &&!next.SuppressPassiveTriggersWhenFullscreen;
                if(pinned)passed &= GetWindow(new WindowInteropHelper(next).Handle,4)==GlassDesk.Platform.DesktopWindowAttachment.FindDesktopHost();
                else passed &= next.Topmost;
            }
            Call("SetManualGameSleep",false);Pump(400);passed &= next.IsSurfacePresented&&!next.IsPerformanceSleeping;
            File.WriteAllText(output,JsonSerializer.Serialize(new{passed,reusedServices=true,reusedCoordinator=ReferenceEquals(coordinator,Get("_performance")),manualSleepPreserved=true,wakePresented=next.IsSurfacePresented,settingsUnchanged=File.ReadAllBytes(paths.SettingsFile).SequenceEqual(settingsBefore)},new JsonSerializerOptions{WriteIndented=true}));
            Console.WriteLine($"Host recovery: {passed}");return passed?0:1;
        }
        finally
        {
            Set("_shuttingDown",true);
            foreach(var name in new[]{"_dock","_statusBar","_launcher","_settings"}) (Get(name) as Window)?.Close();
            ((IDisposable)host).Dispose();app.Shutdown();Directory.Delete(root,true);
        }
    }
    private static bool Above(nint upper,nint lower)
    {
        for(var next=GetWindow(upper,2);next!=0;next=GetWindow(next,2))if(next==lower)return true;
        return false;
    }
    private static void Pump(int milliseconds)
    {
        var frame=new DispatcherFrame();var timer=new DispatcherTimer(DispatcherPriority.Background){Interval=TimeSpan.FromMilliseconds(milliseconds)};
        timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);
    }
    private delegate bool Callback(nint window,nint parameter);
    [DllImport("user32.dll")]private static extern bool EnumWindows(Callback callback,nint parameter);
    [DllImport("user32.dll")]private static extern nint GetWindow(nint window,uint command);
    [DllImport("user32.dll")]private static extern nint GetShellWindow();
    [DllImport("user32.dll")]private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")]private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(nint window,out uint process);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetWindowText(nint window,StringBuilder text,int size);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetClassName(nint window,StringBuilder text,int size);
    [DllImport("user32.dll",EntryPoint="GetWindowLongW")]private static extern int GetWindowLong(nint window,int index);
    [DllImport("user32.dll")]private static extern bool SetWindowPos(nint window,nint after,int x,int y,int width,int height,uint flags);
}
