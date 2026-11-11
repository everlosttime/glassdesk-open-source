using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using GlassDesk.AppServices;
using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;
using GlassDesk.Platform.Displays;
using GlassDesk.Settings;
using GlassDesk.UI.Settings;
using GlassDesk.UI.Theming;

internal static partial class Program
{
    private static int VerifyDeferredSettingsHost(string[] args)
    {
        const BindingFlags instance=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
        var assemblyFile=Path.GetFullPath(Argument(args,"--app-assembly")??throw new ArgumentException("--app-assembly required"));
        var report=Path.GetFullPath(Argument(args,"--report")??throw new ArgumentException("--report required"));
        var root=Path.Combine(Path.GetDirectoryName(report)!,"data-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        var application=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        var assembly=Assembly.LoadFrom(assemblyFile);
        application.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/GlassDesk;component/Assets/GlassDeskBrandMark.xaml")});
        application.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("pack://application:,,,/GlassDesk.UI;component/Theming/LiquidGlassTheme.xaml")});
        using var theme=ThemeService.Initialize(application.Dispatcher);
        var data=new GlassDeskDataStore(new AppPaths(root));data.LoadAll();
        data.Settings.Current.Appearance.PinBarsToDesktop=false;
        data.Settings.Current.Appearance.GlassMaterial=GlassMaterialMode.PlainGlass;
        data.Settings.Current.Nearby.Enabled=false;data.Settings.Current.OnboardingDismissed=true;
        theme.Update(data.Settings.Current.Appearance);
        var services=new AppServiceHost(data);
        var type=assembly.GetType("GlassDesk.App.AppHost",true)!;
        var options=assembly.GetType("GlassDesk.App.Startup.CommandLineOptions",true)!.GetMethod("Parse")!.Invoke(null,[Array.Empty<string>()]);
        var host=(IDisposable)Activator.CreateInstance(type,instance,null,[application,options],null)!;
        void Set(string name,object value)=>type.GetField(name,instance)!.SetValue(host,value);
        object? Get(string name)=>type.GetField(name,instance)!.GetValue(host);
        object? Call(string name,params object?[] values)=>type.GetMethod(name,instance)!.Invoke(host,values);
        var lifetime=Stopwatch.StartNew();
        long Commit(){using var p=Process.GetCurrentProcess();return p.PrivateMemorySize64;}
        try
        {
            Set("_data",data);Set("_services",services);
            Set("_primaryDisplaySession",new PrimaryDisplaySession(DisplayService.Primary()??throw new InvalidOperationException("Display unavailable")));
            var settingsHost=DockSettingsHost.Initialize(application.Dispatcher);settingsHost.Update(data.Settings.Current.Dock);Set("_dockSettings",settingsHost);
            var beforeCommit=Commit();var beforeAllocated=GC.GetTotalAllocatedBytes(true);
            Call("CreateWindows");
            if(args.Contains("--eager-settings") && Get("_settings") is null)Call("EnsureSettingsWindow");
            Pump(TimeSpan.FromMilliseconds(250));
            var startupCommit=Commit();var startupAllocated=GC.GetTotalAllocatedBytes(true)-beforeAllocated;
            var coldSettings=Get("_settings") as SettingsWindow;
            if(args.Contains("--memory-only"))
            {
                File.WriteAllText(report,JsonSerializer.Serialize(new{startupCommitBytes=startupCommit,startupCommitDeltaBytes=startupCommit-beforeCommit,
                    startupManagedAllocatedBytes=startupAllocated,settingsCreatedAtStartup=coldSettings is not null,
                    componentFixtureOnly=true,fullProductMemoryProven=false,installedInstanceStartedOrStopped=false},new JsonSerializerOptions{WriteIndented=true}));
                return 0;
            }
            Check("冷启动不构造设置窗口","residency/settings",args.Contains("--eager-settings")||coldSettings is null,"null until requested",coldSettings?.GetType().Name??"null");
            Check("Dock 与信息栏仍构造","residency/settings",Get("_dock") is not null&&Get("_statusBar") is not null,"both",$"dock={Get("_dock") is not null}; status={Get("_statusBar") is not null}");
            Call("InitializePerformanceCoordinator");Call("InitializeNearby");Call("InitializeUpdates");Call("InitializeOnboarding");
            Check("后台初始化无需设置窗口","residency/settings",Get("_settings") is null&&Get("_performance") is not null,"Settings null, performance active",$"settings={Get("_settings") is not null}; performance={Get("_performance") is not null}");
            // These updates happen before a settings window is requested.
            data.Settings.Current.Launcher.Rows=7;
            var status=type.GetMethod("ShowUpdateStatus",instance);status?.Invoke(host,["1.5.3","cached update result",false]);
            Call("ReportInteractionError","cached interaction result");
            Set("_startupRegistrationFailure","cached startup failure");
            data.Settings.Current.Appearance.LauncherTextBrightness=.83;
            Call("OnThemeChanged",null,EventArgs.Empty);
            var ensure=type.GetMethod("EnsureSettingsWindow",instance);
            Check("存在按需创建入口","residency/settings",ensure is not null,"EnsureSettingsWindow",ensure?.Name??"missing");
            var window=(SettingsWindow?)(ensure?.Invoke(host,null)??Get("_settings"))??throw new InvalidOperationException("Settings unavailable");
            Check("首次打开读取当前设置","residency/settings",((Slider)window.FindName("LauncherRowsSlider")).Value==7,"7",((Slider)window.FindName("LauncherRowsSlider")).Value.ToString());
            Check("附近传输和恢复回调完整","residency/settings",window.NearbySettingsApply is not null&&window.OpenNearbyPanel is not null&&window.BeforeDataRestore is not null&&window.AfterDataRestoreFailure is not null,"all bound","checked delegates");
            Check("快捷操作回调完整","residency/settings",window.InteractionSettingsApply is not null&&window.DesktopIconsRead is not null&&window.DesktopIconsSet is not null,"all bound","checked delegates");
            Check("首次打开保留快捷操作状态","residency/settings",((TextBlock)window.FindName("InteractionSettingsStatus")).Text=="cached interaction result","cached interaction result",((TextBlock)window.FindName("InteractionSettingsStatus")).Text);
            Check("首次打开保留更新结果","residency/settings",((TextBlock)window.FindName("UpdateStatusText")).Text=="cached update result","cached update result",((TextBlock)window.FindName("UpdateStatusText")).Text);
            var category=(ListBox)window.FindName("SettingsPageList");
            Call("ShowSettings");Pump(TimeSpan.FromMilliseconds(150));
            Check("首次显示保留开机注册错误","residency/settings",((TextBlock)window.FindName("AutostartStatus")).Text.Contains("cached startup failure"),"cached startup failure",((TextBlock)window.FindName("AutostartStatus")).Text);
            var sleepCheck=(CheckBox)window.FindName("AutoSuspendWhenFullscreenCheck");sleepCheck.IsChecked=false;
            var coordinator=Get("_performance")!;
            Check("性能设置继续通知后台策略","residency/settings",coordinator.GetType().GetProperty("AutoSuspend")!.GetValue(coordinator) is false,"false",coordinator.GetType().GetProperty("AutoSuspend")!.GetValue(coordinator)!.ToString()!);
            status!.Invoke(host,["1.5.3","live update result",false]);
            Check("窗口创建后更新状态继续刷新","residency/settings",((TextBlock)window.FindName("UpdateStatusText")).Text=="live update result","live update result",((TextBlock)window.FindName("UpdateStatusText")).Text);
            category.SelectedItem="附近传输";Pump(TimeSpan.FromMilliseconds(250));
            window.Close();Pump(TimeSpan.FromMilliseconds(80));
            Check("关闭设置只隐藏窗口","residency/settings",!window.IsVisible&&ReferenceEquals(window,Get("_settings")),"hidden and retained",$"visible={window.IsVisible}");
            Call("ShowSettings");Pump(TimeSpan.FromMilliseconds(100));
            Check("重新打开保留选择和实例","residency/settings",ReferenceEquals(window,Get("_settings"))&&Equals(category.SelectedItem,"附近传输"),"same window, same category",category.SelectedItem?.ToString()??"null");
            var launcher=Get("_launcher");
            ((Slider)window.FindName("LauncherRowsSlider")).Value=6;
            var launcherSettings=launcher?.GetType().GetField("_settings",instance)!.GetValue(launcher) as GlassDesk.Core.Models.Settings.LauncherSettings;
            Check("延迟创建后设置仍实时通知启动台","residency/settings",data.Settings.Current.Launcher.Rows==6&&launcherSettings?.Rows==6,"6 in data and launcher",$"data={data.Settings.Current.Launcher.Rows}; launcher={launcherSettings?.Rows}");
            var allocatedBeforeReopen=GC.GetTotalAllocatedBytes(true);
            for(var i=0;i<3;i++){window.Hide();Call("ShowSettings");}
            var reportData=new{passed=_failed==0,cases=Cases,elapsedMs=lifetime.Elapsed.TotalMilliseconds,startupCommitBytes=startupCommit,
                startupCommitDeltaBytes=startupCommit-beforeCommit,startupManagedAllocatedBytes=startupAllocated,
                repeatedOpenManagedAllocatedBytes=GC.GetTotalAllocatedBytes(true)-allocatedBeforeReopen,settingsCreatedAtStartup=coldSettings is not null,
                componentFixtureOnly=true,fullProductMemoryProven=false,installedInstanceStartedOrStopped=false,profile=root};
            File.WriteAllText(report,JsonSerializer.Serialize(reportData,new JsonSerializerOptions{WriteIndented=true}));
            return _failed==0?0:1;
        }
        finally
        {
            Set("_shuttingDown",true);
            foreach(Window window in application.Windows.Cast<Window>().ToArray())window.Close();
            host.Dispose();application.Shutdown();
        }
    }
}
