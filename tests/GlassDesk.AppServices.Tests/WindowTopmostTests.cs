using System.Diagnostics;
using System.Runtime.InteropServices;
using GlassDesk.Platform;

namespace GlassDesk.AppServices.Tests;

[CollectionDefinition("Window topmost native", DisableParallelization = true)]
public sealed class WindowTopmostCollection;

[Collection("Window topmost native")]
public sealed class WindowTopmostTests
{
    [Fact]
    public void SelectingTwiceChangesNativeTopmostWithoutMovingWindow()
    {
        using var fixture = new TopmostFixture();
        var target = Assert.IsType<WindowTopmostTarget>(WindowTopmostWindows.Inspect(fixture.Main));
        GetWindowRect(fixture.Main, out var before);
        using var session = new WindowTopmostSession();

        Assert.True(session.TryToggle(target, out var pinned, out var error), error);
        Assert.True(pinned);
        Assert.True(IsTopmost(fixture.Main));
        GetWindowRect(fixture.Main, out var after);
        Assert.Equal(before, after);
        Assert.True(session.TryToggle(target, out pinned, out error), error);
        Assert.False(pinned);
        Assert.False(IsTopmost(fixture.Main));
    }

    [Fact]
    public void DisposeRestoresEachOriginalOwnerAndOwnedState()
    {
        using var fixture = new TopmostFixture();
        Assert.False(IsTopmost(fixture.Main));
        Assert.False(IsTopmost(fixture.Owned));
        Assert.True(IsTopmost(fixture.OriginallyTopmostOwned));
        var session = new WindowTopmostSession();
        var target = Assert.IsType<WindowTopmostTarget>(WindowTopmostWindows.Inspect(fixture.Main));
        Assert.True(session.TryToggle(target, out _, out var error), error);
        Assert.True(SpinWait.SpinUntil(() => IsTopmost(fixture.Owned), TimeSpan.FromMilliseconds(300)), $"root={fixture.Main} owner={GetWindow(fixture.Owned, 4)} rootstyle={GetWindowLong(fixture.Main, -20):X} ownedstyle={GetWindowLong(fixture.Owned, -20):X}");

        session.Dispose();

        Assert.False(IsTopmost(fixture.Main));
        Assert.False(IsTopmost(fixture.Owned));
        Assert.True(IsTopmost(fixture.OriginallyTopmostOwned));
        Assert.False(session.TryToggle(target, out _, out _));
    }

    [Fact]
    public void MultipleWindowsToggleIndependently()
    {
        using var first = new TopmostFixture();
        using var second = new TopmostFixture();
        using var session = new WindowTopmostSession();
        var a = Assert.IsType<WindowTopmostTarget>(WindowTopmostWindows.Inspect(first.Main));
        var b = Assert.IsType<WindowTopmostTarget>(WindowTopmostWindows.Inspect(second.Main));
        Assert.True(session.TryToggle(a, out _, out var error), error);
        Assert.True(session.TryToggle(b, out _, out error), error);
        Assert.True(session.TryToggle(a, out _, out error), error);
        Assert.False(IsTopmost(first.Main));
        Assert.True(IsTopmost(second.Main));
    }

