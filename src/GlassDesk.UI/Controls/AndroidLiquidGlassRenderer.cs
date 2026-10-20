// Lens profile and expanded-radius gradient adapted from Kyant0/AndroidLiquidGlass,
// Copyright 2025 Kyant, Apache-2.0. See docs/third-party/android-liquid-glass/NOTICE.md.
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Windows;

namespace GlassDesk.UI.Controls;

/// <summary>Background-only WPF adaptation of Backdrop's bottom-tabs material.</summary>
internal sealed class AndroidLiquidGlassRenderer
{
    internal const double BodyOpacity = GlassDesk.Core.Models.Settings.AppearanceSettings.DefaultGlassTintOpacity;
    internal const double BlurDip = 3.2;
    private readonly record struct Sample(float X, float Y, float Coverage, float Highlight, float Shade,
        float DispersionX, float DispersionY, float OuterBlend);
    private Sample[]? _map;
    private (int Width, int Height, Size Size, Rect Bounds, double Radius, bool Squircle, double Strength, bool Launchpad) _key;

    internal static double LensOffset(double depth, double height, double amount)
    {
        if (height <= 0 || amount <= 0 || depth >= height) return 0;
        var t = Math.Clamp(1 - Math.Max(0, depth) / height, 0, 1);
        return -(1 - Math.Sqrt(Math.Max(0, 1 - t * t))) * amount;
    }

