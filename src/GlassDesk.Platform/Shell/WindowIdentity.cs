using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using GlassDesk.Platform.Interop;

namespace GlassDesk.Platform.Shell;

/// <summary>
/// 读出一个窗口或进程的「应用身份」（AppUserModelID）。
/// </summary>
/// <remarks>
/// 按进程映像路径判定窗口归属（见 <see cref="WindowLocator"/>）对三类应用无效：
/// 控制台程序（窗口归 WindowsTerminal）、System32 存根（窗口归打包版）、
/// UWP（窗口归 ApplicationFrameHost）。它们共同的特征是身份不再等于
/// 「窗口归属进程的映像路径」，而系统给它们安排的身份就是 AppUserModelID。
///
/// <para>
/// 身份有两个来源，能不能都对上由探针实测决定：
/// <list type="number">
/// <item>窗口侧 <c>SHGetPropertyStoreForWindow</c>——读窗口上<strong>显式设置</strong>的身份；</item>
/// <item>进程侧 <c>GetApplicationUserModelId</c>——只对有包身份的进程有效。</item>
/// </list>
/// 两条路都读不出来时返回 <c>null</c>，调用方退回映像路径匹配。
/// </para>
/// </remarks>
public static class WindowIdentity
{
    /// <summary>一个进程内共享的包装器。理由见 <see cref="ShortcutResolver"/> 同名字段的注释。</summary>
    private static readonly StrategyBasedComWrappers Wrappers = new();

    /// <summary>身份字符串缓冲区的初始容量。大多数 AUMID 短于 128 字符。</summary>
    private const int IdentityCapacity = 512;

