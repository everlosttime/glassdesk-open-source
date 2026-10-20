using System.Runtime.InteropServices;

namespace GlassDesk.Platform.Interop;

/// <summary>
/// ole32 中的 COM 激活入口点。
/// </summary>
/// <remarks>
/// 快捷方式解析要创建 <c>ShellLink</c> 对象，而它是 COM 组件，只能经
/// <c>CoCreateInstance</c> 取得。这里不引用 <c>ShellLink</c> 协类（coclass），
/// 因此不依赖内置 COM 互操作对类型库的推断——
/// 拿到的原始指针交给源生成的接口包装器即可。
/// </remarks>
internal static partial class Ole32
{
    /// <summary>进程内服务器。快捷方式对象就在 shell32 里。</summary>
    internal const uint ClsCtxInprocServer = 0x1;

    /// <summary>介质文件。<c>IPersistFile::Load</c> 用它表示从文件加载。</summary>
    internal const uint StgmRead = 0x00000000;

    internal const int SoK = 0;

    /// <summary><c>E_INSUFFICIENTBUFFER</c>：调用方给的缓冲区装不下结果。</summary>
    internal const int EInsufficientBuffer = unchecked((int)0x8007007A);

    [LibraryImport("ole32.dll")]
    internal static partial int CoCreateInstance(in Guid classId, nint outer, uint context, in Guid interfaceId, out nint instance);

    /// <summary>
    /// 释放 COM 任务分配器返回的内存。
    /// </summary>
    /// <remarks>
    /// <c>SHGetKnownFolderPath</c> 用 <c>CoTaskMemAlloc</c> 分配结果字符串，
    /// 规定要用这个函数释放；用 <c>Marshal.FreeHGlobal</c> 释放是错配，
    /// 会破坏堆。
    /// </remarks>
    [LibraryImport("ole32.dll")]
    internal static partial void CoTaskMemFree(nint memory);
}