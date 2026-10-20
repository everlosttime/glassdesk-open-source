using System.Runtime.InteropServices;
using GlassDesk.Platform.Interop;

namespace GlassDesk.Platform;

/// <summary>
/// 如果本进程是从某个控制台启动的，就附着到那个控制台上。
/// </summary>
/// <remarks>
/// WinExe 没有自己的控制台，因此从窗口宿主里用 <c>Console.WriteLine</c> 写出的文字
/// 无处可去。附着到父进程的控制台，才让 <c>--selftest</c> 这样的命令
/// 行模式可以在终端或构建脚本中使用。
///
/// 从资源管理器启动意味着根本没有父控制台；这属于正常情况，
/// 只会报回普通的 false，而不是当作错误。
/// </remarks>
public static class ConsoleAttach
{
    private const int AttachParentProcess = -1;

    /// <summary>
    /// 当标准输出现在能到达父进程的控制台时为 true。
    /// </summary>
    /// <remarks>
    /// 结果会被缓存：附着两次没有意义，而且第二次调用会因为
    /// 进程已经附着而失败。
    /// </remarks>
    public static bool TryAttachToParentConsole()
    {
        if (Field is not null)
        {
            return Field.Value;
        }

        var attached = false;

        try
        {
            attached = Kernel32.AttachConsole(AttachParentProcess);

            if (attached)
            {
                // 本进程按 UTF-8 写输出。控制台若停留在系统 ANSI 代码页上，
                // 中文输出会渲染成乱码，所以把控制台切过去。
                Kernel32.SetConsoleOutputCP(Utf8CodePage);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            attached = false;
        }

        Field = attached;
        return attached;
    }

    /// <summary>UTF-8。与我们写入所用编码相匹配的代码页。</summary>
    private const uint Utf8CodePage = 65001;

    // 不需要考虑 [ThreadStatic]：一个进程只有一个控制台，而且这个值在
    // 首次调用之后就不再变化。
    private static bool? Field;
}