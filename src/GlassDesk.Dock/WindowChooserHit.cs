using System.Windows;
using System.Windows.Controls;

namespace GlassDesk.Dock;

/// <summary>
/// 从一次点击的「来源元素」找出它所属列表项里的窗口句柄。
/// </summary>
/// <remarks>
/// <para>
/// <strong>不能只读 <c>OriginalSource.Tag</c>。</strong>
/// 句柄挂在 <see cref="ListBoxItem"/> 上，而点击的 <c>OriginalSource</c> 通常是
/// 里面的文字块或内容承载元素——它自己没有 Tag，于是处理器安静地什么都不做：
/// 列表看着能点，点了没有任何反应，也不报错。实测这是本项目里最容易漏掉的一类缺陷。
/// </para>
/// <para>
/// 正确做法是往上找到所属的列表项容器（<see cref="ItemsControl.ContainerFromElement(ItemsControl, DependencyObject)"/>
/// 就是为这件事提供的 API），再从容器上取句柄。
/// </para>
/// </remarks>
public static class WindowChooserHit
{
    /// <summary>找出这次点击对应的窗口句柄；找不到返回 0。</summary>
    public static nint Resolve(ItemsControl items, object? originalSource)
    {
        ArgumentNullException.ThrowIfNull(items);

        if (originalSource is not DependencyObject source)
        {
            return 0;
        }

        // 先看来源自身（点到的正好是列表项时），再往上找容器。
        if (source is ListBoxItem own && TryRead(own, out var direct))
        {
            return direct;
        }

        if (ItemsControl.ContainerFromElement(items, source) is ListBoxItem container && TryRead(container, out var handle))
        {
            return handle;
        }

        return 0;
    }

    private static bool TryRead(ListBoxItem item, out nint handle)
    {
        if (item.Tag is nint value && value != 0)
        {
            handle = value;
            return true;
        }

        handle = 0;
        return false;
    }
}