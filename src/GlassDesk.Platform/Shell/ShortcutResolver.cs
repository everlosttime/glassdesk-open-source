using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using GlassDesk.Platform.Interop;

namespace GlassDesk.Platform.Shell;

/// <summary>一个 <c>.lnk</c> 里读出来的东西。</summary>
/// <param name="Target">目标路径，或者当快捷方式指向网页时的 URL。</param>
/// <param name="Arguments">命令行参数；没有参数时为空串。</param>
/// <param name="WorkingDirectory">工作目录；快捷方式没写时为空串。</param>
/// <param name="IconSource">图标来源。索引为 0 时就是目标文件本身，否则形如 <c>C:\Windows\system32\shell32.dll,3</c>。</param>
/// <param name="Description">快捷方式的描述文字，可能为空。</param>
/// <param name="HasShellItem">
/// 快捷方式确实指向某个外壳项，只是那个项没有文件路径。
/// </param>
/// <param name="Aumid">
/// 快捷方式上显式设置的应用身份（<c>System.AppUserModel.ID</c>）；没设置为 null。
/// </param>
public sealed record ShortcutInfo(
    string Target,
    string Arguments,
    string WorkingDirectory,
    string IconSource,
    string Description,
    bool HasShellItem,
    string? Aumid = null)
{
    /// <summary>目标是个 http/https 地址，而不是文件路径。</summary>
    public bool TargetIsUrl =>
        Uri.TryCreate(Target, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>
    /// 读出了文件路径或网址，可以直接用。
    /// </summary>
    /// <remarks>
    /// 为 false 且 <see cref="HasShellItem"/> 为 true 时，调用方应当保留 <c>.lnk</c>
    /// 本身作为目标——外壳仍然打得开它，只是我们自己解不出它指向什么。
    /// </remarks>
    public bool HasResolvedTarget => !string.IsNullOrWhiteSpace(Target);
}

/// <summary>
/// 解析 Windows 快捷方式（<c>.lnk</c>）。
/// </summary>
/// <remarks>
/// 走 COM 的 <c>IShellLink</c> + <c>IPersistFile</c>——外壳自己解析，而不是
/// 我们去读 <c>.lnk</c> 的二进制格式。这样各种边角情况（相对路径、环境变量、
/// 已知文件夹引用、UNC 目标）都由系统负责，我们不必自己维护一份格式解析器。
///
/// <para>
/// 读不出来时返回 <c>null</c> 而不是抛异常：一个坏掉的快捷方式不该让整批导入失败。
/// </para>
/// </remarks>
public static class ShortcutResolver
{
    /// <summary>
    /// 路径缓冲区容量。
    /// </summary>
    /// <remarks>
    /// <c>MAX_PATH</c> 是 260，但长路径（已解除 260 限制的机器）与
    /// 环境变量展开后的结果都可能更长，所以留一倍多。
    /// </remarks>
    private const int PathCapacity = 1024;

    /// <summary>
    /// 一个进程内共享的包装器。
    /// </summary>
    /// <remarks>
    /// 源生成的 COM 互操作需要一个 <c>ComWrappers</c> 实例把接口指针变成托管对象。
    /// 调用一律带 <c>UniqueInstance</c>，它不做缓存，所以一个实例可以服务全部解析；
    /// 每解析一个快捷方式就新建一个，等于白白注册上百次。
    /// </remarks>
    private static readonly StrategyBasedComWrappers Wrappers = new();

    /// <summary>解析一个 <c>.lnk</c>；读不出来时返回 null。</summary>
    public static ShortcutInfo? Resolve(string shortcutPath)
    {
        if (string.IsNullOrWhiteSpace(shortcutPath))
        {
            return null;
        }

        IShellLinkW? link = null;
        IPersistFile? persist = null;
        string? aumid = null;

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

            // 包装器会自己 AddRef 一次（阶段 3 探针实测：引用计数由 1 变 2），
            // 所以我们手上这一个引用照常释放，不会多放。
            link = (IShellLinkW)Wrappers.GetOrCreateObjectForComInstance(shellLinkPointer, CreateObjectFlags.UniqueInstance);

            try
            {
                var qi = Marshal.QueryInterface(shellLinkPointer, in ShellComIds.PersistFile, out var persistPointer);
                if (qi != Ole32.SoK || persistPointer == 0)
                {
                    return null;
                }

                persist = (IPersistFile)Wrappers.GetOrCreateObjectForComInstance(persistPointer, CreateObjectFlags.UniqueInstance);

                try
                {
                    persist.Load(shortcutPath, Ole32.StgmRead);
                }
                finally
                {
                    Marshal.Release(persistPointer);
                }

                // 加载成功的 ShellLink 上顺带读显式应用身份（System.AppUserModel.ID）。
                // 打包应用与 UWP 的窗口不归目标进程所有，这个身份是「快捷方式 ↔ 已运行窗口」
                // 之间唯一可靠的纽带（阶段 4 探针实测）。读不出来保持 null——
                // 身份匹配是尽力而为的增强，绝不是导入成败的一部分。
                var storeQI = Marshal.QueryInterface(shellLinkPointer, in ShellComIds.PropertyStore, out var storePointer);

                if (storeQI == Ole32.SoK && storePointer != 0)
                {
                    IPropertyStore? identityStore = null;
                    try
                    {
                        identityStore = (IPropertyStore)Wrappers.GetOrCreateObjectForComInstance(
                            storePointer, CreateObjectFlags.UniqueInstance);
                        aumid = WindowIdentity.ReadAumid(identityStore);
                    }
                    finally
                    {
                        Marshal.Release(storePointer);

                        if (identityStore is IDisposable disposableStore)
                        {
                            disposableStore.Dispose();
                        }
                    }
                }
            }
            finally
            {
                Marshal.Release(shellLinkPointer);
            }

            var target = Read(link, static (instance, buffer, capacity) => instance.GetPath(buffer, capacity, 0, 0));

            // 目标是网址时 GetPath 一定返回空串——网址存在 ID 列表里，不在路径字段里。
            // 实测四种标志（默认 / SLGP_RAWPATH / SLGP_SHORTPATH / UNC 优先）全是空串，
            // 所以只能改用 ID 列表去问。不补这一步的话，网页快捷方式会被当成
            // 「读不出目标」而降级，用户看到的是一个叫「某某网页」的快捷方式条目，
            // 而不是一条网页记录。
            var itemIdList = ReadIdList(link);

            try
            {
                if (string.IsNullOrWhiteSpace(target))
                {
                    target = ReadUrlFromIdList(itemIdList) ?? string.Empty;
                }

                // 路径与 ID 列表都是空，说明这个快捷方式不指向任何东西——
                // 空文件、或者一个只剩外壳的坏文件。这类条目收下只会得到
                // 「按下去没反应」，不如逐条报出来。
                if (string.IsNullOrWhiteSpace(target) && itemIdList == 0)
                {
                    return null;
                }

                var arguments = Read(link, static (instance, buffer, capacity) => instance.GetArguments(buffer, capacity));
                var workingDirectory = Read(link, static (instance, buffer, capacity) => instance.GetWorkingDirectory(buffer, capacity));
                var description = Read(link, static (instance, buffer, capacity) => instance.GetDescription(buffer, capacity));

                var iconIndex = 0;
                var iconLocation = Read(link, (instance, buffer, capacity) => instance.GetIconLocation(buffer, capacity, out iconIndex));

                return new ShortcutInfo(
                    target,
                    arguments,
                    workingDirectory,
                    BuildIconSource(iconLocation, iconIndex, target),
                    description,
                    HasShellItem: itemIdList != 0,
                    Aumid: aumid);
            }
            finally
            {
                // PIDL 是外壳给的结构，必须用 ILFree 释放，不能用 CoTaskMemFree。
                if (itemIdList != 0)
                {
                    Shell32.ILFree(itemIdList);
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 这里刻意兜住**所有**异常，而不是列举几种类型。
            //
            // 源生成的 COM 互操作会把失败 HRESULT 映射成具体的 BCL 异常，映射面很宽：
            // 实测目标文件不存在时 <c>IPersistFile::Load</c> 抛的是
            // <c>FileNotFoundException</c>（HRESULT 0x80070002），而不是 <c>COMException</c>。
            // 第一版只捕获了 COMException / InvalidComObjectException / DllNotFound /
            // EntryPointNotFound 四种，于是这个异常直接穿过了「绝不抛出」的契约。
            //
            // 漏掉一个类型的代价不是「少报告一条失败」，而是**整批导入被这个异常带走**——
            // 用户拖进来二十个文件，其中一个快捷方式坏了，二十个全没了。
            // 对这样一个契约是「读不出来就返回 null」的尽力而为解析器，
            // 列举类型是一场必输的赌博。
            //
            // 唯一的例外是 OutOfMemoryException：那时进程已经不可信，
            // 继续吞掉它只会让后续行为更不可预期。
            _ = ex;
            return null;
        }
        finally
        {
            // 两个引用都要放：包装器各持一次，我们各持一次。
            if (persist is IDisposable disposablePersist)
            {
                disposablePersist.Dispose();
            }

            if (link is IDisposable disposableLink)
            {
                disposableLink.Dispose();
            }
        }
    }

    /// <summary>取快捷方式的 ID 列表；调用方负责用 <c>ILFree</c> 释放。</summary>
    private static nint ReadIdList(IShellLinkW link)
    {
        link.GetIDList(out var itemIdList);
        return itemIdList;
    }

    /// <summary>
    /// 从快捷方式的 ID 列表里读出网址。
    /// </summary>
    /// <remarks>
    /// 只在结果是 http/https 时才采纳。对普通的文件快捷方式，同一个调用会返回
    /// <c>file:///C:/...</c> 这样的文件 URL——那不是我们要的「网页」，
    /// 而且它出现的前提本来就是路径字段已经读出了内容，所以这里再挡一道。
    /// </remarks>
    private static string? ReadUrlFromIdList(nint itemIdList)
    {
        if (itemIdList == 0)
        {
            return null;
        }

        nint name = 0;

        try
        {
            var hr = Shell32.SHGetNameFromIDList(itemIdList, Shell32.SigdnUrl, out name);
            if (hr != Ole32.SoK || name == 0)
            {
                return null;
            }

            var url = Marshal.PtrToStringUni(name);

            return Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                    ? url
                    : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            if (name != 0)
            {
                Ole32.CoTaskMemFree(name);
            }
        }
    }

    /// <summary>
    /// 把 <c>IShellLink::GetIconLocation</c> 的位置与索引拼成可以再次提取的形式。
    /// </summary>
    /// <remarks>
    /// 索引非 0 时必须把索引一起带上。索引指的是「那个文件里的第几个图标」，
    /// <c>C:\Windows\system32\shell32.dll,3</c> 与 <c>shell32.dll</c> 是两回事——
    /// 后者会拿到 shell32 的第 0 个图标，完全不是用户看到的那个。
    /// </remarks>
    private static string BuildIconSource(string iconLocation, int iconIndex, string target)
    {
        var location = string.IsNullOrWhiteSpace(iconLocation) ? target : iconLocation;

        if (string.IsNullOrWhiteSpace(location))
        {
            return string.Empty;
        }

        return iconIndex == 0 ? location : $"{location},{iconIndex}";
    }

    /// <summary>在非托管缓冲区上调用一个「写字符串」的方法，再把它读回托管字符串。</summary>
    private static string Read(IShellLinkW link, Action<IShellLinkW, nint, int> read)
    {
        var byteCount = PathCapacity * sizeof(char);
        var buffer = Marshal.AllocHGlobal(byteCount);

        try
        {
            // 先清零，这样即使方法什么都没写，读回来也是空串而不是垃圾。
            unsafe
            {
                new Span<byte>((void*)buffer, byteCount).Clear();
            }

            read(link, buffer, PathCapacity);

            // 这些方法保证以 null 结尾，所以一直读到结束符即可。
            return Marshal.PtrToStringUni(buffer) ?? string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}