    internal byte[] Render(byte[] source, int width, int height, Size size, Rect bounds,
        double radius, bool squircle, bool dark, double strength, double tintOpacity = BodyOpacity,
        double backgroundClarity = .5, LiquidGlassDispersionParameters? dispersionParameters = null,
        bool launchpadOptics = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (width <= 0 || height <= 0 || source.Length != checked(width * height * 4)
            || !double.IsFinite(size.Width) || !double.IsFinite(size.Height)
            || size.Width <= 0 || size.Height <= 0 || bounds.IsEmpty
            || bounds.Width <= 0 || bounds.Height <= 0 || !new Rect(size).Contains(bounds))
            throw new ArgumentOutOfRangeException(nameof(width));
        strength = double.IsFinite(strength) ? Math.Clamp(strength, 0, 1) : 0;
        radius = double.IsFinite(radius) ? Math.Clamp(radius, 0, Math.Min(bounds.Width, bounds.Height) / 2) : 0;
        var key = (width, height, size, bounds, radius, squircle, strength, launchpadOptics);
        if (_map is null || _key != key)
        {
            _key = key;
            _map = new Sample[width * height];
            var pixelDip = Math.Max(size.Width / width, size.Height / height);
            var halfWidth = bounds.Width / 2;
            var halfHeight = bounds.Height / 2;
            var band = Math.Min(24, halfHeight);
            var gradRadius = Math.Min(radius * 1.5, Math.Min(halfWidth, halfHeight));
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var p = new Point((x + .5) * size.Width / width, (y + .5) * size.Height / height);
                var sd = squircle ? GlassSignedDistance.Squircle(bounds, radius, p)
                    : GlassSignedDistance.Continuous(bounds, radius, p);
                var coverage = Math.Clamp(.5 - sd / pixelDip, 0, 1);
                if (coverage == 0) continue;
                var cx = p.X - bounds.Left - halfWidth;
                var cy = p.Y - bounds.Top - halfHeight;
                var qx = Math.Abs(cx) - halfWidth + gradRadius;
                var qy = Math.Abs(cy) - halfHeight + gradRadius;
                double gx, gy;
                if (qx >= 0 || qy >= 0)
                {
                    gx = Math.Max(qx, 0); gy = Math.Max(qy, 0);
                    var length = Math.Sqrt(gx * gx + gy * gy);
                    if (length > 1e-9) { gx /= length; gy /= length; }
                    else { gx = qx >= qy ? 1 : 0; gy = 1 - gx; }
                    gx *= cx < 0 ? -1 : 1; gy *= cy < 0 ? -1 : 1;
                }
                else
                {
                    gx = qx >= qy ? (cx < 0 ? -1 : 1) : 0;
                    gy = qx >= qy ? 0 : (cy < 0 ? -1 : 1);
                }
                var displacement = LensOffset(-sd, band, 32 * strength);
                var displacementX = displacement * gx;
                var displacementY = displacement * gy;
                if (launchpadOptics)
                {
                    var bandX = Math.Min(48, halfWidth);
                    var bandY = Math.Min(48, halfHeight);
                    displacementX = LensOffset(Math.Min(p.X - bounds.Left, bounds.Right - p.X), bandX,
                        Math.Min(64, bandX * 4 / 3) * strength) * (cx < 0 ? -1 : 1);
                    displacementY = LensOffset(Math.Min(p.Y - bounds.Top, bounds.Bottom - p.Y), bandY,
                        Math.Min(64, bandY * 4 / 3) * strength) * (cy < 0 ? -1 : 1);
                    if (qx >= 0 && qy >= 0)
                    {
                        var cornerBand = Math.Min(bandX, bandY);
                        var cornerOffset = LensOffset(-sd, cornerBand, Math.Min(64, cornerBand * 4 / 3) * strength);
                        displacementX = displacementX * .35 + cornerOffset * gx * .65;
                        displacementY = displacementY * .35 + cornerOffset * gy * .65;
                    }
                }
                // Upstream's default highlight is a 0.5 DIP directional white stroke.
                // Coverage integrates the stroke at reduced capture resolution.
                var stroke = Math.Clamp((.75 + pixelDip / 2 - Math.Max(0, -sd)) / pixelDip, 0, 1);
                var highlight = Math.Abs((gx + gy) / Math.Sqrt(2)) * .5 * stroke * strength;
                // Broad bevel lighting adds depth without putting a veil over the face.
                // It follows the same lens gradient and fades completely within 10 DIP.
                var bevel = Math.Pow(Math.Clamp(1 - Math.Max(0, -sd) / (launchpadOptics ? 18 : 10), 0, 1), 2);
                var light = -.6 * gx - .8 * gy;
                highlight += Math.Pow(Math.Max(0, light), 2) * .18 * bevel * strength;
                var shade = Math.Pow(Math.Max(0, -light), 2) * .20 * bevel * strength;
                var displacementLength = launchpadOptics
                    ? Math.Sqrt(displacementX * displacementX + displacementY * displacementY) : Math.Abs(displacement);
                var dispersionNormalX = launchpadOptics && displacementLength > 1e-9 ? -displacementX / displacementLength : gx;
                var dispersionNormalY = launchpadOptics && displacementLength > 1e-9 ? -displacementY / displacementLength : gy;
                var refracting = Math.Clamp(displacementLength / 8, 0, 1);
                var curvature = Math.Clamp(2 * Math.Abs(gx * gy), 0, 1);
                var splitDip = Math.Min(.75, displacementLength * .035)
                    * refracting * refracting * (.65 + .35 * curvature);
                _map[y * width + x] = new Sample(
                    (float)(x + displacementX * width / size.Width),
                    (float)(y + displacementY * height / size.Height),
                    (float)coverage, (float)highlight, (float)shade,
                    (float)(splitDip * dispersionNormalX * width / size.Width),
                    (float)(splitDip * dispersionNormalY * height / size.Height),
                    (float)Math.Exp(-Math.Max(0, -sd) / 5));
            }
        }

