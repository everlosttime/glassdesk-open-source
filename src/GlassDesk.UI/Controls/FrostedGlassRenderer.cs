using System.Windows;

namespace GlassDesk.UI.Controls;

/// <summary>Diffused background with a quiet rim; no lens displacement or chromatic split.</summary>
internal static class FrostedGlassRenderer
{
    internal static byte[] Render(byte[] source, int width, int height, Size size, Rect bounds,
        double radius, bool squircle, bool dark, double strength, double clarity)
    {
        var pixels = new byte[source.Length];
        var scratch = new byte[source.Length];
        RenderInto(source, pixels, scratch, width, height, size, bounds, radius, squircle, dark, strength, clarity);
        return pixels;
    }

    internal static void RenderInto(byte[] source, byte[] pixels, byte[] scratch, int width, int height,
        Size size, Rect bounds, double radius, bool squircle, bool dark, double strength, double clarity, ClipCache? cache = null)
    {
        AndroidLiquidGlassRenderer.RenderFrostedBackdropInto(source, pixels, scratch,
            width, height, size, dark, strength, clarity);
        var pixelDip = Math.Max(size.Width / width, size.Height / height);
        var mask = cache?.Get(width, height, size, bounds, radius, squircle, pixelDip);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var index = y * width + x;
            var coverage = mask is null ? Coverage(x, y, width, height, size, bounds, radius, squircle, pixelDip) : mask[index];
            if (coverage == 1) continue;
            var i = index * 4;
            if (coverage == 0) pixels.AsSpan(i, 4).Clear();
            else pixels[i + 3] = (byte)Math.Round(pixels[i + 3] * coverage);
        }
    }

    private static double Coverage(int x, int y, int width, int height, Size size, Rect bounds,
        double radius, bool squircle, double pixelDip)
    {
        var point = new Point((x + .5) * size.Width / width, (y + .5) * size.Height / height);
        var distance = squircle ? GlassSignedDistance.Squircle(bounds, radius, point)
            : GlassSignedDistance.Continuous(bounds, radius, point);
        return Math.Clamp(.5 - distance / pixelDip, 0, 1);
    }

    internal sealed class ClipCache
    {
        private (int Width, int Height, Size Size, Rect Bounds, double Radius, bool Squircle) _key;
        private double[]? _coverage;

        internal double[]? Get(int width, int height, Size size, Rect bounds, double radius, bool squircle, double pixelDip)
        {
            var count = checked(width * height);
            // Cap a surface's clip cache at 512 KiB. Large panels preferentially
            // use the existing transition prefilter and GPU shape instead.
            if (count > 65_536) { _coverage = null; return null; }
            var key = (width, height, size, bounds, radius, squircle);
            if (_coverage is not null && _key == key) return _coverage;
            if (_coverage?.Length != count) _coverage = new double[count];
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                _coverage[y * width + x] = Coverage(x, y, width, height, size, bounds, radius, squircle, pixelDip);
            _key = key;
            return _coverage;
        }
    }
}
