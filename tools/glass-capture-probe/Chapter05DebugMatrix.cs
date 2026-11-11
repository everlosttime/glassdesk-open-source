using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassDesk.UI.Controls;

internal static class Chapter05DebugMatrix
{
    private const int CapturePaddingDip = 6;
    private const int GlassWidthDip = 320;
    private const int GlassHeightDip = 120;

    public static int Run(string outputDirectory)
    {
        try
        {
            var width = GlassWidthDip + CapturePaddingDip * 2;
            var height = GlassHeightDip + CapturePaddingDip * 2;
            var captureSize = new Size(width, height);
            var glassBounds = new Rect(CapturePaddingDip, CapturePaddingDip, GlassWidthDip, GlassHeightDip);
            var cornerRadius = 30d;
            var source = CreateSyntheticBackdrop(width, height);
            var tuning = LiquidGlassSamplingParameters.Default;
            var channels = new (LiquidGlassDebugView View, string FileName)[]
            {
                (LiquidGlassDebugView.FaceOnly, "FaceOnly.png"),
                (LiquidGlassDebugView.InnerOnly, "InnerOnly.png"),
                (LiquidGlassDebugView.OuterOnly, "OuterOnly.png"),
            };
            var outputs = new Dictionary<LiquidGlassDebugView, byte[]>();
            var summaries = new List<object>();

            Directory.CreateDirectory(outputDirectory);
            foreach (var (view, fileName) in channels)
            {
                var output = new LiquidGlassOptics().Render(source, width, height, captureSize,
                    cornerRadius, squircle: true, debugView: view, samplingParameters: tuning,
                    glassBoundsDip: glassBounds);
                Require(output.Length == source.Length, $"{view} output has the capture dimensions");

                var insidePixels = 0;
                var changedPixels = 0;
                var outsidePixelsRemainTransparent = true;
                for (var pixel = 0; pixel < width * height; pixel++)
                {
                    var index = pixel * 4;
                    if (output[index + 3] == 0)
                    {
                        if (output[index] != 0 || output[index + 1] != 0 || output[index + 2] != 0)
                            outsidePixelsRemainTransparent = false;
                        continue;
                    }

                    insidePixels++;
                    if (output[index] != source[index] || output[index + 1] != source[index + 1]
                        || output[index + 2] != source[index + 2]) changedPixels++;
                }

                Require(insidePixels > GlassWidthDip * GlassHeightDip * .9,
                    $"{view} renders the glass silhouette");
                Require(outsidePixelsRemainTransparent, $"{view} keeps color output inside the glass silhouette");
                Require(changedPixels > 0, $"{view} produces an independent sampled-color result");

                var centerX = CapturePaddingDip + GlassWidthDip / 2;
                var centerY = CapturePaddingDip + GlassHeightDip / 2;
                var center = (centerY * width + centerX) * 4;
                Require(output[center + 3] == 255, $"{view} preserves an opaque center");
                Require(ChannelsWithinOne(output, source, center), $"{view} keeps the optical center stable");

                var topEdge = (CapturePaddingDip * width + centerX) * 4;
                if (view == LiquidGlassDebugView.InnerOnly)
                    Require(output[topEdge + 1] > source[topEdge + 1],
                        "Inner Only bends the top-edge sample inward along the physical refraction");
                if (view == LiquidGlassDebugView.OuterOnly)
                    Require(output[topEdge + 1] < source[topEdge + 1],
                        "Outer Only bends the top-edge sample outward into the padded background");

                SavePng(output, width, height, Path.Combine(outputDirectory, fileName));
                outputs.Add(view, output);
                summaries.Add(new
                {
                    mode = view.ToString(),
                    file = fileName,
                    insidePixels,
                    changedPixels,
                    centerRgb = new[] { (int)output[center + 2], output[center + 1], output[center] },
                    topEdgeGreen = output[topEdge + 1],
                    topEdgeSourceGreen = source[topEdge + 1],
                });
            }

            var diagnosticTopEdge = (CapturePaddingDip * width + CapturePaddingDip + GlassWidthDip / 2) * 4;
            Require(outputs[LiquidGlassDebugView.InnerOnly][diagnosticTopEdge + 1]
                    > outputs[LiquidGlassDebugView.FaceOnly][diagnosticTopEdge + 1],
                "Inner Only adds compression beyond the Face dual-interface base at the bevel");
            Require(outputs[LiquidGlassDebugView.OuterOnly][diagnosticTopEdge + 1]
                    < outputs[LiquidGlassDebugView.FaceOnly][diagnosticTopEdge + 1],
                "Outer Only samples opposite the Face dual-interface base at the bevel");
            Require(!outputs[LiquidGlassDebugView.FaceOnly].SequenceEqual(outputs[LiquidGlassDebugView.InnerOnly]),
                "Face Only and Inner Only are independently rendered");
            Require(!outputs[LiquidGlassDebugView.InnerOnly].SequenceEqual(outputs[LiquidGlassDebugView.OuterOnly]),
                "Inner Only and Outer Only are independently rendered");

            var manifest = new
            {
                input = "Procedural synthetic RGB gradient with sharp vertical color bands; no desktop pixels captured.",
                captureSizeDip = new { width, height },
                glassBoundsDip = new { x = glassBounds.X, y = glassBounds.Y, width = glassBounds.Width, height = glassBounds.Height },
                outerCapturePaddingDip = CapturePaddingDip,
                cornerRadiusDip = cornerRadius,
                samplingParameters = tuning,
                checks = summaries,
            };
            File.WriteAllText(Path.Combine(outputDirectory, "manifest.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Chapter 05 previews: {Path.GetFullPath(outputDirectory)}");
            Console.WriteLine("PASS: Face Only, Inner Only, and Outer Only production sampling rendered as separate debug frames.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static byte[] CreateSyntheticBackdrop(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var band = (x / 28) % 2 == 0 ? 0 : 74;
            var index = (y * width + x) * 4;
            pixels[index] = (byte)Math.Clamp(28 + x * .22 + y * .54 + band, 0, 255);
            var verticalGreen = y <= 14 ? y * 12d : 168 + (y - 14) * .35;
            pixels[index + 1] = (byte)Math.Clamp(20 + x * .05 + verticalGreen, 0, 255);
            pixels[index + 2] = (byte)Math.Clamp(34 + x * .45 + y * .36 + band * .35, 0, 255);
            pixels[index + 3] = 255;
        }
        return pixels;
    }

    private static bool ChannelsWithinOne(byte[] left, byte[] right, int index) =>
        Math.Abs(left[index] - right[index]) <= 1
        && Math.Abs(left[index + 1] - right[index + 1]) <= 1
        && Math.Abs(left[index + 2] - right[index + 2]) <= 1;

    private static void SavePng(byte[] pixels, int width, int height, string path)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null,
            pixels, width * 4);
        bitmap.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void Require(bool condition, string description)
    {
        Console.WriteLine((condition ? "PASS " : "FAIL ") + description);
        if (!condition) throw new InvalidOperationException(description);
    }
}
