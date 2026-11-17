using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using GlassDesk.Platform;
using GlassDesk.Platform.Displays;
using GlassDesk.UI;

namespace GlassDesk.Dock.Tests;

[CollectionDefinition("Window topmost UI", DisableParallelization = true)]
public sealed class WindowTopmostUiCollection;

[Collection("Window topmost UI")]
public sealed class WindowTopmostPickerNativeTests
{
    [Fact]
    public void RealLeftClickSelectsWithoutPressingTheUnderlyingButton()
    {
        using var fixture = new PickerButtonFixture();
        StaTest.Run(() =>
        {
            var oldCursor = DisplayService.GetCursorPixelPoint();
            using var picker = new WindowTopmostPicker();
            try
            {
                WindowActivation.TryBringToForeground(fixture.Main);
                MoveTo(fixture.ButtonCenter);
                Pump(80);
                WindowTopmostTarget? selected = null;
                picker.Begin(target => selected = target);
                Pump(180);
                Click(0x0002, 0x0004);
                Pump(180);
                Assert.False(picker.IsSelecting);
                Assert.Equal(fixture.Main, Assert.IsType<WindowTopmostTarget>(selected).Handle);
                Assert.Equal(0, fixture.ClickCount);
                Assert.Equal(0, fixture.MouseEventCount);

                // Prove that this exact location and input really operate the foreign button.
                Click(0x0002, 0x0004);
                Pump(150);
                Assert.Equal(1, fixture.ClickCount);
                Assert.Equal(2, fixture.MouseEventCount);
            }
            finally { if (oldCursor is { } p) SetCursorPos(p.X, p.Y); }
        });
    }

    [Fact]
    public void RealEscapeAndRightClickCancelWithoutLeakingMouseInput()
    {
        using var fixture = new PickerButtonFixture();
        StaTest.Run(() =>
        {
            var oldCursor = DisplayService.GetCursorPixelPoint();
            using var picker = new WindowTopmostPicker();
            var selected = 0;
            try
            {
                MoveTo(fixture.ButtonCenter);
                picker.Begin(_ => selected++);
                Pump(150);
                Send([new Input { Type = 1, Data = new() { Keyboard = new() { VirtualKey = 0x1B } } },
                      new Input { Type = 1, Data = new() { Keyboard = new() { VirtualKey = 0x1B, Flags = 2 } } }]);
                Pump(120);
                Assert.False(picker.IsSelecting);

                picker.Begin(_ => selected++);
                Pump(150);
                Click(0x0008, 0x0010);
                Pump(120);
                Assert.False(picker.IsSelecting);
                Assert.Equal(0, selected);
                Assert.Equal(0, fixture.MouseEventCount);
            }
            finally { if (oldCursor is { } p) SetCursorPos(p.X, p.Y); }
        });
    }

    [Fact]
    public void RealClickOnAnExcludedScreenAreaKeepsSelectionOpen()
    {
        StaTest.Run(() =>
        {
            var oldCursor = DisplayService.GetCursorPixelPoint();
            using var picker = new WindowTopmostPicker();
            try
            {
                var blank = DisplayService.Enumerate().SelectMany(display => new[]
                {
                    (display.Bounds.X + display.Bounds.Width / 2, display.Bounds.Bottom - 2),
                    (display.Bounds.X + 2, display.Bounds.Y + display.Bounds.Height / 2),
                    (display.Bounds.Right - 2, display.Bounds.Y + display.Bounds.Height / 2),
                }).FirstOrDefault(point => WindowTopmostWindows.FindAt(point.Item1, point.Item2) is null, (int.MinValue, int.MinValue));
                Assert.NotEqual(int.MinValue, blank.Item1);
                MoveTo(blank);
                picker.Begin(_ => Assert.Fail("An excluded screen area must not select a window."));
                Pump(150);
                Click(0x0002, 0x0004);
                Pump(120);
                Assert.True(picker.IsSelecting);
                picker.Cancel();
                Assert.False(picker.IsSelecting);
            }
            finally { if (oldCursor is { } p) SetCursorPos(p.X, p.Y); }
        });
    }

    private static void MoveTo((int X, int Y) point) => Assert.True(SetCursorPos(point.X, point.Y));
    private static void Click(uint down, uint up) => Send([
        new Input { Data = new() { Mouse = new() { Flags = down } } },
        new Input { Data = new() { Mouse = new() { Flags = up } } },
    ]);

    private static void Send(Input[] inputs) => Assert.Equal((uint)inputs.Length, SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()));

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputData Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputData
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint MouseData, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort VirtualKey, ScanCode; public uint Flags, Time; public nuint Extra; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
}

internal sealed class PickerButtonFixture : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "GlassDesk-picker-input-" + Guid.NewGuid().ToString("N"));
    private readonly Process _process;
    public nint Main { get; }
    public (int X, int Y) ButtonCenter { get; }
    public int ClickCount => ReadCount("clicks.txt");
    public int MouseEventCount => ReadCount("mouse.txt");

    public PickerButtonFixture()
    {
        Directory.CreateDirectory(_directory);
        var source = Path.Combine(_directory, "Fixture.cs");
        var executable = Path.Combine(_directory, "Fixture.exe");
        var ready = Path.Combine(_directory, "ready.txt");
        File.WriteAllText(source, """
            using System;
            using System.Drawing;
            using System.IO;
            using System.Windows.Forms;
            class Fixture {
                [STAThread] static void Main(string[] args) {
                    var form = new Form { Text = "GlassDesk picker input fixture", Bounds = new Rectangle(180, 160, 440, 280), StartPosition = FormStartPosition.Manual };
                    var button = new Button { Text = "Selection must not press this button", Dock = DockStyle.Fill };
                    form.Controls.Add(button);
                    int clicks = 0, mouse = 0;
                    button.Click += delegate { File.WriteAllText(Path.Combine(args[0], "clicks.txt"), (++clicks).ToString()); };
                    button.MouseDown += delegate { File.WriteAllText(Path.Combine(args[0], "mouse.txt"), (++mouse).ToString()); };
                    button.MouseUp += delegate { File.WriteAllText(Path.Combine(args[0], "mouse.txt"), (++mouse).ToString()); };
                    form.Shown += delegate {
                        var bounds = button.RectangleToScreen(button.ClientRectangle);
                        File.WriteAllText(Path.Combine(args[0], "ready.txt"), form.Handle.ToInt64() + "," + (bounds.Left + bounds.Width / 2) + "," + (bounds.Top + bounds.Height / 2));
                    };
                    var deadline = new Timer { Interval = 20000 }; deadline.Tick += delegate { form.Close(); }; deadline.Start();
                    Application.Run(form);
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
        start.ArgumentList.Add(_directory);
        _process = Process.Start(start)!;
        Assert.True(SpinWait.SpinUntil(() => File.Exists(ready) || _process.HasExited, TimeSpan.FromSeconds(5)), "Fixture did not start");
        Assert.False(_process.HasExited, "Fixture exited before creating a window");
        var values = File.ReadAllText(ready).Split(',').Select(long.Parse).ToArray();
        Main = (nint)values[0]; ButtonCenter = ((int)values[1], (int)values[2]);
    }

    private int ReadCount(string name) => File.Exists(Path.Combine(_directory, name)) ? int.Parse(File.ReadAllText(Path.Combine(_directory, name))) : 0;

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                PostMessage(Main, 0x0010, 0, 0);
                if (!_process.WaitForExit(3000)) _process.Kill();
            }
            _process.WaitForExit(3000);
        }
        finally { _process.Dispose(); Directory.Delete(_directory, true); }
    }

    [DllImport("user32.dll")] private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
}
