using Microsoft.Win32;
using GlassDesk.Platform.Startup;

namespace GlassDesk.AppServices.Tests;

public sealed class StartupRegistrationTests : IDisposable
{
    private const string ValueName = "GlassDesk.Tests.Startup";
    private readonly string _registryPath = @"Software\GlassDesk.Tests\StartupRegistration\" + Guid.NewGuid().ToString("N");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GlassDesk-Startup-" + Guid.NewGuid().ToString("N"));
    private readonly RegistryKey _runKey;
    private readonly string _executablePath;

    public StartupRegistrationTests()
    {
        Directory.CreateDirectory(_root);
        _executablePath = Path.Combine(_root, "GlassDesk Startup 测试.exe");
        File.WriteAllText(_executablePath, "test executable placeholder");
        _runKey = Registry.CurrentUser.CreateSubKey(_registryPath, writable: true)!;
    }

    [Fact]
    public void 识别原有GlassDesk启动登记并且启停只管理本程序()
    {
        const string productName = "GlassDesk.Independent";
        const string captureName = "GlassDesk.Capture";
        const string captureCommand = "\"D:\\GlassDesk-Capture\\GlassDesk.exe\" --autostart";
        var productCommand = $"\"{Path.GetFullPath(_executablePath)}\" --autostart";
        _runKey.SetValue(productName, productCommand, RegistryValueKind.String);
        _runKey.SetValue(captureName, captureCommand, RegistryValueKind.String);
        var registration = new StartupRegistration(_runKey, StartupRegistration.ValueName);

        Assert.Equal(StartupRegistrationState.Owned, registration.Query(_executablePath).State);
        Assert.True(registration.SetEnabled(_executablePath, enabled: true).Succeeded);
        Assert.Equal(StartupRegistrationState.Owned, registration.Query(_executablePath).State);
        Assert.Equal(productCommand, _runKey.GetValue(productName));
        Assert.Equal(captureCommand, _runKey.GetValue(captureName));
        Assert.True(registration.SetEnabled(_executablePath, enabled: false).Succeeded);
        Assert.Equal(StartupRegistrationState.Missing, registration.Query(_executablePath).State);
        Assert.Null(_runKey.GetValue(productName));
        Assert.Equal(captureCommand, _runKey.GetValue(captureName));
    }

    [Fact]
    public void 缺少值时查询为Missing()
    {
        var status = CreateRegistration().Query(_executablePath);

        Assert.Equal(StartupRegistrationState.Missing, status.State);
        Assert.Null(status.Error);
    }

    [Fact]
    public void 启用写入规范化完整命令并重复启用保持Owned()
    {
        var registration = CreateRegistration();

        var first = registration.SetEnabled(_executablePath, enabled: true);
        var repeated = registration.SetEnabled(_executablePath, enabled: true);

        Assert.True(first.Succeeded, first.Error);
        Assert.True(repeated.Succeeded, repeated.Error);
        Assert.Equal(StartupRegistrationState.Owned, repeated.Status.State);
        Assert.Equal($"\"{Path.GetFullPath(_executablePath)}\" --autostart", _runKey.GetValue(ValueName));
        Assert.Equal(RegistryValueKind.String, _runKey.GetValueKind(ValueName));
    }

    [Fact]
    public void 禁用只删除本实例拥有的值且重复禁用幂等()
    {
        var registration = CreateRegistration();
        Assert.True(registration.SetEnabled(_executablePath, enabled: true).Succeeded);

        var first = registration.SetEnabled(_executablePath, enabled: false);
        var repeated = registration.SetEnabled(_executablePath, enabled: false);

        Assert.True(first.Succeeded, first.Error);
        Assert.True(repeated.Succeeded, repeated.Error);
        Assert.Equal(StartupRegistrationState.Missing, repeated.Status.State);
        Assert.Null(_runKey.GetValue(ValueName));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OtherTarget不被覆盖或删除(bool enabled)
    {
        const string other = "\"C:\\Other Copy\\GlassDesk.exe\" --autostart";
        _runKey.SetValue(ValueName, other, RegistryValueKind.String);

        var result = CreateRegistration().SetEnabled(_executablePath, enabled);

        Assert.False(result.Succeeded);
        Assert.Equal(StartupRegistrationState.OtherTarget, result.Status.State);
        Assert.Equal(other, _runKey.GetValue(ValueName));
    }

    [Fact]
    public void 外部修改后查询重新报告OtherTarget()
    {
        var registration = CreateRegistration();
        Assert.True(registration.SetEnabled(_executablePath, enabled: true).Succeeded);
        const string other = "\"C:\\Moved Copy\\GlassDesk.exe\" --autostart";
        _runKey.SetValue(ValueName, other, RegistryValueKind.String);

        Assert.Equal(StartupRegistrationState.OtherTarget, registration.Query(_executablePath).State);
    }

    [Fact]
    public void 无效可执行路径不写入注册表()
    {
        var missing = CreateRegistration().SetEnabled(Path.Combine(_root, "missing.exe"), enabled: true);
        var nonExecutable = Path.Combine(_root, "notes.txt");
        File.WriteAllText(nonExecutable, "not an executable");
        var wrongExtension = CreateRegistration().SetEnabled(nonExecutable, enabled: true);

        Assert.False(missing.Succeeded);
        Assert.False(wrongExtension.Succeeded);
        Assert.Equal(StartupRegistrationState.Missing, CreateRegistration().Query(_executablePath).State);
        Assert.Empty(_runKey.GetValueNames());
    }

    [Fact]
    public void 超过Run命令限制时拒绝且不写入()
    {
        var longPath = @"C:\" + new string('x', 280) + ".exe";

        var result = CreateRegistration().SetEnabled(longPath, enabled: true);

        Assert.False(result.Succeeded);
        Assert.Contains("260", result.Error, StringComparison.Ordinal);
        Assert.Empty(_runKey.GetValueNames());
    }

    [Fact]
    public void 注册表拒绝写入时返回错误且保持Missing()
    {
        using var readOnly = Registry.CurrentUser.OpenSubKey(_registryPath, writable: false)!;
        var registration = new StartupRegistration(readOnly, ValueName);

        var result = registration.SetEnabled(_executablePath, enabled: true);

        Assert.False(result.Succeeded);
        Assert.Equal(StartupRegistrationState.Missing, result.Status.State);
        Assert.NotNull(result.Error);
        Assert.Null(_runKey.GetValue(ValueName));
    }

    [Fact]
    public void 非REGSZ目标作为OtherTarget保留()
    {
        _runKey.SetValue(ValueName, 12, RegistryValueKind.DWord);

        var status = CreateRegistration().Query(_executablePath);

        Assert.Equal(StartupRegistrationState.OtherTarget, status.State);
        Assert.Equal(12, _runKey.GetValue(ValueName));
    }

    private StartupRegistration CreateRegistration() => new(_runKey, ValueName);

    public void Dispose()
    {
        _runKey.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_registryPath, throwOnMissingSubKey: false);
        Directory.Delete(_root, recursive: true);
    }
}
