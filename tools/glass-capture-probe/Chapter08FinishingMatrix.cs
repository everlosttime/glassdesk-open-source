using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassDesk.UI.Controls;

internal static class Chapter08FinishingMatrix
{
    private const int Width = 480;
    private const int Height = 280;
    private const double CornerRadius = 30;
    private static readonly Rect GlassBounds = new(44, 42, Width - 88, Height - 84);
    private static readonly LiquidGlassDebugView[] Views =
    [
        LiquidGlassDebugView.TintOff,
        LiquidGlassDebugView.TintOnly,
        LiquidGlassDebugView.AmbientShadowOnly,
        LiquidGlassDebugView.ContactShadowOnly,
        LiquidGlassDebugView.TintAndShadowFinal,
    ];
    private static readonly string[] Backgrounds = ["light", "dark", "contrast", "grid"];
    private static readonly string[] ViewNames =
    [
        "tint-off",
        "tint-only",
        "ambient-shadow-only",
        "contact-shadow-only",
        "tint-shadow-final",
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
            var parameters = LiquidGlassFinishingParameters.Default;
            var owner = new ShadowPreviewOwner();
            var noAdditions = parameters with { AmbientShadowOpacity = 0, ContactShadowOpacity = 0 };
            var chapter07Shadow = RasterizeShadow(owner, LiquidGlassDebugView.None, noAdditions);
            var tintOffShadow = RasterizeShadow(owner, LiquidGlassDebugView.TintOff, parameters);
            Require(chapter07Shadow.AsSpan().SequenceEqual(tintOffShadow),
                "Zero Chapter 08 shadow additions preserve the pixel-identical Chapter 07 shadow layers.");
            var metrics = new List<PreviewMetric>();
            var frames = new Dictionary<(string Background, LiquidGlassDebugView View), BitmapSource>();
            var interiorShadowPixels = new Dictionary<LiquidGlassDebugView, int>();
            var shadowPixelCounts = new Dictionary<LiquidGlassDebugView, int>();

            foreach (var backgroundName in Backgrounds)
            {
                var sourceBitmap = RasterizeBackground(backgroundName);
                var source = new byte[Width * Height * 4];
                sourceBitmap.CopyPixels(source, Width * 4, 0);
                var optics = new LiquidGlassOptics();
                var optical = optics.Render(source, Width, Height, new Size(Width, Height), CornerRadius,
                    squircle: true, debugView: LiquidGlassDebugView.None, glassBoundsDip: GlassBounds,
                    lodParameters: LiquidGlassLodParameters.Default);

                foreach (var view in Views)
                {
                    var comparisonOptical = optics.Render(source, Width, Height, new Size(Width, Height), CornerRadius,
                        squircle: true, debugView: LiquidGlassFinishComposer.OpticsView(view), glassBoundsDip: GlassBounds,
                        lodParameters: LiquidGlassLodParameters.Default);
                    Require(optical.AsSpan().SequenceEqual(comparisonOptical),
                        $"{view} preserves the Chapter 07 optical and LOD frame for {backgroundName}.");

                    var opticalWithFinish = LiquidGlassFinishComposer.UsesTint(view)
                        ? LiquidGlassFinishComposer.ApplyTint(optical, parameters)
                        : optical;
                    var shadowPixels = RasterizeShadow(owner, view, parameters);
                    var insideShadowPixels = CountInteriorShadowPixels(shadowPixels);
                    var shadowPixelCount = CountShadowPixels(shadowPixels);
                    if (!interiorShadowPixels.TryGetValue(view, out var priorInterior))
                        interiorShadowPixels.Add(view, insideShadowPixels);
                    else
                        interiorShadowPixels[view] = Math.Max(priorInterior, insideShadowPixels);
                    if (!shadowPixelCounts.TryGetValue(view, out var priorCount))
                        shadowPixelCounts.Add(view, shadowPixelCount);
                    else
                        shadowPixelCounts[view] = Math.Max(priorCount, shadowPixelCount);

                    var materialPixels = RenderMaterial(opticalWithFinish, view);
                    var composed = Compose(sourceBitmap, shadowPixels, materialPixels);
                    var file = $"{backgroundName}-{ViewNames[Array.IndexOf(Views, view)]}.png";
                    SavePng(composed, Path.Combine(root, file));
                    frames.Add((backgroundName, view), composed);
                    metrics.Add(Analyze(backgroundName, view, optical, opticalWithFinish, source,
                        insideShadowPixels, shadowPixelCount, file));
                }
            }

            Require(interiorShadowPixels.Values.All(count => count == 0),
                "Ambient and contact shadow alpha stays outside the glass interior.");
            Require(shadowPixelCounts[LiquidGlassDebugView.AmbientShadowOnly]
                    > shadowPixelCounts[LiquidGlassDebugView.ContactShadowOnly],
                $"Ambient/contact visible pixels: {shadowPixelCounts[LiquidGlassDebugView.AmbientShadowOnly]}/" +
                $"{shadowPixelCounts[LiquidGlassDebugView.ContactShadowOnly]}; ambient must cover more area.");
            Require(shadowPixelCounts[LiquidGlassDebugView.TintOff] > 0
                    && shadowPixelCounts[LiquidGlassDebugView.TintOff]
                        == shadowPixelCounts[LiquidGlassDebugView.TintOnly],
                "Tint Off and Tint Only retain the same Chapter 07 shadow baseline without Chapter 08 additions.");
            Require(metrics.Max(metric => metric.MaximumTintChannelDelta) <= 7,
                "Default adaptive tint stays within seven channel levels of the Chapter 07 optical frame.");
            Require(metrics.Max(metric => metric.AlphaMismatches) == 0,
                "Tint never changes Chapter 07 optical alpha or silhouette coverage.");

            var contactSheet = RenderContactSheet(frames);
            SavePng(contactSheet, Path.Combine(root, "chapter08-comparison.png"));
            var manifest = new
            {
                chapter = "08",
                pipeline = "Background -> existing Optical Refraction / LOD -> optional post-optics Tint -> separate external shadow composition -> independent Content layer",
                source = "Four procedurally rendered WPF backgrounds; no desktop capture or user data.",
                captureSizeDip = new { width = Width, height = Height },
                glassBoundsDip = new { x = GlassBounds.X, y = GlassBounds.Y, width = GlassBounds.Width, height = GlassBounds.Height },
                cornerRadiusDip = CornerRadius,
                finishingParameters = parameters,
                backgrounds = Backgrounds,
                views = Views.Select((view, index) => new { name = ViewNames[index], view }).ToArray(),
                checks = new
                {
                    chapter07OpticsByteMatchesForEveryCompareMode = true,
                    tintMaximumChannelDelta = metrics.Max(metric => metric.MaximumTintChannelDelta),
                    tintMeanAbsoluteLuminanceDelta = metrics.Average(metric => metric.MeanAbsoluteTintLuminanceDelta),
                    tintAlphaMismatches = metrics.Max(metric => metric.AlphaMismatches),
                    shadowInteriorPixelsWithAlpha = interiorShadowPixels,
                    shadowNonTransparentPixelCount = shadowPixelCounts,
                    ambientCoversMorePixelsThanContact = shadowPixelCounts[LiquidGlassDebugView.AmbientShadowOnly]
                        > shadowPixelCounts[LiquidGlassDebugView.ContactShadowOnly],
                    zeroShadowAdditionsReturnToChapter07Pixels = chapter07Shadow.AsSpan().SequenceEqual(tintOffShadow),
                },
                previews = metrics,
                matrix = Path.GetFullPath(Path.Combine(root, "chapter08-comparison.png")),
            };
            File.WriteAllText(Path.Combine(root, "chapter08-matrix.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"PASS: Chapter 08 comparison matrix exported to {root}");
            Console.WriteLine($"Optical bytes matched in all views; max tint delta {manifest.checks.tintMaximumChannelDelta}; " +
                $"shadow interior pixels {manifest.checks.shadowInteriorPixelsWithAlpha.Values.Max()}; " +
                $"ambient/contact extents {shadowPixelCounts[LiquidGlassDebugView.AmbientShadowOnly]}/" +
                $"{shadowPixelCounts[LiquidGlassDebugView.ContactShadowOnly]} pixels.");
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
        var element = new FinalFrameBackground(name) { Width = Width, Height = Height };
        element.Measure(new Size(Width, Height));
        element.Arrange(new Rect(0, 0, Width, Height));
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
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
            .Invoke(drawing, [owner, GlassBounds, CornerRadius, new Size(Width, Height), 1d]);
        drawingType.GetMethod("SetAppearance", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(drawing, [view, parameters]);
        drawing.Measure(new Size(Width, Height));
        drawing.Arrange(new Rect(0, 0, Width, Height));
        var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var pixels = new byte[Width * Height * 4];
        bitmap.CopyPixels(pixels, Width * 4, 0);
        return pixels;
    }

    private static int CountInteriorShadowPixels(byte[] shadow)
    {
        var inner = GlassBounds;
        inner.Inflate(-1, -1);
        var count = 0;
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
        {
            if (!SquircleGlassGeometry.Contains(inner, Math.Max(0, CornerRadius - 1), new Point(x + .5, y + .5))) continue;
            if (shadow[(y * Width + x) * 4 + 3] > 0) count++;
        }
        return count;
    }

    private static int CountShadowPixels(byte[] shadow)
    {
        var count = 0;
        for (var index = 3; index < shadow.Length; index += 4)
            if (shadow[index] > 0) count++;
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
            surface.Measure(new Size(Width, Height));
            surface.Arrange(new Rect(0, 0, Width, Height));
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

    private static PreviewMetric Analyze(string background, LiquidGlassDebugView view, byte[] optical,
        byte[] finished, byte[] source, int interiorShadowPixels, int shadowPixelCount, string file)
    {
        var maximumTintChannelDelta = 0;
        var alphaMismatches = 0;
        double totalLuminanceDelta = 0;
        var tintedPixelCount = 0;
        for (var index = 0; index < optical.Length; index += 4)
        {
            var opticalAlpha = optical[index + 3];
            if (opticalAlpha == 0) continue;
            if (finished[index + 3] != opticalAlpha) alphaMismatches++;
            var delta = Math.Max(Math.Abs(optical[index] - finished[index]),
                Math.Max(Math.Abs(optical[index + 1] - finished[index + 1]),
                    Math.Abs(optical[index + 2] - finished[index + 2])));
            maximumTintChannelDelta = Math.Max(maximumTintChannelDelta, delta);
            totalLuminanceDelta += Math.Abs(Luminance(optical, index) - Luminance(finished, index));
            tintedPixelCount++;
        }
        var center = ((int)(GlassBounds.Y + GlassBounds.Height / 2) * Width
            + (int)(GlassBounds.X + GlassBounds.Width / 2)) * 4;
        var centerTintDelta = Math.Max(Math.Abs(optical[center] - finished[center]),
            Math.Max(Math.Abs(optical[center + 1] - finished[center + 1]),
                Math.Abs(optical[center + 2] - finished[center + 2])));
        return new PreviewMetric(background, view.ToString(), file,
            maximumTintChannelDelta,
            tintedPixelCount == 0 ? 0 : totalLuminanceDelta / tintedPixelCount,
            centerTintDelta,
            alphaMismatches,
            interiorShadowPixels,
            shadowPixelCount,
            MeanLuminance(source));
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

    private static double MeanLuminance(byte[] pixels)
    {
        double sum = 0;
        var count = pixels.Length / 4;
        for (var index = 0; index < pixels.Length; index += 4) sum += Luminance(pixels, index);
        return count == 0 ? 0 : sum / count;
    }

    private static double Luminance(byte[] pixels, int index) =>
        .2126 * pixels[index + 2] + .7152 * pixels[index + 1] + .0722 * pixels[index];

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

    private sealed record PreviewMetric(string Background, string View, string File,
        int MaximumTintChannelDelta, double MeanAbsoluteTintLuminanceDelta,
        int CenterTintChannelDelta, int AlphaMismatches,
        int ShadowInteriorPixels, int ShadowPixelCount, double SourceMeanLuminance);

    private sealed class ShadowPreviewOwner : GlassWindow;
}
