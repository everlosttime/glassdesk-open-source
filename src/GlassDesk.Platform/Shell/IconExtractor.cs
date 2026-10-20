using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using GlassDesk.Platform.Interop;

namespace GlassDesk.Platform.Shell;

/// <summary>一张提取出来的图标：自上而下的 BGRA 像素。</summary>
/// <param name="Width">宽（像素）。</param>
/// <param name="Height">高（像素）。</param>
/// <param name="Bgra">每像素 4 字节，首行为顶部。</param>
public sealed record ExtractedIcon(int Width, int Height, byte[] Bgra)
{
    /// <summary>真正有内容的像素数（alpha 不为 0）。全空说明这是一张空图，不该被缓存。</summary>
    public int VisiblePixelCount
    {
        get
        {
            var count = 0;
            for (var i = 3; i < Bgra.Length; i += 4)
            {
                if (Bgra[i] != 0)
                {
                    count++;
                }
            }

            return count;
        }
    }
}

/// <summary>
/// 从文件或 shell 图标位置取出图标像素。
/// </summary>
/// <remarks>
/// 三级降级，按能得到的分辨率从高到低：
/// <list type="number">
/// <item><c>IShellItemImageFactory</c>——实测 256×256，带真实 alpha。高 DPI 下不糊，是首选。</item>
/// <item><c>ExtractIconEx</c>——按索引取，用于快捷方式图标位置写成
/// <c>shell32.dll,3</c> 这类「某文件里的第 N 个图标」的情况。实测 32×32。</item>
/// <item><c>SHGetFileInfo</c>——按扩展名兜底，即使目标不存在也能出图。实测 32×32。</item>
/// </list>
///
/// <para>
/// 任一级失败就落到下一级，全部失败返回 null。图标取不到只是界面少一张图，
/// 不该让导入失败。
/// </para>
/// </remarks>
public static class IconExtractor
{
    /// <summary>向 <c>IShellItemImageFactory</c> 请求的边长。够程序坞在 200% 缩放下使用。</summary>
    public const int PreferredSize = 256;

    private static readonly StrategyBasedComWrappers Wrappers = new();

    /// <summary>
    /// 从 <paramref name="iconSource"/> 取图标。
    /// </summary>
    /// <param name="iconSource">
    /// 文件路径，或 <c>路径,索引</c> 形式。索引可以是负数——那表示 DLL 里的资源号，
    /// <c>ExtractIconEx</c> 接受这种写法，我们原样传给它。
    /// </param>
    public static ExtractedIcon? Extract(string? iconSource)
    {
        if (string.IsNullOrWhiteSpace(iconSource))
        {
            return null;
        }

        var (path, index) = SplitIconSource(iconSource);
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        // 有明确索引时先按索引取：那条路径给出的正是用户看到的那个图标，
        // 而 IShellItemImageFactory 只会给这个文件的第 0 个图标。
        if (index is not null || Path.GetExtension(path).Length == 0)
        {
            var byIndex = ExtractResource(path, index ?? 0) ?? ExtractByIndex(path, index ?? 0);
            if (byIndex is { VisiblePixelCount: > 0 })
            {
                return byIndex;
            }
        }

        return ExtractByShellItem(path, PreferredSize)
            ?? ExtractByIndex(path, 0)
            ?? ExtractByFileInfo(path);
    }

    private static ExtractedIcon? ExtractResource(string path, int index)
    {
        nint icon = 0;

        try
        {
            var result = Shell32.SHDefExtractIcon(path, index, 0, out icon, 0, PreferredSize);
            return result == Ole32.SoK && icon != 0 ? FromIconHandle(icon) : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            if (icon != 0)
            {
                _ = User32.DestroyIcon(icon);
            }
        }
    }

    /// <summary>
    /// 把 <c>路径,索引</c> 拆开。
    /// </summary>
    /// <remarks>
    /// 必须从**最后一个**逗号拆，因为目录名里可以有逗号（实测 <c>D:\a,b\app.exe</c>
    /// 是合法路径）。只有当逗号后面整体是整数时才认为它是索引。
    /// </remarks>
    public static (string Path, int? Index) SplitIconSource(string iconSource)
    {
        var trimmed = iconSource.Trim();
        var comma = trimmed.LastIndexOf(',');

        if (comma <= 0 || comma == trimmed.Length - 1)
        {
            return (trimmed, null);
        }

        var tail = trimmed[(comma + 1)..].Trim();
        if (int.TryParse(tail, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var index))
        {
            return (trimmed[..comma].Trim(), index);
        }

        return (trimmed, null);
    }

