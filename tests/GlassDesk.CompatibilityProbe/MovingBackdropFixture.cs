using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using GlassDesk.Platform.Displays;

// A separate process supplies animated desktop pixels; the production capture
// must exclude its own launcher windows but still capture this ordinary window.
internal static class MovingBackdropFixture
{
    internal static int Run(string readyPath, bool coverWorkArea = true, bool coded = false, string? deviceName = null)
    {
        var display = (deviceName is null ? DisplayService.Primary() : DisplayService.FindByDeviceName(deviceName))
            ?? throw new InvalidOperationException("The fixture display is unavailable.");
        var background = new SolidColorBrush(Color.FromRgb(42, 91, 133));
        var canvas = new Canvas { Background = background };
        var width = display.WorkArea.Width / display.ScaleFactor;
        var height = display.WorkArea.Height / display.ScaleFactor;
        if (!coverWorkArea) { width = Math.Min(width, 900); height = 220; }
        for (var i = 0; !coded && i < 5; i++)
        {
            var transform = new TranslateTransform();
            var stripe = new Border { Width = 150, Height = height, Background = new SolidColorBrush(
                i % 2 == 0 ? Color.FromRgb(223, 167, 74) : Color.FromRgb(122, 179, 111)), RenderTransform = transform };
            Canvas.SetLeft(stripe, i * 420 - 700); canvas.Children.Add(stripe);
            transform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, 1100,
                TimeSpan.FromSeconds(2)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        }
        var window = new Window { Content = canvas, Width = width, Height = height,
            Left = coverWorkArea ? display.WorkArea.X / display.ScaleFactor
                : display.WorkArea.X / display.ScaleFactor + (display.WorkArea.Width / display.ScaleFactor - width) / 2,
            Top = coverWorkArea ? display.WorkArea.Y / display.ScaleFactor : display.WorkArea.Bottom / display.ScaleFactor - height - 4,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowActivated = false,
            ShowInTaskbar = false, Title = "GlassDesk controlled moving background" };
        var application = new Application();
        var lastRender = TimeSpan.MinValue;
        var phase = 0;
        void UpdateCode(object? sender, EventArgs args)
        {
            if (args is not RenderingEventArgs render || render.RenderingTime == lastRender) return;
            lastRender = render.RenderingTime;
            // A long sequence of distinct colors at approximately constant
            // luma. Both the naked reference and filtered center can see it.
            phase = (phase + 37) % 127;
            background.Color = Color.FromRgb((byte)(54 + phase), (byte)(154 - phase / 3), (byte)(210 - phase));
        }
        if (coded) CompositionTarget.Rendering += UpdateCode;
        if (deviceName is not null)
            window.SourceInitialized += (_, _) =>
                DisplayService.MoveWindowToPixelRect(new WindowInteropHelper(window).Handle, display.WorkArea);
        window.Loaded += (_, _) => File.WriteAllText(readyPath, JsonSerializer.Serialize(new
        { processId = Environment.ProcessId, windowHandle = new WindowInteropHelper(window).Handle.ToInt64() }));
        try { application.Run(window); }
        finally { if (coded) CompositionTarget.Rendering -= UpdateCode; }
        return 0;
    }
}
