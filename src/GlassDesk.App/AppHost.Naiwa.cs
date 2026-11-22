using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading.Channels;
using GlassDesk.Core.Models.Settings;

namespace GlassDesk.App;

internal sealed partial class AppHost
{
    private const string NaiwaComponentMissing = "当前安装缺少黄色回收站组件，请安装支持素材导入的新版 GlassDesk。";
    private const string NaiwaAssetsMissing = "请先点击「导入素材包」，选择官方 naiwa-pet-v2.5.1-portable.zip，无需解压。";

    private static string NaiwaExecutable => Path.Combine(AppContext.BaseDirectory, "components", "Naiwa", "GlassDesk.Naiwa.exe");
    private string NaiwaRoot => Path.Combine(_data!.Paths.RootDirectory, "naiwa");
    private string NaiwaImportedAssets => Path.Combine(NaiwaRoot, "assets-v2.5.1");
    private string NaiwaStateDirectory => Path.Combine(NaiwaRoot, "state");
    private NaiwaSession? _naiwa;
    private bool _naiwaExitedThisSession, _naiwaWasEnabled;
    private string? _naiwaStatus;

    private void LoadNaiwaOptions()
    {
        var settings = _data!.Settings.Current;
        if (settings.NaiwaOptions is not null) return;

        var legacy = Path.Combine(NaiwaStateDirectory, "settings.ini");
        try
        {
            settings.NaiwaOptions = File.Exists(legacy) ? NaiwaOptions.FromLegacyIni(File.ReadLines(legacy)) : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            settings.NaiwaOptions = new();
        }
    }

    private string? RunNaiwaAction(string action)
    {
        if (_naiwa is not { } session) return "黄色回收站当前未运行。";
        if (action == "hungry")
            return session.SendAction("hungry") ? null : "黄色回收站正在启动，请稍后重试。";
        if (action != "stop") return "不支持的回收站操作。";

        OnNaiwaEnded(session, null);
        return null;
    }

    private string? GetNaiwaSetupIssue() => !File.Exists(NaiwaExecutable) ? NaiwaComponentMissing
        : !NaiwaAssets.AreReady(NaiwaImportedAssets) ? NaiwaAssetsMissing : null;

    private Task<string?> PrepareNaiwaAsync()
    {
        var error = GetNaiwaSetupIssue();
        if (error is null) SetNaiwaStatus(null);
        return Task.FromResult(error);
    }

    private async Task<string?> ImportNaiwaAssetsAsync(string archivePath)
    {
        try
        {
            await NaiwaAssets.ImportAsync(archivePath, NaiwaImportedAssets);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return "导入素材包失败：" + ex.Message;
        }

        if (!File.Exists(NaiwaExecutable)) return "素材包已导入。" + NaiwaComponentMissing;
        SetNaiwaStatus(null);
        if (_data?.Settings.Current.Appearance.NaiwaEnabled == true && _naiwa is null)
        {
            _naiwaExitedThisSession = false;
            ReconcileNaiwa();
        }
        return _naiwaStatus;
    }

    private async Task<(bool AssetsReady, string Status)> RemoveNaiwaAssetsAsync()
    {
        var directory = NaiwaImportedAssets;
        var wasExited = _naiwaExitedThisSession;
        string? error = null;
        try
        {
            // Keep reconciliation from reopening the imported files during removal.
            _naiwaExitedThisSession = true;
            StopNaiwa();
            await Task.Run(() => NaiwaAssets.RemoveImportedFiles(directory));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            error = "移除素材包失败：" + ex.Message;
        }

        var assetsReady = NaiwaAssets.AreReady(directory);
        if (assetsReady)
        {
            _naiwaExitedThisSession = wasExited;
            ReconcileNaiwa();
        }

        return (assetsReady, error ?? "素材包已移除，重新启用前请先导入素材包。");
    }

