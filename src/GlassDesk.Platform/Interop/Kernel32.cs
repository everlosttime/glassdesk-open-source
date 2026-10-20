using System.Runtime.InteropServices;

namespace GlassDesk.Platform.Interop;

/// <summary>
/// kernel32 中的控制台与线程入口点。
/// </summary>
/// <remarks>
/// 原本声明在 <c>ConsoleAttach</c> 文件末尾，因为当时只有控制台附着用到它。
/// 全屏事件钩子又需要线程相关的入口点，于是集中到互操作目录下——
/// 与其余 Win32 声明放在一起，见本目录的约定。
/// </remarks>
internal static partial class Kernel32
{
    [LibraryImport("kernel32.dll", EntryPoint = "AttachConsole", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AttachConsole(int processId);

    [LibraryImport("kernel32.dll", EntryPoint = "SetConsoleOutputCP")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetConsoleOutputCP(uint codePage);

    /// <summary>
    /// 当前线程的系统级 id。
    /// </summary>
    /// <remarks>
    /// 事件钩子所在的消息循环需要它：结束循环只能靠向该线程投递 <c>WM_QUIT</c>，
    /// 而投递的目标是系统级线程 id，不是托管线程 id。
    /// </remarks>
    [LibraryImport("kernel32.dll")]
    internal static partial uint GetCurrentThreadId();

    // ---- 进程（阶段 3 的窗口归属判定） ----

    /// <summary>只够问到映像路径与退出码，权限要求最低。</summary>
    internal const uint ProcessQueryLimitedInformation = 0x1000;

    /// <summary>
    /// 打开进程。
    /// </summary>
    /// <remarks>
    /// 用 <see cref="ProcessQueryLimitedInformation"/> 而不是 <c>PROCESS_QUERY_INFORMATION</c>：
    /// 前者对**已提权**的进程也能拿到，而后者会被拒绝访问。
    /// 判定「这个窗口是不是某条应用库记录的」正需要跨权限级别看进程。
    /// </remarks>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint handle);

    /// <summary>问出进程映像的完整路径。<paramref name="size"/> 传入和传出缓冲区容量/长度。</summary>
    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool QueryFullProcessImageName(nint process, uint flags, [Out] char[] name, ref uint size);

    /// <summary>进程是否仍在运行。</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    internal const uint WaitTimeout = 0x00000102;

    /// <summary>由进程句柄问出进程 id。</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint GetProcessId(nint process);

    /// <summary>
    /// 问出打包进程的应用身份（AppUserModelID）。
    /// </summary>
    /// <remarks>
    /// 只对有包身份的进程有效；普通 Win32 进程返回错误码
    /// <c>APPMODEL_ERROR_NO_APPLICATION</c>（0x80270112），这正是探针要实测确认的边界。
    /// <paramref name="length"/> 传入缓冲区容量（字符数），传出实际长度（含结尾 null）；
    /// 容量不足时返回 <c>ERROR_INSUFFICIENT_BUFFER</c> 并把需要的长度写回。
    /// </remarks>
    [LibraryImport("kernel32.dll", EntryPoint = "GetApplicationUserModelId", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int GetApplicationUserModelId(nint process, ref uint length, [Out] char[] buffer);

    /// <summary><c>GetApplicationUserModelId</c>：目标进程没有应用身份（非打包应用）。</summary>
    internal const int AppModelErrorNoApplication = unchecked((int)0x80270112);
}