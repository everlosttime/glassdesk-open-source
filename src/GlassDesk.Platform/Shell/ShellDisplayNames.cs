using System.Runtime.InteropServices;
using GlassDesk.Platform.Interop;

namespace GlassDesk.Platform.Shell;

/// <summary>读取 Explorer 显示的名称，包括 desktop.ini 中的本地化名称。</summary>
public static class ShellDisplayNames
{
    public static string GetFileName(string path, bool removeExtension = false)
    {
        var name = Read(path) ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        if (string.IsNullOrWhiteSpace(name)) return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // 只去掉来源的实际扩展名；本地化名称中的 v2.0 等文字必须完整保留。
        var extension = Path.GetExtension(path);
        if (removeExtension && extension.Length > 0 && name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            name = name[..^extension.Length];
        return name;
    }

    private static string? Read(string path)
    {
        nint itemIdList = 0, name = 0;
        try
        {
            if (Shell32.SHParseDisplayName(path, 0, out itemIdList, 0, out _) != Ole32.SoK || itemIdList == 0)
                return null;
            // SIGDN_NORMALDISPLAY = 0: 与 Explorer 图标下方显示的文字一致。
            if (Shell32.SHGetNameFromIDList(itemIdList, 0, out name) != Ole32.SoK || name == 0)
                return null;
            var value = Marshal.PtrToStringUni(name);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or COMException or ArgumentException)
        {
            return null;
        }
        finally
        {
            if (name != 0) Ole32.CoTaskMemFree(name);
            if (itemIdList != 0) Shell32.ILFree(itemIdList);
        }
    }
}
