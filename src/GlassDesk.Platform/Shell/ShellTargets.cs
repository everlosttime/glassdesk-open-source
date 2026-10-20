using System.Runtime.InteropServices;
using GlassDesk.Platform.Interop;

namespace GlassDesk.Platform.Shell;

/// <summary>
/// 问外壳「这个目标你认不认」——不改动桌面、不打开任何窗口。
/// </summary>
/// <remarks>
/// <c>shell:RecycleBinFolder</c> 这类目标不能用「目标存在吗」来判断（它不是文件路径），
/// 而验证它也确实不需要真的把回收站窗口打开：<c>SHParseDisplayName</c> 让外壳自己解析，
/// 认得出就给回一个 PIDL，认不出就失败。
///
/// <para>
/// 这条路径的存在理由之一是<strong>测试卫生</strong>：验收里「打开回收站再关掉」
/// 会在用户桌面上闪一个窗口，而「拼错的 <c>shell:</c> 目标」还会让外壳弹错误框。
/// 解析检查零窗口、零弹窗，且断言的是同一件事（外壳接不接受这个目标）。
/// 真正的打开动作由人工核对清单覆盖（点一下回收站入口磁贴）。
/// </para>
/// </remarks>
public static class ShellTargets
{
    /// <summary>
    /// 外壳能否解析这个目标；无法判断时返回 null（调用方应记为「跳过」而不是通过）。
    /// </summary>
    public static bool? CanResolve(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return false;
        }

        nint itemIdList = 0;

        try
        {
            var hr = Shell32.SHParseDisplayName(target, 0, out itemIdList, 0, out _);
            return hr == Ole32.SoK && itemIdList != 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            if (itemIdList != 0)
            {
                // PIDL 是外壳给的结构，必须用 ILFree 释放，不能用 CoTaskMemFree。
                Shell32.ILFree(itemIdList);
            }
        }
    }
}