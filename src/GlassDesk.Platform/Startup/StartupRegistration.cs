using System.Security;
using Microsoft.Win32;

namespace GlassDesk.Platform.Startup;

/// <summary>当前用户登录启动项相对本产品的状态。</summary>
public enum StartupRegistrationState
{
    /// <summary>产品值不存在。</summary>
    Missing,

    /// <summary>产品值精确指向当前可执行文件及 --autostart。</summary>
    Owned,

    /// <summary>同名值存在，但不属于当前可执行文件。</summary>
    OtherTarget,

    /// <summary>无法读取或校验注册状态。</summary>
    Unavailable,
}

/// <summary>当前用户登录启动项的可读状态。</summary>
public sealed record StartupRegistrationStatus(
    StartupRegistrationState State,
    string? TargetPath,
    string? Error);

/// <summary>修改当前用户登录启动项后的实际结果。</summary>
public sealed record StartupChangeResult(
    bool Succeeded,
    StartupRegistrationStatus Status,
    string? Error);

/// <summary>管理当前用户登录启动计划任务，并兼容旧 HKCU Run 登记。</summary>
public sealed partial class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const int MaximumCommandLineLength = 260;
    private const string AutostartArgument = "--autostart";

    /// <summary>GlassDesk 的当前用户 Run 值名，与原安装登记保持一致。</summary>
    public const string ValueName = "GlassDesk.Independent";

    private readonly Func<bool, RegistryKey?> _openRunKey;
    private readonly string _valueName;
    private readonly bool _ownsOpenedKeys;
    private readonly ScheduledStartupRegistration? _scheduled;

    /// <summary>创建当前用户登录任务及旧 Run 项的生产服务。</summary>
    public StartupRegistration()
        : this(OpenCurrentUserRunKey, ValueName, ownsOpenedKeys: true)
    {
        _scheduled = new ScheduledStartupRegistration();
    }

    /// <summary>为测试注入一个独立注册表键；服务不会处置该键。</summary>
    internal StartupRegistration(RegistryKey runKey, string valueName)
        : this(_ => runKey, valueName, ownsOpenedKeys: false)
    {
        ArgumentNullException.ThrowIfNull(runKey);
    }

    internal StartupRegistration(Func<bool, RegistryKey?> openRunKey, string valueName)
        : this(openRunKey, valueName, ownsOpenedKeys: true)
    {
    }

    private StartupRegistration(Func<bool, RegistryKey?> openRunKey, string valueName, bool ownsOpenedKeys)
    {
        ArgumentNullException.ThrowIfNull(openRunKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(valueName);
        _openRunKey = openRunKey;
        _valueName = valueName;
        _ownsOpenedKeys = ownsOpenedKeys;
    }

    /// <summary>读取注册表的当前状态；不会创建 Run 键或写入设置。</summary>
    private StartupRegistrationStatus QueryLegacy(string executablePath)
    {
        if (!TryCreateCommand(executablePath, out var fullPath, out var command, out var error))
            return new(StartupRegistrationState.Unavailable, null, error);

        return ReadStatus(fullPath, command);
    }

    /// <summary>显式注册或撤销本产品的当前用户登录启动项。</summary>
    private StartupChangeResult SetLegacyEnabled(string executablePath, bool enabled)
    {
        if (!TryCreateCommand(executablePath, out var fullPath, out var command, out var validationError))
        {
            var unavailable = new StartupRegistrationStatus(StartupRegistrationState.Unavailable, null, validationError);
            return new(false, unavailable, validationError);
        }

        var before = ReadStatus(fullPath, command);
        if (before.State == StartupRegistrationState.Unavailable)
            return new(false, before, before.Error);

        if (enabled && before.State == StartupRegistrationState.Owned)
            return new(true, before, null);
        if (!enabled && before.State == StartupRegistrationState.Missing)
            return new(true, before, null);
        if (before.State == StartupRegistrationState.OtherTarget)
        {
            var conflict = "同名登录启动项指向其它文件；为保护该项，GlassDesk 没有修改它。";
            return new(false, before with { Error = conflict }, conflict);
        }

        RegistryKey? key = null;
        try
        {
            key = _openRunKey(true);
            if (key is null)
                throw new IOException("无法打开当前用户的登录启动注册表项。");

            var current = ReadStatusFromKey(key, fullPath, command);
            if (enabled && current.State == StartupRegistrationState.Owned)
                return new(true, current, null);
            if (!enabled && current.State == StartupRegistrationState.Missing)
                return new(true, current, null);
            if (current.State == StartupRegistrationState.OtherTarget)
            {
                var conflict = "登录启动项已被其它位置修改；为保护该项，GlassDesk 没有覆盖或删除它。";
                return new(false, current with { Error = conflict }, conflict);
            }
            if (current.State == StartupRegistrationState.Unavailable)
                return new(false, current, current.Error);

            if (enabled)
                key.SetValue(_valueName, command, RegistryValueKind.String);
            else
                key.DeleteValue(_valueName, throwOnMissingValue: false);

            var after = ReadStatus(fullPath, command);
            var expectedState = enabled ? StartupRegistrationState.Owned : StartupRegistrationState.Missing;
            if (after.State == expectedState)
                return new(true, after, null);

            var mismatch = after.Error ?? "登录启动项写入后核验未达到预期状态。";
            if (enabled && before.State == StartupRegistrationState.Missing)
                TryRemoveOwnedValue(command);
            var final = ReadStatus(fullPath, command);
            return new(false, final with { Error = mismatch }, mismatch);
        }
        catch (Exception ex) when (IsRegistryFailure(ex))
        {
            if (enabled && before.State == StartupRegistrationState.Missing)
                TryRemoveOwnedValue(command);

            var current = ReadStatus(fullPath, command);
            if (enabled && current.State == StartupRegistrationState.Owned)
            {
                TryRemoveOwnedValue(command);
                current = ReadStatus(fullPath, command);
            }

            var message = ToUserError(ex);
            return new(false, current with { Error = message }, message);
        }
        finally
        {
            if (_ownsOpenedKeys) key?.Dispose();
        }
    }

    private StartupRegistrationStatus ReadStatus(string fullPath, string expectedCommand)
    {
        RegistryKey? key = null;
        try
        {
            key = _openRunKey(false);
            return key is null
                ? new(StartupRegistrationState.Missing, fullPath, null)
                : ReadStatusFromKey(key, fullPath, expectedCommand);
        }
        catch (Exception ex) when (IsRegistryFailure(ex))
        {
            return new(StartupRegistrationState.Unavailable, fullPath, ToUserError(ex));
        }
        finally
        {
            if (_ownsOpenedKeys) key?.Dispose();
        }
    }

    private StartupRegistrationStatus ReadStatusFromKey(RegistryKey key, string fullPath, string expectedCommand)
    {
        if (!key.GetValueNames().Contains(_valueName, StringComparer.OrdinalIgnoreCase))
            return new(StartupRegistrationState.Missing, fullPath, null);

        if (key.GetValueKind(_valueName) != RegistryValueKind.String)
            return new(StartupRegistrationState.OtherTarget, null, "同名登录启动项不是普通字符串值，GlassDesk 不会更改它。");

        var stored = key.GetValue(_valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        return string.Equals(stored, expectedCommand, StringComparison.OrdinalIgnoreCase)
            ? new(StartupRegistrationState.Owned, fullPath, null)
            : new(StartupRegistrationState.OtherTarget, null, "同名登录启动项指向其它文件或包含其它参数。");
    }

    private void TryRemoveOwnedValue(string expectedCommand)
    {
        RegistryKey? key = null;
        try
        {
            key = _openRunKey(true);
            if (key is null) return;
            var status = ReadStatusFromKey(key, string.Empty, expectedCommand);
            if (status.State == StartupRegistrationState.Owned)
                key.DeleteValue(_valueName, throwOnMissingValue: false);
        }
        catch (Exception ex) when (IsRegistryFailure(ex))
        {
            // Preserve the original write failure; the caller re-queries and reports the actual state.
        }
        finally
        {
            if (_ownsOpenedKeys) key?.Dispose();
        }
    }

    private static RegistryKey? OpenCurrentUserRunKey(bool writable) => writable
        ? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
        : Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);

    private static bool TryCreateCommand(string executablePath, out string fullPath, out string command, out string? error)
    {
        fullPath = string.Empty;
        command = string.Empty;
        error = null;

        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
            if (!Path.IsPathFullyQualified(executablePath))
                throw new ArgumentException("必须提供 GlassDesk.exe 的完整路径。", nameof(executablePath));

            fullPath = Path.GetFullPath(executablePath);
            if (fullPath.Contains('"'))
                throw new ArgumentException("程序路径包含无效引号。", nameof(executablePath));
            if (!string.Equals(Path.GetExtension(fullPath), ".exe", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("登录启动目标必须是 .exe 文件。", nameof(executablePath));

            command = $"\"{fullPath}\" {AutostartArgument}";
            if (command.Length > MaximumCommandLineLength)
                throw new ArgumentException($"登录启动命令不能超过 {MaximumCommandLineLength} 个字符。", nameof(executablePath));
            if (!File.Exists(fullPath))
                throw new FileNotFoundException("找不到 GlassDesk 可执行文件。", fullPath);

            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or SecurityException)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool IsRegistryFailure(Exception ex) => ex is IOException or UnauthorizedAccessException
        or SecurityException or ArgumentException or InvalidOperationException or ObjectDisposedException;

    private static string ToUserError(Exception ex) => ex switch
    {
        UnauthorizedAccessException or SecurityException => "没有权限修改当前用户的登录启动项。" + Environment.NewLine + ex.Message,
        _ => "读取或修改当前用户的登录启动项失败：" + ex.Message,
    };
}
