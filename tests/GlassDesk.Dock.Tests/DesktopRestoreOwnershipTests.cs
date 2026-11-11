using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using GlassDesk.Platform;

namespace GlassDesk.Dock.Tests;

public sealed class DesktopRestoreOwnershipTests
{
    [Fact]
    public void ForegroundReconciliationDoesNotReorderTheDesktopOwner()
    {
        StaTest.Run(() =>
        {
            var underneath = new Window { Width = 200, Height = 100, ShowActivated = false };
            var desktop = new Window { Width = 250, Height = 150, ShowActivated = false };
            var foreground = new Window { Width = 300, Height = 200, ShowActivated = false };
            var bar = new Window { Width = 150, Height = 60, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                underneath.Show(); desktop.Show(); foreground.Show();
                var helper = new WindowInteropHelper(bar);
                var desktopHandle = new WindowInteropHelper(desktop).Handle;
                var underneathHandle = new WindowInteropHelper(underneath).Handle;
                var foregroundHandle = new WindowInteropHelper(foreground).Handle;
                var ctor = typeof(DesktopWindowAttachment).GetConstructor(System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.NonPublic, null, [typeof(nint), typeof(Action<nint>), typeof(Func<nint>)], null)!;
                using var attachment = (DesktopWindowAttachment)ctor.Invoke([helper.EnsureHandle(),
                    (Action<nint>)(owner => helper.Owner = owner), (Func<nint>)(() => desktopHandle)]);
                Assert.True(attachment.TryAttach()); bar.Show();
                Assert.True(SetWindowPos(underneathHandle, 1, 0, 0, 0, 0, 0x0213));
                Assert.True(SetWindowPos(foregroundHandle, 0, 0, 0, 0, 0, 0x0213));
                AssertAbove(desktopHandle, underneathHandle);
                attachment.Reconcile();
                AssertAbove(desktopHandle, underneathHandle);
                Assert.Equal(desktopHandle, GetWindow(helper.Handle, 4));
            }
            finally { bar.Close(); foreground.Close(); desktop.Close(); underneath.Close(); }
        });
    }

    private static void AssertAbove(nint upper, nint lower)
    {
        for (var next = GetWindow(upper, 2); next != 0; next = GetWindow(next, 2))
            if (next == lower) return;
        Assert.Fail("A foreground notification must not change the desktop owner's native depth or interrupt Shell restore state.");
    }

    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
