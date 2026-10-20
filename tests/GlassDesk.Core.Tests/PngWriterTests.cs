using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using GlassDesk.Core.Imaging;

namespace GlassDesk.Core.Tests;

/// <summary>
/// PNG 编码器的测试。
/// </summary>
/// <remarks>
/// 这个编码器是手写的——产品不引第三方图像库，而图标要存成 PNG。
/// 手写二进制格式最容易出的错是「文件写出来了、大小也对，但内容不合规范」，
/// 因此这里的每一条都不是「文件存在」，而是把字节解回来核对：
/// 分块结构、CRC、行首滤波字节、以及 BGRA 到 RGBA 的通道顺序。
///
/// <para>
/// 解压用的是 .NET 自带的 <see cref="ZLibStream"/>，与编码器用的是同一个实现，
/// 所以它不能证明「别的程序也读得懂」。那件事由产品里的
/// <c>--verify-icons</c> 负责——它用系统的图像解码器（WIC）去读真正的文件。
/// 两者互补：这里验证我们自己写下去的每个字节，那里验证文件能被独立实现接受。
/// </para>
/// </remarks>
public class PngWriterTests
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Fact]
    public void 文件头是标准的八字节签名()
    {
        var png = PngWriter.EncodeBgra(new byte[4], 1, 1);

        Assert.Equal(PngSignature, png.Take(8));
    }

    [Fact]
    public void 分块结构与长度都正确()
    {
        var png = PngWriter.EncodeBgra(new byte[16], 2, 2);
        var chunks = ReadChunks(png);

        Assert.Equal(["IHDR", "IDAT", "IEND"], chunks.Select(chunk => chunk.Type));
        Assert.Equal(13, chunks[0].Length);
        Assert.Equal(0, chunks[2].Length);

        // 长度字段要回过头去对得上，不然文件停在任何长度检查上都过不了。
        foreach (var (length, type, data, offset) in chunks)
        {
            Assert.Equal(data.Length, length);
            Assert.Equal(length, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset)));
            Assert.Equal(Encoding.ASCII.GetBytes(type), png.Skip(offset + 4).Take(4));
        }
    }

    [Fact]
    public void 每个分块的CRC都对得上()
    {
        var png = PngWriter.EncodeBgra(RandomPixels(7, 5), 7, 5);

        foreach (var (_, _, _, offset) in ReadChunks(png))
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset));
            var stored = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset + 8 + length));

            // CRC 覆盖类型与数据两段，不含长度字段。
            var expected = ComputeCrc(png.AsSpan(offset + 4, 4 + length));

            Assert.Equal(expected, stored);
        }
    }

    [Fact]
    public void IHDR里的宽高与颜色类型正确()
    {
        var png = PngWriter.EncodeBgra(new byte[300 * 200 * 4], 300, 200);
        var header = ReadChunks(png)[0].Data;

        Assert.Equal(300, BinaryPrimitives.ReadInt32BigEndian(header));
        Assert.Equal(200, BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4)));
        Assert.Equal(8, header[8]);
        Assert.Equal(6, header[9]);
        Assert.Equal(0, header[10]);
        Assert.Equal(0, header[11]);
        Assert.Equal(0, header[12]);
    }

    [Fact]
    public void 逐行可解且通道顺序从BGRA换成了RGBA()
    {
        // 一个 3×1 的图，三个像素分别是红、绿、蓝（BGRA 排列）。
        byte[] bgra =
        [
            0x00, 0x00, 0xFF, 0xFF,  // 蓝=00 绿=00 红=FF → 红
            0x00, 0xFF, 0x00, 0xFF,  // → 绿
            0xFF, 0x00, 0x00, 0xFF,  // → 蓝
        ];

        var png = PngWriter.EncodeBgra(bgra, 3, 1);
        var raw = InflateImageData(png);

        // 每行开头一个滤波字节，这里恒为 0（None）。
        Assert.Equal(1 + (3 * 4), raw.Length);
        Assert.Equal(0, raw[0]);

        Assert.Equal([0xFF, 0x00, 0x00, 0xFF], raw[1..5]);
        Assert.Equal([0x00, 0xFF, 0x00, 0xFF], raw[5..9]);
        Assert.Equal([0x00, 0x00, 0xFF, 0xFF], raw[9..13]);
    }

    [Fact]
    public void 多行时每行都有自己的滤波字节()
    {
        var png = PngWriter.EncodeBgra(RandomPixels(4, 3), 4, 3);
        var raw = InflateImageData(png);

        var stride = 1 + (4 * 4);
        Assert.Equal(stride * 3, raw.Length);

        for (var y = 0; y < 3; y++)
        {
            Assert.Equal(0, raw[y * stride]);
        }
    }

    [Fact]
    public void 像素数据被原样保留()
    {
        var pixels = RandomPixels(16, 12);
        var png = PngWriter.EncodeBgra(pixels, 16, 12);
        var raw = InflateImageData(png);

        var stride = 1 + (16 * 4);

        for (var y = 0; y < 12; y++)
        {
            for (var x = 0; x < 16; x++)
            {
                var from = ((y * 16) + x) * 4;
                var to = (y * stride) + 1 + (x * 4);

                Assert.Equal(pixels[from + 2], raw[to]);
                Assert.Equal(pixels[from + 1], raw[to + 1]);
                Assert.Equal(pixels[from], raw[to + 2]);
                Assert.Equal(pixels[from + 3], raw[to + 3]);
            }
        }
    }

    [Fact]
    public void 全透明的图像也能被编码()
    {
        var png = PngWriter.EncodeBgra(new byte[64], 4, 4);
        var raw = InflateImageData(png);

        Assert.All(raw, value => Assert.Equal(0, value));
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(5, 0)]
    [InlineData(-1, 5)]
    public void 宽高非法时拒绝编码(int width, int height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PngWriter.EncodeBgra(new byte[4], width, height));
    }

    [Fact]
    public void 像素数量与宽高对不上时拒绝编码()
    {
        // 少给一个像素：不拒绝的话，写出来的是一个宽度正确、最后一行缺一截的图。
        var pixels = new byte[(4 * 4 * 4) - 4];

        var error = Assert.Throws<ArgumentException>(() => PngWriter.EncodeBgra(pixels, 4, 4));
        Assert.Contains("像素数据长度", error.Message);
    }

    private static byte[] RandomPixels(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        new Random(width * 1000 + height).NextBytes(pixels);
        return pixels;
    }

    private static byte[] InflateImageData(byte[] png)
    {
        var idat = ReadChunks(png).First(chunk => chunk.Type == "IDAT").Data;

        using var input = new MemoryStream(idat);
        using var inflate = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();

        inflate.CopyTo(output);
        return output.ToArray();
    }

    private static List<(int Length, string Type, byte[] Data, int Offset)> ReadChunks(byte[] png)
    {
        var chunks = new List<(int, string, byte[], int)>();
        var position = 8;

        while (position + 12 <= png.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(position));
            var type = Encoding.ASCII.GetString(png, position + 4, 4);
            var data = png.Skip(position + 8).Take(length).ToArray();

            chunks.Add((length, type, data, position));

            position += 12 + length;

            if (type == "IEND")
            {
                break;
            }
        }

        return chunks;
    }

    /// <summary>测试里独立实现一遍 CRC，避免与产品代码共用同一份实现而互相掩护。</summary>
    private static uint ComputeCrc(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;

        foreach (var value in data)
        {
            crc ^= value;

            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }

        return crc ^ 0xFFFFFFFFu;
    }
}