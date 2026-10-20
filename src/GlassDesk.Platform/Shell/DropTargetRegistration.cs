using System.Runtime.InteropServices;
using GlassDesk.Platform.Interop;

namespace GlassDesk.Platform.Shell;

/// <summary>
/// 查询一个窗口句柄上是否已经注册了 OLE 拖放目标（<c>IDropTarget</c>）。
/// </summary>
/// <remarks>
/// <c>RegisterDragDrop</c> 的公开行为：注册成功时在窗口句柄的属性表里留下
/// <c>OleDropTargetInterface</c> 原子。WPF 在 <see cref="System.Windows.UIElement.AllowDrop"/>
/// 置 true 时自己完成注册——这条查询让产品能<strong>证实</strong>这一点，
/// 从而把「同一个句柄只能有一个拖放注册方」的规则落在证据上
/// （阶段 4 探针 C 的实测手段，见 <c>docs/evidence/阶段4-探针实测输出.txt</c>）。
/// </remarks>
public static class DropTargetRegistration
{
    /// <summary>OLE 在窗口属性表里留下的原子名。</summary>
    private const string OleDropTargetInterfaceAtom = "OleDropTargetInterface";

    /// <summary>该窗口是否已注册 OLE 拖放目标。</summary>
    public static bool IsOleDropTargetRegistered(nint window)
    {
        if (window == 0)
        {
            return false;
        }

        try
        {
            return User32.GetProp(window, OleDropTargetInterfaceAtom) != 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }
}
