using System.IO;
using System.Windows;
using System.Windows.Threading;
using GlassDesk.AppServices;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Storage;
using GlassDesk.Dock;
using GlassDesk.Settings;
using GlassDesk.UI.Settings;
using GlassDesk.UI.Theming;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length != 0) return;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var logPath = Path.Combine(AppContext.BaseDirectory, "probe-ui.log");
        app.DispatcherUnhandledException += (_, e) => File.AppendAllText(logPath, e.Exception.ToString() + "\n");
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/GlassDesk.UI;component/Theming/LiquidGlassTheme.xaml") });
        ThemeService.Initialize(Dispatcher.CurrentDispatcher).Update(new AppearanceSettings { GlassMaterial = GlassDesk.Core.Models.GlassMaterialMode.PlainGlass });
        var root = Path.Combine(AppContext.BaseDirectory, "ProbeData");
        var data = new GlassDeskDataStore(new AppPaths(root)); data.LoadAll();
        data.Settings.Current.Nearby.ReceiveDirectory = Path.Combine(AppContext.BaseDirectory, "ReceivedFixtures");
        using var services = new AppServiceHost(data);
        var settings = new SettingsWindow(); settings.AttachDataStore(data, null);
        var panel = new NearbyTransferWindow(services.Nearby, async () =>
        {
            data.Settings.Current.Nearby.Enabled = true; data.Settings.Save();
            await services.Nearby.StartAsync(data.Settings.Current.Nearby);
        }, () => { settings.Show(); settings.Activate(); });
        settings.OpenNearbyPanel = () => { panel.Show(); panel.Activate(); };
        settings.NearbySettingsApply = async next =>
        {
            try { await services.Nearby.StopAsync(); if (next.Enabled) await services.Nearby.StartAsync(next); return null; }
            catch (Exception error) { return error.Message; }
        };
        NearbyIncomingNotice? notice = null;
        string? requestId = null;
        services.Nearby.Changed += (_, _) => app.Dispatcher.BeginInvoke(new Action(() =>
        {
            var state = services.Nearby.State;
            File.AppendAllText(logPath, $"{DateTimeOffset.UtcNow:o} running={state.Running} incoming={state.Incoming is not null} receive={state.Receiving?.State} panel={panel.IsVisible}\n");
            settings.UpdateNearbyStatus(state.Running ? "后台正在待命" : "已关闭");
            if (state.Incoming is { } incoming && incoming.Id != requestId)
            {
                requestId = incoming.Id; notice?.Close();
                notice = new NearbyIncomingNotice(incoming, () => { panel.Show(); panel.Activate(); panel.ShowIncomingRequest(); }); notice.Show();
            }
            else if (state.Incoming is null) { notice?.Close(); notice = null; requestId = null; }
        }));
        var deadline = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
        deadline.Tick += (_, _) => app.Shutdown(); deadline.Start();
        app.Exit += (_, _) => { deadline.Stop(); services.Nearby.StopAsync().GetAwaiter().GetResult(); };
        panel.Show(); app.Run();
    }
}
