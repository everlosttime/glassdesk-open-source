using System.Globalization;
using System.Windows;
using System.Windows.Media;

internal sealed class FinalFrameBackground(string scene, double pixelsPerDip = 1) : FrameworkElement
{
    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var bounds = new Rect(0, 0, Math.Max(1, ActualWidth), Math.Max(1, ActualHeight));

        switch (scene)
        {
            case "light":
            case "bright-low-contrast":
                DrawLight(drawingContext, bounds);
                break;
            case "dark":
                DrawDark(drawingContext, bounds);
                break;
            case "contrast":
            case "high-contrast-text-window":
                DrawWindowAndText(drawingContext, bounds);
                break;
            case "grid":
            case "straight-lines-grid":
                DrawGrid(drawingContext, bounds);
                break;
        }
    }

    private static void DrawLight(DrawingContext dc, Rect bounds)
    {
        dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(249, 250, 251), Color.FromRgb(232, 235, 239), 35), null, bounds);
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(100, 255, 255, 255)), null,
            new Point(bounds.Width * .27, bounds.Height * .43), bounds.Width * .28, bounds.Height * .52);
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(56, 191, 204, 219)), null,
            new Point(bounds.Width * .77, bounds.Height * .56), bounds.Width * .31, bounds.Height * .40);
        var lowContrast = new SolidColorBrush(Color.FromArgb(95, 192, 199, 207));
        for (var index = 0; index < 6; index++)
            dc.DrawRoundedRectangle(lowContrast, null,
                new Rect(bounds.Width * (.08 + index * .15), bounds.Height * .2, bounds.Width * .10, bounds.Height * .59), 14, 14);
    }

    private static void DrawDark(DrawingContext dc, Rect bounds)
    {
        dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(17, 20, 26), Color.FromRgb(43, 49, 59), 28), null, bounds);
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(72, 143, 165, 191)), null,
            new Point(bounds.Width * .24, bounds.Height * .62), bounds.Width * .14, bounds.Height * .22);
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(38, 108, 123, 147)), null,
            new Point(bounds.Width * .74, bounds.Height * .39), bounds.Width * .19, bounds.Height * .28);
    }

    private void DrawWindowAndText(DrawingContext dc, Rect bounds)
    {
        dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(218, 225, 235), Color.FromRgb(248, 249, 251), 90), null, bounds);
        var insetX = Math.Max(3, bounds.Width * .06);
        var insetY = Math.Max(2, bounds.Height * .08);
        var window = new Rect(insetX, insetY, Math.Max(1, bounds.Width - 2 * insetX), Math.Max(1, bounds.Height - 2 * insetY));
        var ink = new SolidColorBrush(Color.FromRgb(25, 31, 40));
        var border = new Pen(new SolidColorBrush(Color.FromRgb(31, 41, 55)), Math.Clamp(bounds.Height / 70, .9, 1.5));
        dc.DrawRectangle(Brushes.White, border, window);

        var titleHeight = Math.Clamp(window.Height * .22, 10, 20);
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(32, 43, 60)), null,
            new Rect(window.X, window.Y, window.Width, Math.Min(titleHeight, window.Height)));
        for (var dot = 0; dot < 3; dot++)
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(236, 243, 250)), null,
                new Point(window.X + 9 + dot * 10, window.Y + Math.Min(titleHeight / 2, window.Height / 2)), 2.3, 2.3);

        var textSize = Math.Clamp(bounds.Height * .19, 8, 14);
        var textX = window.X + Math.Min(13, window.Width * .08);
        var contentTop = window.Y + titleHeight;
        var labelY = contentTop + Math.Max(1, (window.Height - titleHeight - textSize) * .12);
        DrawText(dc, "Aa 0123456789  AbCd", textX, labelY, textSize, ink);
        var linePen = new Pen(new SolidColorBrush(Color.FromRgb(45, 56, 70)), Math.Clamp(bounds.Height / 82, .75, 1.2));
        var firstRuleY = Math.Min(window.Bottom - 4, labelY + textSize + Math.Max(2, bounds.Height * .09));
        dc.DrawLine(linePen, new Point(window.X + 5, firstRuleY), new Point(window.Right - 5, firstRuleY));
        var secondRuleY = Math.Min(window.Bottom - 2, firstRuleY + Math.Max(3, bounds.Height * .10));
        dc.DrawLine(linePen, new Point(textX, secondRuleY), new Point(window.X + window.Width * .69, secondRuleY));
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(231, 235, 241)), null,
            new Rect(window.X + 3, contentTop + 2, Math.Max(2, window.Width * .035), Math.Max(1, window.Bottom - contentTop - 5)));
    }

    private static void DrawGrid(DrawingContext dc, Rect bounds)
    {
        dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(220, 224, 229), Color.FromRgb(191, 198, 207), 90), null, bounds);
        var thin = new Pen(new SolidColorBrush(Color.FromArgb(205, 37, 43, 51)), .85);
        var major = new Pen(new SolidColorBrush(Color.FromArgb(235, 17, 22, 29)), 1.6);
        const double spacing = 24;
        var column = 0;
        for (var x = 0.0; x <= bounds.Width; x += spacing, column++)
            dc.DrawLine(column % 5 == 0 ? major : thin, new Point(x, 0), new Point(x, bounds.Height));
        var row = 0;
        for (var y = 0.0; y <= bounds.Height; y += spacing, row++)
            dc.DrawLine(row % 5 == 0 ? major : thin, new Point(0, y), new Point(bounds.Width, y));
    }

    private void DrawText(DrawingContext dc, string text, double x, double y, double size, Brush brush)
    {
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, brush, pixelsPerDip);
        dc.DrawText(formatted, new Point(x, y));
    }
}
