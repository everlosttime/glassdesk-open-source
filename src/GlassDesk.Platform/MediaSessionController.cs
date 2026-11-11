using Windows.Media.Control;
using Windows.Storage.Streams;

namespace GlassDesk.Platform;

public readonly record struct MediaSessionSnapshot(
    bool Available,
    string Title,
    string Artist,
    bool IsPlaying,
    bool CanGoPrevious,
    bool CanTogglePlayPause,
    bool CanGoNext,
    byte[]? Artwork);

/// <summary>封装 Windows 全局媒体会话，让玻璃胶囊无需了解 WinRT 细节。</summary>
public sealed class MediaSessionController : IDisposable
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private bool _disposed;
    private bool _usingAudioFallback;
    private MediaSessionSnapshot? _lastSnapshot;
    private string? _lastSourceAppUserModelId;

    public string LastDiagnostic { get; private set; } = "尚未读取媒体会话";

    public async Task<MediaSessionSnapshot> ReadAsync()
    {
        try
        {
            var session = await GetCurrentSessionAsync();
            if (session is null)
            {
                return ReadAudioFallback();
            }
            _usingAudioFallback = false;
            var properties = await session.TryGetMediaPropertiesAsync();
            var playback = session.GetPlaybackInfo();
            var controls = playback.Controls;
            var artwork = await ReadArtworkAsync(properties.Thumbnail);
            LastDiagnostic = $"已读取媒体会话：{session.SourceAppUserModelId}";
            var snapshot = new MediaSessionSnapshot(
                true,
                string.IsNullOrWhiteSpace(properties.Title) ? "正在播放" : properties.Title,
                string.IsNullOrWhiteSpace(properties.Artist) ? session.SourceAppUserModelId : properties.Artist,
                playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                controls.IsPreviousEnabled,
                controls.IsPlayPauseToggleEnabled || controls.IsPlayEnabled || controls.IsPauseEnabled,
                controls.IsNextEnabled,
                artwork);
            _lastSnapshot = snapshot;
            _lastSourceAppUserModelId = session.SourceAppUserModelId;
            return snapshot;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException
            or PlatformNotSupportedException or TypeLoadException or System.Runtime.InteropServices.COMException)
        {
            LastDiagnostic = $"{ex.GetType().Name} (0x{ex.HResult:X8})：{ex.Message}";
            return ReadAudioFallback();
        }
    }

    private MediaSessionSnapshot ReadAudioFallback()
    {
        var audio = ActiveAudioApplicationReader.Read();
        if (!audio.Available)
        {
            _usingAudioFallback = false;
            if (_lastSnapshot is { Available: true } remembered)
            {
                // Some players remove their GSMTC session after being paused for a
                // while. Keep the last useful controls: a global media key can wake
                // that player and the real session will be selected when it returns.
                var retained = remembered with { IsPlaying = false, CanTogglePlayPause = true };
                _lastSnapshot = retained;
                LastDiagnostic = _manager is null
                    ? "媒体会话管理器暂不可用；已保留上次媒体，可用系统媒体键恢复"
                    : "Windows 暂未返回媒体会话；已保留上次媒体，可用系统媒体键恢复";
                return retained;
            }
            LastDiagnostic = _manager is null ? "媒体会话管理器不可用，且没有活动音频应用" : "Windows 未返回媒体会话，且没有活动音频应用";
            return default;
        }

        _usingAudioFallback = true;
        LastDiagnostic = $"兼容模式：正在发声的应用为 {audio.ApplicationName} ({audio.ProcessId})";
        var snapshot = new MediaSessionSnapshot(
            true,
            audio.Title,
            $"{audio.ApplicationName} · 兼容模式",
            true,
            true,
            true,
            true,
            null);
        _lastSnapshot = snapshot;
        _lastSourceAppUserModelId = null;
        return snapshot;
    }

    private static async Task<byte[]?> ReadArtworkAsync(IRandomAccessStreamReference? reference)
    {
        if (reference is null) return null;
        try
        {
            using var stream = await reference.OpenReadAsync();
            if (stream.Size is 0 or > 4_194_304) return null;
            var length = checked((uint)stream.Size);
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            var loaded = await reader.LoadAsync(length);
            if (loaded == 0) return null;
            var bytes = new byte[loaded];
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch (Exception ex) when (ex is InvalidOperationException or OverflowException
            or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    public Task<bool> PreviousAsync() => InvokeAsync(
        session => session.TrySkipPreviousAsync(), MediaKeyCommand.Previous);

    public Task<bool> TogglePlayPauseAsync() => InvokeAsync(
        session => session.TryTogglePlayPauseAsync(), MediaKeyCommand.PlayPause);

    public Task<bool> NextAsync() => InvokeAsync(
        session => session.TrySkipNextAsync(), MediaKeyCommand.Next);

    private async Task<GlobalSystemMediaTransportControlsSession?> GetCurrentSessionAsync()
    {
        if (_disposed) return null;
        _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        var current = _manager.GetCurrentSession();
        if (current is not null && MediaSourceFilter.IsExcluded(current.SourceAppUserModelId)) current = null;
        var sessions = _manager.GetSessions()
            .Where(session => !MediaSourceFilter.IsExcluded(session.SourceAppUserModelId)).ToArray();
        foreach (var session in sessions)
        {
            try
            {
                if (session.GetPlaybackInfo().PlaybackStatus
                    == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                    return session;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // A session may disappear while its source app switches tracks.
            }
        }

        if (!string.IsNullOrWhiteSpace(_lastSourceAppUserModelId))
        {
            foreach (var session in sessions)
            {
                if (string.Equals(session.SourceAppUserModelId, _lastSourceAppUserModelId,
                    StringComparison.OrdinalIgnoreCase)) return session;
            }
        }

        return current ?? sessions.FirstOrDefault();
    }

    private async Task<bool> InvokeAsync(
        Func<GlobalSystemMediaTransportControlsSession, Windows.Foundation.IAsyncOperation<bool>> action,
        MediaKeyCommand fallbackCommand)
    {
        try
        {
            var session = await GetCurrentSessionAsync();
            if (session is not null)
            {
                _usingAudioFallback = false;
                if (await action(session)) return true;
            }

            var canResumeRememberedMedia = _lastSnapshot is { Available: true };
            var sent = (_usingAudioFallback || canResumeRememberedMedia) && MediaKeySender.Send(fallbackCommand);
            if (sent && fallbackCommand == MediaKeyCommand.PlayPause && _lastSnapshot is { } remembered)
                _lastSnapshot = remembered with { IsPlaying = !remembered.IsPlaying, CanTogglePlayPause = true };
            return sent;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException
            or PlatformNotSupportedException or TypeLoadException or System.Runtime.InteropServices.COMException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _manager = null;
        _lastSnapshot = null;
        _lastSourceAppUserModelId = null;
    }
}