    /// <summary>首选路径：向外壳要一张指定尺寸的图标。</summary>
    private static ExtractedIcon? ExtractByShellItem(string path, int size)
    {
        nint instancePointer = 0;
        IShellItemImageFactory? factory = null;

        try
        {
            var hr = Shell32.SHCreateItemFromParsingName(path, 0, in ShellComIds.ShellItemImageFactory, out instancePointer);
            if (hr != Ole32.SoK || instancePointer == 0)
            {
                return null;
            }

            factory = (IShellItemImageFactory)Wrappers.GetOrCreateObjectForComInstance(instancePointer, CreateObjectFlags.UniqueInstance);

            // SIZE 必须整块传；拆成两个 int 会让后续参数整体错位（实测 0xC0000005）。
            var flags = Siigbf.IconOnly | Siigbf.BiggerSizeOk;

            var imageHr = factory.GetImage(new Size { Width = size, Height = size }, flags, out var bitmap);
            if (imageHr != Ole32.SoK || bitmap == 0)
            {
                return null;
            }

            try
            {
                return ReadBitmapPixels(bitmap);
            }
            finally
            {
                _ = Gdi32.DeleteObject(bitmap);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 兜住所有异常，理由与 ShortcutResolver 那里同：COM 层会把失败 HRESULT
            // 映射成各式各样的 BCL 异常，列举类型一定会漏，而漏掉一个的后果是
            // 取图标失败变成导入失败。取不到图标只是少一张图。
            _ = ex;
            return null;
        }
        finally
        {
            if (factory is IDisposable disposable)
            {
                disposable.Dispose();
            }

            if (instancePointer != 0)
            {
                Marshal.Release(instancePointer);
            }
        }
    }

    /// <summary>按索引从 exe/dll 里取图标。索引为负时表示资源号。</summary>
    private static ExtractedIcon? ExtractByIndex(string path, int index)
    {
        nint large = 0;
        nint small = 0;

        try
        {
            var count = Shell32.ExtractIconEx(path, index, out large, out small, 1);

            return count == 0 || large == 0 ? null : FromIconHandle(large);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            if (large != 0)
            {
                _ = User32.DestroyIcon(large);
            }

            if (small != 0)
            {
                _ = User32.DestroyIcon(small);
            }
        }
    }

    /// <summary>兜底：按文件类型取系统图标。目标不存在时也能出图。</summary>
    private static ExtractedIcon? ExtractByFileInfo(string path)
    {
        try
        {
            var info = new Shell32.ShellFileInfo();
            var flags = Shell32.ShgfiIcon | Shell32.ShgfiLargeIcon;

            // 目标不在时按扩展名推断，否则会返回「未知文件」的通用图标。
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                flags |= Shell32.ShgfiUseFileAttributes;
            }

            var result = Shell32.SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<Shell32.ShellFileInfo>(), flags);
            if (result == 0 || info.Icon == 0)
            {
                return null;
            }

            try
            {
                return FromIconHandle(info.Icon);
            }
            finally
            {
                _ = User32.DestroyIcon(info.Icon);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>把一个 <c>HICON</c> 读成 BGRA 像素。</summary>
    private static ExtractedIcon? FromIconHandle(nint icon)
    {
        if (icon == 0 || !User32.GetIconInfo(icon, out var info))
        {
            return null;
        }

        try
        {
            if (info.ColorBitmap == 0)
            {
                return null;
            }

            return ReadBitmapPixels(info.ColorBitmap);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            // GetIconInfo 复制出两张位图，归调用方所有，必须显式删除，
            // 否则每次取图标都泄漏一对 GDI 对象。
            if (info.ColorBitmap != 0)
            {
                _ = Gdi32.DeleteObject(info.ColorBitmap);
            }

            if (info.MaskBitmap != 0)
            {
                _ = Gdi32.DeleteObject(info.MaskBitmap);
            }
        }
    }

    /// <summary>
    /// 从位图读出 BGRA。
    /// </summary>
    /// <remarks>
    /// 实测外壳给出的 alpha 是<strong>直通</strong>的，不是预乘的：
    /// <c>GetImage</c> 的结果里有 6215 个像素的某个色通道大于 alpha，
    /// 预乘时这不可能出现。所以写 PNG 前不需要反预乘。
    /// </remarks>
    private static unsafe ExtractedIcon? ReadBitmapPixels(nint bitmap)
    {
        if (bitmap == 0 || Gdi32.GetObject(bitmap, Marshal.SizeOf<Bitmap>(), out var source) == 0)
        {
            return null;
        }

        if (source.Width <= 0 || source.Height <= 0 || source.Width > 4096 || source.Height > 4096)
        {
            return null;
        }

        var width = source.Width;
        var height = source.Height;
        var pixels = new byte[width * height * 4];

        // 高度取负数 = 自上而下，读出来直接就是 PNG 的行序。
        var header = new BitmapInfoHeader
        {
            Size = Marshal.SizeOf<BitmapInfoHeader>(),
            Width = width,
            Height = -height,
            Planes = 1,
            BitCount = 32,
            Compression = 0,
        };

        var info = new BitmapInfo { Header = header };
        var deviceContext = Gdi32.GetDC(0);

        if (deviceContext == 0)
        {
            return null;
        }

        try
        {
            fixed (byte* destination = pixels)
            {
                if (Gdi32.GetDIBits(deviceContext, bitmap, 0, (uint)height, destination, &info) == 0)
                {
                    return null;
                }
            }
        }
        finally
        {
            _ = Gdi32.ReleaseDC(0, deviceContext);
        }

        return new ExtractedIcon(width, height, pixels);
    }
}
