using Microsoft.Win32;

namespace GlassDesk.Platform.Startup;

public sealed partial class StartupRegistration
{
    internal StartupRegistration(RegistryKey runKey, string valueName, string taskName)
        : this(runKey, valueName)
    {
        _scheduled = new ScheduledStartupRegistration(taskName);
    }

    /// <summary>只读查询计划任务和旧 Run 登记，不隐式启用或迁移。</summary>
    public StartupRegistrationStatus Query(string executablePath)
    {
        var legacy = QueryLegacy(executablePath);
        if (_scheduled is null || legacy.State is StartupRegistrationState.OtherTarget or StartupRegistrationState.Unavailable)
            return legacy;
        var scheduled = _scheduled.Query(Path.GetFullPath(executablePath));
        if (scheduled.State != StartupRegistrationState.Missing) return scheduled;
        return legacy;
    }

    /// <summary>使用无延迟的当前用户登录任务；验证成功后才撤下旧 Run 项。</summary>
    public StartupChangeResult SetEnabled(string executablePath, bool enabled)
    {
        if (_scheduled is null) return SetLegacyEnabled(executablePath, enabled);
        var legacy = QueryLegacy(executablePath);
        if (legacy.State is StartupRegistrationState.OtherTarget or StartupRegistrationState.Unavailable)
            return new(false, legacy, legacy.Error);

        var scheduled = _scheduled.SetEnabled(Path.GetFullPath(executablePath), enabled);
        if (!scheduled.Succeeded) return new(false, Query(executablePath), scheduled.Error);

        // Enabling is transactional in this direction: the old login entry remains
        // usable if task registration fails (including permission/service failures).
        var removedLegacy = SetLegacyEnabled(executablePath, false);
        if (!removedLegacy.Succeeded)
            return new(false, Query(executablePath), removedLegacy.Error);
        var actual = Query(executablePath);
        var expected = enabled ? StartupRegistrationState.Owned : StartupRegistrationState.Missing;
        return new(actual.State == expected, actual,
            actual.State == expected ? null : actual.Error ?? "登录启动登记未达到预期状态。");
    }
}
