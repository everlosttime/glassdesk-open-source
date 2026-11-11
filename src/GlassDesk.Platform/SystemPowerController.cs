using System.Diagnostics;

namespace GlassDesk.Platform;

public enum SystemPowerAction
{
    Shutdown,
    Restart,
}

/// <summary>Requests a Windows power action only after the caller confirms it.</summary>
public sealed class SystemPowerController
{
    private readonly Func<SystemPowerAction, bool> _confirm;
    private readonly Func<ProcessStartInfo, Task> _execute;

    public SystemPowerController(Func<SystemPowerAction, bool> confirm)
        : this(confirm, RunAsync) { }

    internal SystemPowerController(Func<SystemPowerAction, bool> confirm, Func<ProcessStartInfo, Task> execute)
    {
        _confirm = confirm;
        _execute = execute;
    }

    public async Task<bool> RequestAsync(SystemPowerAction action)
    {
        var command = action switch
        {
            SystemPowerAction.Shutdown => "/s",
            SystemPowerAction.Restart => "/r",
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
        if (!_confirm(action)) return false;

        var startInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(command);
        // A nonzero timeout implicitly forces apps closed. Leave unsaved-work handling to Windows.
        startInfo.ArgumentList.Add("/t");
        startInfo.ArgumentList.Add("0");
        await _execute(startInfo).ConfigureAwait(false);
        return true;
    }

    private static async Task RunAsync(ProcessStartInfo startInfo)
    {
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 Windows 电源命令。");
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        var diagnostic = (await error.ConfigureAwait(false)).Trim();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Windows 电源命令执行失败（{process.ExitCode}）。{diagnostic}");
    }
}