    private void ReconcileNaiwa()
    {
        if (_shuttingDown || _disposed || _data is null || _performance is null) return;
        var enabled = _data.Settings.Current.Appearance.NaiwaEnabled;
        if (!enabled)
        {
            if (!_naiwaWasEnabled) return;
            _naiwaWasEnabled = _naiwaExitedThisSession = false;
            StopNaiwa();
            SetNaiwaStatus(null);
            return;
        }

        _naiwaWasEnabled = true;
        var sleeping = _performance.IsSuppressionRequested;
        var options = _data.Settings.Current.NaiwaOptions!;
        if (_naiwa is { } current)
        {
            current.Update(sleeping, options);
            return;
        }
        if (_naiwaExitedThisSession || sleeping) return;

        var assets = NaiwaImportedAssets;
        var componentAvailable = File.Exists(NaiwaExecutable);
        if (!componentAvailable || !NaiwaAssets.AreReady(assets))
        {
            _naiwaExitedThisSession = true;
            SetNaiwaStatus(componentAvailable ? NaiwaAssetsMissing : NaiwaComponentMissing);
            return;
        }

        try
        {
            _naiwa = new NaiwaSession(NaiwaExecutable, assets, NaiwaStateDirectory, options,
                (session, error) => _application.Dispatcher.BeginInvoke(() => OnNaiwaEnded(session, error)));
            SetNaiwaStatus(null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _naiwaExitedThisSession = true;
            SetNaiwaStatus("奶蛙启动失败：" + ex.Message);
        }
    }

    private void OnNaiwaEnded(NaiwaSession session, string? error)
    {
        if (!ReferenceEquals(_naiwa, session)) return;
        _naiwaExitedThisSession = true;
        StopNaiwa();
        SetNaiwaStatus(error ?? "黄色回收站本次已退出。重新启用或重启 GlassDesk 可恢复。");
    }

    private void StopNaiwa()
    {
        var session = _naiwa;
        _naiwa = null;
        session?.Dispose();
    }

    private void SetNaiwaStatus(string? status)
    {
        _naiwaStatus = status;
        _settings?.ShowNaiwaStatus(status);
    }

    private sealed class NaiwaSession : IDisposable
    {
        private readonly NamedPipeServerStream _pipe;
        private readonly Process _process;
        private readonly Microsoft.Win32.SafeHandles.SafeFileHandle _job;
        private readonly Lock _stateGate = new();
        private readonly Channel<string> _commands = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        private bool _sleeping, _ready;
        private NaiwaOptions _options;

        internal NaiwaSession(string executable, string assets, string state, NaiwaOptions options, Action<NaiwaSession, string?> ended)
        {
            _options = options;
            var pipeName = "GlassDesk.Naiwa." + Guid.NewGuid().ToString("N");
            _pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                _job = NaiwaProcessJob.Create();
                var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
                foreach (var argument in new[] { "--managed", pipeName, assets, state }) start.ArgumentList.Add(argument);
                _process = Process.Start(start) ?? throw new IOException("未能创建奶蛙进程。");
                try { NaiwaProcessJob.Assign(_job, _process.SafeHandle); }
                catch
                {
                    // The child has received no startup state and exits when the pipe closes.
                    _process.Dispose();
                    throw;
                }
            }
            catch
            {
                _pipe.Dispose();
                _job?.Dispose();
                throw;
            }
            _ = ConnectAsync(ended);
        }

        internal void Update(bool sleeping, NaiwaOptions options)
        {
            lock (_stateGate)
            {
                if (_sleeping != sleeping)
                {
                    _sleeping = sleeping;
                    if (_ready) _commands.Writer.TryWrite(sleeping ? "sleep" : "wake");
                }
                if (!ReferenceEquals(_options, options))
                {
                    _options = options;
                    if (_ready) _commands.Writer.TryWrite(options.ToPipeCommand());
                }
            }
        }

        internal bool SendAction(string action)
        {
            lock (_stateGate) return _ready && _commands.Writer.TryWrite(action);
        }

        private async Task ConnectAsync(Action<NaiwaSession, string?> ended)
        {
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                await _pipe.WaitForConnectionAsync(deadline.Token).ConfigureAwait(false);
                using var reader = new StreamReader(_pipe, leaveOpen: true);
                await using var writer = new StreamWriter(_pipe, leaveOpen: true) { AutoFlush = true };
                bool initial;
                lock (_stateGate) initial = _sleeping;
                await writer.WriteLineAsync(initial ? "sleep" : "wake").ConfigureAwait(false);
                if (await reader.ReadLineAsync(deadline.Token).ConfigureAwait(false) != "ready")
                    throw new IOException("奶蛙未完成启动。");
                // Read the newest visibility and preferences together before first presentation.
                string options;
                lock (_stateGate)
                {
                    initial = _sleeping;
                    options = _options.ToPipeCommand();
                    _ready = true;
                }
                await writer.WriteLineAsync(initial ? "sleep" : "wake").ConfigureAwait(false);
                await writer.WriteLineAsync(options).ConfigureAwait(false);
                // Watch EOF alongside subsequent commands.
                var eof = reader.ReadLineAsync();
                while (true)
                {
                    var update = _commands.Reader.WaitToReadAsync().AsTask();
                    if (await Task.WhenAny(eof, update).ConfigureAwait(false) == eof
                        || !await update.ConfigureAwait(false)) break;
                    if (_commands.Reader.TryRead(out var command))
                        await writer.WriteLineAsync(command).ConfigureAwait(false);
                }
                ended(this, null);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                ended(this, "奶蛙进程连接中断：" + ex.Message);
            }
        }

        public void Dispose()
        {
            _commands.Writer.TryComplete();
            _pipe.Dispose();
            try
            {
                if (!_process.WaitForExit(2000))
                {
                    _process.Kill();
                    _process.WaitForExit(2000);
                }
            }
            catch (InvalidOperationException) when (_process.HasExited) { }
            finally { _job.Dispose(); _process.Dispose(); }
        }
    }
}
