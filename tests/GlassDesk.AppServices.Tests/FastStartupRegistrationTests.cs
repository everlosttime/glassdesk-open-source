using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Xml.Linq;
using GlassDesk.Platform.Startup;
using Microsoft.Win32;

namespace GlassDesk.AppServices.Tests;

public sealed class FastStartupRegistrationTests : IDisposable
{
    private readonly string _id = "GlassDesk.Tests.FastStartup-" + Guid.NewGuid().ToString("N");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GlassDesk-FastStartup-" + Guid.NewGuid().ToString("N"));
    private readonly RegistryKey _key;
    private readonly string _executable;

    public FastStartupRegistrationTests()
    {
        Directory.CreateDirectory(_root);
        _executable = Path.Combine(_root, "GlassDesk 中文.exe");
        File.WriteAllText(_executable, "test placeholder, never executed");
        _key = Registry.CurrentUser.CreateSubKey(@"Software\GlassDesk.Tests\" + _id)!;
    }

    private StartupRegistration Create() => new(_key, _id, _id);

    [Fact]
    public void 启用登记无延迟交互登录任务且没有Run重复项()
    {
        var service = Create();
        var enabled = service.SetEnabled(_executable, true);
        Assert.True(enabled.Succeeded, enabled.Error + "\n" + ReadTask());
        Assert.True(service.SetEnabled(_executable, true).Succeeded);
        Assert.Equal(StartupRegistrationState.Owned, service.Query(_executable).State);
        Assert.Null(_key.GetValue(_id));
        var xml = ReadTask();
        XNamespace ns = xml.Root!.Name.Namespace;
        var trigger = xml.Descendants(ns + "LogonTrigger").Single();
        Assert.Contains(trigger.Element(ns + "UserId")!.Value,
            new[] { WindowsIdentity.GetCurrent().User!.Value, WindowsIdentity.GetCurrent().Name });
        Assert.True(trigger.Element(ns + "Delay") is null || trigger.Element(ns + "Delay")!.Value == "PT0S");
        Assert.Equal(_executable, xml.Descendants(ns + "Command").Single().Value);
        Assert.Equal("--autostart", xml.Descendants(ns + "Arguments").Single().Value);
        Assert.Equal(_root, xml.Descendants(ns + "WorkingDirectory").Single().Value);
        Assert.Equal("InteractiveToken", xml.Descendants(ns + "LogonType").Single().Value);
        Assert.Equal("LeastPrivilege", xml.Descendants(ns + "RunLevel").SingleOrDefault()?.Value ?? "LeastPrivilege");
        Assert.Equal("6", xml.Descendants(ns + "Priority").Single().Value);
        Assert.Equal("false", xml.Descendants(ns + "DisallowStartIfOnBatteries").Single().Value);
        Assert.Equal("false", xml.Descendants(ns + "StopIfGoingOnBatteries").Single().Value);
        Assert.Equal("false", xml.Descendants(ns + "RunOnlyIfIdle").SingleOrDefault()?.Value ?? "false");
        Assert.Equal("false", xml.Descendants(ns + "RunOnlyIfNetworkAvailable").SingleOrDefault()?.Value ?? "false");
        Assert.Equal("PT0S", xml.Descendants(ns + "ExecutionTimeLimit").Last().Value);
    }

    [Fact]
    public void 迁移旧Run登记且关闭时删除任务并保持幂等()
    {
        _key.SetValue(_id, $"\"{_executable}\" --autostart");
        var service = Create();
        Assert.Equal(StartupRegistrationState.Owned, service.Query(_executable).State);
        Assert.True(service.SetEnabled(_executable, true).Succeeded);
        Assert.Null(_key.GetValue(_id));
        Assert.True(service.SetEnabled(_executable, false).Succeeded);
        Assert.True(service.SetEnabled(_executable, false).Succeeded);
        Assert.Equal(StartupRegistrationState.Missing, service.Query(_executable).State);
        Assert.Throws<FileNotFoundException>(() => ReadTask());
    }

    [Fact]
    public void 同名任务指向其它副本时不覆盖或删除()
    {
        var service = Create();
        Assert.True(service.SetEnabled(_executable, true).Succeeded);
        var other = Path.Combine(_root, "Other.exe");
        File.WriteAllText(other, "placeholder");
        Assert.Equal(StartupRegistrationState.OtherTarget, service.Query(other).State);
        Assert.False(service.SetEnabled(other, true).Succeeded);
        Assert.False(service.SetEnabled(other, false).Succeeded);
        Assert.Equal(StartupRegistrationState.Owned, service.Query(_executable).State);
    }

    [Fact]
    public void 计划任务不可写时保留原有Run启动()
    {
        _key.SetValue(_id, $"\"{_executable}\" --autostart");
        var service = new StartupRegistration(_key, _id, _id + "?");
        var result = service.SetEnabled(_executable, true);
        Assert.False(result.Succeeded);
        Assert.Equal($"\"{_executable}\" --autostart", _key.GetValue(_id));
    }

    [Fact]
    public void 被外部禁用的任务保持关闭直到明确启用()
    {
        var service = Create();
        Assert.True(service.SetEnabled(_executable, true).Succeeded);
        WithFolder(folder => { dynamic task = folder.GetTask(_id); try { task.Enabled = false; } finally { Marshal.FinalReleaseComObject(task); } });
        Assert.Equal(StartupRegistrationState.Missing, service.Query(_executable).State);
        Assert.True(service.SetEnabled(_executable, true).Succeeded);
        Assert.Equal(StartupRegistrationState.Owned, service.Query(_executable).State);
    }

    private XDocument ReadTask()
    {
        XDocument? result = null;
        WithFolder(folder => { dynamic task = folder.GetTask(_id); try { result = XDocument.Parse((string)task.Xml); } finally { Marshal.FinalReleaseComObject(task); } });
        return result!;
    }

    private static void WithFolder(Action<dynamic> action)
    {
        dynamic scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        object? folder = null;
        try { scheduler.Connect(); folder = scheduler.GetFolder(@"\"); action(folder); }
        finally { if (folder is not null) Marshal.FinalReleaseComObject(folder); Marshal.FinalReleaseComObject(scheduler); }
    }

    public void Dispose()
    {
        WithFolder(folder => { try { folder.DeleteTask(_id, 0); } catch (Exception ex) when (ex.HResult == unchecked((int)0x80070002)) { } });
        _key.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\GlassDesk.Tests\" + _id, false);
        Directory.Delete(_root, true);
    }
}
