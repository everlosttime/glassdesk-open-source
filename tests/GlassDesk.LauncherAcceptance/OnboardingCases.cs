using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using GlassDesk.Core.Storage;
using GlassDesk.Platform.Displays;
using GlassDesk.Settings;
using GlassDesk.AppServices;
using GlassDesk.Launcher;
using GlassDesk.UI.Controls;
using GlassDesk.UI.Theming;

internal static partial class Program
{
    /// <summary>验证已加载资料后的真实宿主教学生命周期；不启动托盘、抢单实例锁或接触已安装资料。</summary>
    private static int VerifyOnboardingHost(string[] args)
    {
        var assemblyPath = Path.GetFullPath(Argument(args, "--app-assembly") ?? throw new ArgumentException("--app-assembly required"));
        var report = Path.GetFullPath(Argument(args, "--report") ?? throw new ArgumentException("--report required"));
        var assembly = Assembly.LoadFrom(assemblyPath);
        var type = assembly.GetType("GlassDesk.App.AppHost", true)!;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/GlassDesk;component/Assets/GlassDeskBrandMark.xaml") });
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/GlassDesk.UI;component/Theming/LiquidGlassTheme.xaml") });
        var root = Path.Combine(Path.GetTempPath(), "GlassDesk-Onboarding-Host-" + Guid.NewGuid().ToString("N"));
        var hosts = new List<IDisposable>();
        var windows = new List<SettingsWindow>();
        var launchers = new List<LauncherWindow>();
        try
        {
            var data = new GlassDeskDataStore(new AppPaths(Path.Combine(root, "new")));
            data.LoadAll();
            var first = CreateHost(data);
            var tutorial = Tutorial(first);
            Check("新资料自动弹出教学", "onboarding/host", tutorial?.IsVisible == true, "visible", $"visible={tutorial?.IsVisible}");
            var settings = Settings(first);
            Click(settings, "ReplayOnboardingButton");
            Check("重复打开复用同一教学窗口", "onboarding/host", ReferenceEquals(tutorial, Tutorial(first)), "same window", $"same={ReferenceEquals(tutorial, Tutorial(first))}");
            if (tutorial is null) throw new InvalidOperationException("Tutorial was not scheduled by host.");
            Click(tutorial, "SkipButton");
            Check("跳过后关闭并写入状态", "onboarding/host", Tutorial(first) is null && data.Settings.Current.OnboardingDismissed == true,
                "closed, dismissed", $"closed={Tutorial(first) is null},dismissed={data.Settings.Current.OnboardingDismissed}");

            var reload = new GlassDeskDataStore(data.Paths);
            reload.LoadAll();
            var second = CreateHost(reload);
            Check("重启后不重复弹出", "onboarding/host", Tutorial(second) is null, "no popup", $"popup={Tutorial(second) is not null}");
            Click(Settings(second), "ReplayOnboardingButton");
            var replay = Tutorial(second)!;
            Check("已有资料可从设置重看", "onboarding/host", replay.IsVisible, "visible", $"visible={replay.IsVisible}");
            Check("教学直接进入五项模拟实操", "onboarding/simulation", replay.FindName("ChapterList") is null && replay.FindName("SimulationSurface") is FrameworkElement,
                "simulation desktop without chapter panel", "simulation surface");
            for (var i = 0; i < 4; i++) Click(replay, "NextButton");
            Click(replay, "ControlCenterArrowButton"); Click(replay, "SettingsGearButton");
            Check("信息栏齿轮只打开模拟设置", "onboarding/simulation", ((FrameworkElement)replay.FindName("MockSettings")).IsVisible && !Settings(second).IsVisible,
                "mock settings visible; real settings hidden", $"mock={((FrameworkElement)replay.FindName("MockSettings")).IsVisible},real={Settings(second).IsVisible}");
            var launcher = (LauncherWindow)type.GetField("_launcher", flags)!.GetValue(second)!;
            Check("模拟练习不打开真实启动台", "onboarding/simulation", !launcher.IsVisible,
                "real launcher hidden", $"visible={launcher.IsVisible}");
            Click(replay, "OpenSettingsButton"); Pump(TimeSpan.FromMilliseconds(100));
            var category = (ListBox)Settings(second).FindName("SettingsPageList");
            Check("主动打开真实设置时关闭教学", "onboarding/host", Tutorial(second) is null && Settings(second).IsVisible && category.SelectedItem as string == "关于",
                "closed lesson, real about settings", $"closed={Tutorial(second) is null},settings={Settings(second).IsVisible},page={category.SelectedItem}");
            var legacyPaths = new AppPaths(Path.Combine(root, "legacy"));
            legacyPaths.EnsureCreated();
            File.WriteAllText(legacyPaths.SettingsFile, "{\"schemaVersion\":1}");
            var legacy = new GlassDeskDataStore(legacyPaths);
            legacy.LoadAll();
            var third = CreateHost(legacy);
            Check("升级旧资料不打扰", "onboarding/host", Tutorial(third) is null, "no popup", $"popup={Tutorial(third) is not null}");
            Check("教学不创建用户应用记录", "onboarding/host", data.Library.Items.Count == 0,
                "empty library", data.Library.Items.Count.ToString());
            var disposalData = new GlassDeskDataStore(new AppPaths(Path.Combine(root, "disposal"))); disposalData.LoadAll();
            var disposal = CreateHost(disposalData);
            var closingLesson = Tutorial(disposal)!;
            closingLesson.Close();
            Check("关闭模拟教学释放宿主引用", "onboarding/host", Tutorial(disposal) is null,
                "no lesson", $"lesson={Tutorial(disposal) is not null}");
            Click(Settings(disposal), "ReplayOnboardingButton");
            var shutdownLesson = Tutorial(disposal)!;
            Click(shutdownLesson, "LauncherToggleButton");
            disposal.Dispose();
            Check("宿主退出清理活动模拟教学", "onboarding/host", Tutorial(disposal) is null && !shutdownLesson.IsVisible,
                "closed lesson", $"lesson={Tutorial(disposal) is not null},visible={shutdownLesson.IsVisible}");
            return _failed == 0 ? 0 : 1;
        }
        finally
        {
            foreach (var host in hosts) Tutorial(host)?.Close();
            foreach (var settings in windows) settings.Close();
            foreach (var launcher in launchers) launcher.Close();
            foreach (var host in hosts) host.Dispose();
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            File.WriteAllText(report, JsonSerializer.Serialize(new { scope = "isolated host onboarding lifecycle; not a full AppHost.Start or physical-input test", failed = _failed, cases = Cases }, new JsonSerializerOptions { WriteIndented = true }));
            if (Directory.Exists(root)) Directory.Delete(root, true);
            application.Shutdown();
        }

        IDisposable CreateHost(GlassDeskDataStore data)
        {
            ThemeService.Initialize(application.Dispatcher).Update(data.Settings.Current.Appearance);
            var settings = new SettingsWindow();
            var primary = new PrimaryDisplaySession(DisplayService.Primary() ?? throw new InvalidOperationException("No primary display"));
            settings.BindPrimaryDisplay(primary);
            settings.AttachDataStore(data, null);
            windows.Add(settings);
            var options = assembly.GetType("GlassDesk.App.Startup.CommandLineOptions", true)!.GetMethod("Parse")!.Invoke(null, [Array.Empty<string>()]);
            var host = (IDisposable)Activator.CreateInstance(type, flags, null, [application, options], null)!;
            var services = new AppServiceHost(data);
            var launcher = new LauncherWindow(); launcher.BindPrimaryDisplay(primary); launcher.AttachServices(services, true);
            ((LiquidGlassSurface)launcher.FindName("GlassSurface")).SetPerformanceSleeping(true);
            launchers.Add(launcher);
            hosts.Add(host);
            type.GetField("_data", flags)!.SetValue(host, data);
            type.GetField("_settings", flags)!.SetValue(host, settings);
            type.GetField("_launcher", flags)!.SetValue(host, launcher);
            type.GetField("_services", flags)!.SetValue(host, services);
            type.GetField("_primaryDisplaySession", flags)!.SetValue(host, primary);
            type.GetMethod("InitializeOnboarding", flags)!.Invoke(host, null);
            Pump(TimeSpan.FromMilliseconds(100));
            return host;
        }

        OnboardingWindow? Tutorial(IDisposable host) => (OnboardingWindow?)type.GetField("_onboarding", flags)!.GetValue(host);
        SettingsWindow Settings(IDisposable host) => (SettingsWindow)type.GetField("_settings", flags)!.GetValue(host)!;
        static void Click(Window window, string name) => ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
}
