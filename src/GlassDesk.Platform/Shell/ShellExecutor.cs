using System.Runtime.InteropServices;
using GlassDesk.Platform.Interop;

namespace GlassDesk.Platform.Shell;

/// <summary>一次外壳启动的结果。</summary>
/// <param name="Started">外壳接受了这次请求。</param>
/// <param name="ErrorCode">外壳返回的错误码；成功为 0。</param>
/// <param name="ProcessId">被启动进程的 id；外壳没给出进程句柄时为 0。</param>
public sealed record ShellLaunchOutcome(bool Started, int ErrorCode, uint ProcessId)
{
    /// <summary>目标文件不存在。</summary>
    public bool IsFileNotFound => ErrorCode == Shell32.SeErrFileNotFound;

    /// <summary>没有程序关联到这种文件。</summary>
    public bool IsNoAssociation => ErrorCode == Shell32.SeErrNoAssociation;

    /// <summary>被拒绝访问（含目标需要提权而用户取消了提示）。</summary>
    public bool IsAccessDenied => ErrorCode is Shell32.SeErrAccessDenied or Shell32.SeErrCancelled;
}

/// <summary>
/// 用外壳打开东西。
/// </summary>
/// <remarks>
/// 四类条目（程序、快捷方式、文件夹、文件）与网页<strong>共用这一条路径</strong>，
/// 而不是「程序走 <c>CreateProcess</c>、其余走外壳」。理由有三条：
/// <list type="bullet">
/// <item>关联、工作目录、UAC 提权这些行为全交给外壳，与用户在资源管理器里双击一致；</item>
/// <item><c>CreateProcess</c> 无法启动需要提权的程序，也不能打开文件夹与网页；</item>
/// <item>少一条分支就少一处「四类里某一类行为不同」的缺陷面。</item>
/// </list>
///
/// 代价是参数要由<strong>我们</strong>拼接后交给外壳。参数一律以原始形式原样传递，
/// 不做任何转义或重排——它本来就来自 <c>.lnk</c>，已经是为命令行准备好的文本。
/// </remarks>
public static class ShellExecutor
{
    /// <summary>打开一个文件、文件夹或网址。</summary>
    public static ShellLaunchOutcome Open(string target, string? arguments = null, string? workingDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return new ShellLaunchOutcome(false, Shell32.SeErrFileNotFound, 0);
        }

        var filePointer = Marshal.StringToHGlobalUni(target);
        var argumentsPointer = string.IsNullOrEmpty(arguments) ? 0 : Marshal.StringToHGlobalUni(arguments);
        var directoryPointer = string.IsNullOrWhiteSpace(workingDirectory) ? 0 : Marshal.StringToHGlobalUni(workingDirectory);

        try
        {
            var info = new Shell32.ShellExecuteInfo
            {
                Size = Marshal.SizeOf<Shell32.ShellExecuteInfo>(),

                // 要进程句柄才能拿到 pid，用于「已运行就激活」；
                // 不要外壳的错误弹窗，提示由我们统一措辞。
                //
                // 刻意不加 SEE_MASK_NOASYNC。文档建议「没有消息循环的调用方」加上它，
                // 而命令行的应用库命令确实没有消息循环，所以它一度被加了进来——
                // 但当时那次「启动被外壳拒绝」的真正原因是下面这个结构体的字段数错了
                // （见 ShellExecuteInfo 的说明）。修好结构体之后，
                // 实测加不加这一位都能成功，因此不保留一个未经证实的开关。
                Mask = Shell32.SeeMaskNoCloseProcess | Shell32.SeeMaskFlagNoUi,

                // Verb 留空 = 让外壳按关联决定动作。
                File = filePointer,
                Parameters = argumentsPointer,
                Directory = directoryPointer,
                Show = 1,
            };

            if (!Shell32.ShellExecuteEx(ref info))
            {
                return new ShellLaunchOutcome(false, Marshal.GetLastWin32Error(), 0);
            }

            var processId = info.Process == 0 ? 0 : Kernel32.GetProcessId(info.Process);

            if (info.Process != 0)
            {
                _ = Kernel32.CloseHandle(info.Process);
            }

            // 文件夹与网址不会产生新进程，句柄为空是正常的，仍算成功。
            return new ShellLaunchOutcome(true, Shell32.SeErrSuccess, processId);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return new ShellLaunchOutcome(false, -1, 0);
        }
        finally
        {
            FreeIfAllocated(filePointer);
            FreeIfAllocated(argumentsPointer);
            FreeIfAllocated(directoryPointer);
        }
    }

    private static void FreeIfAllocated(nint pointer)
    {
        if (pointer != 0)
        {
            Marshal.FreeHGlobal(pointer);
        }
    }
}