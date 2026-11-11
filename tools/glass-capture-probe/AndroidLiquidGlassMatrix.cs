using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GlassDesk.Core.Models;
using GlassDesk.Core.Models.Settings;
using GlassDesk.UI.Controls;
using GlassDesk.UI.Theming;

internal static class AndroidLiquidGlassMatrix
{
    internal static int Run(string outputDirectory, string? previousDirectory = null)
    {
        var root = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(root);
        using var theme = ThemeService.Initialize(Dispatcher.CurrentDispatcher);
        var rows = new[] { "light", "dark", "contrast", "grid" };
        var timings = new List<object>();
        var sheet = new DrawingVisual();
        using (var drawing = sheet.RenderOpen())
        {
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(27, 30, 37)), null, new Rect(0, 0, 1520, 880));
            for (var row = 0; row < rows.Length; row++)
            for (var column = 0; column < 2; column++)
            {
                var dark = row is 1 or 3;
                var material = column == 0 ? GlassMaterialMode.PhysicalGlass : GlassMaterialMode.AndroidLiquidGlass;
                var name = $"{rows[row]}-{material}";
                theme.Update(new AppearanceSettings { Theme = dark ? GlassDesk.Core.Models.ThemeMode.Dark : GlassDesk.Core.Models.ThemeMode.Light,
                    GlassMaterial = material });
                var size = new Size(760, 180);
                var bounds = new Rect(40, 44, 680, 88);
                var background = new FinalFrameBackground(rows[row]) { Width = 760, Height = 180 };
                background.Measure(size); background.Arrange(new Rect(size));
                var inputBitmap = new RenderTargetBitmap(760, 180, 96, 96, PixelFormats.Pbgra32);
                inputBitmap.Render(background);
                var input = new byte[760 * 180 * 4];
                inputBitmap.CopyPixels(input, 760 * 4, 0);
                Save(inputBitmap, Path.Combine(root, rows[row] + "-source.png"));
                var renderer = new AndroidLiquidGlassRenderer();
                var optics = new LiquidGlassOptics();
                byte[] Compose() => LiquidGlassFrameComposer.Compose(optics, input, 760, 180, size, 40,
                    true, 1, 0, LiquidGlassDebugView.None, LiquidGlassSamplingParameters.Default,
                    LiquidGlassWeightParameters.Default, bounds, LiquidGlassLodParameters.Default,
                    LiquidGlassFinishingParameters.Default, LiquidGlassBrdfParameters.Default,
                    LiquidGlassDispersionParameters.Default, material: material, dark: dark, androidRenderer: renderer);
                var started = Stopwatch.StartNew();
                var pixels = Compose();
                var cold = started.Elapsed.TotalMilliseconds;
                var warm = new List<double>();
                for (var frame = 0; frame < 8; frame++)
                { started.Restart(); pixels = Compose(); warm.Add(started.Elapsed.TotalMilliseconds); }
                timings.Add(new { background = rows[row], material = material.ToString(), coldMilliseconds = cold,
                    warmMedianMilliseconds = warm.Order().ElementAt(4), warmMaxMilliseconds = warm.Max() });
                using var surface = new LiquidGlassSurface { Width = 760, Height = 180 };
                surface.SetPanelShape(bounds, 40);
                var bitmap = new WriteableBitmap(760, 180, 96, 96, PixelFormats.Bgra32, null);
                bitmap.WritePixels(new Int32Rect(0, 0, 760, 180), pixels, 760 * 4, 0);
                Field("_frame").SetValue(surface, bitmap);
                Field("_sampledPanelBounds").SetValue(surface, new Rect(size));
                Field("_sampledGlassPanelBounds").SetValue(surface, bounds);
                Field("_frameUsesAndroidGlass").SetValue(surface, material == GlassMaterialMode.AndroidLiquidGlass);
                surface.Measure(size); surface.Arrange(new Rect(size));
                var chrome = new RenderTargetBitmap(760, 180, 96, 96, PixelFormats.Pbgra32);
                chrome.Render(surface);
                var preview = new DrawingVisual();
                using (var cell = preview.RenderOpen())
                {
                    cell.DrawImage(inputBitmap, new Rect(size));
                    cell.DrawImage(chrome, new Rect(size));
                    DrawForeground(cell, dark);
                }
                var final = new RenderTargetBitmap(760, 180, 96, 96, PixelFormats.Pbgra32);
                final.Render(preview);
                Save(final, Path.Combine(root, name + ".png"));
                var x = column * 760; var y = row * 220;
                drawing.DrawText(Text($"{rows[row]} / {(column == 0 ? "Original optical glass" : "AndroidLiquidGlass adaptation")}", Brushes.White, 14), new Point(x + 12, y + 10));
                drawing.DrawImage(final, new Rect(x, y + 36, 760, 180));
            }
        }
        var matrix = new RenderTargetBitmap(1520, 880, 96, 96, PixelFormats.Pbgra32);
        matrix.Render(sheet);
        Save(matrix, Path.Combine(root, "comparison.png"));
        if (previousDirectory is not null)
        {
            var comparison = new DrawingVisual();
            using (var draw = comparison.RenderOpen())
            {
                draw.DrawRectangle(new SolidColorBrush(Color.FromRgb(27, 30, 37)), null, new Rect(0, 0, 1520, 880));
                for (var row = 0; row < rows.Length; row++)
                for (var column = 0; column < 2; column++)
                {
                    var directory = column == 0 ? Path.GetFullPath(previousDirectory) : root;
                    var source = new BitmapImage(new Uri(Path.Combine(directory, rows[row] + "-AndroidLiquidGlass.png")));
                    var x = column * 760; var y = row * 220;
                    draw.DrawText(Text(column == 0 ? "Previous liquid glass" : "Clearer liquid glass + deeper bevel", Brushes.White, 14), new Point(x + 12, y + 10));
                    draw.DrawImage(source, new Rect(x, y + 36, 760, 180));
                }
            }
            var beforeAfter = new RenderTargetBitmap(1520, 880, 96, 96, PixelFormats.Pbgra32);
            beforeAfter.Render(comparison);
            Save(beforeAfter, Path.Combine(root, "before-after.png"));
        }
        File.WriteAllText(Path.Combine(root, "timings.json"), JsonSerializer.Serialize(timings, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Exported synthetic material comparisons through production LiquidGlassFrameComposer and LiquidGlassSurface: " + root);
        return 0;
    }

    private static void DrawForeground(DrawingContext dc, bool dark)
    {
        var colors = new[] { Colors.DodgerBlue, Colors.OrangeRed, Colors.MediumSeaGreen, Colors.MediumPurple, Colors.Goldenrod };
        var glyphs = new[] { "▣", "♫", "✓", "✦", "☼" };
        for (var i = 0; i < colors.Length; i++)
        {
            var x = 200 + i * 66;
            dc.DrawRoundedRectangle(new SolidColorBrush(colors[i]), null, new Rect(x, 60, 48, 48), 12, 12);
            dc.DrawText(Text(glyphs[i], Brushes.White, 25), new Point(x + 12, 67));
        }
        dc.DrawText(Text("Foreground fixture remains sharp", dark ? Brushes.White : Brushes.Black, 10), new Point(293, 112));
    }

    private static FormattedText Text(string text, Brush color, double size) => new(text,
        CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, color, 1);

    private static FieldInfo Field(string name) => typeof(LiquidGlassSurface).GetField(name,
        BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static void Save(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