        var filtered = FilterBackdrop(source, width, height, size, strength, backgroundClarity);
        var result = new byte[source.Length];
        var tint = dark ? 18d : 250d;
        var opacity = NormalizeTintOpacity(tintOpacity) * BodyTintScale(backgroundClarity, launchpadOptics) * strength;
        var dispersion = (dispersionParameters ?? LiquidGlassDispersionParameters.Default).Normalize();
        var dispersionScale = dispersion.DispersionStrength / LiquidGlassDispersionParameters.DefaultDispersionStrength;
        for (var pixel = 0; pixel < _map.Length; pixel++)
        {
            var sample = _map[pixel];
            if (sample.Coverage == 0) continue;
            var regionGain = sample.OuterBlend * dispersion.OuterDispersionGain / LiquidGlassDispersionParameters.DefaultOuterDispersionGain
                + (1 - sample.OuterBlend) * dispersion.InnerDispersionGain / LiquidGlassDispersionParameters.DefaultInnerDispersionGain;
            var i = pixel * 4;
            for (var c = 0; c < 4; c++)
            {
                // BGRA: blue bends slightly further, red slightly less. Green and alpha
                // keep the original mapping; clarity only changes the filtered input.
                var separation = (c == 0 ? -1 : c == 2 ? 1 : 0) * dispersionScale * regionGain;
                var x = Math.Clamp(sample.X + sample.DispersionX * separation, 0, width - 1);
                var y = Math.Clamp(sample.Y + sample.DispersionY * separation, 0, height - 1);
                var x0 = (int)x; var y0 = (int)y;
                var x1 = Math.Min(x0 + 1, width - 1); var y1 = Math.Min(y0 + 1, height - 1);
                var fx = x - x0; var fy = y - y0;
                var top = filtered[(y0 * width + x0) * 4 + c] * (1 - fx)
                    + filtered[(y0 * width + x1) * 4 + c] * fx;
                var bottom = filtered[(y1 * width + x0) * 4 + c] * (1 - fx)
                    + filtered[(y1 * width + x1) * 4 + c] * fx;
                var value = top * (1 - fy) + bottom * fy;
                result[i + c] = c == 3 ? Byte(value * sample.Coverage)
                    : Byte((value * (1 - opacity) + tint * opacity) * (1 - sample.Shade) + sample.Highlight * 255);
            }
        }
        return result;
    }

    /// <summary>Unmasked material in fixed screen coordinates; moving edges belong to WPF.</summary>
    internal static byte[] RenderTransitionBackdrop(byte[] source, int width, int height, Size size,
        bool dark, double strength, double tintOpacity = BodyOpacity, double backgroundClarity = .5, bool launchpadOptics = false)
    {
        var filtered = FilterBackdrop(source, width, height, size, strength, backgroundClarity);
        // Filtering already owns its output; only the unfiltered path aliases the capture.
        var result = ReferenceEquals(filtered, source) ? source.ToArray() : filtered;
        var tint = dark ? 18d : 250d;
        var opacity = NormalizeTintOpacity(tintOpacity) * BodyTintScale(backgroundClarity, launchpadOptics) * strength;
        TintBackdrop(result, tint, opacity);
        return result;
    }

    internal static void RenderTransitionBackdropInto(byte[] source, byte[] result, byte[] scratch,
        int width, int height, Size size, bool dark, double strength, double tintOpacity = BodyOpacity,
        double backgroundClarity = .5, bool launchpadOptics = false)
    {
        if (source.Length != checked(width * height * 4) || result.Length != source.Length || scratch.Length != source.Length)
            throw new ArgumentException("Backdrop buffers must match the complete BGRA frame.");
        if (ReferenceEquals(source, result) || ReferenceEquals(source, scratch) || ReferenceEquals(result, scratch))
            throw new ArgumentException("Capture, output and scratch buffers must be distinct.");
        source.CopyTo(result, 0);
        if (strength > 0) ApplyBackdropFilter(source, result, width, height, size, strength, backgroundClarity, scratch);
        var tint = dark ? 18d : 250d;
        var opacity = NormalizeTintOpacity(tintOpacity) * BodyTintScale(backgroundClarity, launchpadOptics) * strength;
        TintBackdrop(result, tint, opacity);
    }

    internal static void RenderFrostedBackdropInto(byte[] source, byte[] result, byte[] scratch,
        int width, int height, Size size, bool dark, double strength, double clarity)
    {
        if (source.Length != checked(width * height * 4) || result.Length != source.Length || scratch.Length != source.Length
            || size.Width <= 0 || size.Height <= 0)
            throw new ArgumentException("Backdrop buffers must match the complete BGRA frame.");
        if (ReferenceEquals(source, result) || ReferenceEquals(source, scratch) || ReferenceEquals(result, scratch))
            throw new ArgumentException("Capture, output and scratch buffers must be distinct.");
        strength = double.IsFinite(strength) ? Math.Clamp(strength, 0, 1) : 1;
        clarity = NormalizeBackgroundClarity(clarity);
        source.CopyTo(result, 0);
        // Transparency adjusts the veil, while frosted glass always diffuses fine detail.
        var sigma = (12 + 8 * (1 - clarity)) * (.7 + .3 * strength);
        Blur(result, width, height, sigma * width / size.Width, sigma * height / size.Height, scratch);
        TintBackdrop(result, dark ? 24 : 246, .5 * (1 - clarity));
    }

    private static void TintBackdrop(byte[] pixels, double tint, double opacity)
    {
        if (pixels.Length == 0 || opacity == 0) return;
        if (Avx.IsSupported && Sse41.IsSupported)
        {
            var scale = Vector256.Create(1 - opacity);
            var overlay = Vector256.Create(tint * opacity);
            ref var start = ref pixels[0];
            for (var i = 0; i < pixels.Length; i += 4)
            {
                var packed = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref start, i));
                var channels = Sse41.ConvertToVector128Int32(Vector128.CreateScalar(packed).AsByte());
                var value = Avx.Add(Avx.Multiply(Avx.ConvertToVector256Double(channels), scale), overlay);
                var rounded = Avx.ConvertToVector128Int32WithTruncation(Avx.RoundToNearestInteger(value));
                var bytes = Sse2.PackUnsignedSaturate(Sse2.PackSignedSaturate(rounded, Vector128<int>.Zero), Vector128<short>.Zero);
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref start, i), (packed & 0xff000000u) | (bytes.AsUInt32().GetElement(0) & 0x00ffffffu));
            }
            return;
        }
        for (var i = 0; i < pixels.Length; i += 4)
        for (var c = 0; c < 3; c++) pixels[i + c] = Byte(pixels[i + c] * (1 - opacity) + tint * opacity);
    }

    internal static double NormalizeTintOpacity(double value) => double.IsFinite(value)
        ? Math.Clamp(value, 0, 1) : BodyOpacity;

    internal static double NormalizeBackgroundClarity(double value) => double.IsFinite(value)
        ? Math.Clamp(value, 0, 1) : .5;

    internal static double BodyTintScale(double clarity, bool launchpadOptics) => launchpadOptics
        ? Math.Min(1, 2 * (1 - NormalizeBackgroundClarity(clarity))) : 1;

    private static byte[] FilterBackdrop(byte[] source, int width, int height, Size size, double strength, double backgroundClarity)
    {
        var filtered = source;
        if (strength > 0)
        {
            filtered = source.ToArray();
            ApplyBackdropFilter(source, filtered, width, height, size, strength, backgroundClarity);
        }
        return filtered;
    }

    private static void ApplyBackdropFilter(byte[] source, byte[] filtered, int width, int height,
        Size size, double strength, double backgroundClarity, byte[]? scratch = null)
    {
        if (Avx.IsSupported && Sse41.IsSupported)
        {
            var saturation = Vector256.Create(1 + .5 * strength);
            for (var i = 0; i < filtered.Length; i += 4)
            {
                var packed = Unsafe.ReadUnaligned<uint>(ref source[i]);
                var channels = Avx.ConvertToVector256Double(Sse41.ConvertToVector128Int32(Vector128.CreateScalar(packed).AsByte()));
                var luminance = Vector256.Create(.072 * source[i] + .715 * source[i + 1] + .213 * source[i + 2]);
                var value = Avx.Add(luminance, Avx.Multiply(Avx.Subtract(channels, luminance), saturation));
                var rounded = Avx.ConvertToVector128Int32WithTruncation(Avx.RoundToNearestInteger(value));
                var bytes = Sse2.PackUnsignedSaturate(Sse2.PackSignedSaturate(rounded, Vector128<int>.Zero), Vector128<short>.Zero);
                Unsafe.WriteUnaligned(ref filtered[i], (packed & 0xff000000u) | (bytes.AsUInt32().GetElement(0) & 0x00ffffffu));
            }
        }
        else
            for (var i = 0; i < filtered.Length; i += 4)
            {
                var luminance = .072 * source[i] + .715 * source[i + 1] + .213 * source[i + 2];
                for (var c = 0; c < 3; c++) filtered[i + c] = Byte(luminance + (source[i + c] - luminance) * (1 + .5 * strength));
            }
        var clarity = NormalizeBackgroundClarity(backgroundClarity);
        var blurScale = clarity <= .5 ? 1.5 - clarity : 2 * (1 - clarity);
        // Only the prefiltered backdrop changes. The lens map and lighting above
        // retain their original strength; the default .5 is exactly the old material.
        if (blurScale > 0)
            Blur(filtered, width, height, BlurDip * strength * blurScale * width / size.Width,
                BlurDip * strength * blurScale * height / size.Height, scratch);
    }

    // Three separable box passes approximate the background Gaussian blur in O(pixels).
    // Edge extension keeps the capture padding from introducing black borders.
    private static byte[] Blur(byte[] source, int width, int height, double sigmaX, double sigmaY, byte[]? scratch = null)
    {
        var temporary = scratch ?? ArrayPool<byte>.Shared.Rent(source.Length);
        try
        {
            for (var pass = 0; pass < 3; pass++)
            {
                Box(source, temporary.AsSpan(0, source.Length), width, height, Radius(sigmaX, pass), true);
                Box(temporary.AsSpan(0, source.Length), source, width, height, Radius(sigmaY, pass), false);
            }
            return source;
        }
        finally { if (scratch is null) ArrayPool<byte>.Shared.Return(temporary); }
    }

    private static int Radius(double sigma, int pass)
    {
        if (sigma < .1) return 0;
        var lower = Math.Max(1, (int)Math.Floor(Math.Sqrt(4 * sigma * sigma + 1)));
        if (lower % 2 == 0) lower--;
        var count = (int)Math.Round((12 * sigma * sigma - 3 * lower * lower - 12 * lower - 9)
            / (-4d * lower - 4));
        return (pass < count ? lower - 1 : lower + 1) / 2;
    }

    private static void Box(ReadOnlySpan<byte> source, Span<byte> target, int width, int height, int radius, bool horizontal)
    {
        if (radius == 0) { source.CopyTo(target); return; }
        var divisor = radius * 2 + 1;
        var stride = width * 4;
        if (horizontal)
        {
            if (Sse41.IsSupported && divisor <= 65535)
            {
                var rounding = Vector128.Create(divisor / 2);
                var denominator = Vector128.Create((float)divisor);
                ref var sourceStart = ref MemoryMarshal.GetReference(source);
                ref var targetStart = ref MemoryMarshal.GetReference(target);
                for (var y = 0; y < height; y++)
                {
                    var start = y * stride;
                    var sum = Vector128<int>.Zero;
                    for (var k = -radius; k <= radius; k++)
                    {
                        var packed = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref sourceStart, start + Math.Clamp(k, 0, width - 1) * 4));
                        sum = Sse2.Add(sum, Sse41.ConvertToVector128Int32(Vector128.CreateScalar(packed).AsByte()));
                    }
                    for (var x = 0; x < width; x++)
                    {
                        var quotient = Sse2.ConvertToVector128Int32WithTruncation(Sse.Divide(Sse2.ConvertToVector128Single(Sse2.Add(sum, rounding)), denominator));
                        var bytes = Sse2.PackUnsignedSaturate(Sse2.PackSignedSaturate(quotient, Vector128<int>.Zero), Vector128<short>.Zero);
                        Unsafe.WriteUnaligned(ref Unsafe.Add(ref targetStart, start + x * 4), bytes.AsUInt32().GetElement(0));
                        var remove = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref sourceStart, start + Math.Clamp(x - radius, 0, width - 1) * 4));
                        var add = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref sourceStart, start + Math.Clamp(x + radius + 1, 0, width - 1) * 4));
                        sum = Sse2.Add(Sse2.Subtract(sum, Sse41.ConvertToVector128Int32(Vector128.CreateScalar(remove).AsByte())),
                            Sse41.ConvertToVector128Int32(Vector128.CreateScalar(add).AsByte()));
                    }
                }
                return;
            }
            Span<int> sums = stackalloc int[4];
            for (var y = 0; y < height; y++)
            {
                sums.Clear();
                var start = y * stride;
                for (var k = -radius; k <= radius; k++)
                {
                    var index = start + Math.Clamp(k, 0, width - 1) * 4;
                    for (var c = 0; c < 4; c++) sums[c] += source[index + c];
                }
                for (var x = 0; x < width; x++)
                {
                    var index = start + x * 4;
                    var remove = start + Math.Clamp(x - radius, 0, width - 1) * 4;
                    var add = start + Math.Clamp(x + radius + 1, 0, width - 1) * 4;
                    for (var c = 0; c < 4; c++)
                    {
                        target[index + c] = (byte)((sums[c] + divisor / 2) / divisor);
                        sums[c] += source[add + c] - source[remove + c];
                    }
                }
            }
            return;
        }

        // Advance complete rows, so the vertical pass does not revisit each cache line
        // separately for every column and color channel. Integer rounding is unchanged.
        var rentedSums = ArrayPool<int>.Shared.Rent(stride);
        try
        {
            var sums = rentedSums.AsSpan(0, stride);
            sums.Clear();
            for (var k = -radius; k <= radius; k++)
            {
                var row = Math.Clamp(k, 0, height - 1) * stride;
                for (var i = 0; i < stride; i++) sums[i] += source[row + i];
            }
            for (var y = 0; y < height; y++)
            {
                var row = y * stride;
                var remove = Math.Clamp(y - radius, 0, height - 1) * stride;
                var add = Math.Clamp(y + radius + 1, 0, height - 1) * stride;
                var i = 0;
                // Numerators below 2^24 are exact floats. With divisor <= 65535,
                // every fractional quotient stays farther from an integer than
                // float rounding can move it, so truncation equals integer division.
                // Eight BGRA channels advance together; scalar tails and machines
                // without AVX2 retain the original arithmetic.
                if (Avx2.IsSupported && divisor <= 65535)
                {
                    var rounding = Vector256.Create(divisor / 2);
                    var denominator = Vector256.Create((float)divisor);
                    ref var sourceStart = ref MemoryMarshal.GetReference(source);
                    ref var targetStart = ref MemoryMarshal.GetReference(target);
                    ref var sumsStart = ref MemoryMarshal.GetReference(sums);
                    for (; i <= stride - 16; i += 8)
                    {
                        var sum = Vector256.LoadUnsafe(ref sumsStart, (nuint)i);
                        var quotient = Avx.ConvertToVector256Int32WithTruncation(
                            Avx.Divide(Avx.ConvertToVector256Single(Avx2.Add(sum, rounding)), denominator));
                        var words = Sse2.PackSignedSaturate(quotient.GetLower(), quotient.GetUpper());
                        var bytes = Sse2.PackUnsignedSaturate(words, Vector128<short>.Zero);
                        Unsafe.WriteUnaligned(ref Unsafe.Add(ref targetStart, row + i), bytes.AsUInt64().GetElement(0));
                        var outgoing = Avx2.ConvertToVector256Int32(Vector128.LoadUnsafe(ref sourceStart, (nuint)(remove + i)));
                        var incoming = Avx2.ConvertToVector256Int32(Vector128.LoadUnsafe(ref sourceStart, (nuint)(add + i)));
                        Avx2.Add(Avx2.Subtract(sum, outgoing), incoming).StoreUnsafe(ref sumsStart, (nuint)i);
                    }
                }
                for (; i < stride; i++)
                {
                    target[row + i] = (byte)((sums[i] + divisor / 2) / divisor);
                    sums[i] += source[add + i] - source[remove + i];
                }
            }
        }
        finally { ArrayPool<int>.Shared.Return(rentedSums); }
    }

    private static byte Byte(double value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);
}
