using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace GlassDesk.Platform.Network;

public sealed record NearbyFirewallStatus(bool Ready, string Message);

internal sealed record NearbyFirewallRule(string Name, string Program, int Protocol, string LocalPorts,
    string RemoteAddresses, string LocalAddresses, int Profiles, bool Enabled, int Direction, int Action, bool EdgeTraversal,
    string RemotePorts = "*", string InterfaceTypes = "All", string ServiceName = "");

/// <summary>App-scoped LAN rules. Only explicit user actions may request elevation.</summary>
public sealed class NearbyFirewall
{
    public const string ConfigureArgument = "--configure-nearby-firewall";
    private readonly string _program;
    private readonly Func<IReadOnlyList<NearbyFirewallRule>> _read;
    private readonly Func<CancellationToken, Task<bool>> _configure;

    public NearbyFirewall(string executablePath)
    {
        _program = Path.GetFullPath(executablePath);
        _read = ReadWindowsRules;
        _configure = ConfigureElevatedAsync;
    }

    internal NearbyFirewall(string executablePath, Func<IReadOnlyList<NearbyFirewallRule>> read, Func<CancellationToken, Task<bool>> configure)
    { _program = Path.GetFullPath(executablePath); _read = read; _configure = configure; }

    private static NearbyFirewallRule[] Requirements(string program) =>
    [
        new("GlassDesk.Nearby.TCP", program, 6, "53317-53327", "LocalSubnet", "*", 7, true, 1, 1, false),
        new("GlassDesk.Nearby.UDP", program, 17, "53317", "LocalSubnet", "*", 7, true, 1, 1, false),
    ];

