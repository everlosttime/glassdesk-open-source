using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using GlassDesk.Launcher;
using GlassDesk.Platform;
using GlassDesk.Platform.Displays;
using GlassDesk.UI.Controls;

internal static partial class Program
{
    private static void VerifyPortableLauncherTransitions(LauncherWindow window, bool expectFallback)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        bool Settled() => window.IsVisible && !(bool)typeof(LauncherWindow).GetField("_transformActive", flags)!.GetValue(window)!;
        void Verify(string label, Func<bool> condition) => Check(label, "portable/launcher",
            PumpUntil(condition, TimeSpan.FromSeconds(5)), "completed", "visible=" + window.IsVisible);
        var display = window.PrimaryDisplaySession!.Display;
        var anchor = new PixelRect(display.WorkArea.X + display.WorkArea.Width / 2 - 24, display.WorkArea.Bottom - 76, 48, 48);
        var originalAnchor = window.ContainerAnchorProvider;
        window.ContainerAnchorProvider = () => anchor;
        try
        {
            Verify("完整宿主首次展开", Settled);
            var surface = (LiquidGlassSurface)window.FindName("GlassSurface");
            var metrics = JsonSerializer.SerializeToElement(surface.InspectPerformance());
            Check("首次 WPF 绘制的材质已准备", "portable/launcher",
                metrics.GetProperty("firstVisibleRenderMaterialReady").GetBoolean(), "true", metrics.ToString());
            if (Environment.GetEnvironmentVariable("GLASSDESK_LAUNCHER_NATIVE_SHADOW") == "1")
            {
                var shadow = (GlassShadow)typeof(GlassWindow).GetField("_shadow", flags)!.GetValue(window)!;
                var status = JsonSerializer.SerializeToElement(shadow.InspectPerformance());
                Check("实际阴影后端与输入穿透", "portable/launcher",
                    (expectFallback ? status.GetProperty("nativeFailed").GetBoolean() && !status.GetProperty("nativeActive").GetBoolean()
                        : status.GetProperty("nativeActive").GetBoolean()) && WindowShadowStyles.IsInputTransparent(shadow.Handle),
                    expectFallback ? "WPF fallback + input transparent" : "native + input transparent", status.ToString());
            }
            window.CloseWithTransition(); Verify("正常关闭", () => !window.IsVisible);
            window.ShowWithTransition(anchor); Pump(TimeSpan.FromMilliseconds(90));
            window.CloseWithTransition(); Verify("打开中反向关闭", () => !window.IsVisible);
            window.ShowWithTransition(anchor); Verify("再次展开", Settled);
            window.CloseWithTransition(); Pump(TimeSpan.FromMilliseconds(90));
            window.ShowWithTransition(anchor); Verify("关闭中反向重开", Settled);
            window.CloseWithTransition(); Pump(TimeSpan.FromMilliseconds(70));
            anchor = anchor with { X = anchor.X + 120 };
            Verify("关闭中锚点变化", () => !window.IsVisible);
            Check("关闭采用最新锚点", "portable/launcher",
                (PixelRect)typeof(LauncherWindow).GetField("_transformSourceBounds", flags)!.GetValue(window)! == anchor,
                anchor.ToString(), typeof(LauncherWindow).GetField("_transformSourceBounds", flags)!.GetValue(window)!.ToString()!);
            window.ShowWithTransition(anchor); Verify("Esc 前展开", Settled);
            window.Activate(); Pump(TimeSpan.FromMilliseconds(80));
            Check("Esc 输入目标已激活", "portable/launcher", window.IsActive, "active", window.IsActive.ToString());
            if (window.IsActive) { IosInput.Key(0x1b, false); IosInput.Key(0x1b, true); }
            Verify("真实 Esc 关闭", () => !window.IsVisible);
            // A disposable topmost peer receives the click, so the test never
            // clicks an unrelated desktop application or a real library entry.
            var peer = new Window { Width = 100, Height = 100, Left = display.WorkArea.X * 96d / display.Dpi + 8,
                Top = display.WorkArea.Y * 96d / display.Dpi + 8, ShowActivated = false, ShowInTaskbar = false,
                WindowStyle = WindowStyle.None, Topmost = true, Background = Brushes.Gray };
            try
            {
                peer.Show(); window.ShowWithTransition(anchor); Verify("外部点击前展开", Settled);
                window.Activate(); Pump(TimeSpan.FromMilliseconds(80));
                var point = peer.PointToScreen(new Point(30, 30));
                IosInput.SetCursorPos((int)point.X, (int)point.Y); IosInput.Mouse(2); IosInput.Mouse(4);
                Verify("真实外部点击关闭", () => !window.IsVisible);
            }
            finally { peer.Close(); }
            window.SetPerformanceSleeping(true); window.SetBackgroundWorkSuspended(true);
            window.SetPerformanceSleeping(false); window.SetBackgroundWorkSuspended(false);
            window.ShowWithTransition(anchor); Verify("模拟资源睡眠恢复后展开", Settled);
        }
        finally { window.ContainerAnchorProvider = originalAnchor; }
    }
}
