using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassDesk.UI.Controls;

internal static class Chapter06WeightMatrix
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
            var sizeDip = new Size(width, height);
            var glassBounds = new Rect(CapturePaddingDip, CapturePaddingDip, GlassWidthDip, GlassHeightDip);
            var cornerRadius = 30d;
            var source = CreateBackdrop(width, height);
            var sampling = LiquidGlassSamplingParameters.Default;
            var weightTuning = LiquidGlassWeightParameters.Default;
            var optics = new LiquidGlassOptics();
            var views = new (LiquidGlassDebugView View, string Name)[]
            {
                (LiquidGlassDebugView.FaceWeightOnly, "FaceWeightOnly"),
                (LiquidGlassDebugView.InnerWeightOnly, "InnerWeightOnly"),
                (LiquidGlassDebugView.OuterWeightOnly, "OuterWeightOnly"),
                (LiquidGlassDebugView.CombinedWeightsPreview, "CombinedWeightsPreview"),
                (LiquidGlassDebugView.FinalMixedSampling, "FinalMixedSampling"),
            };
            var outputFrames = new Dictionary<LiquidGlassDebugView, byte[]>();
            Directory.CreateDirectory(outputDirectory);
            foreach (var (view, name) in views)
            {
                var frame = optics.Render(source, width, height, sizeDip, cornerRadius, squircle: true,
                    debugView: view, samplingParameters: sampling, weightParameters: weightTuning,
                    glassBoundsDip: glassBounds);
                Require(frame.Length == source.Length, $"{name} dimensions match the capture frame");
                outputFrames.Add(view, frame);
                SavePng(frame, width, height, Path.Combine(outputDirectory, name + ".png"));
            }

            var face = outputFrames[LiquidGlassDebugView.FaceWeightOnly];
            var inner = outputFrames[LiquidGlassDebugView.InnerWeightOnly];
            var outer = outputFrames[LiquidGlassDebugView.OuterWeightOnly];
            var combined = outputFrames[LiquidGlassDebugView.CombinedWeightsPreview];
            var mixed = outputFrames[LiquidGlassDebugView.FinalMixedSampling];
            var centerX = CapturePaddingDip + GlassWidthDip / 2;
            var top = CapturePaddingDip;
            var innerRow = CapturePaddingDip + 4;
            var flatRow = CapturePaddingDip + 25;
            var centerRow = CapturePaddingDip + GlassHeightDip / 2;
            byte At(byte[] frame, int x, int y, int channel) => frame[(y * width + x) * 4 + channel];

            Require(At(outer, centerX, top, 0) > At(inner, centerX, top, 0)
                && At(outer, centerX, top, 0) > At(face, centerX, top, 0),
                "Outer weight dominates the narrow silhouette edge");
            Require(At(inner, centerX, innerRow, 0) > At(face, centerX, innerRow, 0)
                && At(inner, centerX, innerRow, 0) > At(outer, centerX, innerRow, 0),
                "Inner weight dominates the bevel-interior band");
            Require(At(face, centerX, flatRow, 0) > At(inner, centerX, flatRow, 0)
                && At(face, centerX, flatRow, 0) > At(outer, centerX, flatRow, 0),
                "Face weight dominates the flat body");
            Require(At(face, centerX, centerRow, 0) >= 250
                && At(inner, centerX, centerRow, 0) <= 3
                && At(outer, centerX, centerRow, 0) <= 3,
                "The central region remains Face-only without rim contamination");
            var insidePixels = 0;
            var maxWeightChannelStep = 0;
            var normalizedEverywhere = true;
            var transparentOutside = true;
            var opaqueInside = true;
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var pixel = (y * width + x) * 4;
                if (combined[pixel + 3] == 0)
                {
                    if (combined[pixel] != 0 || combined[pixel + 1] != 0 || combined[pixel + 2] != 0)
                        transparentOutside = false;
                    continue;
                }
                insidePixels++;
                if (combined[pixel + 3] != 255) opaqueInside = false;
                var sum = combined[pixel] + combined[pixel + 1] + combined[pixel + 2];
                if (sum is < 252 or > 258) normalizedEverywhere = false;
                if (x == centerX && y > 0)
                {
                    var previous = ((y - 1) * width + x) * 4;
                    if (combined[previous + 3] != 0)
                        maxWeightChannelStep = Math.Max(maxWeightChannelStep,
                            Math.Max(Math.Abs(combined[pixel] - combined[previous]),
                                Math.Max(Math.Abs(combined[pixel + 1] - combined[previous + 1]),
                                    Math.Abs(combined[pixel + 2] - combined[previous + 2]))));
                }
            }
            Require(insidePixels > GlassWidthDip * GlassHeightDip * .9, "Weight frames cover the rounded glass surface");
            Require(transparentOutside, "Combined weights stay transparent outside the SDF silhouette");
            Require(opaqueInside, "Combined weights retain opaque pixels inside the silhouette");
            Require(normalizedEverywhere, "Face + Inner + Outer weights normalize to one per pixel");
            Require(maxWeightChannelStep <= 120, "Weight transitions stay gradual across adjacent SDF samples");

            var colorIndependent = optics.Render(InvertBackdrop(source), width, height, sizeDip,
                cornerRadius, squircle: true, debugView: LiquidGlassDebugView.CombinedWeightsPreview,
                weightParameters: weightTuning, glassBoundsDip: glassBounds);
            Require(combined.SequenceEqual(colorIndependent), "Weight maps are derived from geometry, not rendered color");

            var linePixels = CreateLineBackdrop(width, height);
            var lineMixed = optics.Render(linePixels, width, height, sizeDip, cornerRadius,
                squircle: true, debugView: LiquidGlassDebugView.FinalMixedSampling,
                samplingParameters: sampling, weightParameters: weightTuning, glassBoundsDip: glassBounds);
            var peakCount = CountRedPeaks(lineMixed, width, centerX, CapturePaddingDip + 10, CapturePaddingDip + 36);
            Require(peakCount <= 1, "A single high-contrast contour does not split into doubled red peaks");
            SavePng(lineMixed, width, height, Path.Combine(outputDirectory, "SingleContourMixed.png"));

            var centerControl = optics.Render(source, width, height, sizeDip, cornerRadius, squircle: true,
                debugView: LiquidGlassDebugView.FinalMixedSampling,
                samplingParameters: sampling with
                {
                    FaceOpticalCenterScale = 0,
                    InnerRimBoost = 0,
                    OuterRimStrength = 0,
                },
                weightParameters: weightTuning, glassBoundsDip: glassBounds);
            var center = (centerRow * width + centerX) * 4;
            var centerDelta = Enumerable.Range(0, 3)
                .Max(channel => Math.Abs(mixed[center + channel] - centerControl[center + channel]));
            Require(centerDelta <= 1, "Face sampling does not create a center bulge against the unscaled dual-interface control");

            var manifest = new
            {
                input = "Procedural RGB bands and a single high-contrast horizontal contour; no desktop pixels captured.",
                captureSizeDip = new { width, height },
                glassBoundsDip = new { x = glassBounds.X, y = glassBounds.Y, width = glassBounds.Width, height = glassBounds.Height },
                cornerRadiusDip = cornerRadius,
                samplingParameters = sampling,
                weightParameters = weightTuning,
                encoding = "Weight Only outputs are grayscale; Combined uses R=Face, G=Inner, B=Outer.",
                checks = new
                {
                    insidePixels,
                    maxWeightChannelStep,
                    edgeWeights = new { face = At(face, centerX, top, 0), inner = At(inner, centerX, top, 0), outer = At(outer, centerX, top, 0) },
                    innerBandWeights = new { face = At(face, centerX, innerRow, 0), inner = At(inner, centerX, innerRow, 0), outer = At(outer, centerX, innerRow, 0) },
                    bodyWeights = new { face = At(face, centerX, flatRow, 0), inner = At(inner, centerX, flatRow, 0), outer = At(outer, centerX, flatRow, 0) },
                    centerWeights = new { face = At(face, centerX, centerRow, 0), inner = At(inner, centerX, centerRow, 0), outer = At(outer, centerX, centerRow, 0) },
                    centerDeltaFromUnscaledControl = centerDelta,
                    singleContourRedPeakCount = peakCount,
                },
            };
            File.WriteAllText(Path.Combine(outputDirectory, "manifest.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Chapter 06 previews: {Path.GetFullPath(outputDirectory)}");
            Console.WriteLine("PASS: SDF weight bands, normalization, center stability, and final mixed sampling.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static byte[] CreateBackdrop(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var brightBand = (x / 28) % 2 == 0;
            var index = (y * width + x) * 4;
            pixels[index] = (byte)(brightBand ? 54 : 158);
            pixels[index + 1] = (byte)Math.Clamp(45 + x * .13 + y * .38, 0, 255);
            pixels[index + 2] = (byte)(brightBand ? 206 : 86);
            if (Math.Abs(y - 27) <= 1)
                pixels[index] = pixels[index + 1] = pixels[index + 2] = 238;
            pixels[index + 3] = 255;
        }
        return pixels;
    }

    private static byte[] InvertBackdrop(byte[] source)
    {
        var inverted = source.ToArray();
        for (var i = 0; i < inverted.Length; i += 4)
        {
            inverted[i] = (byte)(255 - inverted[i]);
            inverted[i + 1] = (byte)(255 - inverted[i + 1]);
            inverted[i + 2] = (byte)(255 - inverted[i + 2]);
        }
        return inverted;
    }

    private static byte[] CreateLineBackdrop(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var index = (y * width + x) * 4;
            var line = Math.Abs(y - 27) <= 2;
            pixels[index] = (byte)(line ? 238 : 28);
            pixels[index + 1] = (byte)(line ? 238 : 32);
            pixels[index + 2] = (byte)(line ? 238 : 36);
            pixels[index + 3] = 255;
        }
        return pixels;
    }

    private static int CountRedPeaks(byte[] frame, int width, int x, int firstY, int lastY)
    {
        var peaks = 0;
        for (var y = firstY + 1; y < lastY; y++)
        {
            var previous = frame[((y - 1) * width + x) * 4 + 2];
            var current = frame[(y * width + x) * 4 + 2];
            var next = frame[((y + 1) * width + x) * 4 + 2];
            if (current > previous && current >= next && current > 45) peaks++;
        }
        return peaks;
    }

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
