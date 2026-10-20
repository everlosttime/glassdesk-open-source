using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Xml;
using System.Xml.Linq;

namespace GlassDesk.Platform.Startup;

/// <summary>当前用户、普通权限、无延迟的交互式登录启动任务。</summary>
internal sealed class ScheduledStartupRegistration
{
    private const string Marker = "GlassDesk.CurrentUser.Autostart";
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    private readonly string _taskName;
    private readonly string _userSid;
    private readonly string _userName;

    internal ScheduledStartupRegistration(string? taskName = null)
    {
        _userSid = WindowsIdentity.GetCurrent().User!.Value;
        _userName = WindowsIdentity.GetCurrent().Name;
        _taskName = taskName ?? "GlassDesk.Autostart-" + _userSid;
    }

    internal StartupRegistrationStatus Query(string executable)
    {
        try
        {
            return WithFolder(folder =>
            {
                object? task = GetTask(folder);
                try { return ReadStatus(task, executable); }
                finally { Release(task); }
            });
        }
        catch (Exception ex) when (IsFailure(ex))
        {
            return new(StartupRegistrationState.Unavailable, executable, "无法读取登录启动计划任务：" + ex.Message);
        }
    }

    internal StartupChangeResult SetEnabled(string executable, bool enabled)
    {
        try
        {
            return WithFolder(folder =>
            {
                object? task = GetTask(folder);
                bool existed = task is not null;
                StartupRegistrationStatus before;
                try { before = ReadStatus(task, executable); }
                finally { Release(task); }
                if (before.State is StartupRegistrationState.OtherTarget or StartupRegistrationState.Unavailable)
                    return new StartupChangeResult(false, before, before.Error);

                if (enabled)
                {
                    // Never launch on registration: only the next logon triggers it.
                    object registered = folder.RegisterTask(_taskName, CreateXml(executable), existed ? 4 : 2,
                        _userSid, null, 3, null);
                    Release(registered);
                }
                else if (existed)
                {
                    folder.DeleteTask(_taskName, 0);
                }

                var after = Query(executable);
                var expected = enabled ? StartupRegistrationState.Owned : StartupRegistrationState.Missing;
                return new StartupChangeResult(after.State == expected, after,
                    after.State == expected ? null : after.Error ?? "计划任务写入后核验失败。");
            });
        }
        catch (Exception ex) when (IsFailure(ex))
        {
            return new(false, Query(executable), "无法更改登录启动计划任务：" + ex.Message);
        }
    }

    private StartupRegistrationStatus ReadStatus(object? task, string executable)
    {
        if (task is null) return new(StartupRegistrationState.Missing, executable, null);
        dynamic registered = task;
        var xml = XDocument.Parse((string)registered.Xml);
        var root = xml.Root!;
        var principal = root.Element(Ns + "Principals")?.Elements().SingleOrDefault();
        var actions = root.Element(Ns + "Actions")?.Elements().ToArray() ?? [];
        var triggers = root.Element(Ns + "Triggers")?.Elements().ToArray() ?? [];
        bool owned = root.Element(Ns + "RegistrationInfo")?.Element(Ns + "Source")?.Value == Marker
            && IsCurrentUser(principal?.Element(Ns + "UserId")?.Value)
            && principal?.Element(Ns + "LogonType")?.Value == "InteractiveToken"
            && (principal?.Element(Ns + "RunLevel")?.Value ?? "LeastPrivilege") == "LeastPrivilege"
            && actions.Length == 1 && actions[0].Name == Ns + "Exec"
            && string.Equals(actions[0].Element(Ns + "Command")?.Value, executable, StringComparison.OrdinalIgnoreCase)
            && actions[0].Element(Ns + "Arguments")?.Value == "--autostart"
            && triggers.Length == 1 && triggers[0].Name == Ns + "LogonTrigger"
            && IsCurrentUser(triggers[0].Element(Ns + "UserId")?.Value);
        if (!owned)
            return new(StartupRegistrationState.OtherTarget, null, "同名登录任务不属于当前 GlassDesk；没有修改它。");
        return new((bool)registered.Enabled ? StartupRegistrationState.Owned : StartupRegistrationState.Missing,
            executable, null);
    }

    private bool IsCurrentUser(string? value) => string.Equals(value, _userSid, StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, _userName, StringComparison.OrdinalIgnoreCase);

    private string CreateXml(string executable)
    {
        XElement E(string name, object value) => new(Ns + name, value);
        return new XDocument(new XElement(Ns + "Task", new XAttribute("version", "1.2"),
            E("RegistrationInfo", new object[] { E("Description", "GlassDesk 登录时立即启动"), E("Source", Marker) }),
            E("Triggers", E("LogonTrigger", new object[] { E("Enabled", "true"), E("UserId", _userSid) })),
            E("Principals", new XElement(Ns + "Principal", new XAttribute("id", "CurrentUser"),
                E("UserId", _userSid), E("LogonType", "InteractiveToken"), E("RunLevel", "LeastPrivilege"))),
            E("Settings", new object[] {
                E("MultipleInstancesPolicy", "IgnoreNew"), E("DisallowStartIfOnBatteries", "false"),
                E("StopIfGoingOnBatteries", "false"), E("StartWhenAvailable", "true"),
                E("RunOnlyIfNetworkAvailable", "false"), E("RunOnlyIfIdle", "false"),
                E("AllowStartOnDemand", "true"), E("Enabled", "true"), E("Hidden", "false"),
                E("ExecutionTimeLimit", "PT0S"), E("Priority", "6") }),
            new XElement(Ns + "Actions", new XAttribute("Context", "CurrentUser"), E("Exec", new object[] {
                E("Command", executable), E("Arguments", "--autostart"), E("WorkingDirectory", Path.GetDirectoryName(executable)!) }))
        )).ToString();
    }

    private object? GetTask(dynamic folder)
    {
        try { return folder.GetTask(_taskName); }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x80070002)) { return null; }
    }

    private static T WithFolder<T>(Func<dynamic, T> action)
    {
        dynamic scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        object? folder = null;
        try { scheduler.Connect(); folder = scheduler.GetFolder(@"\"); return action(folder); }
        finally { Release(folder); Release(scheduler); }
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }

    private static bool IsFailure(Exception ex) => ex is COMException or UnauthorizedAccessException or SecurityException
        or IOException or ArgumentException or InvalidOperationException or XmlException;
}
