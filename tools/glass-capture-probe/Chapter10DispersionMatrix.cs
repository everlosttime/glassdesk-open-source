using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassDesk.Dock;
using GlassDesk.UI.Controls;

internal static class Chapter10DispersionMatrix
{
    private const int Width = 480;
    private const int Height = 280;
    private const double CornerRadius = 30;
    private static readonly Size SampleSize = new(Width, Height);
    private static readonly Rect GlassBounds = new(44, 42, Width - 88, Height - 84);
    private static readonly string[] Backgrounds =
    [
        "black-white-lines", "grid", "black-white-text", "white-black-text", "light", "dark",
    ];
    private static readonly LiquidGlassDebugView[] Views =
    [
        LiquidGlassDebugView.DispersionOff,
        LiquidGlassDebugView.RRayUv,
        LiquidGlassDebugView.GRayUv,
        LiquidGlassDebugView.BRayUv,
        LiquidGlassDebugView.InnerDispersionOnly,
        LiquidGlassDebugView.OuterDispersionOnly,
        LiquidGlassDebugView.FinalWithDispersion,
        LiquidGlassDebugView.DispersionTimesTenDebug,
    ];
    private static readonly string[] ViewNames =
    [
        "dispersion-off", "r-ray-uv", "g-ray-uv", "b-ray-uv",
        "inner-dispersion-only", "outer-dispersion-only", "final-with-dispersion", "dispersion-x10-debug",
    ];
    private static readonly FieldInfo SurfaceFrame = Field(typeof(LiquidGlassSurface), "_frame");
    private static readonly FieldInfo SurfaceBounds = Field(typeof(LiquidGlassSurface), "_sampledPanelBounds");

