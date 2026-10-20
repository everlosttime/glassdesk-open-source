using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows.Threading;
using GlassDesk.App.Updates;
using GlassDesk.AppServices.Updates;
using GlassDesk.Platform.Installation;

namespace GlassDesk.App;

internal sealed partial class AppHost
{
    private readonly CancellationTokenSource _updateLifetime = new();
    private readonly HttpClient _updateHttp = new() { Timeout = Timeout.InfiniteTimeSpan };
    private UpdateWindow? _updateWindow;
    private bool _checkingUpdate;

    internal static Version ProductVersion => Version.TryParse(
        typeof(AppHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0].Split('-')[0], out var version)
        ? new Version(version.Major, version.Minor, Math.Max(0, version.Build)) : new Version(0, 2, 9);

    private void InitializeUpdates()
    {
        _settings!.CheckForUpdatesRequested += async (_, _) => await CheckUpdatesAsync(manual: true);
        _settings.ShowUpdateStatus(ProductVersion.ToString(), "启动时会自动检查新版本，也可以手动检查。");
        // Only the installed normal application contacts the service automatically; build/acceptance copies do not.
        if (_options.Sample == Startup.AppearanceSample.None && new InstallationLocator().Inspect(Environment.ProcessPath ?? string.Empty).State == InstallationState.VerifiedInstalled)
            _application.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () => await CheckUpdatesAsync(manual: false)));
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
        if (_updateWindow is not null) { if (manual) _updateWindow.Activate(); return; }
        _checkingUpdate = true;
        _settings?.ShowUpdateStatus(ProductVersion.ToString(), "正在检查更新…", checking: true);
        try
        {
            var client = CreateUpdateClient();
            var release = await client.CheckAsync(ProductVersion, _updateLifetime.Token);
            if (_shuttingDown || _updateLifetime.IsCancellationRequested) return;
            if (release is null)
            {
                _settings?.ShowUpdateStatus(ProductVersion.ToString(), "当前已是最新正式版本。");
                return;
            }
            _settings?.ShowUpdateStatus(ProductVersion.ToString(), $"发现新版本 {release.Version}。");
            var installation = new InstallationLocator().Inspect(Environment.ProcessPath ?? string.Empty);
            _updateWindow = new UpdateWindow(release, installation.State == InstallationState.VerifiedInstalled,
                (progress, token) => InstallUpdateAsync(client, release, progress, token));
            _updateWindow.Icon = _settings?.Icon;
            _updateWindow.Closed += (_, _) => _updateWindow = null;
            _updateWindow.Show();
        }
        catch (OperationCanceledException)
        {
            if (!_shuttingDown) _settings?.ShowUpdateStatus(ProductVersion.ToString(), "检查更新已取消或超时，请稍后重试。");
        }
        catch (Exception ex)
        {
            if (!_shuttingDown) _settings?.ShowUpdateStatus(ProductVersion.ToString(), "无法检查更新，请确认网络连接后重试。" + (manual ? "\n" + ex.Message : string.Empty));
        }
        finally { _checkingUpdate = false; }
    }

    private async Task InstallUpdateAsync(UpdateClient client, UpdateRelease release, IProgress<int> progress, CancellationToken token)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, _updateLifetime.Token);
        var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlassDesk", "Updates");
        var file = await client.DownloadAsync(release, cache, progress, cancellation.Token);
        var launched = false;
        try
        {
            var installation = new InstallationLocator().Inspect(Environment.ProcessPath ?? string.Empty);
            var start = await UpdateInstaller.PrepareAsync(release, file, installation, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            using var process = Process.Start(start) ?? throw new InvalidOperationException("安装程序未能启动。");
            launched = true;
            _updateWindow?.MarkInstalling();
            ExitApplication();
        }
        finally
        {
            if (!launched)
            {
                try { Directory.Delete(Path.GetDirectoryName(file)!, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private void DisposeUpdates()
    {
        _updateLifetime.Cancel();
        _updateWindow?.Close();
        _updateHttp.Dispose();
        _updateLifetime.Dispose();
    }
}