    [Fact]
    public void MismatchedIdentityCannotChangeWindow()
    {
        using var fixture = new TopmostFixture();
        var target = Assert.IsType<WindowTopmostTarget>(WindowTopmostWindows.Inspect(fixture.Main));
        using var session = new WindowTopmostSession();
        Assert.False(session.TryToggle(target with { ProcessId = target.ProcessId + 1 }, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.False(IsTopmost(fixture.Main));
        Assert.Null(WindowTopmostWindows.Inspect(0));
    }

    [Fact]
    public void FindAtResolvesOwnedPopupToApplicationOwner()
    {
        using var fixture = new TopmostFixture();
        GetWindowRect(fixture.OriginallyTopmostOwned, out var bounds);
        var target = Assert.IsType<WindowTopmostTarget>(WindowTopmostWindows.FindAt(bounds.Left + 15, bounds.Top + 40));
        Assert.True(fixture.Main == target.Handle, $"Expected {fixture.Main} pid={fixture.ProcessId}, selected {target.Handle} ({target.Title}) pid={target.ProcessId}; owned={fixture.Owned} popup={fixture.OriginallyTopmostOwned} style={GetWindowLong(fixture.OriginallyTopmostOwned, -20):X} owner={GetWindow(fixture.OriginallyTopmostOwned, 4)} bounds={bounds}");
        Assert.Equal((uint)fixture.ProcessId, target.ProcessId);
        Assert.Equal("GlassDesk topmost fixture", target.Title);
    }

    [Fact]
    public void ClosingTargetBeforeSelectionReturnsFailure()
    {
        using var fixture = new TopmostFixture();
        var target = Assert.IsType<WindowTopmostTarget>(WindowTopmostWindows.Inspect(fixture.Main));
        using var session = new WindowTopmostSession();
        fixture.Close();
        Assert.False(session.TryToggle(target, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void NewOwnedPopupDoesNotKeepInheritedTopmostAfterUnpin()
    {
        using var fixture = new TopmostFixture();
        var target = Assert.IsType<WindowTopmostTarget>(WindowTopmostWindows.Inspect(fixture.Main));
        using var session = new WindowTopmostSession();
        Assert.True(session.TryToggle(target, out _, out var error), error);
        var popup = fixture.CreateOwnedPopup();
        Assert.True(IsTopmost(popup), "A native popup inherits the topmost owner state");

        Assert.True(session.TryToggle(target, out _, out error), error);

        Assert.False(IsTopmost(popup));
        Assert.True(IsTopmost(fixture.OriginallyTopmostOwned));
        session.Dispose();
        Assert.False(IsTopmost(popup));
    }

    [Fact]
    public void MissingLifetimeTokenRejectsOldSelectionButAllowsFreshInspection()
    {
        using var fixture = new TopmostFixture();
        var target = Assert.IsType<WindowTopmostTarget>(WindowTopmostWindows.Inspect(fixture.Main));
        using var session = new WindowTopmostSession();
        Assert.True(session.TryToggle(target, out _, out var error), error);
        // The missing native lifetime marker also occurs when Windows recycles an
        // HWND on the same process/thread. Clear only our fixture's feature marker.
        var removed = false;
        EnumProps(fixture.Main, (window, name, value) =>
        {
            if (name.ToInt64() > ushort.MaxValue && Marshal.PtrToStringUni(name)?.StartsWith("GlassDesk.WindowTopmost.", StringComparison.Ordinal) == true)
            { RemoveProp(window, Marshal.PtrToStringUni(name)!); removed = true; }
            return 1;
        });
        Assert.True(removed);
        Assert.False(session.TryToggle(target, out _, out _));
        var fresh = Assert.IsType<WindowTopmostTarget>(WindowTopmostWindows.Inspect(fixture.Main));

        Assert.True(session.TryToggle(fresh, out var pinned, out error), error);

        Assert.False(pinned);
    }

    [Fact]
    public void TimedOutForeignWindowDoesNotFreezeOrRemainPinnedAfterDispose()
    {
        using var fixture = new TopmostFixture();
        var target = Assert.IsType<WindowTopmostTarget>(WindowTopmostWindows.Inspect(fixture.Main));
        using var session = new WindowTopmostSession();
        fixture.BeginTemporaryHang();
        var elapsed = Stopwatch.StartNew();

        Assert.False(session.TryToggle(target, out _, out var error));

        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(1), $"Foreign message loop blocked for {elapsed.Elapsed}");
        session.Dispose();
        fixture.WaitForTemporaryHang();
        Assert.False(IsTopmost(fixture.Main));
        Assert.True(IsTopmost(fixture.OriginallyTopmostOwned));
    }

    [Fact]
    public void VisibleForeignToolWindowBlocksSelectionOfApplicationBehindIt()
    {
        using var fixture = new TopmostFixture();
        var tool = fixture.CreateToolWindow();
        Assert.Equal(0, DwmGetWindowAttribute(tool, 9, out var bounds, Marshal.SizeOf<NativeRect>()));
        ShowWindow(tool, 0);
        Assert.Equal(fixture.Main, WindowTopmostWindows.FindAt(bounds.Left + 20, bounds.Top + 20)?.Handle);
        ShowWindow(tool, 8);
        SetWindowPos(tool, -1, 0, 0, 0, 0, 0x13);
        Assert.True((GetWindowLong(tool, -20) & 0x88) == 0x88);

        Assert.Null(WindowTopmostWindows.FindAt(bounds.Left + 20, bounds.Top + 20));
    }

    [Fact]
    public void RepeatedUnchangedInspectionHasStableSnapshotEquality()
    {
        using var fixture = new TopmostFixture();
        var first = Assert.IsType<WindowTopmostTarget>(WindowTopmostWindows.Inspect(fixture.Main));
        var second = Assert.IsType<WindowTopmostTarget>(WindowTopmostWindows.Inspect(fixture.Main));
        Assert.Equal(first, second);
    }

    [Fact]
    public void OriginallyTopmostMainCanBeUnpinnedAndRestoresOnDispose()
    {
        using var fixture = new TopmostFixture();
        Assert.True(SetWindowPos(fixture.Main, -1, 0, 0, 0, 0, 0x13));
        Assert.True(IsTopmost(fixture.Main));
        var target = Assert.IsType<WindowTopmostTarget>(WindowTopmostWindows.Inspect(fixture.Main));
        using var session = new WindowTopmostSession();

        Assert.True(session.TryToggle(target, out var pinned, out var error), error);
        Assert.False(pinned);
        Assert.False(IsTopmost(fixture.Main));
        session.Dispose();

        Assert.True(IsTopmost(fixture.Main));
    }

    [Fact]
    public void PinnedWindowKeepsTopmostAfterMinimizeAndRestore()
    {
        using var fixture = new TopmostFixture();
        var target = Assert.IsType<WindowTopmostTarget>(WindowTopmostWindows.Inspect(fixture.Main));
        using var session = new WindowTopmostSession();
        Assert.True(session.TryToggle(target, out _, out var error), error);

        ShowWindow(fixture.Main, 6);
        Assert.True(SpinWait.SpinUntil(() => IsIconic(fixture.Main), TimeSpan.FromSeconds(2)));
        Assert.Null(WindowTopmostWindows.Inspect(fixture.Main));
        ShowWindow(fixture.Main, 9);
        Assert.True(SpinWait.SpinUntil(() => !IsIconic(fixture.Main), TimeSpan.FromSeconds(2)));

        Assert.True(IsTopmost(fixture.Main));
        Assert.True(Assert.IsType<WindowTopmostTarget>(WindowTopmostWindows.Inspect(fixture.Main)).IsTopmost);
    }

    private static bool IsTopmost(nint window) => (GetWindowLong(window, -20) & 8) != 0;

    [StructLayout(LayoutKind.Sequential)]
    private record struct NativeRect(int Left, int Top, int Right, int Bottom);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint window, int index);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out NativeRect bounds);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    private delegate int PropCallback(nint window, nint name, nint value);
    [DllImport("user32.dll", EntryPoint = "EnumPropsW")] private static extern int EnumProps(nint window, PropCallback callback);
    [DllImport("user32.dll", EntryPoint = "RemovePropW", CharSet = CharSet.Unicode)] private static extern nint RemoveProp(nint window, string name);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out NativeRect bounds, int size);
}

internal sealed class TopmostFixture : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "GlassDesk-topmost-" + Guid.NewGuid().ToString("N"));
    private readonly Process _process;
    public nint Main { get; }
    public nint Owned { get; }
    public nint OriginallyTopmostOwned { get; }
    public int ProcessId => _process.Id;

    public TopmostFixture()
    {
        Directory.CreateDirectory(_directory);
        var source = Path.Combine(_directory, "Fixture.cs");
        var executable = Path.Combine(_directory, "Fixture.exe");
        var ready = Path.Combine(_directory, "ready.txt");
        File.WriteAllText(source, """
            using System;
            using System.Drawing;
            using System.IO;
            using System.Runtime.InteropServices;
            using System.Windows.Forms;
            class Fixture {
                [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowEx(int exStyle, string cls, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
                [STAThread] static void Main(string[] args) {
                    var main = new Form { Text = "GlassDesk topmost fixture", Bounds = new Rectangle(100, 100, 340, 240), StartPosition = FormStartPosition.Manual };
                    var owned = new Form { Text = "Owned normal", Bounds = new Rectangle(150, 150, 160, 110), StartPosition = FormStartPosition.Manual, ShowInTaskbar = false };
                    main.Shown += delegate {
                        owned.Show(main);
                        var pinned = CreateWindowEx(8, "STATIC", "Owned originally topmost", 0x90000000, 230, 180, 180, 120, main.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                        File.WriteAllText(args[0] + ".tmp", main.Handle.ToInt64() + "," + owned.Handle.ToInt64() + "," + pinned.ToInt64());
                        File.Move(args[0] + ".tmp", args[0]);
                    };
                    var timeout = new Timer { Interval = 20000 }; timeout.Tick += delegate { main.Close(); }; timeout.Start();
                    var commands = new Timer { Interval = 25 };
                    commands.Tick += delegate {
                        if (File.Exists(args[0] + ".hang-request")) {
                            File.Delete(args[0] + ".hang-request");
                            File.WriteAllText(args[0] + ".hang-ready", "waiting");
                            System.Threading.Thread.Sleep(1500);
                            File.WriteAllText(args[0] + ".hang-finished", "finished");
                            return;
                        }
                        if (File.Exists(args[0] + ".tool-request")) {
                            File.Delete(args[0] + ".tool-request");
                            var tool = CreateWindowEx(0x88, "STATIC", "Unselectable tool", 0x90000000, 110, 110, 200, 100, main.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                            File.WriteAllText(args[0] + ".tool-tmp", tool.ToInt64().ToString());
                            File.Move(args[0] + ".tool-tmp", args[0] + ".tool-ready");
                            return;
                        }
                        if (!File.Exists(args[0] + ".late-request")) return;
                        File.Delete(args[0] + ".late-request");
                        var popup = CreateWindowEx(0, "STATIC", "Late owned native popup", 0x90000000, 300, 230, 120, 90, main.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                        File.WriteAllText(args[0] + ".late-tmp", popup.ToInt64().ToString());
                        File.Move(args[0] + ".late-tmp", args[0] + ".late-ready");
                    };
                    commands.Start();
                    Application.Run(main);
                }
            }
            """);
        var compiler = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Microsoft.NET", "Framework64", "v4.0.30319", "csc.exe");
        var build = new ProcessStartInfo(compiler) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "/nologo", "/target:winexe", "/reference:System.Windows.Forms.dll", "/reference:System.Drawing.dll", "/out:" + executable, source }) build.ArgumentList.Add(argument);
        using (var compilation = Process.Start(build)!)
        {
            var output = compilation.StandardOutput.ReadToEnd();
            var error = compilation.StandardError.ReadToEnd();
            Assert.True(compilation.WaitForExit(10000), "Fixture compilation timed out");
            Assert.True(compilation.ExitCode == 0, output + error);
        }
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(ready);
        _process = Process.Start(start)!;
        Assert.True(SpinWait.SpinUntil(() => File.Exists(ready) || _process.HasExited, TimeSpan.FromSeconds(5)), "Fixture did not start");
        Assert.False(_process.HasExited, "Fixture exited before creating windows");
        var handles = File.ReadAllText(ready).Split(',').Select(long.Parse).ToArray();
        Main = (nint)handles[0]; Owned = (nint)handles[1]; OriginallyTopmostOwned = (nint)handles[2];
    }

    public nint CreateOwnedPopup()
    {
        var ready = Path.Combine(_directory, "ready.txt");
        File.WriteAllText(ready + ".late-request", "create");
        Assert.True(SpinWait.SpinUntil(() => File.Exists(ready + ".late-ready"), TimeSpan.FromSeconds(2)), "Popup was not created");
        return (nint)long.Parse(File.ReadAllText(ready + ".late-ready"));
    }

    public void BeginTemporaryHang()
    {
        var ready = Path.Combine(_directory, "ready.txt");
        File.WriteAllText(ready + ".hang-request", "hang");
        Assert.True(SpinWait.SpinUntil(() => File.Exists(ready + ".hang-ready"), TimeSpan.FromSeconds(2)), "Fixture did not start blocking its message loop");
    }

    public nint CreateToolWindow()
    {
        var ready = Path.Combine(_directory, "ready.txt");
        File.WriteAllText(ready + ".tool-request", "create");
        Assert.True(SpinWait.SpinUntil(() => File.Exists(ready + ".tool-ready"), TimeSpan.FromSeconds(2)), "Tool window was not created");
        return (nint)long.Parse(File.ReadAllText(ready + ".tool-ready"));
    }

    public void WaitForTemporaryHang()
    {
        var ready = Path.Combine(_directory, "ready.txt");
        Assert.True(SpinWait.SpinUntil(() => File.Exists(ready + ".hang-finished"), TimeSpan.FromSeconds(3)), "Fixture did not resume");
        Thread.Sleep(100); // Allow the foreign message loop to drain queued window-position requests.
    }

    public void Close()
    {
        if (_process.HasExited) return;
        PostMessage(Main, 0x0010, 0, 0);
        Assert.True(_process.WaitForExit(5000), "Fixture did not close");
    }

    public void Dispose()
    {
        try { Close(); }
        finally
        {
            if (!_process.HasExited) _process.Kill();
            _process.Dispose();
            Directory.Delete(_directory, true);
        }
    }

    [DllImport("user32.dll")] private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
}
