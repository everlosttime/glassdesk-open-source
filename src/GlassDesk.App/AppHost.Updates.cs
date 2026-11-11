using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows.Threading;
using GlassDesk.App.Updates;
using GlassDesk.AppServices.Updates;
using GlassDesk.Platform.Installation;
using GlassDesk.Settings;

namespace GlassDesk.App;

internal sealed partial class AppHost
{
    private readonly CancellationTokenSource _updateLifetime = new();
    private readonly HttpClient _updateHttp = new() { Timeout = Timeout.InfiniteTimeSpan };
    private CloudDownloadWindow? _updateWindow;
    private DownloadEntryWindow? _downloadEntryWindow;
    private bool _checkingUpdate;
    private (string Version,string Message,bool Checking) _settingsUpdateStatus =
        (ProductVersion.ToString(),"启动时会检查新版本；也可点击“下载入口”，扫描官网二维码。",false);

    internal static Version ProductVersion => Version.TryParse(
        typeof(AppHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0].Split('-')[0], out var version)
        ? new Version(version.Major, version.Minor, Math.Max(0, version.Build)) : new Version(0, 2, 9);

    private void InitializeUpdates()
    {
#if GLASSDESK_COMPATIBILITY_PREVIEW
        ShowUpdateStatus($"{ProductVersion} Win10/Win11 合并测试版",
            "合并测试版：请使用单独提供的测试包更新。", checking: true);
#else
        // Only the installed normal application contacts the service automatically; build/acceptance copies do not.
        if (_options.Sample == Startup.AppearanceSample.None && new InstallationLocator().Inspect(Environment.ProcessPath ?? string.Empty).State == InstallationState.VerifiedInstalled)
            _application.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () => await CheckUpdatesAsync(manual: false)));
#endif
    }

    private void BindSettingsUpdates(SettingsWindow window)
    {
#if !GLASSDESK_COMPATIBILITY_PREVIEW
        window.CheckForUpdatesRequested+=async (_,_)=>await CheckUpdatesAsync(manual:true);
        window.CloudDownloadsRequested+=(_,_)=>ShowDownloadEntry();
#endif
        var status=_settingsUpdateStatus;
        window.ShowUpdateStatus(status.Version,status.Message,status.Checking);
    }

    private void ShowUpdateStatus(string version,string message,bool checking=false)
    {
        _settingsUpdateStatus=(version,message,checking);
        _settings?.ShowUpdateStatus(version,message,checking);
    }

    private UpdateClient CreateUpdateClient()
    {
        const string defaultSource = "https://api.github.com/repos/everlosttime/glassdesk-updates/releases/latest";
        var file = Path.Combine(AppContext.BaseDirectory, "update-source.json");
        var source = defaultSource;
        if (File.Exists(file))
        {
            if (new FileInfo(file).Length > 8192) throw new InvalidDataException("更新源配置过大。");
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            source = document.RootElement.GetProperty("releasesApi").GetString() ?? throw new InvalidDataException("更新源未配置。");
        }
        return new UpdateClient(_updateHttp, new Uri(source));
    }

    private async Task CheckUpdatesAsync(bool manual)
    {
        if (_shuttingDown || _checkingUpdate) return;
        _checkingUpdate = true;
        ShowUpdateStatus(ProductVersion.ToString(), "正在检查更新…", checking: true);
        try
        {
            var client = CreateUpdateClient();
            var release = await client.CheckAsync(ProductVersion, _updateLifetime.Token);
            if (_shuttingDown || _updateLifetime.IsCancellationRequested) return;
            if (release is null)
            {
                ShowUpdateStatus(ProductVersion.ToString(), "当前已是最新正式版本。");
                return;
            }
            ShowUpdateStatus(ProductVersion.ToString(), $"发现新版本 {release.Version}。");
            if (_data!.Settings.Current.ShouldShowUpdateNotice(manual, DateTimeOffset.UtcNow))
                ShowCloudDownloads(release);
        }
        catch (OperationCanceledException)
        {
            if (!_shuttingDown) ShowUpdateStatus(ProductVersion.ToString(), "检查更新已取消或超时，可重试或点击“下载入口”扫描官网二维码。");
        }
        catch (Exception ex)
        {
            if (!_shuttingDown) ShowUpdateStatus(ProductVersion.ToString(), "无法检查更新，仍可点击“下载入口”扫描官网二维码。" + (manual ? "\n" + ex.Message : string.Empty));
        }
        finally { _checkingUpdate = false; }
    }

    private void ShowDownloadEntry()
    {
        if (_shuttingDown) return;
        if (_downloadEntryWindow is not null) { _downloadEntryWindow.Activate(); return; }
        try
        {
            var next = new DownloadEntryWindow { Owner = _settings, Icon = _settings?.Icon ?? CreateWindowIcon() };
            if (_primaryDisplaySession is { } session) next.BindPrimaryDisplay(session);
            next.Closed += (_, _) => { if (ReferenceEquals(_downloadEntryWindow, next)) _downloadEntryWindow = null; };
            _downloadEntryWindow = next;
            next.Show();
        }
        catch (Exception ex)
        {
            _downloadEntryWindow = null;
            ShowUpdateStatus(ProductVersion.ToString(), "无法显示下载入口：" + ex.Message);
        }
    }

    private void ShowCloudDownloads(UpdateNotice? release = null)
    {
        if (_shuttingDown) return;
        if (_updateWindow is not null && release is null) { _updateWindow.Activate(); return; }
        try
        {
            var next = new CloudDownloadWindow(ReadCloudDownloadConfiguration(), release?.Version, release?.Notes,
                snoozeNotice: SnoozeUpdateNotice)
            { Icon = _settings?.Icon ?? CreateWindowIcon() };
            next.Closed += (_, _) => { if (ReferenceEquals(_updateWindow, next)) _updateWindow = null; };
            _updateWindow?.Close();
            _updateWindow = next;
            next.Show();
        }
        catch (Exception ex)
        {
            ShowUpdateStatus(ProductVersion.ToString(), "无法显示网盘下载入口：" + ex.Message);
        }
    }

    private bool SnoozeUpdateNotice()
    {
        var settings = _data!.Settings;
        var previous = settings.Current.UpdateNoticeSnoozedUntilUtc;
        try
        {
            settings.Current.UpdateNoticeSnoozedUntilUtc = DateTimeOffset.UtcNow.AddDays(7);
            settings.Save();
            return true;
        }
        catch
        {
            settings.Current.UpdateNoticeSnoozedUntilUtc = previous;
            return false;
        }
    }

    private static string ReadCloudDownloadConfiguration()
    {
        var file = Path.Combine(AppContext.BaseDirectory, "update-source.json");
        if (File.Exists(file))
        {
            if (new FileInfo(file).Length > 8192) throw new InvalidDataException("网盘下载配置过大。");
            var text = File.ReadAllText(file);
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.TryGetProperty("cloudDownloads", out _)) return text;
        }
        // Older installations may retain the old source file. The bundled links also work offline.
        using var resource = typeof(AppHost).Assembly.GetManifestResourceStream("GlassDesk.UpdateSourceDefaults.json")
            ?? throw new InvalidDataException("未找到网盘下载配置。");
        using var reader = new StreamReader(resource);
        return reader.ReadToEnd();
    }

    private void DisposeUpdates()
    {
        _updateLifetime.Cancel();
        _updateWindow?.Close();
        _downloadEntryWindow?.Close();
        _updateHttp.Dispose();
        _updateLifetime.Dispose();
    }
}
