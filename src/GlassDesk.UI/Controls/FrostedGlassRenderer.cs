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
        AndroidLiquidGlassRenderer.RenderFrostedBackdropInto(source, pixels, scratch,
            width, height, size, dark, strength, clarity);
        var pixelDip = Math.Max(size.Width / width, size.Height / height);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var point = new Point((x + .5) * size.Width / width, (y + .5) * size.Height / height);
            var distance = squircle ? GlassSignedDistance.Squircle(bounds, radius, point)
                : GlassSignedDistance.Continuous(bounds, radius, point);
            var coverage = Math.Clamp(.5 - distance / pixelDip, 0, 1);
            var i = (y * width + x) * 4;
            pixels[i + 3] = (byte)Math.Round(pixels[i + 3] * coverage);
            if (coverage == 0) pixels.AsSpan(i, 4).Clear();
        }
        return pixels;
    }
}
