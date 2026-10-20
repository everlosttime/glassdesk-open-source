using System.Windows.Interop;
using System.Windows.Threading;
using GlassDesk.AppServices;
using GlassDesk.Core.Input;

namespace GlassDesk.Dock.Tests;

public sealed class NativeMouseHotkeyTests
{
    [Fact]
    public void SleepingSideButtonPassesThroughWithoutPostingALauncherCommand()
    {
        StaTest.Run(() =>
        {
            using var source = new HwndSource(new HwndSourceParameters("GlassDesk-game-side-button-test"));
            var sleeping = true;
            using var registry = new NativeHotkeyRegistry(source.Handle, _ => !sleeping);
            var received = 0;
            source.AddHook((nint hwnd, int message, nint wParam, nint lParam, ref bool handled) =>
            { if (message == 0x0312) received++; return 0; });
            Assert.True(registry.TryRegister(44, new HotkeyChord(0, 5), out _));
            Assert.False(registry.DispatchSideButton(1, 0));
            Pump();
            Assert.Equal(0, received);
            sleeping = false;
            Assert.True(registry.DispatchSideButton(1, 0));
            Pump();
            Assert.Equal(1, received);
        });
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    [Fact]
    public void WheelAccumulatesOneNotchAndPassesThroughWhenActionIsUnavailable()
    {
        StaTest.Run(() =>
        {
            using var source = new HwndSource(new HwndSourceParameters("GlassDesk-wheel-hotkey-test"));
            var available = true;
            using var registry = new NativeHotkeyRegistry(source.Handle, _ => available);
            var received = new List<int>();
            source.AddHook((nint hwnd, int message, nint wParam, nint lParam, ref bool handled) =>
            {
                if (message == 0x0312) received.Add(wParam.ToInt32());
                return 0;
            });
            Assert.True(registry.TryRegister(43, new HotkeyChord(0, 0x100), out _));
            Assert.False(registry.DispatchWheel(-120, 0));
            Assert.True(registry.DispatchWheel(40, 0));
            Assert.True(registry.DispatchWheel(40, 0));
            Assert.True(registry.DispatchWheel(40, 0));
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Assert.Equal(43, Assert.Single(received));
            available = false;
            Assert.False(registry.DispatchWheel(120, 0));
        });
    }
    [Fact]
    public void BoundSideButtonPostsExistingHotkeyMessageAndUnboundButtonsPassThrough()
    {
        StaTest.Run(() =>
        {
            using var source = new HwndSource(new HwndSourceParameters("GlassDesk-mouse-hotkey-test"));
            using var registry = new NativeHotkeyRegistry(source.Handle);
            var received = new List<(int Id, long Chord)>();
            source.AddHook((nint hwnd, int message, nint wParam, nint lParam, ref bool handled) =>
            {
                if (message == 0x0312) received.Add((wParam.ToInt32(), lParam.ToInt64()));
                return 0;
            });
            Assert.True(registry.TryRegister(42, new HotkeyChord(2, 5), out _));
            Assert.False(registry.DispatchSideButton(1, 0));
            Assert.False(registry.DispatchSideButton(2, 2));
            Assert.True(registry.DispatchSideButton(1, 2));
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Assert.Equal((42, (5L << 16) | 2), Assert.Single(received));
            Assert.True(registry.Unregister(42));
            Assert.False(registry.DispatchSideButton(1, 2));
        });
    }
}
