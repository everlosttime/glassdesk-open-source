using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace GlassDesk.Core.Imaging;

/// <summary>
/// 把 BGRA 像素写成 PNG。
/// </summary>
/// <remarks>
/// 自己写而不是引一个图像库：产品是零第三方依赖的，
/// 而 .NET 自带的 <see cref="ZLibStream"/> 已经提供了 PNG 需要的 deflate 流，
/// 剩下的只是分块、CRC 与行首滤波字节——这些是几十行确定性代码，
/// 比多一个依赖便宜。
///
/// <para>
/// 输入是 <strong>BGRA</strong>（Win32 位图的排列），PNG 要求 <strong>RGBA</strong>，
/// 因此每个像素要把 R 与 B 对调。alpha 直接照搬：实测外壳给出的 alpha 是直通的，
/// 不需要反预乘。
/// </para>
///
/// <para>
/// 只写 8 位 RGBA、无隔行、滤波字节恒为 0（None）。这是 PNG 里最朴素也最通用的形式，
/// 所有解码器都支持；对一个最多几百像素的图标来说，压缩率不值得为之增加复杂度。
/// </para>
/// </remarks>
public static class PngWriter
{
    /// <summary>PNG 的固定文件头。</summary>
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>颜色类型 6 = 真彩带 alpha（RGBA）。</summary>
    private const byte ColorTypeRgba = 6;

    /// <summary>滤波方式 0 = 无滤波。</summary>
    private const byte FilterNone = 0;

    /// <summary>把 BGRA 像素编码成 PNG 字节。</summary>
    /// <param name="bgra">每像素 4 字节，首行为顶部；长度必须是 <c>width * height * 4</c>。</param>
    public static byte[] EncodeBgra(ReadOnlySpan<byte> bgra, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        var expected = checked(width * height * 4);
        if (bgra.Length != expected)
        {
            throw new ArgumentException($"像素数据长度应为 {expected} 字节，实际 {bgra.Length} 字节。", nameof(bgra));
        }

        using var output = new MemoryStream(Signature.Length + expected + 128);

        output.Write(Signature);

        WriteHeader(output, width, height);
        WriteImageData(output, bgra, width, height);
        WriteChunk(output, "IEND", ReadOnlySpan<byte>.Empty);

        return output.ToArray();
    }

    private static void WriteHeader(Stream output, int width, int height)
    {
        Span<byte> header = stackalloc byte[13];

        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 8;
        header[9] = ColorTypeRgba;
        header[10] = 0;
        header[11] = FilterNone;
        header[12] = 0;

        WriteChunk(output, "IHDR", header);
    }

    private static void WriteImageData(Stream output, ReadOnlySpan<byte> bgra, int width, int height)
    {
        // 每行前面多一个滤波字节。
        var stride = (width * 4) + 1;
        var raw = new byte[stride * height];

        for (var y = 0; y < height; y++)
        {
            var destination = y * stride;
            raw[destination] = FilterNone;

            var source = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                var from = source + (x * 4);
                var to = destination + 1 + (x * 4);

                // BGRA -> RGBA：只交换首尾两个通道。
                raw[to] = bgra[from + 2];
                raw[to + 1] = bgra[from + 1];
                raw[to + 2] = bgra[from];
                raw[to + 3] = bgra[from + 3];
            }
        }

        using var compressed = new MemoryStream(raw.Length / 4 + 64);

        // ZLibStream 写出的是完整的 zlib 流（含 2 字节头与结尾的 adler32），
        // 正是 PNG 的 IDAT 需要的格式，不必自己补这两段。
        using (var deflate = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(raw);
        }

        WriteChunk(output, "IDAT", compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);

        // 类型与数据连在一起算 CRC，所以这里不能只把 type 单独喂给一个增量对象后丢弃。
        var typeBytes = Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);

        var crc = Crc32.Compute(typeBytes, data);

        Span<byte> checksum = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, crc);
        output.Write(checksum);
    }

    /// <summary>
    /// PNG 用的 CRC-32：反射多项式 <c>0xEDB88320</c>，初值与末值都取反。
    /// </summary>
    internal static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        internal static uint Compute(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
        {
            var crc = 0xFFFFFFFFu;

            crc = Update(crc, first);
            crc = Update(crc, second);

            return crc ^ 0xFFFFFFFFu;
        }

        private static uint Update(uint crc, ReadOnlySpan<byte> data)
        {
            foreach (var value in data)
            {
                crc = Table[(crc ^ value) & 0xFF] ^ (crc >> 8);
            }

            return crc;
        }

        private static uint[] BuildTable()
        {
            var table = new uint[256];

            for (var i = 0u; i < table.Length; i++)
            {
                var value = i;

                for (var bit = 0; bit < 8; bit++)
                {
                    value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
                }

                table[i] = value;
            }

            return table;
        }
    }
}