using Microsoft.Win32;

namespace GlassDesk.Platform.Startup;

public sealed partial class StartupRegistration
{
    /// <summary>Only an explicit user action may replace a recognizable registration for another installation.</summary>
    public bool CanUseCurrentInstallation(string executablePath)
    {
        if (!TryCreateCommand(executablePath, out var path, out _, out _)) return false;
        try
        {
            var legacy = ReadLegacyCommand();
            if (legacy is not null && !IsSameAutostartCommand(legacy, path)) return false;
            return _scheduled is null || _scheduled.Query(path, allowOtherInstallation: true).State
                is StartupRegistrationState.Missing or StartupRegistrationState.Owned;
        }
        catch (Exception error) when (IsMigrationFailure(error)) { return false; }
    }

    public StartupChangeResult SetEnabledForCurrentInstallation(string executablePath)
    {
        if (!TryCreateCommand(executablePath, out var path, out var command, out var validation))
            return new(false, new(StartupRegistrationState.Unavailable, executablePath, validation), validation);
        if (!CanUseCurrentInstallation(path))
            return new(false, Query(path), "现有自启动登记不属于可识别的 GlassDesk 副本，未修改它。");
        string? previousTask = null, createdTask = null;
        try
        {
            var legacy = ReadLegacyCommand();
            if (legacy is not null && !IsSameAutostartCommand(legacy, path))
                throw new InvalidOperationException("自启动登记已被其它程序更改。");
            if (_scheduled is null) return ReplaceLegacyCommand(path, command, legacy);
            previousTask = _scheduled.CaptureXml();
            var registered = _scheduled.SetEnabled(path, true, allowOtherInstallation: true);
            if (!registered.Succeeded) return registered;
            createdTask = _scheduled.CaptureXml() ?? throw new InvalidOperationException("新自启动任务未保留。");
            RemoveLegacyCommand(legacy);
            var actual = Query(path);
            if (actual.State != StartupRegistrationState.Owned) throw new InvalidOperationException("自启动写入后核验失败。");
            return new(true, actual, null);
        }
        catch (Exception error) when (IsMigrationFailure(error))
        {
            if (_scheduled is not null && createdTask is not null)
            {
                try { _scheduled.RestoreSnapshot(previousTask, createdTask); }
                catch (Exception rollback) when (IsMigrationFailure(rollback)) { }
            }
            return new(false, Query(path), "未能切换开机自启动路径：" + error.Message);
        }
    }

    internal static bool IsSameAutostartExecutable(string? candidate, string current)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(candidate) && Path.IsPathFullyQualified(candidate)
                && string.Equals(Path.GetFileName(candidate), Path.GetFileName(current), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException) { return false; }
    }

    private static bool IsSameAutostartCommand(string command, string current)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            return end > 1 && command[(end + 1)..].Trim() == AutostartArgument
                && IsSameAutostartExecutable(command[1..end], current);
        }
        var suffix = " " + AutostartArgument;
        if (!command.EndsWith(suffix, StringComparison.Ordinal)) return false;
        var path = command[..^suffix.Length];
        return !path.Any(char.IsWhiteSpace) && IsSameAutostartExecutable(path, current);
    }

    private string? ReadLegacyCommand()
    {
        RegistryKey? key = null;
        try
        {
            key = _openRunKey(false);
            if (key is null || !key.GetValueNames().Contains(_valueName, StringComparer.OrdinalIgnoreCase)) return null;
            if (key.GetValueKind(_valueName) != RegistryValueKind.String) throw new InvalidOperationException("启动值类型不匹配。");
            return key.GetValue(_valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string
                ?? throw new InvalidOperationException("无法读取启动值。");
        }
        finally { if (_ownsOpenedKeys) key?.Dispose(); }
    }

    private void RemoveLegacyCommand(string? expected)
    {
        if (expected is null)
        {
            if (ReadLegacyCommand() is not null) throw new InvalidOperationException("启动值在切换期间发生变化。");
            return;
        }
        RegistryKey? key = null;
        try
        {
            key = _openRunKey(true) ?? throw new IOException("无法更新启动值。");
            if (key.GetValueKind(_valueName) != RegistryValueKind.String
                || !string.Equals(key.GetValue(_valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string, expected, StringComparison.Ordinal))
                throw new InvalidOperationException("启动值在切换期间发生变化。");
            key.DeleteValue(_valueName, false);
        }
        finally { if (_ownsOpenedKeys) key?.Dispose(); }
    }

    private StartupChangeResult ReplaceLegacyCommand(string path, string command, string? expected)
    {
        RegistryKey? key = null;
        try
        {
            key = _openRunKey(true) ?? throw new IOException("无法更新启动值。");
            var current = key.GetValue(_valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (!Equals(current, expected)) throw new InvalidOperationException("启动值在切换期间发生变化。");
            key.SetValue(_valueName, command, RegistryValueKind.String);
            var actual = QueryLegacy(path);
            if (actual.State != StartupRegistrationState.Owned) throw new IOException("启动值写入后核验失败。");
            return new(true, actual, null);
        }
        catch
        {
            if (key is not null && Equals(key.GetValue(_valueName), command))
            {
                if (expected is null) key.DeleteValue(_valueName, false);
                else key.SetValue(_valueName, expected, RegistryValueKind.String);
            }
            throw;
        }
        finally { if (_ownsOpenedKeys) key?.Dispose(); }
    }

    private static bool IsMigrationFailure(Exception error) => IsRegistryFailure(error)
        || error is System.Runtime.InteropServices.COMException or InvalidOperationException or System.Xml.XmlException;
}