    /// <summary>
    /// 读窗口上显式设置的应用身份；没有设置时返回 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// 返回 <c>null</c> 不代表「这个应用没有身份」——只代表窗口属性存储里
    /// 没有显式的那一条。隐式身份（按包身份推导）是否可从窗口读到，
    /// 以探针实测为准。
    /// </remarks>
    public static string? TryGetWindowAumid(nint window)
    {
        if (window == 0)
        {
            return null;
        }

        IPropertyStore? store = null;

        try
        {
            var hr = Shell32.SHGetPropertyStoreForWindow(window, in ShellComIds.PropertyStore, out var pointer);
            if (hr != Ole32.SoK || pointer == 0)
            {
                return null;
            }

            // 包装器会自己 AddRef 一次（与 ShortcutResolver 同一形态），
            // 所以这里持有的接口引用照常释放。
            store = (IPropertyStore)Wrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.UniqueInstance);

            return ReadAumid(store);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 与 ShortcutResolver 同一理由：属性存储读不出来就返回 null，
            // 让调用方退回别的匹配路径，绝不向上抛。
            _ = ex;
            return null;
        }
        finally
        {
            if (store is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    /// <summary>
    /// 读打包进程的应用身份；普通 Win32 进程返回 <c>null</c>。
    /// </summary>
    public static string? TryGetProcessAumid(uint processId)
    {
        if (processId == 0)
        {
            return null;
        }

        var handle = Kernel32.OpenProcess(Kernel32.ProcessQueryLimitedInformation, false, processId);
        if (handle == 0)
        {
            return null;
        }

        try
        {
            var buffer = new char[IdentityCapacity];
            uint length = (uint)buffer.Length;

            var hr = Kernel32.GetApplicationUserModelId(handle, ref length, buffer);

            // 容量不足就按系统给出的长度再问一次，仍是尽力而为。
            if (hr == Ole32.EInsufficientBuffer && length is > 0 and < 4096)
            {
                buffer = new char[length];
                length = (uint)buffer.Length;
                hr = Kernel32.GetApplicationUserModelId(handle, ref length, buffer);
            }

            if (hr != Ole32.SoK)
            {
                // 普通进程在这里返回 APPMODEL_ERROR_NO_APPLICATION——这不是故障，
                // 是「它没有身份」的正式答案，调用方据此退回映像路径匹配。
                return null;
            }

            var value = new string(buffer[..(int)Math.Min(length, (uint)buffer.Length)]).TrimEnd('\0');
            return value.Length == 0 ? null : value;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            _ = Kernel32.CloseHandle(handle);
        }
    }

    /// <summary>
    /// 读 <c>.lnk</c> 上显式设置的 <c>System.AppUserModel.ID</c>；没设置时返回 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// 打包应用与 UWP 的「开始」菜单快捷方式大多带着显式身份，它是
    /// 快捷方式与已运行窗口之间唯一的稳定纽带——这类目标的窗口不归
    /// 快捷方式指向的进程所有，映像路径匹配必失败。
    /// 读不出来返回 <c>null</c>，不抛异常（理由与 <see cref="ShortcutResolver"/> 相同）。
    /// </remarks>
    public static string? TryGetShortcutAumid(string shortcutPath)
    {
        if (string.IsNullOrWhiteSpace(shortcutPath))
        {
            return null;
        }

        IShellLinkW? link = null;
        IPropertyStore? store = null;

        try
        {
            var hr = Ole32.CoCreateInstance(
                in ShellComIds.ShellLinkClass,
                0,
                Ole32.ClsCtxInprocServer,
                in ShellComIds.ShellLinkW,
                out var shellLinkPointer);

            if (hr != Ole32.SoK || shellLinkPointer == 0)
            {
                return null;
            }

            try
            {
                link = (IShellLinkW)Wrappers.GetOrCreateObjectForComInstance(shellLinkPointer, CreateObjectFlags.UniqueInstance);

                var qi = Marshal.QueryInterface(shellLinkPointer, in ShellComIds.PropertyStore, out var storePointer);
                if (qi != Ole32.SoK || storePointer == 0)
                {
                    return null;
                }

                try
                {
                    store = (IPropertyStore)Wrappers.GetOrCreateObjectForComInstance(storePointer, CreateObjectFlags.UniqueInstance);

                    // 身份在「加载之后」才有意义：未加载的 ShellLink 不含文件内容。
                    var persist = (IPersistFile)Wrappers.GetOrCreateObjectForComInstance(
                        shellLinkPointer, CreateObjectFlags.UniqueInstance);

                    try
                    {
                        persist.Load(shortcutPath, Ole32.StgmRead);
                    }
                    finally
                    {
                        if (persist is IDisposable disposablePersist)
                        {
                            disposablePersist.Dispose();
                        }
                    }

                    return ReadAumid(store);
                }
                finally
                {
                    Marshal.Release(storePointer);
                }
            }
            finally
            {
                Marshal.Release(shellLinkPointer);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _ = ex;
            return null;
        }
        finally
        {
            if (link is IDisposable disposableLink)
            {
                disposableLink.Dispose();
            }

            if (store is IDisposable disposableStore)
            {
                disposableStore.Dispose();
            }
        }
    }

    /// <summary>从属性存储里读出 <c>System.AppUserModel.ID</c>。</summary>
    /// <remarks>
    /// internal：快捷方式解析器（<see cref="ShortcutResolver"/>）在已经加载好的
    /// ShellLink 上复用它读显式身份，避免为了一个属性把文件再加载一遍。
    /// </remarks>
    internal static string? ReadAumid(IPropertyStore store)
    {
        var hr = store.GetValue(in PropertyKeys.AppUserModelId, out var value);

        if (hr != Ole32.SoK || value.Type == VarType.Empty || value.Pointer == 0)
        {
            return null;
        }

        if (value.Type != VarType.Lpwstr)
        {
            // 意料之外的类型。值不是字符串就不当身份用，指针也不归我们管。
            return null;
        }

        try
        {
            var text = Marshal.PtrToStringUni(value.Pointer);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        finally
        {
            // VT_LPWSTR 的缓冲区由属性分配器给出，必须用 CoTaskMemFree 释放。
            Ole32.CoTaskMemFree(value.Pointer);
        }
    }
}
