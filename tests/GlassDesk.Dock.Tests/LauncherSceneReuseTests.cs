using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Media;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock.Tests;

public sealed class LauncherSceneReuseTests
{
    [Fact]
    public void PaintUsesVerifiedStateButRefreshStillChecksTheCurrentDesktop()
    {
        StaTest.Run(() =>
        {
            using var surface = new LiquidGlassSurface();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var option = typeof(LiquidGlassSurface).GetProperty("ValidateSceneDuringPaint", flags);
            Assert.NotNull(option); option.SetValue(surface, false);
            var reads = 0; var scene = new GlassBackdropScene();
            surface.ReadBackdropScene = () => { reads++; return scene; };
            surface.RequestBackdropRefresh();
            var initial = reads;
            var render = typeof(LiquidGlassSurface).GetMethod("OnRender", flags)!;
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen()) { render.Invoke(surface, [context]); render.Invoke(surface, [context]); }
            Assert.Equal(initial, reads);
            surface.RequestBackdropRefresh();
            Assert.Equal(initial + 1, reads);
        });
    }

    [Fact]
    public void SameDispatcherTurnReusesVerificationButExplicitRefreshAndRevealReadAgain()
    {
        StaTest.Run(() =>
        {
            using var surface = new LiquidGlassSurface();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var option = typeof(LiquidGlassSurface).GetProperty("ReuseSceneQueriesWithinTurn", flags);
            Assert.NotNull(option); option.SetValue(surface, true);
            var reads = 0; var scene = new GlassBackdropScene();
            surface.ReadBackdropScene = () => { reads++; return scene; };
            var ensure = typeof(LiquidGlassSurface).GetMethod("EnsureBackdropScene", flags)!;
            ensure.Invoke(surface, [false]); ensure.Invoke(surface, [false]);
            Assert.Equal(1, reads);
            scene = new(new GlassBackdropScene.SceneWindow(5, 10, 0, 0, 30, 30));
            surface.RequestBackdropRefresh();
            Assert.Equal(2, reads);
            ensure.Invoke(surface, [true]);
            Assert.Equal(3, reads);
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            ensure.Invoke(surface, [false]);
            Assert.Equal(4, reads);
        });
    }
}
