namespace GlassDesk.AppServices;

/// <summary>认得出哪些图片格式。</summary>
public enum ImageFormat
{
    Png = 0,
    Jpeg = 1,
    Bmp = 2,
    Gif = 3,
    Ico = 4,
}

/// <summary>
/// 按文件头判断图片格式。
/// </summary>
/// <remarks>
/// 不看扩展名：用户完全可能把一个 PNG 存成 <c>.jpg</c>，
/// 也可能给自定义图标起任何名字。文件头是唯一可靠的依据。
///
/// <para>
/// 只判断格式，不解码。<strong>用能解码来验证合规性需要图像库，而产品不引第三方依赖</strong>；
/// 判断格式能挡住「拖了一个 PDF 进来」这类明显错误，这正是这个入口最需要防的。
/// 真的解码失败时，界面自己会显示占位图，不会更糟。
/// </para>
/// </remarks>
public static class ImageFormatSniffer
{
    /// <summary>认不出来的格式返回 null。</summary>
    public static ImageFormat? Detect(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        // 足够的字节数覆盖下面每一种签名里最长的一个。
        Span<byte> header = stackalloc byte[16];

        int read;

        try
        {
            using var stream = File.OpenRead(path);
            read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }

        if (read < 4)
        {
            return null;
        }

        var bytes = header[..read];

        if (StartsWith(bytes, PngSignature))
        {
            return ImageFormat.Png;
        }

        if (StartsWith(bytes, JpegSignature))
        {
            return ImageFormat.Jpeg;
        }

        if (StartsWith(bytes, BmpSignature))
        {
            return ImageFormat.Bmp;
        }

        if (StartsWith(bytes, GifSignature))
        {
            return ImageFormat.Gif;
        }

        // ICO 用 00 00 01 00 开头。CUR 是 00 00 02 00，光标不当图标用，因此只认前者。
        if (StartsWith(bytes, IcoSignature))
        {
            return ImageFormat.Ico;
        }

        return null;
    }

    /// <summary>字节序列比较，避免为了比几个常量而分配数组。</summary>
    public static bool StartsWith(ReadOnlySpan<byte> data, ReadOnlySpan<byte> prefix)
        => data.Length >= prefix.Length && data[..prefix.Length].SequenceEqual(prefix);

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static ReadOnlySpan<byte> JpegSignature => [0xFF, 0xD8, 0xFF];

    private static ReadOnlySpan<byte> BmpSignature => [0x42, 0x4D];

    private static ReadOnlySpan<byte> GifSignature => [0x47, 0x49, 0x46, 0x38];

    private static ReadOnlySpan<byte> IcoSignature => [0x00, 0x00, 0x01, 0x00];

    /// <summary>缓存文件用什么扩展名。名字要与格式一致，否则后面加载时会挑错解码器。</summary>
    public static string FileExtensionFor(ImageFormat format) => format switch
    {
        ImageFormat.Png => ".png",
        ImageFormat.Jpeg => ".jpg",
        ImageFormat.Bmp => ".bmp",
        ImageFormat.Gif => ".gif",
        ImageFormat.Ico => ".ico",
        _ => ".bin",
    };
}