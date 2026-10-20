using GlassDesk.Platform.Interop;

namespace GlassDesk.Platform.Shell;

/// <summary>
/// 读已知文件夹的实际路径。
/// </summary>
/// <remarks>
/// 用 <c>SHGetKnownFolderPath</c> 而不是拼 <c>%APPDATA%</c> 之类的环境变量：
/// 这些目录可以被重定向（企业策略、OneDrive 接管、用户自己搬走），
/// 拼字符串在重定向过的机器上会指向一个不存在的地方。
/// </remarks>
public static class KnownFolders
{
    /// <summary>所有用户的「开始」菜单程序目录。</summary>
    public static string? CommonPrograms => Resolve(Shell32.FolderIdCommonPrograms);

    /// <summary>当前用户的「开始」菜单程序目录。</summary>
    public static string? Programs => Resolve(Shell32.FolderIdPrograms);

    /// <summary>取不到时返回 null，由调用方决定这是不是问题。</summary>
    private static string? Resolve(Guid folderId)
    {
        nint pointer = 0;

        try
        {
            var hr = Shell32.SHGetKnownFolderPath(in folderId, 0, 0, out pointer);
            if (hr != Ole32.SoK || pointer == 0)
            {
                return null;
            }

            // 返回的是以 null 结尾的 UTF-16 字符串。
            var path = System.Runtime.InteropServices.Marshal.PtrToStringUni(pointer);
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            // 这块内存是 CoTaskMemAlloc 分配的，必须用配对的释放函数，
            // 否则是错配堆释放。也正因为必须释放，才不能直接 return 读出来的字符串。
            if (pointer != 0)
            {
                Ole32.CoTaskMemFree(pointer);
            }
        }
    }
}