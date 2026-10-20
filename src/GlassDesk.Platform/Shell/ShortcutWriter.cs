using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using GlassDesk.Platform.Interop;

namespace GlassDesk.Platform.Shell;

/// <summary>写一个 <c>.lnk</c> 需要填的内容。</summary>
/// <param name="TargetPath">目标路径或网址。</param>
/// <param name="Arguments">命令行参数；没有就留空。</param>
/// <param name="WorkingDirectory">工作目录；没有就留空。</param>
/// <param name="IconLocation">图标位置，<c>路径,索引</c> 形式；没有就留空（用目标的图标）。</param>
/// <param name="Description">描述文字；没有就留空。</param>
/// <param name="Aumid">
/// 显式写入的应用身份（<c>System.AppUserModel.ID</c>）；没有就留空。
/// 阶段 4 的验收样本要用它造出「打包应用快捷方式」的替身。
/// </param>
public sealed record ShortcutDefinition(
    string TargetPath,
    string Arguments = "",
    string WorkingDirectory = "",
    string IconLocation = "",
    string Description = "",
    string Aumid = "");

/// <summary>
/// 创建一个 <c>.lnk</c>。
/// </summary>
/// <remarks>
/// 产品本身的运行路径<strong>不写</strong>快捷方式——导入只读不写。这个类型是给
/// 构造测试样本用的：验收要求「含中文、空格、无效路径、带参数的快捷方式」这些样本
/// 必须能被重复地造出来，而靠人工在资源管理器里右键新建既不可复现，
/// 也没法交给脚本。
///
/// <para>
/// 走的是与 <see cref="ShortcutResolver"/> 同一套 COM 接口，只是调 <c>Set*</c> 与
/// <c>IPersistFile::Save</c>。这样样本的写法与读法共用一份声明，
/// 不会出现「测试样本用别的方式造出来，因而测不到真实情况」。
/// </para>
/// </remarks>
public static class ShortcutWriter
{
    private static readonly StrategyBasedComWrappers Wrappers = new();

    /// <summary>把一份定义写成 <paramref name="shortcutPath"/>；失败返回 false。</summary>
    public static bool Write(string shortcutPath, ShortcutDefinition definition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shortcutPath);
        ArgumentNullException.ThrowIfNull(definition);

        IShellLinkW? link = null;
        IPersistFile? persist = null;

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
                return false;
            }

            link = (IShellLinkW)Wrappers.GetOrCreateObjectForComInstance(shellLinkPointer, CreateObjectFlags.UniqueInstance);

            try
            {
                var qi = Marshal.QueryInterface(shellLinkPointer, in ShellComIds.PersistFile, out var persistPointer);
                if (qi != Ole32.SoK || persistPointer == 0)
                {
                    return false;
                }

                persist = (IPersistFile)Wrappers.GetOrCreateObjectForComInstance(persistPointer, CreateObjectFlags.UniqueInstance);

                try
                {
                    link.SetPath(definition.TargetPath);

                    if (!string.IsNullOrEmpty(definition.Arguments))
                    {
                        link.SetArguments(definition.Arguments);
                    }

                    if (!string.IsNullOrEmpty(definition.WorkingDirectory))
                    {
                        link.SetWorkingDirectory(definition.WorkingDirectory);
                    }

                    if (!string.IsNullOrEmpty(definition.Description))
                    {
                        link.SetDescription(definition.Description);
                    }

                    if (!string.IsNullOrEmpty(definition.IconLocation))
                    {
                        // 图标位置是「路径,索引」的形式，但 SetIconLocation 要的是分开的两段。
                        var (iconPath, iconIndex) = IconExtractor.SplitIconSource(definition.IconLocation);
                        link.SetIconLocation(iconPath, iconIndex ?? 0);
                    }

                    if (!string.IsNullOrEmpty(definition.Aumid))
                    {
                        // 显式身份走属性存储：SetValue 之后必须 Commit，否则内容不落盘。
                        var storeQI = Marshal.QueryInterface(shellLinkPointer, in ShellComIds.PropertyStore, out var storePointer);
                        if (storeQI != Ole32.SoK || storePointer == 0)
                        {
                            return false;
                        }

                        IPropertyStore? store = null;
                        try
                        {
                            store = (IPropertyStore)Wrappers.GetOrCreateObjectForComInstance(
                                storePointer, CreateObjectFlags.UniqueInstance);

                            var text = Marshal.StringToCoTaskMemUni(definition.Aumid);
                            if (text == 0)
                            {
                                return false;
                            }

                            try
                            {
                                var value = new PropVariant
                                {
                                    Type = VarType.Lpwstr,
                                    Pointer = text,
                                };

                                // SetValue 的约定：存储在 Commit 之前只引用这份缓冲，
                                // 所有权仍在调用方，所以统一在 finally 里释放。
                                if (store.SetValue(in PropertyKeys.AppUserModelId, in value) != Ole32.SoK
                                    || store.Commit() != Ole32.SoK)
                                {
                                    return false;
                                }
                            }
                            finally
                            {
                                Ole32.CoTaskMemFree(text);
                            }
                        }
                        finally
                        {
                            Marshal.Release(storePointer);

                            if (store is IDisposable disposableStore)
                            {
                                disposableStore.Dispose();
                            }
                        }
                    }

                    // 第二个参数 true 表示立即写盘，而不是等对象被释放。
                    persist.Save(shortcutPath, true);
                }
                finally
                {
                    Marshal.Release(persistPointer);
                }
            }
            finally
            {
                Marshal.Release(shellLinkPointer);
            }

            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 同 ShortcutResolver：COM 层的异常类型列举不全，而这里是
            // 「能不能写出样本」这个是非问题，用返回值表达就够了。
            _ = ex;
            return false;
        }
        finally
        {
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
}