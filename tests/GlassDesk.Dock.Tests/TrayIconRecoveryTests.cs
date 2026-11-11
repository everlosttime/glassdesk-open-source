using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using GlassDesk.Platform;

namespace GlassDesk.Dock.Tests;

public sealed class TrayIconRecoveryTests
{
    [Fact]
    public void UpdateReaddsRegistrationRemovedByTheShell()
    {
        StaTest.Run(() =>
        {
            using var owner = Owner();
            using var tray = new TrayIcon(owner.Handle, 74);
            var icon = Icon();
            Assert.True(tray.Show(icon, "GlassDesk tray recovery test"));
            var removed = Data(owner.Handle, 74);
            Assert.True(Notify(2, ref removed));
            Assert.False(Notify(1, ref removed));
            Assert.True(tray.Update(icon, "GlassDesk restored"));
            Assert.True(Notify(1, ref removed));
        });
    }

    [Fact]
    public void ShowingTheSameIconAgainKeepsOneUsableRegistration()
    {
        StaTest.Run(() =>
        {
            using var owner = Owner();
            using var tray = new TrayIcon(owner.Handle, 75);
            var icon = Icon();
            Assert.True(tray.Show(icon, "GlassDesk tray recovery test"));
            Assert.True(tray.Show(icon, "GlassDesk tray recovery test"));
            var probe = Data(owner.Handle, 75);
            Assert.True(Notify(1, ref probe));
        });
    }

    [Fact]
    public void HealthCheckRestoresMissingRegistrationButDoesNotResurrectAnIntentionallyHiddenIcon()
    {
        StaTest.Run(() =>
        {
            using var owner = Owner();
            using var tray = new TrayIcon(owner.Handle, 76);
            Assert.True(tray.Show(Icon(), "GlassDesk tray recovery test"));
            var probe = Data(owner.Handle, 76);
            Assert.True(Notify(2, ref probe));
            var ensure = typeof(TrayIcon).GetMethod("EnsureVisible");
            Assert.NotNull(ensure);
            Assert.Equal(true, ensure.Invoke(tray, null));
            Assert.True(Notify(1, ref probe));
            tray.Hide();
            Assert.Equal(false, ensure.Invoke(tray, null));
            Assert.False(Notify(1, ref probe));
        });
    }

    private static HwndSource Owner() => new(new HwndSourceParameters("GlassDesk-tray-recovery-test")
        { Width = 0, Height = 0, WindowStyle = 0, ParentWindow = 0 });
    private static nint Icon()
    {
        var pixels = new byte[16 * 16 * 4];
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = 220; pixels[i + 1] = 140; pixels[i + 3] = 255; }
        var icon = IconFactory.CreateIconFromBgra(pixels, 16, 16);
        Assert.NotEqual(0, icon); return icon;
    }
    private static NotificationData Data(nint owner, int id) => new()
        { Size = Marshal.SizeOf<NotificationData>(), Window = owner, Id = id, Tip = "", Info = "", Title = "" };

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Notify(int message, ref NotificationData data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotificationData
    {
        public int Size; public nint Window; public int Id, Flags, Callback; public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public int State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public int Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Title;
        public int InfoFlags; public Guid Guid; public nint Balloon;
    }
}
