using System.Reflection;
using System.Windows.Threading;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock.Tests;

public sealed class GlassInputSchedulingTests
{
    [Fact]
    public void NewRenderTickUnblocksBackdropWorkDuringSustainedInput()
    {
        StaTest.Run(() =>
        {
            using var surface = new LiquidGlassSurface();
            const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
            // Force the periodic low-priority yield while the input queue remains active.
            typeof(LiquidGlassSurface).GetField("_renderFrameSignals", fields)!.SetValue(surface, 7);
            var rendering = typeof(LiquidGlassSurface).GetMethod("OnRenderingFrame", fields)!;
            rendering.Invoke(surface, [null, EventArgs.Empty]);
            rendering.Invoke(surface, [null, EventArgs.Empty]);
            var dispatcher = Dispatcher.CurrentDispatcher;
            var frame = new DispatcherFrame();
            dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Assert.False((bool)typeof(LiquidGlassSurface).GetField("_renderFrameQueued", fields)!.GetValue(surface)!,
                "A new display tick must release a backdrop update that input prevented from running in the background queue.");
            dispatcher.InvokeShutdown();
        });
    }
}
