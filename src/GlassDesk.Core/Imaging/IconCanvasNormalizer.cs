namespace GlassDesk.Core.Imaging;

/// <summary>
/// 裁掉图标位图中多余的透明边距，让实际图案在启动台和程序坞里占据合适的面积。
/// </summary>
public static class IconCanvasNormalizer
{
    private const byte MinimumContentAlpha = 16;
    private const long MinimumProjectionAlpha = 510;
    private const int PaddingDivisor = 14;
    private const int EdgeComponentAreaDivisor = 8;
    private const int EdgeCoverageDivisor = 20;

    /// <summary>
    /// 根据 alpha 内容边界裁剪像素，并在四周保留少量透明边距。
    /// 无法可靠识别内容边界或图案已接近画布边缘时，原样返回。
    /// </summary>
    /// <param name="bgra">每像素 4 字节，首行为顶部。</param>
    /// <param name="width">图像宽度。</param>
    /// <param name="height">图像高度。</param>
    /// <returns>图像像素、宽度和高度。</returns>
    public static (byte[] Bgra, int Width, int Height) Normalize(byte[] bgra, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        var expectedLength = checked(width * height * 4);
        if (bgra.Length != expectedLength)
        {
            throw new ArgumentException($"像素数据长度应为 {expectedLength} 字节，实际 {bgra.Length} 字节。", nameof(bgra));
        }

        var pixelCount = checked(width * height);
        var visible = new bool[pixelCount];

        for (var pixel = 0; pixel < pixelCount; pixel++)
        {
            visible[pixel] = bgra[(pixel * 4) + 3] >= MinimumContentAlpha;
        }

        // 外壳偶尔会在透明图标画布最外沿附带一个细的、连成一圈的边框。
        // 如果不先排除它，alpha 边界会错误地覆盖整张图，中心图案就不会放大。
        var ignoredFrame = FindThinFrameComponents(visible, width, height);
        var rowAlpha = new long[height];
        var columnAlpha = new long[width];
        for (var pixel = 0; pixel < pixelCount; pixel++)
        {
            if (!visible[pixel] || ignoredFrame[pixel])
            {
                continue;
            }

            var alpha = bgra[(pixel * 4) + 3];
            var x = pixel % width;
            var y = pixel / width;
            rowAlpha[y] += alpha;
            columnAlpha[x] += alpha;
        }

        // 低强度散点不能把内容边界扩展到整张位图。
        var top = Array.FindIndex(rowAlpha, mass => mass >= MinimumProjectionAlpha);
        var bottom = Array.FindLastIndex(rowAlpha, mass => mass >= MinimumProjectionAlpha);
        var left = Array.FindIndex(columnAlpha, mass => mass >= MinimumProjectionAlpha);
        var right = Array.FindLastIndex(columnAlpha, mass => mass >= MinimumProjectionAlpha);

        if (top < 0 || bottom < top || left < 0 || right < left)
        {
            return (bgra, width, height);
        }

        var contentWidth = right - left + 1;
        var contentHeight = bottom - top + 1;
        var horizontalPadding = Math.Max(1, (contentWidth + PaddingDivisor - 1) / PaddingDivisor);
        var verticalPadding = Math.Max(1, (contentHeight + PaddingDivisor - 1) / PaddingDivisor);
        left = Math.Max(0, left - horizontalPadding);
        right = Math.Min(width - 1, right + horizontalPadding);
        top = Math.Max(0, top - verticalPadding);
        bottom = Math.Min(height - 1, bottom + verticalPadding);

        var croppedWidth = right - left + 1;
        var croppedHeight = bottom - top + 1;
        if (croppedWidth == width && croppedHeight == height)
        {
            return (bgra, width, height);
        }

        var cropped = new byte[checked(croppedWidth * croppedHeight * 4)];
        var rowByteCount = croppedWidth * 4;
        for (var y = 0; y < croppedHeight; y++)
        {
            var sourceOffset = ((top + y) * width + left) * 4;
            Buffer.BlockCopy(bgra, sourceOffset, cropped, y * rowByteCount, rowByteCount);
        }

        return (cropped, croppedWidth, croppedHeight);
    }

    private static bool[] FindThinFrameComponents(bool[] visible, int width, int height)
    {
        var pixelCount = visible.Length;
        var visited = new bool[pixelCount];
        var ignored = new bool[pixelCount];
        var queue = new int[pixelCount];

        void InspectEdgeComponent(int start)
        {
            if (!visible[start] || visited[start])
            {
                return;
            }

            var head = 0;
            var tail = 0;
            queue[tail++] = start;
            visited[start] = true;
            var touchesTop = false;
            var touchesBottom = false;
            var touchesLeft = false;
            var touchesRight = false;
            var topPixels = 0;
            var bottomPixels = 0;
            var leftPixels = 0;
            var rightPixels = 0;

            while (head < tail)
            {
                var pixel = queue[head++];
                var x = pixel % width;
                var y = pixel / width;

                if (y == 0) { touchesTop = true; topPixels++; }
                if (y == height - 1) { touchesBottom = true; bottomPixels++; }
                if (x == 0) { touchesLeft = true; leftPixels++; }
                if (x == width - 1) { touchesRight = true; rightPixels++; }

                for (var offsetY = -1; offsetY <= 1; offsetY++)
                {
                    for (var offsetX = -1; offsetX <= 1; offsetX++)
                    {
                        if (offsetX == 0 && offsetY == 0)
                        {
                            continue;
                        }

                        var neighborX = x + offsetX;
                        var neighborY = y + offsetY;
                        if (neighborX < 0 || neighborX >= width || neighborY < 0 || neighborY >= height)
                        {
                            continue;
                        }

                        var neighbor = neighborY * width + neighborX;
                        if (!visible[neighbor] || visited[neighbor])
                        {
                            continue;
                        }

                        visited[neighbor] = true;
                        queue[tail++] = neighbor;
                    }
                }
            }

            var minimumTopBottomCoverage = Math.Max(2, width / EdgeCoverageDivisor);
            var minimumLeftRightCoverage = Math.Max(2, height / EdgeCoverageDivisor);
            var isThinFrame = touchesTop && touchesBottom && touchesLeft && touchesRight
                && tail <= pixelCount / EdgeComponentAreaDivisor
                && topPixels >= minimumTopBottomCoverage && bottomPixels >= minimumTopBottomCoverage
                && leftPixels >= minimumLeftRightCoverage && rightPixels >= minimumLeftRightCoverage;

            if (isThinFrame)
            {
                for (var index = 0; index < tail; index++)
                {
                    ignored[queue[index]] = true;
                }
            }
        }

        for (var x = 0; x < width; x++)
        {
            InspectEdgeComponent(x);
            if (height > 1)
            {
                InspectEdgeComponent(((height - 1) * width) + x);
            }
        }

        for (var y = 1; y < height - 1; y++)
        {
            InspectEdgeComponent(y * width);
            if (width > 1)
            {
                InspectEdgeComponent((y * width) + width - 1);
            }
        }

        return ignored;
    }
}
