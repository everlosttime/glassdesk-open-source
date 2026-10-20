using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace GlassDesk.DesktopAcceptance;

// Independent test window. No product assembly or handler is called here.
internal static class OwnedTarget
{
    internal static int Run(string marker, int count, string mode)
    {
        var app = new Application();
        var windows = new List<Window>();
        for (var i = 0; i < count; i++)
        {
            var window = new Window
            {
                Title = $"GD target {Path.GetFileNameWithoutExtension(marker)} #{i + 1}",
                Width = 250, Height = 150, Left = 80 + 290 * i, Top = 80,
                Content = new TextBlock { Text = $"Owned test window #{i + 1}", Margin = new Thickness(20) },
            };
            if (mode == "fullscreen")
            {
                window.WindowStyle = WindowStyle.None;
                window.ResizeMode = ResizeMode.NoResize;
                window.Left = 0; window.Top = 0;
                window.Width = SystemParameters.PrimaryScreenWidth;
                window.Height = SystemParameters.PrimaryScreenHeight;
            }
            window.Show();
            if (mode == "minimized") window.WindowState = WindowState.Minimized;
            windows.Add(window);
        }
        var process = Process.GetCurrentProcess();
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        File.WriteAllText(marker, JsonSerializer.Serialize(new
        {
            pid = process.Id,
            image = process.MainModule!.FileName,
            startedUtc = process.StartTime.ToUniversalTime(),
            handles = windows.Select(w => new WindowInteropHelper(w).Handle.ToInt64()).ToArray(),
        }));
        var deadline = DateTime.UtcNow.AddMinutes(5);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        timer.Tick += (_, _) => { if (File.Exists(marker + ".close") || DateTime.UtcNow >= deadline) app.Shutdown(); };
        timer.Start();
        return app.Run();
    }
}
