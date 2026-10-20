using System.Diagnostics;
using System.Security.Principal;

namespace GlassDesk.Platform;

/// <summary>将单实例对象限定到明确的 Windows 用户 SID 与会话。</summary>
internal sealed class InstanceIdentity
{
    private const string MutexPrefix = "GlassDesk.SingleInstance.";
    private const string PipePrefix = "GlassDesk.Pipe.";

    public InstanceIdentity(string userSid, int sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userSid);
        if (userSid.Contains('\\') || userSid.Contains('/') || sessionId < 0)
            throw new ArgumentException("单实例身份必须包含有效 SID 和非负会话编号。");

        UserSid = userSid;
        SessionId = sessionId;
    }

    public string UserSid { get; }

    public int SessionId { get; }

    public static InstanceIdentity Current
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            using var process = Process.GetCurrentProcess();
            var sid = identity.User?.Value ?? throw new InvalidOperationException("无法读取当前 Windows 用户 SID。");
            return new InstanceIdentity(sid, process.SessionId);
        }
    }

    public string GetMutexName(string instanceName)
    {
        ValidateInstanceName(instanceName);
        return $@"Local\{MutexPrefix}{UserSid}.{SessionId}.{instanceName}";
    }

    public string GetPipeName(string instanceName)
    {
        ValidateInstanceName(instanceName);
        return $"{PipePrefix}{UserSid}.{SessionId}.{instanceName}";
    }

    private static void ValidateInstanceName(string instanceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceName);
        if (instanceName.Contains('\\') || instanceName.Contains('/') || instanceName.Any(char.IsControl))
            throw new ArgumentException("单实例名称不能包含路径分隔符或控制字符。", nameof(instanceName));
    }
}