    public async Task<NearbyFirewallStatus> EnsureAsync(bool requestPermission, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var status = Inspect();
        if (status.Ready || !requestPermission) return status;
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (!await _configure(cancellationToken).ConfigureAwait(false))
                return new(false, "未完成防火墙授权，手机发送可能一直等待。请点击“修复接收连接”重试，并允许 Windows 授权提示。");
            return Inspect();
        }
        catch (Exception error) when (error is Win32Exception or UnauthorizedAccessException or IOException or InvalidOperationException or COMException)
        { return new(false, "无法配置防火墙接收规则。请点击“修复接收连接”重试；组织管理的网络需联系管理员。"); }
    }

    private NearbyFirewallStatus Inspect()
    {
        try
        {
            var rules = _read();
            var ready = Requirements(_program).All(expected => rules.Count(actual => Matches(actual, expected)) == 1);
            return ready ? new(true, "本地网络接收规则已就绪，切换 Wi-Fi 或热点后自动适用。")
                : new(false, "接收防火墙规则未就绪，手机发送可能一直等待。请点击“修复接收连接”。");
        }
        catch (Exception error) when (error is COMException or UnauthorizedAccessException or InvalidOperationException or Win32Exception or IOException)
        { return new(false, "无法确认 Windows 防火墙接收规则，或入站连接受系统策略限制。请尝试修复接收连接；组织网络需联系管理员。"); }
    }

    private static bool Matches(NearbyFirewallRule actual, NearbyFirewallRule expected) =>
        actual.Name == expected.Name && actual.Program.Equals(expected.Program, StringComparison.OrdinalIgnoreCase)
        && actual.Protocol == expected.Protocol && actual.LocalPorts == expected.LocalPorts
        && actual.RemoteAddresses.Equals("LocalSubnet", StringComparison.OrdinalIgnoreCase) && actual.LocalAddresses == "*"
        && (actual.Profiles & 7) == 7 && actual.Enabled && actual.Direction == 1 && actual.Action == 1 && !actual.EdgeTraversal
        && actual.RemotePorts == "*" && actual.InterfaceTypes.Equals("All", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(actual.ServiceName);

    private static dynamic CreateCom(string name) => Activator.CreateInstance(Type.GetTypeFromProgID(name)
        ?? throw new InvalidOperationException("Windows 防火墙接口不可用。")) ?? throw new InvalidOperationException("Windows 防火墙接口不可用。");

    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }

    internal static void IgnoreMissingRule(Action operation)
    {
        try { operation(); }
        catch (Exception error) when (error.HResult == unchecked((int)0x80070002)) { }
    }

    private static List<NearbyFirewallRule> ReadWindowsRules()
    {
        dynamic policy = CreateCom("HNetCfg.FwPolicy2"); object? collection = null;
        try
        {
            // A local allow rule cannot override an administrative block-all policy.
            var active = (int)policy.CurrentProfileTypes;
            foreach (var profile in new[] { 1, 2, 4 })
                if ((active & profile) != 0 && (bool)policy.FirewallEnabled[profile] && (bool)policy.BlockAllInboundTraffic[profile])
                    throw new InvalidOperationException("当前网络阻止所有入站连接。");
            if ((int)policy.LocalPolicyModifyState != 0) throw new InvalidOperationException("防火墙受组织策略管理。");
            collection = policy.Rules;
            var result = new List<NearbyFirewallRule>();
            foreach (var raw in (IEnumerable)collection)
            {
                try
                {
                    dynamic rule = raw;
                    if ((string)rule.Name is not ("GlassDesk.Nearby.TCP" or "GlassDesk.Nearby.UDP")) continue;
                    result.Add(new((string)rule.Name, (string)rule.ApplicationName ?? "", (int)rule.Protocol, (string)rule.LocalPorts ?? "",
                        (string)rule.RemoteAddresses ?? "", (string)rule.LocalAddresses ?? "", (int)rule.Profiles, (bool)rule.Enabled,
                        (int)rule.Direction, (int)rule.Action, (bool)rule.EdgeTraversal, (string)rule.RemotePorts ?? "*",
                        (string)rule.InterfaceTypes ?? "All", (string)rule.ServiceName ?? ""));
                }
                finally { Release(raw); }
            }
            return result;
        }
        finally { Release(collection); Release(policy); }
    }

    private async Task<bool> ConfigureElevatedAsync(CancellationToken cancellationToken)
    {
        using var process = await Task.Run(() => Process.Start(new ProcessStartInfo(_program, ConfigureArgument)
        { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetDirectoryName(_program) }), cancellationToken).ConfigureAwait(false);
        if (process is null) return false;
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return process.ExitCode == 0;
    }

    /// <summary>Dedicated elevated entry point; accepts no paths, commands or output files.</summary>
    public static bool RunConfigurationIfRequested(IReadOnlyList<string> arguments, out int exitCode)
    {
        exitCode = 2;
        if (!arguments.Any(a => a.Equals(ConfigureArgument, StringComparison.OrdinalIgnoreCase))) return false;
        if (arguments.Count != 1) return true;
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) { exitCode = 5; return true; }
        try
        {
            var program = Environment.ProcessPath ?? throw new InvalidOperationException();
            InstallWindowsRules(program);
            exitCode = new NearbyFirewall(program).Inspect().Ready ? 0 : 1;
        }
        catch (Exception error) when (error is COMException or UnauthorizedAccessException or InvalidOperationException or Win32Exception or IOException)
        { exitCode = 1; }
        return true;
    }

    private static void InstallWindowsRules(string program)
    {
        dynamic policy = CreateCom("HNetCfg.FwPolicy2"); object? collection = null;
        try
        {
            collection = policy.Rules; dynamic rules = collection;
            foreach (var expected in Requirements(program))
            {
                dynamic rule = CreateCom("HNetCfg.FWRule");
                try
                {
                    rule.Name = expected.Name; rule.Description = "GlassDesk 附近传输：仅本地子网，自动适应 Wi-Fi / 热点变化。";
                    rule.ApplicationName = program; rule.Protocol = expected.Protocol; rule.LocalPorts = expected.LocalPorts;
                    rule.RemotePorts = "*"; rule.LocalAddresses = "*"; rule.RemoteAddresses = "LocalSubnet";
                    rule.Profiles = 7; rule.Direction = 1; rule.Action = 1; rule.InterfaceTypes = "All";
                    rule.EdgeTraversal = false; rule.Enabled = true;
                    IgnoreMissingRule(() => rules.Remove(expected.Name));
                    rules.Add(rule);
                }
                finally { Release(rule); }
            }
            // Retire only the two one-time diagnostic rules created for this executable.
            foreach (var name in new[] { "GlassDesk nearby transfer - current Wi-Fi TCP", "GlassDesk nearby transfer - current Wi-Fi discovery" })
            {
                object? old = null;
                try { IgnoreMissingRule(() => { old = rules.Item(name); if (((string)((dynamic)old).ApplicationName).Equals(program, StringComparison.OrdinalIgnoreCase)) rules.Remove(name); }); }
                finally { Release(old); }
            }
        }
        finally { Release(collection); Release(policy); }
    }
}
