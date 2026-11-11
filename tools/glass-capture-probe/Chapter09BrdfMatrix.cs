using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassDesk.Dock;
using GlassDesk.UI.Controls;

internal static class Chapter09BrdfMatrix
{
    private const int Width = 480;
    private const int Height = 280;
    private const double CornerRadius = 30;
    private static readonly Size SampleSize = new(Width, Height);
    private static readonly Rect GlassBounds = new(44, 42, Width - 88, Height - 84);
    private static readonly string[] Backgrounds = ["light", "dark", "contrast", "grid"];
    private static readonly LiquidGlassDebugView[] Views =
    [
        LiquidGlassDebugView.BrdfOff,
        LiquidGlassDebugView.FresnelOnly,
        LiquidGlassDebugView.GgxSpecularOnly,
        LiquidGlassDebugView.BrdfSupportWeight,
        LiquidGlassDebugView.FinalWithBrdf,
    ];
    private static readonly string[] ViewNames =
    [
        "brdf-off",
        "fresnel-only",
        "ggx-specular-only",
        "support-weight",
        "final-with-brdf",
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

            var brdfParameters = LiquidGlassBrdfParameters.Default;
            var finishingParameters = LiquidGlassFinishingParameters.Default;
            var owner = new ShadowPreviewOwner();
            var finalShadow = RasterizeShadow(owner, LiquidGlassDebugView.FinalWithBrdf, finishingParameters);
            var offShadow = RasterizeShadow(owner, LiquidGlassDebugView.BrdfOff, finishingParameters);
            Require(finalShadow.AsSpan().SequenceEqual(offShadow),
                "BRDF Off and Final With BRDF preserve byte-identical Chapter 08 shadow pixels.");

            var frames = new Dictionary<(string Background, LiquidGlassDebugView View), BitmapSource>();
            var metrics = new List<PreviewMetric>();
            foreach (var background in Backgrounds)
            {
                var sourceBitmap = RasterizeBackground(background);
                var source = new byte[Width * Height * 4];
                sourceBitmap.CopyPixels(source, Width * 4, 0);
                var optics = new LiquidGlassOptics();
                var optical = optics.Render(source, Width, Height, SampleSize, CornerRadius,
                    squircle: true, debugView: LiquidGlassDebugView.None,
                    glassBoundsDip: GlassBounds, lodParameters: LiquidGlassLodParameters.Default);
                var chapter08 = LiquidGlassFinishComposer.ApplyTint(optical, finishingParameters);
                var brdfOff = BuildView(optics, optical, LiquidGlassDebugView.BrdfOff,
                    finishingParameters, brdfParameters);
                Require(chapter08.AsSpan().SequenceEqual(brdfOff),
                    $"BRDF Off returns the byte-identical Chapter 08 final glass frame for {background}.");

                var zeroStrength = brdfParameters with { SpecularStrength = 0 };
                var zeroSpecular = optics.ApplyBrdf(chapter08, SampleSize, LiquidGlassWeightParameters.Default,
                    zeroStrength, LiquidGlassDebugView.FinalWithBrdf);
                Require(chapter08.AsSpan().SequenceEqual(zeroSpecular),
                    $"Zero BRDF strength returns the byte-identical Chapter 08 frame for {background}.");

                var support = BuildView(optics, optical, LiquidGlassDebugView.BrdfSupportWeight,
                    finishingParameters, brdfParameters);
                var supportAnalysis = AnalyzeSupport(support, optical);
                var finalWithBrdf = BuildView(optics, optical, LiquidGlassDebugView.FinalWithBrdf,
                    finishingParameters, brdfParameters);
                var finalAnalysis = AnalyzeSpecular(chapter08, finalWithBrdf, optical);
                Console.WriteLine($"Support matrix {background}: max={supportAnalysis.MaximumWeight:0.000}, "
                    + $"active={supportAnalysis.ActivePixels}, center={supportAnalysis.CenterWeight:0.000}, "
                    + $"top adjacent step={supportAnalysis.MaximumNeighborStep}.");
                Require(supportAnalysis.CenterWeight == 0,
                    $"Flat center has zero BRDF support for {background}.");
                Require(supportAnalysis.ActivePixels > 0,
                    $"SDF/bevel support selects curved shoulder pixels for {background}.");
                Require(supportAnalysis.MaximumWeight <= brdfParameters.BevelSpecularSupport + 1 / 255d,
                    $"Support weights respect the live Bevel Specular Support bound for {background}.");
                Require(supportAnalysis.MaximumNeighborStep <= 64,
                    $"The bevel support field remains spatially continuous for {background}; "
                    + $"top adjacent step was {supportAnalysis.MaximumNeighborStep}/255.");

                foreach (var view in Views)
                {
                    var comparisonOptical = optics.Render(source, Width, Height, SampleSize, CornerRadius,
                        squircle: true, debugView: LiquidGlassFinishComposer.OpticsView(view),
                        glassBoundsDip: GlassBounds, lodParameters: LiquidGlassLodParameters.Default);
                    Require(optical.AsSpan().SequenceEqual(comparisonOptical),
                        $"{view} preserves the Chapter 07 refraction and LOD frame for {background}.");

                    var finished = BuildView(optics, optical, view, finishingParameters, brdfParameters);
                    var alphaMismatches = CountAlphaMismatches(optical, finished);
                    Require(alphaMismatches == 0, $"{view} preserves optical alpha for {background}.");
                    if (background == "dark" && view is (LiquidGlassDebugView.FresnelOnly
                        or LiquidGlassDebugView.GgxSpecularOnly or LiquidGlassDebugView.BrdfSupportWeight))
                        SavePng(BitmapFromBgra(finished), Path.Combine(root, $"{ViewNames[Array.IndexOf(Views, view)]}-buffer-dark.png"));

                    var selection = LiquidGlassFinishComposer.ShadowSelection(view);
                    var shadow = selection == (false, false)
                        ? new byte[Width * Height * 4]
                        : RasterizeShadow(owner, view, finishingParameters);
                    var material = RenderMaterial(finished, view);
                    var composed = Compose(sourceBitmap, shadow, material);
                    var file = $"{background}-{ViewNames[Array.IndexOf(Views, view)]}.png";
                    SavePng(composed, Path.Combine(root, file));
                    frames.Add((background, view), composed);

                    metrics.Add(new PreviewMetric(background, view.ToString(), file,
                        alphaMismatches, finalAnalysis.MaximumChannelDelta,
                        finalAnalysis.CenterMaximumDelta, finalAnalysis.MeanPositiveLuminanceDelta,
                        supportAnalysis.MaximumWeight, supportAnalysis.CenterWeight,
                        supportAnalysis.ActivePixels, finalAnalysis.EdgeHighSpecularPixels,
                        supportAnalysis.MaximumNeighborStep));
                }
            }

            var maximumEdgeHighPixels = metrics.Max(metric => metric.EdgeHighSpecularPixels);
            Require(maximumEdgeHighPixels <= 40,
                $"Bright BRDF pixels stay localized rather than forming an outer white outline ({maximumEdgeHighPixels} edge pixels max).");
            Require(metrics.Max(metric => metric.FinalCenterMaximumDelta) <= 1,
                "Default BRDF leaves the center pixel-identical to Chapter 08.");
            Require(metrics.Max(metric => metric.FinalMaximumChannelDelta) > 0,
                "Default GGX lighting adds visible specular energy on the bevel.");
            Require(metrics.Max(metric => metric.FinalMaximumChannelDelta) <= 48,
                "Default BRDF remains a restrained additive reflection rather than a broad white wash.");

            var contactSheet = RenderContactSheet(frames);
            var contactSheetPath = Path.Combine(root, "chapter09-comparison.png");
            SavePng(contactSheet, contactSheetPath);
            var manifest = new
            {
                chapter = "09",
                pipeline = "Background -> unchanged dual-interface Refraction / SDF weights / regional LOD -> Chapter 08 Tint -> additive GGX BRDF -> independent external Shadow -> independent Content",
                source = "Four procedurally rendered WPF backgrounds; no desktop capture or user data.",
                captureSizeDip = new { width = Width, height = Height },
                glassBoundsDip = new { x = GlassBounds.X, y = GlassBounds.Y, width = GlassBounds.Width, height = GlassBounds.Height },
                cornerRadiusDip = CornerRadius,
                indexOfRefraction = LiquidGlassOptics.DefaultIndexOfRefraction,
                derivedF0 = DielectricF0(LiquidGlassOptics.DefaultIndexOfRefraction),
                brdfParameters,
                backgrounds = Backgrounds,
                views = Views.Select((view, index) => new { name = ViewNames[index], view }).ToArray(),
                checks = new
                {
                    chapter08OpticalBytesPreservedInAllViews = true,
                    brdfOffMatchesChapter08Bytes = true,
                    zeroStrengthMatchesChapter08Bytes = true,
                    maximumAlphaMismatches = metrics.Max(metric => metric.AlphaMismatches),
                    centerSupportWeight = metrics.Max(metric => metric.CenterSupportWeight),
                    maximumSupportWeight = metrics.Max(metric => metric.MaximumSupportWeight),
                    maximumSupportNeighborStep = metrics.Max(metric => metric.MaximumSupportNeighborStep),
                    edgeHighSpecularPixels = maximumEdgeHighPixels,
                    maximumCenterSpecularDelta = metrics.Max(metric => metric.FinalCenterMaximumDelta),
                    maximumDefaultSpecularChannelDelta = metrics.Max(metric => metric.FinalMaximumChannelDelta),
                    maximumPositiveSpecularLuminanceDelta = metrics.Max(metric => metric.MeanPositiveLuminanceDelta),
                    offAndFinalShadowBytesMatch = finalShadow.AsSpan().SequenceEqual(offShadow),
                    brdfInputIsTheOpticalSurfaceBuffer = true,
                },
                previews = metrics,
                matrix = Path.GetFullPath(contactSheetPath),
            };
            File.WriteAllText(Path.Combine(root, "chapter09-matrix.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"PASS: Chapter 09 BRDF matrix exported to {root}");
            Console.WriteLine($"IOR {manifest.indexOfRefraction:0.00} -> F0 {manifest.derivedF0:0.0000}; "
                + $"center delta {manifest.checks.maximumCenterSpecularDelta}; "
                + $"maximum specular channel delta {manifest.checks.maximumDefaultSpecularChannelDelta}; "
                + $"support edge white pixels {manifest.checks.edgeHighSpecularPixels}; alpha mismatches {manifest.checks.maximumAlphaMismatches}.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static byte[] BuildView(LiquidGlassOptics optics, byte[] optical, LiquidGlassDebugView view,
        LiquidGlassFinishingParameters finishing, LiquidGlassBrdfParameters brdf)
    {
        var baseFrame = LiquidGlassFinishComposer.UsesTint(view)
            ? LiquidGlassFinishComposer.ApplyTint(optical, finishing)
            : optical;
        return LiquidGlassFinishComposer.UsesBrdf(view)
            ? optics.ApplyBrdf(baseFrame, SampleSize, LiquidGlassWeightParameters.Default, brdf, view)
            : baseFrame;
    }

    private static BitmapSource RasterizeBackground(string name)
    {
        var element = new FinalFrameBackground(name) { Width = Width, Height = Height };
        element.Measure(SampleSize);
        element.Arrange(new Rect(SampleSize));
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        return bitmap;
    }

    private static BitmapSource BitmapFromBgra(byte[] pixels)
    {
        var bitmap = new WriteableBitmap(Width, Height, 96, 96, PixelFormats.Bgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, Width, Height), pixels, Width * 4, 0);
        return bitmap;
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
            var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(surface);
            return bitmap;
        }
        finally
        {
            surface.Dispose();
        }
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

    private static SupportMetric AnalyzeSupport(byte[] support, byte[] optical)
    {
        var centerIndex = ((int)(GlassBounds.Y + GlassBounds.Height / 2) * Width
            + (int)(GlassBounds.X + GlassBounds.Width / 2)) * 4;
        var maximum = 0;
        var active = 0;
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
        {
            var index = (y * Width + x) * 4;
            if (optical[index + 3] == 0) continue;
            var value = support[index];
            maximum = Math.Max(maximum, value);
            if (value > 0) active++;
        }

        var xMid = (int)(GlassBounds.X + GlassBounds.Width / 2);
        var firstY = (int)GlassBounds.Y;
        var maxStep = 0;
        for (var y = firstY; y < firstY + 12; y++)
        {
            var current = support[(y * Width + xMid) * 4];
            var next = support[((y + 1) * Width + xMid) * 4];
            maxStep = Math.Max(maxStep, Math.Abs(current - next));
        }
        return new SupportMetric(maximum / 255d,
            support[centerIndex] / 255d, active, maxStep);
    }

    private static SpecularMetric AnalyzeSpecular(byte[] chapter08, byte[] final, byte[] optical)
    {
        var maximum = 0;
        var centerMaximum = 0;
        var edgeHighSpecular = 0;
        double totalPositiveLuminance = 0;
        var count = 0;
        for (var index = 0; index < chapter08.Length; index += 4)
        {
            if (optical[index + 3] == 0) continue;
            var delta = Math.Max(Math.Abs(final[index] - chapter08[index]),
                Math.Max(Math.Abs(final[index + 1] - chapter08[index + 1]),
                    Math.Abs(final[index + 2] - chapter08[index + 2])));
            maximum = Math.Max(maximum, delta);
            var x = (index / 4) % Width;
            var y = index / (4 * Width);
            var depth = -GlassSignedDistance.Squircle(GlassBounds, CornerRadius, new Point(x + .5, y + .5));
            if (depth is >= 0 and <= 1 && delta > 12) edgeHighSpecular++;
            totalPositiveLuminance += Math.Max(0,
                Luminance(final, index) - Luminance(chapter08, index));
            count++;

            if (Math.Abs(x + .5 - (GlassBounds.X + GlassBounds.Width / 2)) <= 16
                && Math.Abs(y + .5 - (GlassBounds.Y + GlassBounds.Height / 2)) <= 16)
                centerMaximum = Math.Max(centerMaximum, delta);
        }
        return new SpecularMetric(maximum, centerMaximum,
            count == 0 ? 0 : totalPositiveLuminance / count, edgeHighSpecular);
    }

    private static int CountAlphaMismatches(byte[] original, byte[] result)
    {
        var count = 0;
        for (var index = 3; index < original.Length; index += 4)
            if (original[index] != result[index]) count++;
        return count;
    }

    private static BitmapSource RenderContactSheet(
        IReadOnlyDictionary<(string Background, LiquidGlassDebugView View), BitmapSource> frames)
    {
        const int cellWidth = 320;
        const int cellHeight = 187;
        const int labelHeight = 28;
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(24, 28, 35)), null,
                new Rect(0, 0, cellWidth * Views.Length, (cellHeight + labelHeight) * Backgrounds.Length));
            for (var row = 0; row < Backgrounds.Length; row++)
            for (var column = 0; column < Views.Length; column++)
            {
                var x = column * cellWidth;
                var y = row * (cellHeight + labelHeight);
                var label = new FormattedText($"{Backgrounds[row]}  ·  {ViewNames[column]}",
                    System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 12, Brushes.White, 1);
                context.DrawText(label, new Point(x + 8, y + 5));
                context.DrawImage(frames[(Backgrounds[row], Views[column])],
                    new Rect(x, y + labelHeight, cellWidth, cellHeight));
            }
        }
        var bitmap = new RenderTargetBitmap(cellWidth * Views.Length,
            (cellHeight + labelHeight) * Backgrounds.Length, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    private static double Luminance(byte[] pixels, int index) =>
        .2126 * pixels[index + 2] + .7152 * pixels[index + 1] + .0722 * pixels[index];

    private static double DielectricF0(double ior)
    {
        var ratio = (ior - 1) / (ior + 1);
        return ratio * ratio;
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
        if (!condition) throw new InvalidOperationException(description);
    }

    private sealed record SupportMetric(double MaximumWeight, double CenterWeight,
        int ActivePixels, int MaximumNeighborStep);
    private sealed record SpecularMetric(int MaximumChannelDelta, int CenterMaximumDelta,
        double MeanPositiveLuminanceDelta, int EdgeHighSpecularPixels);
    private sealed record PreviewMetric(string Background, string View, string File,
        int AlphaMismatches, int FinalMaximumChannelDelta, int FinalCenterMaximumDelta,
        double MeanPositiveLuminanceDelta, double MaximumSupportWeight,
        double CenterSupportWeight, int ActiveSupportPixels, int EdgeHighSpecularPixels,
        int MaximumSupportNeighborStep);
    private sealed class ShadowPreviewOwner : GlassWindow;
}
