using GlassDesk.App.Startup;
using GlassDesk.Platform;

namespace GlassDesk.AppServices.Tests;

public sealed class AutostartCommandTests
{
    [Fact]
    public void Autostart首次启动保持Dock与托盘但不要求打开启动台或设置()
    {
        var options = CommandLineOptions.Parse(["--autostart"]);

        Assert.True(options.Autostart);
        Assert.Empty(options.Unknown);
        Assert.False(StartupIntent.ShouldOpenLauncher(options));
        Assert.False(StartupIntent.ShouldOpenSettings(options));
    }

    [Fact]
    public void Autostart重复实例不向现有实例发送唤起命令()
    {
        var options = CommandLineOptions.Parse(["--autostart"]);

        Assert.Null(StartupIntent.GetDuplicateInstanceCommand(options));
    }

    [Fact]
    public void 普通重复启动仍然打开启动台()
    {
        var options = CommandLineOptions.Parse([]);

        Assert.Equal(InstanceCommands.ShowLauncher, StartupIntent.GetDuplicateInstanceCommand(options));
    }

    [Theory]
    [InlineData("--show-launcher")]
    [InlineData("--show-settings")]
    [InlineData("--exit")]
    [InlineData("--selftest")]
    [InlineData("--help")]
    [InlineData("--list-library")]
    public void Autostart与其它启动模式组合时产生参数错误(string conflictingArgument)
    {
        var options = CommandLineOptions.Parse(["--autostart", conflictingArgument]);

        Assert.True(options.Autostart);
        Assert.NotEmpty(options.Unknown);
    }

    [Fact]
    public void 首个实例收到Exit时只执行退出而不初始化产品窗口()
    {
        var options = CommandLineOptions.Parse(["--exit"]);

        Assert.True(StartupIntent.ShouldExitWithoutExistingInstance(options));
    }

    [Fact]
    public void 旧协议先创建Mutex时新版无法取得兼容租约并生成阻止提示()
    {
        var instanceName = "GlassDesk.LegacyProbe." + Guid.NewGuid().ToString("N");
        using var legacyMutex = new Mutex(initiallyOwned: false, $@"Local\GlassDesk.SingleInstance.{instanceName}");
        using var modernGuard = SingleInstanceGuard.Acquire(instanceName);
        Assert.True(modernGuard.IsFirstInstance);

        Assert.False(modernGuard.TryAcquireLegacyProtocolLease(out var status));
        Assert.Equal(LegacyInstanceStatus.Running, status);
        var message = StartupIntent.GetLegacyInstanceBlockMessage(LegacyInstanceStatus.Running);
        Assert.Contains("旧版", message, StringComparison.Ordinal);
        Assert.Contains("退出", message, StringComparison.Ordinal);
    }

    [Fact]
    public void 新版首实例持有旧协议Mutex租约直到自身退出()
    {
        var instanceName = "GlassDesk.LegacyLease." + Guid.NewGuid().ToString("N");

        using (var modernGuard = SingleInstanceGuard.Acquire(instanceName))
        {
            Assert.True(modernGuard.IsFirstInstance);
            Assert.True(modernGuard.TryAcquireLegacyProtocolLease(out var status));
            Assert.Equal(LegacyInstanceStatus.NotRunning, status);

            using var legacyAttempt = new Mutex(initiallyOwned: false, $@"Local\GlassDesk.SingleInstance.{instanceName}", out var legacyCreatedNew);
            Assert.False(legacyCreatedNew);
        }

        using var afterModernExit = new Mutex(initiallyOwned: false, $@"Local\GlassDesk.SingleInstance.{instanceName}", out var createdAfterExit);
        Assert.True(createdAfterExit);
    }
}
