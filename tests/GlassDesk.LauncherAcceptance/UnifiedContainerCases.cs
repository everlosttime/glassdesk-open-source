using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassDesk.Launcher;
using GlassDesk.Platform.Displays;
using GlassDesk.UI.Controls;

internal static partial class Program
{
    private static void VerifyUnifiedContainer(LauncherWindow window, UniformGrid grid, string evidence)
    {
        var display = DisplayService.Primary();
        if (display is null) return;
        window.Hide();
        var live = new PixelRect(display.WorkArea.X + 60, display.WorkArea.Bottom - 84, 48, 48);
        window.ContainerAnchorProvider = () => live;
        window.ShowWithTransition(new PixelRect(live.X + 100, live.Y, 48, 48));
        Invoke("StopContainerTransform");
        var motion = (ContainerMorphMotion)Field("_containerMotion");
        Invoke("ApplyContainerTransformFrame", motion.CurrentFrame);
        var surface = (LiquidGlassSurface)window.FindName("GlassSurface")!;
        Check("启动台复用Dock玻璃参数而不启用独立光学", "L-01", !surface.UseLaunchpadOptics,
            "UseLaunchpadOptics false", surface.UseLaunchpadOptics.ToString());
        var host = (PixelRect)Field("_transformHostBounds");
        var dip = (double)Field("_transformDipPerPixel");
        var expected = new Rect((live.X - host.X) * dip, (live.Y - host.Y) * dip, live.Width * dip, live.Height * dip);
        Check("实时锚点覆盖过期调用坐标", "L-01", Near(window.Clip.Bounds, expected), expected.ToString(), window.Clip.Bounds.ToString());
        Check("首帧玻璃保持可见而不是淡入", "L-01", window.Opacity == 1, "Opacity 1", window.Opacity.ToString());
        window.UpdateLayout();
        var glyph = (FrameworkElement)window.FindName("CollapsedGlyph")!;
        var panelRoot = (FrameworkElement)window.FindName("LauncherPanelRoot")!;
        var launcherContent = (FrameworkElement)window.FindName("LauncherContent")!;
        var contentScale = (ScaleTransform)window.FindName("ContentScaleTransform")!;
        var clipRoot = (FrameworkElement)window.FindName("ContainerTransformClipRoot")!;
        Check("窗口和内容复用同一轮廓", "L-01", ReferenceEquals(window.Clip, clipRoot.Clip),
            "同一个冻结轮廓", ReferenceEquals(window.Clip, clipRoot.Clip).ToString());
        var previousTime = 0d;
        foreach (var seconds in new[] { 0d, .05, .10, .18, .26, .38, .45 })
        {
            motion.Advance(seconds - previousTime);
            previousTime = seconds;
            Invoke("ApplyContainerTransformFrame", motion.CurrentFrame);
            var bounds = window.Clip.Bounds;
            var glyphBounds = glyph.RenderTransform.TransformBounds(new Rect(0, 0, glyph.Width, glyph.Height));
            var glyphSize = Math.Min(64, Math.Min(bounds.Width, bounds.Height));
            Check($"图标变换保持尺寸和居中 {seconds:0.00}s", "L-01",
                Math.Abs(glyphBounds.Width - glyphSize) < .1 && Math.Abs(glyphBounds.Height - glyphSize) < .1
                && Math.Abs(glyphBounds.X + glyphBounds.Width / 2 - bounds.X - bounds.Width / 2) < .1
                && Math.Abs(glyphBounds.Y + glyphBounds.Height / 2 - bounds.Y - bounds.Height / 2) < .1,
                "与容器同心且尺寸为min(64,容器宽高)", glyphBounds.ToString());
            var same = surface.CaptureBoundsInWindow is { } capture && Near(capture, bounds)
                && surface.RenderTransform.Value.IsIdentity;
            Check($"几何/背景采样同帧且玻璃不随内容拉伸 {seconds:0.00}s", "L-01", same,
                "采样矩形=可见轮廓；玻璃变换为单位矩阵", $"clip {bounds}; capture {surface.CaptureBoundsInWindow}; transform {surface.RenderTransform.Value}");
            Check($"玻璃外壳形变不会缩放最终内容布局 {seconds:0.00}s", "L-01", panelRoot.RenderTransform.Value.IsIdentity,
                "内容根节点保持单位变换", panelRoot.RenderTransform.Value.ToString());
            Check($"动画帧不重新测量应用布局 {seconds:0.00}s", "L-01",
                panelRoot.IsMeasureValid && panelRoot.IsArrangeValid && launcherContent.IsMeasureValid && launcherContent.IsArrangeValid,
                "应用布局保持有效", $"根节点 {panelRoot.IsMeasureValid}/{panelRoot.IsArrangeValid}; 内容 {launcherContent.IsMeasureValid}/{launcherContent.IsArrangeValid}");
            Check($"应用内容始终只轻微缩放 {seconds:0.00}s", "L-01",
                contentScale.ScaleX >= .96 && contentScale.ScaleX <= 1 && contentScale.ScaleX == contentScale.ScaleY,
                "等比例 .96..1", contentScale.Value.ToString());
            Check($"回弹轮廓保持在固定宿主内 {seconds:0.00}s", "L-01",
                bounds.X >= -.1 && bounds.Y >= -.1 && bounds.Right <= host.Width * dip + .1 && bounds.Bottom <= host.Height * dip + .1,
                "完整轮廓在宿主边界内", $"轮廓 {bounds}; 宿主 {host.Width * dip:0.00}x{host.Height * dip:0.00}");
            if (seconds == .05)
            {
                Check("展开早期先形成玻璃外壳再显示应用内容", "L-01", launcherContent.Opacity == 0,
                    "50ms 应用内容完全隐藏", launcherContent.Opacity.ToString());
            }
            if (seconds == .45)
                Check("玻璃展开后应用内容完成淡入", "L-01", launcherContent.Opacity == 1 && contentScale.ScaleX == 1,
                    "450ms 内容完整显示并保持原尺寸", $"Opacity {launcherContent.Opacity:0.000}; Scale {contentScale.ScaleX:0.000}");
            if (seconds is .05 or .18 or .38)
                Capture(window, Path.Combine(evidence, $"transition-{seconds:0.00}s.png"));
        }
        motion.SnapToTarget();
        var captureField = typeof(LiquidGlassSurface).GetField("_transitionCaptureBounds", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var movingCapture = (Rect?)captureField.GetValue(surface);
        VerifySettlingPixels();
        var settledCapture = (Rect?)captureField.GetValue(surface);
        if (RenderCapability.IsPixelShaderVersionSupported(3, 0))
            Check("展开落稳不切换背景采样范围和光学路径", "L-01",
                movingCapture is { Width: > 0, Height: > 0 } && settledCapture == movingCapture,
                $"保留固定宿主采样 {movingCapture}", $"落稳采样 {settledCapture?.ToString() ?? "面板重新采样"}");
        Pump(TimeSpan.FromMilliseconds(120));
        Capture(window, Path.Combine(evidence, "unified-grid-expanded.png"));
        live = live with { X = live.X + 36 };
        window.CloseWithTransition();
        if (RenderCapability.IsPixelShaderVersionSupported(3, 0))
            Check("收起首帧沿用展开后的背景采样坐标", "L-01",
                settledCapture is not null && captureField.GetValue(surface) is Rect closingCapture && closingCapture == settledCapture,
                $"采样 {settledCapture}", $"采样 {captureField.GetValue(surface)}");
        Check("反向关闭重新读取按钮位置", "L-01", (PixelRect)Field("_transformSourceBounds") == live, live.ToString(), Field("_transformSourceBounds").ToString()!);
        Pump(TimeSpan.FromMilliseconds(120));
        Check("关闭先淡出内容再收回玻璃外壳", "L-01", window.IsVisible && launcherContent.Opacity == 0,
            "120ms 内容隐藏且外壳仍可见", $"窗口 {window.IsVisible}; 内容 {launcherContent.Opacity:0.000}");
        window.ShowWithTransition(null);
        var reversed = PumpUntil(() => window.IsVisible && window.CanNavigatePages, TimeSpan.FromSeconds(2));
        Check("关闭中再次打开连续反转", "L-01", reversed && !window.IsClosingContainer, "重新展开并可交互", reversed.ToString());
        window.CloseWithTransition();
        Check("反向收回后隐藏", "L-01", PumpUntil(() => !window.IsVisible, TimeSpan.FromSeconds(2)), "Hidden", window.IsVisible.ToString());
        window.ShowWithTransition(live);
        Pump(TimeSpan.FromMilliseconds(50));
        var interruptedOpening = window.IsVisible && !window.CanNavigatePages;
        window.RefreshDisplayPlacement();
        Check("展开中刷新显示位置恢复完整可交互内容", "L-01",
            interruptedOpening && window.IsVisible && window.CanNavigatePages
            && launcherContent.Opacity == 1 && contentScale.ScaleX == 1 && contentScale.ScaleY == 1,
            "50ms 中断展开后窗口可见、可翻页、内容 Opacity/Scale 1",
            $"已中断展开 {interruptedOpening}; 窗口 {window.IsVisible}; 可翻页 {window.CanNavigatePages}; Opacity {launcherContent.Opacity:0.000}; Scale {contentScale.ScaleX:0.000}");
        window.Hide();
        window.ShowWithTransition(live);
        PumpUntil(() => window.IsVisible && window.CanNavigatePages, TimeSpan.FromSeconds(2));
        window.CloseWithTransition();
        Pump(TimeSpan.FromMilliseconds(120));
        var interruptedClosing = window.IsVisible && window.IsClosingContainer;
        window.RefreshDisplayPlacement();
        Check("关闭中刷新显示位置恢复完整可交互内容", "L-01",
            interruptedClosing && window.IsVisible && window.CanNavigatePages
            && launcherContent.Opacity == 1 && contentScale.ScaleX == 1 && contentScale.ScaleY == 1,
            "120ms 中断关闭后窗口可见、可翻页、内容 Opacity/Scale 1",
            $"已中断关闭 {interruptedClosing}; 窗口 {window.IsVisible}; 可翻页 {window.CanNavigatePages}; Opacity {launcherContent.Opacity:0.000}; Scale {contentScale.ScaleX:0.000}");
        window.Hide();
        window.ShowWithTransition(live);
        PumpUntil(() => window.IsVisible && window.CanNavigatePages, TimeSpan.FromSeconds(2));
        if (window.PrimaryDisplaySession is { } session)
        {
            var originalDisplay = session.Display;
            var originalPanel = (PixelRect)Field("_panelPixelBounds");
            try
            {
                var changedDpi = originalDisplay.Dpi == 144 ? 192u : 144u;
                var dpiRefreshed = session.TryRefreshPrimary(originalDisplay with { Dpi = changedDpi });
                window.RefreshDisplayPlacement();
                var refreshedBounds = window.Clip.Bounds;
                window.CloseWithTransition();
                var closingBounds = window.Clip.Bounds;
                Check("同一物理工作区DPI变化后关闭不复用过期尺寸映射", "L-01",
                    dpiRefreshed && (PixelRect)Field("_panelPixelBounds") == originalPanel
                    && (!window.IsVisible || Math.Abs(closingBounds.Width - refreshedBounds.Width) < .1
                        && Math.Abs(closingBounds.Height - refreshedBounds.Height) < .1),
                    "物理面板不变，关闭安全隐藏或首帧可见尺寸不跳变",
                    $"DPI {originalDisplay.Dpi}->{changedDpi}; 可见 {window.IsVisible}; 刷新尺寸 {refreshedBounds.Width:0.00}x{refreshedBounds.Height:0.00}; 关闭首帧 {closingBounds.Width:0.00}x{closingBounds.Height:0.00}");
            }
            finally
            {
                session.TryRefreshPrimary(originalDisplay);
                window.Hide();
                window.RefreshDisplayPlacement();
            }
        }
        window.ContainerAnchorProvider = null;
        window.Hide();
        window.ShowWithTransition(null);
        Pump(TimeSpan.FromMilliseconds(100));

        object Field(string name) => typeof(LauncherWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        void Invoke(string name, params object[] args) => typeof(LauncherWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window,args);
        static bool Near(Rect a, Rect b) => Math.Abs(a.X-b.X) < .1 && Math.Abs(a.Y-b.Y) < .1 && Math.Abs(a.Width-b.Width) < .1 && Math.Abs(a.Height-b.Height) < .1;

        void VerifySettlingPixels()
        {
            // A live desktop may advance while its older CPU optical frame still
            // matches the final shape. Settling must keep painting the current
            // transition texture, rather than replaying that older desktop frame.
            var names = new[] { "_transitionBackdrop", "_transitionBackdropBounds", "_frame",
                "_sampledPanelBounds", "_sampledGlassPanelBounds", "_sampledGlassPanelRadius", "_frameUsesAndroidGlass" };
            var fields = names.Select(name => typeof(LiquidGlassSurface).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!).ToArray();
            var saved = fields.Select(field => field.GetValue(surface)).ToArray();
            try
            {
                Invoke("ApplyContainerTransformFrame", motion.CurrentFrame);
                window.UpdateLayout();
                var finalBounds = (PixelRect)Field("_panelPixelBounds");
                var finalHost = (PixelRect)Field("_transformHostBounds");
                var finalDip = (double)Field("_panelDipPerPixel");
                var panel = new Rect((finalBounds.X - finalHost.X) * finalDip,
                    (finalBounds.Y - finalHost.Y) * finalDip, finalBounds.Width * finalDip, finalBounds.Height * finalDip);
                var sample = new Rect(surface.RenderSize);
                var current = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 200, 80, 30, 255 }, 4);
                current.Freeze();
                var previous = new WriteableBitmap(1, 1, 96, 96, PixelFormats.Bgra32, null);
                previous.WritePixels(new Int32Rect(0, 0, 1, 1), new byte[] { 30, 80, 200, 255 }, 4, 0);
                object[] values = [current, sample, previous, sample, panel, window.PanelCornerRadius.TopLeft, true];
                for (var i = 0; i < fields.Length; i++) fields[i].SetValue(surface, values[i]);
                surface.SetPanelShape(panel, window.PanelCornerRadius.TopLeft);
                var before = ReadCenter("settling-current-backdrop.png");
                Invoke("CompleteContainerTransform", 1d);
                var after = ReadCenter("settling-after-backdrop.png");
                if (RenderCapability.IsPixelShaderVersionSupported(3, 0))
                    Check("展开落稳不重放旧桌面帧", "L-01",
                        before.SequenceEqual(after) && after[0] > after[2],
                        $"保持当前蓝色背景 BGRA={string.Join(',', before)}", $"落稳 BGRA={string.Join(',', after)}");

                byte[] ReadCenter(string file)
                {
                    surface.InvalidateVisual();
                    window.UpdateLayout();
                    var image = new RenderTargetBitmap((int)Math.Ceiling(sample.Width), (int)Math.Ceiling(sample.Height), 96, 96, PixelFormats.Pbgra32);
                    image.Render(surface);
                    var result = new byte[4];
                    image.CopyPixels(new Int32Rect((int)(panel.X + panel.Width / 2), (int)(panel.Y + panel.Height / 2), 1, 1), result, 4, 0);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(image));
                    using var output = File.Create(Path.Combine(evidence, file));
                    encoder.Save(output);
                    return result;
                }
            }
            finally
            {
                for (var i = 0; i < fields.Length; i++) fields[i].SetValue(surface, saved[i]);
                surface.SetPanelShape((Rect)Field("_transitionPanelBoundsDip"), window.PanelCornerRadius.TopLeft);
            }
        }
    }
}