    internal static int Run(string outputDirectory)
    {
        try
        {
            var root = Path.GetFullPath(outputDirectory);
            if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
                throw new IOException($"Output directory is not empty: {root}");
            Directory.CreateDirectory(root);
            LiquidGlassDebugState.ShowPerformanceOverlay = false;
            LiquidGlassDebugState.DispersionParameters = LiquidGlassDispersionParameters.Default;

            var parameters = LiquidGlassDispersionParameters.Default;
            var zeroParameters = parameters with { DispersionStrength = 0 };
            var finishing = LiquidGlassFinishingParameters.Default;
            var brdf = LiquidGlassBrdfParameters.Default;
            var owner = new ShadowPreviewOwner();
            var offShadow = RasterizeShadow(owner, LiquidGlassDebugView.DispersionOff, finishing);
            var finalShadow = RasterizeShadow(owner, LiquidGlassDebugView.FinalWithDispersion, finishing);
            Require(offShadow.AsSpan().SequenceEqual(finalShadow),
                "Dispersion does not change the independently rendered external Shadow pixels.");

            var frames = new Dictionary<(string Background, LiquidGlassDebugView View), BitmapSource>();
            var previews = new List<PreviewMetric>();
            var rayComparisons = new List<RayComparison>();
            foreach (var background in Backgrounds)
            {
                var sourceBitmap = RasterizeBackground(background);
                var source = new byte[Width * Height * 4];
                sourceBitmap.CopyPixels(source, Width * 4, 0);
                var optics = new LiquidGlassOptics();
                var chapter09 = optics.Render(source, Width, Height, SampleSize, CornerRadius,
                    squircle: true, indexOfRefraction: LiquidGlassOptics.DefaultIndexOfRefraction,
                    debugView: LiquidGlassDebugView.None, glassBoundsDip: GlassBounds,
                    lodParameters: LiquidGlassLodParameters.Default, dispersionParameters: zeroParameters);
                var chapter09Finished = LiquidGlassFinishComposer.ApplyTint(chapter09, finishing);
                chapter09Finished = optics.ApplyBrdf(chapter09Finished, SampleSize,
                    LiquidGlassWeightParameters.Default, brdf, LiquidGlassDebugView.DispersionOff);
                var off = optics.Render(source, Width, Height, SampleSize, CornerRadius,
                    squircle: true, debugView: LiquidGlassDebugView.DispersionOff,
                    glassBoundsDip: GlassBounds, lodParameters: LiquidGlassLodParameters.Default,
                    dispersionParameters: parameters);
                var zero = optics.Render(source, Width, Height, SampleSize, CornerRadius,
                    squircle: true, debugView: LiquidGlassDebugView.FinalWithDispersion,
                    glassBoundsDip: GlassBounds, lodParameters: LiquidGlassLodParameters.Default,
                    dispersionParameters: zeroParameters);
                Require(chapter09.AsSpan().SequenceEqual(off),
                    $"Dispersion Off returns the byte-identical Chapter 09 frame for {background}.");
                Require(chapter09.AsSpan().SequenceEqual(zero),
                    $"DispersionStrength=0 returns the byte-identical Chapter 09 frame for {background}.");

                var redPath = optics.Render(source, Width, Height, SampleSize, CornerRadius,
                    squircle: true, debugView: LiquidGlassDebugView.RRayUv,
                    glassBoundsDip: GlassBounds, dispersionParameters: parameters);
                var greenPath = optics.Render(source, Width, Height, SampleSize, CornerRadius,
                    squircle: true, debugView: LiquidGlassDebugView.GRayUv,
                    glassBoundsDip: GlassBounds, dispersionParameters: parameters);
                var bluePath = optics.Render(source, Width, Height, SampleSize, CornerRadius,
                    squircle: true, debugView: LiquidGlassDebugView.BRayUv,
                    glassBoundsDip: GlassBounds, dispersionParameters: parameters);
                rayComparisons.Add(new RayComparison(background,
                    CountDifferentPixels(redPath, greenPath), CountDifferentPixels(greenPath, bluePath)));

                OpticalMetric? finalMetric = null;
                OpticalMetric? x10Metric = null;
                foreach (var (view, index) in Views.Select((view, index) => (view, index)))
                {
                    var optical = view switch
                    {
                        LiquidGlassDebugView.RRayUv => redPath,
                        LiquidGlassDebugView.GRayUv => greenPath,
                        LiquidGlassDebugView.BRayUv => bluePath,
                        _ => optics.Render(source, Width, Height, SampleSize, CornerRadius,
                            squircle: true, debugView: view, glassBoundsDip: GlassBounds,
                            lodParameters: LiquidGlassLodParameters.Default,
                            dispersionParameters: parameters),
                    };
                    var isUvView = view is LiquidGlassDebugView.RRayUv
                        or LiquidGlassDebugView.GRayUv or LiquidGlassDebugView.BRayUv;
                    var finished = LiquidGlassFinishComposer.UsesTint(view)
                        ? LiquidGlassFinishComposer.ApplyTint(optical, finishing)
                        : optical;
                    if (LiquidGlassFinishComposer.UsesBrdf(view))
                        finished = optics.ApplyBrdf(finished, SampleSize, LiquidGlassWeightParameters.Default,
                            brdf, view);
                    var alphaMismatches = isUvView ? 0 : CountAlphaMismatches(chapter09, finished);
                    if (!isUvView)
                        Require(alphaMismatches == 0, $"{view} preserves Chapter 09 alpha for {background}.");

                    var comparisonBase = LiquidGlassFinishComposer.UsesTint(view)
                        || LiquidGlassFinishComposer.UsesBrdf(view) ? chapter09Finished : chapter09;
                    var metric = Analyze(comparisonBase, finished, source, view);
                    if (view is LiquidGlassDebugView.FinalWithDispersion
                        or LiquidGlassDebugView.DispersionTimesTenDebug)
                        metric = metric with { RgbSplitPixels = CountRgbSplitPixels(chapter09, optical) };
                    if (view == LiquidGlassDebugView.FinalWithDispersion) finalMetric = metric;
                    if (view == LiquidGlassDebugView.DispersionTimesTenDebug) x10Metric = metric;
                    if (view == LiquidGlassDebugView.DispersionOff)
                    {
                        Require(chapter09Finished.AsSpan().SequenceEqual(finished),
                            $"Dispersion Off preserves finished Chapter 09 RGB and alpha for {background}.");
                    }

                    var shadow = RasterizeShadow(owner, view, finishing);
                    var material = RenderMaterial(finished, view);
                    var composed = Compose(sourceBitmap, shadow, material);
                    var file = $"{background}-{ViewNames[index]}.png";
                    SavePng(composed, Path.Combine(root, file));
                    frames.Add((background, view), composed);
                    previews.Add(new PreviewMetric(background, view.ToString(), file,
                        alphaMismatches, metric.MaximumChannelDelta, metric.CoreMaximumDelta,
                        metric.ChangedRimPixels, metric.ChangedOutsideRimPixels,
                        metric.RgbSplitPixels, metric.CornerChangedPixels));
                }

                if (finalMetric is null || x10Metric is null)
                    throw new InvalidOperationException($"Final or x10 diagnostic is missing for {background}.");
                Require(finalMetric.CoreMaximumDelta == 0,
                    $"The Face center remains byte-identical under default dispersion for {background}.");
                Require(finalMetric.ChangedOutsideRimPixels == 0,
                    $"Default RGB changes stay in the SDF bevel bands for {background}.");
                Require(finalMetric.MaximumChannelDelta <= 12,
                    $"Default dispersion remains restrained on {background}; max channel delta was {finalMetric.MaximumChannelDelta}.");
                Require(x10Metric.RgbSplitPixels >= finalMetric.RgbSplitPixels,
                    $"The explicit x10 view exposes at least as many channel-separation pixels as normal mode for {background}.");
            }

            var wallpaperMaximum = previews
                .Where(metric => metric.Background is "light" or "dark"
                    && metric.View == LiquidGlassDebugView.FinalWithDispersion.ToString())
                .Max(metric => metric.MaximumChannelDelta);
            Require(wallpaperMaximum <= 1,
                $"Ordinary bright/dark wallpapers remain practically neutral; max default delta was {wallpaperMaximum}.");
            Require(rayComparisons.Where(item => item.Background is "black-white-lines" or "grid")
                    .Any(item => item.RedGreenDifferentPixels > 0 && item.GreenBlueDifferentPixels > 0),
                "High-contrast line and grid backgrounds produce distinct R/G/B IOR ray/UV diagnostics.");

            var symmetryMetric = AnalyzeCornerSymmetry(previews);
            Require(symmetryMetric.MaximumCount > 0
                    && symmetryMetric.MaximumCountDifference <= Math.Max(8, symmetryMetric.MaximumCount * .08),
                $"Four squircle corners respond continuously and symmetrically to the centered grid; corner counts {string.Join('/', symmetryMetric.CornerCounts)}.");

            var contactSheet = RenderContactSheet(frames);
            var contactPath = Path.Combine(root, "chapter10-comparison.png");
            SavePng(contactSheet, contactPath);
            var manifest = new
            {
                chapter = "10",
                pipeline = "Background -> existing dual-interface Snell refraction at IOR-dispersion / IOR / IOR+dispersion -> existing per-region Face/Inner/Outer UV and LOD sampling -> existing normalized SDF weights -> Chapter 08 Tint -> Chapter 09 GGX/Smith/Schlick -> independent external Shadow -> independent Content",
                source = "Six controlled WPF-rendered line, grid, text, bright-wallpaper and dark-wallpaper backgrounds; no desktop capture or user data.",
                captureSizeDip = new { width = Width, height = Height },
                glassBoundsDip = new { x = GlassBounds.X, y = GlassBounds.Y, width = GlassBounds.Width, height = GlassBounds.Height },
                cornerRadiusDip = CornerRadius,
                indexOfRefraction = LiquidGlassOptics.DefaultIndexOfRefraction,
                dispersionParameters = parameters,
                backgrounds = Backgrounds,
                views = Views.Select((view, index) => new { name = ViewNames[index], view }).ToArray(),
                checks = new
                {
                    dispersionOffMatchesChapter09Bytes = true,
                    zeroStrengthMatchesChapter09Bytes = true,
                    alphaMismatchMaximum = previews.Where(metric => metric.View is not nameof(LiquidGlassDebugView.RRayUv)
                        and not nameof(LiquidGlassDebugView.GRayUv) and not nameof(LiquidGlassDebugView.BRayUv))
                        .Max(metric => metric.AlphaMismatches),
                    defaultFaceCenterMaximumChannelDelta = previews
                        .Where(metric => metric.View == nameof(LiquidGlassDebugView.FinalWithDispersion))
                        .Max(metric => metric.CoreMaximumDelta),
                    defaultChangedPixelsOutsideRim = previews
                        .Where(metric => metric.View == nameof(LiquidGlassDebugView.FinalWithDispersion))
                        .Max(metric => metric.ChangedOutsideRimPixels),
                    defaultMaximumChannelDelta = previews
                        .Where(metric => metric.View == nameof(LiquidGlassDebugView.FinalWithDispersion))
                        .Max(metric => metric.MaximumChannelDelta),
                    ordinaryWallpaperMaximumChannelDelta = wallpaperMaximum,
                    highContrastDefaultRgbSplitPixels = previews
                        .Where(metric => metric.Background is "black-white-lines" or "grid"
                            && metric.View == nameof(LiquidGlassDebugView.FinalWithDispersion))
                        .Sum(metric => metric.RgbSplitPixels),
                    x10DebugRgbSplitPixels = previews
                        .Where(metric => metric.View == nameof(LiquidGlassDebugView.DispersionTimesTenDebug))
                        .Sum(metric => metric.RgbSplitPixels),
                    shadowUnchanged = offShadow.AsSpan().SequenceEqual(finalShadow),
                    fourCornerSymmetry = symmetryMetric,
                    rayUvDiagnosticImageDifferences = rayComparisons,
                },
                previews,
                matrix = Path.GetFullPath(contactPath),
            };
            File.WriteAllText(Path.Combine(root, "chapter10-matrix.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"PASS: Chapter 10 RGB dispersion matrix exported to {root}");
            Console.WriteLine($"Zero-strength identity and alpha verified; ordinary wallpaper max delta {wallpaperMaximum}; "
                + $"center delta {manifest.checks.defaultFaceCenterMaximumChannelDelta}; "
                + $"outside-rim changes {manifest.checks.defaultChangedPixelsOutsideRim}; "
                + $"x10 split pixels {manifest.checks.x10DebugRgbSplitPixels}.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static BitmapSource RasterizeBackground(string name)
    {
        if (name is "light" or "dark")
        {
            var established = new FinalFrameBackground(name) { Width = Width, Height = Height };
            established.Measure(SampleSize);
            established.Arrange(new Rect(SampleSize));
            established.UpdateLayout();
            var image = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
            image.Render(established);
            return image;
        }

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var bounds = new Rect(0, 0, Width, Height);
            switch (name)
            {
                case "black-white-lines":
                    dc.DrawRectangle(Brushes.White, null, bounds);
                    for (var x = 0; x < Width; x += 20)
                        dc.DrawRectangle(Brushes.Black, null, new Rect(x, 0, 10, Height));
                    break;
                case "grid":
                    dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(225, 228, 232)), null, bounds);
                    var gridPen = new Pen(new SolidColorBrush(Color.FromRgb(24, 30, 38)), 1.25);
                    for (var x = 0.0; x <= Width; x += 20)
                        dc.DrawLine(gridPen, new Point(x, 0), new Point(x, Height));
                    for (var y = 0.0; y <= Height; y += 20)
                        dc.DrawLine(gridPen, new Point(0, y), new Point(Width, y));
                    break;
                case "black-white-text":
                    dc.DrawRectangle(Brushes.Black, null, bounds);
                    DrawCenteredText(dc, "Aa 0123456789", Brushes.White, 30);
                    DrawCenteredText(dc, "OPTICAL EDGE", Brushes.White, 78);
                    DrawCenteredText(dc, "WXYZ  grid  2026", Brushes.White, 126);
                    DrawCenteredText(dc, "glass refraction", Brushes.White, 174);
                    DrawCenteredText(dc, "chapter ten", Brushes.White, 222);
                    break;
                case "white-black-text":
                    dc.DrawRectangle(Brushes.White, null, bounds);
                    DrawCenteredText(dc, "Aa 0123456789", Brushes.Black, 30);
                    DrawCenteredText(dc, "OPTICAL EDGE", Brushes.Black, 78);
                    DrawCenteredText(dc, "WXYZ  grid  2026", Brushes.Black, 126);
                    DrawCenteredText(dc, "glass refraction", Brushes.Black, 174);
                    DrawCenteredText(dc, "chapter ten", Brushes.Black, 222);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(name));
            }
        }
        var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;

        static void DrawCenteredText(DrawingContext dc, string text, Brush brush, double centerY)
        {
            var formatted = new FormattedText(text, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 24, brush, 1);
            dc.DrawText(formatted, new Point((Width - formatted.Width) / 2, centerY - formatted.Height / 2));
        }
    }

    private static OpticalMetric Analyze(byte[] baseline, byte[] output, byte[] source,
        LiquidGlassDebugView view)
    {
        var maximum = 0;
        var coreMaximum = 0;
        var changedRim = 0;
        var changedOutsideRim = 0;
        var rgbSplit = 0;
        var cornerCounts = new int[4];
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
        {
            var index = (y * Width + x) * 4;
            var delta = Math.Max(Math.Abs(output[index] - baseline[index]),
                Math.Max(Math.Abs(output[index + 1] - baseline[index + 1]),
                    Math.Abs(output[index + 2] - baseline[index + 2])));
            maximum = Math.Max(maximum, delta);
            if (output[index + 2] != output[index + 1] || output[index + 1] != output[index]) rgbSplit++;
            if (baseline[index + 3] == 0) continue;

            var depth = -GlassSignedDistance.Squircle(GlassBounds, CornerRadius, new Point(x + .5, y + .5));
            if (depth >= 18) coreMaximum = Math.Max(coreMaximum, delta);
            if (depth is >= 0 and <= 12)
            {
                if (delta > 0)
                {
                    changedRim++;
                    IncrementCorner(x, y, cornerCounts);
                }
            }
            else if (depth > 12 && delta > 0)
            {
                changedOutsideRim++;
            }
        }
        return new OpticalMetric(view, maximum, coreMaximum, changedRim,
            changedOutsideRim, rgbSplit, cornerCounts);
    }

    private static void IncrementCorner(int x, int y, int[] counts)
    {
        const int extent = 52;
        var left = x < GlassBounds.X + extent;
        var right = x >= GlassBounds.Right - extent;
        var top = y < GlassBounds.Y + extent;
        var bottom = y >= GlassBounds.Bottom - extent;
        if (left && top) counts[0]++;
        if (right && top) counts[1]++;
        if (left && bottom) counts[2]++;
        if (right && bottom) counts[3]++;
    }

    private static CornerSymmetry AnalyzeCornerSymmetry(IReadOnlyCollection<PreviewMetric> previews)
    {
        var metric = previews.Single(item => item.Background == "grid"
            && item.View == LiquidGlassDebugView.FinalWithDispersion.ToString());
        var counts = metric.CornerChangedPixels;
        var max = counts.Max();
        var min = counts.Min();
        return new CornerSymmetry(counts, max, max - min);
    }

    private static int CountDifferentPixels(byte[] left, byte[] right)
    {
        var count = 0;
        for (var index = 0; index < left.Length; index += 4)
            if (left[index] != right[index] || left[index + 1] != right[index + 1]
                || left[index + 2] != right[index + 2]) count++;
        return count;
    }

    private static int CountRgbSplitPixels(byte[] baseline, byte[] pixels)
    {
        var count = 0;
        for (var index = 0; index < pixels.Length; index += 4)
        {
            var redDelta = pixels[index + 2] - baseline[index + 2];
            var blueDelta = pixels[index] - baseline[index];
            if (redDelta != 0 && blueDelta != 0 && redDelta != blueDelta) count++;
        }
        return count;
    }

    private static int CountAlphaMismatches(byte[] baseline, byte[] output)
    {
        var count = 0;
        for (var index = 3; index < baseline.Length; index += 4)
            if (baseline[index] != output[index]) count++;
        return count;
    }

    private static BitmapSource RenderMaterial(byte[] pixels, LiquidGlassDebugView view)
    {
        LiquidGlassDebugState.View = view;
        var surface = new LiquidGlassSurface();
        try
        {
            surface.SetPanelShape(GlassBounds, CornerRadius);
            var frame = new WriteableBitmap(Width, Height, 96, 96, PixelFormats.Bgra32, null);
            frame.WritePixels(new Int32Rect(0, 0, Width, Height), pixels, Width * 4, 0);
            SurfaceFrame.SetValue(surface, frame);
            SurfaceBounds.SetValue(surface, new Rect(0, 0, Width, Height));
            surface.Measure(SampleSize);
            surface.Arrange(new Rect(SampleSize));
            var result = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
            result.Render(surface);
            return result;
        }
        finally
        {
            surface.Dispose();
        }
    }

    private static byte[] RasterizeShadow(GlassWindow owner, LiquidGlassDebugView view,
        LiquidGlassFinishingParameters parameters)
    {
        var drawingType = typeof(GlassShadow).GetNestedType("ShadowDrawing", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("GlassShadow drawing layer was not found.");
        var drawing = (FrameworkElement)(Activator.CreateInstance(drawingType, nonPublic: true)
            ?? throw new InvalidOperationException("GlassShadow drawing layer could not be created."));
        drawingType.GetMethod("SetGeometry", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(drawing, [owner, GlassBounds, CornerRadius, SampleSize, 1d]);
        drawingType.GetMethod("SetAppearance", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(drawing, [view, parameters]);
        drawing.Measure(SampleSize);
        drawing.Arrange(new Rect(SampleSize));
        var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var pixels = new byte[Width * Height * 4];
        bitmap.CopyPixels(pixels, Width * 4, 0);
        return pixels;
    }

    private static BitmapSource Compose(BitmapSource source, byte[] shadowPixels, BitmapSource material)
    {
        var shadow = new WriteableBitmap(Width, Height, 96, 96, PixelFormats.Bgra32, null);
        shadow.WritePixels(new Int32Rect(0, 0, Width, Height), shadowPixels, Width * 4, 0);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawImage(source, new Rect(0, 0, Width, Height));
            context.DrawImage(shadow, new Rect(0, 0, Width, Height));
            context.DrawImage(material, new Rect(0, 0, Width, Height));
        }
        var result = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        result.Render(visual);
        return result;
    }

    private static BitmapSource RenderContactSheet(
        IReadOnlyDictionary<(string Background, LiquidGlassDebugView View), BitmapSource> frames)
    {
        const int cellWidth = 260;
        const int cellHeight = 155;
        const int labelHeight = 25;
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(24, 28, 35)), null,
                new Rect(0, 0, cellWidth * Views.Length,
                    (cellHeight + labelHeight) * Backgrounds.Length));
            for (var row = 0; row < Backgrounds.Length; row++)
            for (var column = 0; column < Views.Length; column++)
            {
                var x = column * cellWidth;
                var y = row * (cellHeight + labelHeight);
                var label = new FormattedText($"{Backgrounds[row]} · {ViewNames[column]}",
                    CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 10, Brushes.White, 1);
                context.DrawText(label, new Point(x + 5, y + 4));
                context.DrawImage(frames[(Backgrounds[row], Views[column])],
                    new Rect(x, y + labelHeight, cellWidth, cellHeight));
            }
        }
        var bitmap = new RenderTargetBitmap(cellWidth * Views.Length,
            (cellHeight + labelHeight) * Backgrounds.Length, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    private static void SavePng(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static FieldInfo Field(Type type, string name) => type.GetField(name,
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"Field {type.Name}.{name} was not found.");

    private static void Require(bool condition, string description)
    {
        Console.WriteLine($"{(condition ? "PASS" : "FAIL")} {description}");
        if (!condition) throw new InvalidOperationException(description);
    }

    private sealed record OpticalMetric(LiquidGlassDebugView View, int MaximumChannelDelta,
        int CoreMaximumDelta, int ChangedRimPixels, int ChangedOutsideRimPixels,
        int RgbSplitPixels, int[] CornerChangedPixels);
    private sealed record PreviewMetric(string Background, string View, string File,
        int AlphaMismatches, int MaximumChannelDelta, int CoreMaximumDelta,
        int ChangedRimPixels, int ChangedOutsideRimPixels, int RgbSplitPixels,
        int[] CornerChangedPixels);
    private sealed record RayComparison(string Background, int RedGreenDifferentPixels,
        int GreenBlueDifferentPixels);
    private sealed record CornerSymmetry(int[] CornerCounts, int MaximumCount, int MaximumCountDifference);
    private sealed class ShadowPreviewOwner : GlassWindow;
}
