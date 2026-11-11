using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using GlassDesk.Launcher;

internal sealed class InputDeviceProbe : IDisposable
{
    internal readonly record struct MousePacket(nint Device, int X, int Y, ushort ButtonFlags);
    internal static bool TryDecodeMouse(nint buffer, uint size, out MousePacket packet)
    {
        packet = default;
        var header = 8 + 2 * IntPtr.Size;
        if (size < header + 24 || Marshal.ReadInt32(buffer) != 0 || Marshal.ReadInt32(buffer, 4) != size) return false;
        packet = new(Marshal.ReadIntPtr(buffer, 8), Marshal.ReadInt32(buffer, header + 12),
            Marshal.ReadInt32(buffer, header + 16), unchecked((ushort)Marshal.ReadInt16(buffer, header + 4)));
        return true;
    }

    private const int BinCount = 6100;
    private readonly long _start = Stopwatch.GetTimestamp();
    private readonly LauncherWindow _window;
    private readonly string _evidence;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly UiBin[] _ui = new UiBin[BinCount];
    private readonly RawBin[] _clock = new RawBin[BinCount];
    private readonly Dictionary<nint, RawBin[]> _devices = [];
    private readonly List<object> _sessions = [];
    private readonly GiveFeedbackEventHandler _feedback;
    private readonly QueryContinueDragEventHandler _query;
    private readonly DragEventHandler _over;
    private readonly PreProcessInputEventHandler _input;
    private Dispatcher? _observerDispatcher;
    private string? _error;
    private int _pulses, _session;
    private long _feedbackCount, _queryCount;
    private bool _disposed;

    private struct UiBin { public int Feedback, Query, DragOver, MouseMove; }
    private struct RawBin { public int Packets, Moving, ButtonPackets, Pulses, MaxQueueAgeMs; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Registration { public ushort Page, Usage; public uint Flags; public nint Target; }
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(Registration[] devices, uint count, uint size);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRegisteredRawInputDevices([Out] Registration[]? devices, ref uint count, uint size);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(nint raw, uint command, nint data, ref uint size, uint headerSize);
    [DllImport("user32.dll")] private static extern int GetMessageTime();

    public InputDeviceProbe(LauncherWindow window, string evidence)
    {
        _window = window; _evidence = evidence;
        _feedback = (_, _) => { _ui[Bin()].Feedback++; _feedbackCount++; };
        _query = (_, _) => { _ui[Bin()].Query++; _queryCount++; };
        _over = (_, _) => _ui[Bin()].DragOver++;
        _input = (_, e) => { if (e.StagingItem.Input is MouseEventArgs mouse && mouse.RoutedEvent == Mouse.MouseMoveEvent) _ui[Bin()].MouseMove++; };
        window.AddHandler(DragDrop.GiveFeedbackEvent, _feedback, true);
        window.AddHandler(DragDrop.QueryContinueDragEvent, _query, true);
        window.AddHandler(DragDrop.DragOverEvent, _over, true);
        InputManager.Current.PreProcessInput += _input;
        window.InternalDragStarted += Started;
        window.InternalDragFinished += Finished;
        _thread = new Thread(Observe) { IsBackground = true, Name = "GlassDesk acceptance raw mouse observer" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(3)); // Before the user begins dragging.
    }

    private int Bin() => (int)Math.Clamp(Stopwatch.GetElapsedTime(_start).TotalMilliseconds / 100, 0, BinCount - 1);
    private void Started(object? sender, EventArgs e) => _sessions.Add(new { session = ++_session, phase = "start", atTicks = Stopwatch.GetTimestamp() });
    private void Finished(object? sender, EventArgs e) => _sessions.Add(new { session = _session, phase = "finish", atTicks = Stopwatch.GetTimestamp() });
    public bool Ready { get; private set; }
    public int BackgroundPulses => Volatile.Read(ref _pulses);
    public long FeedbackCount => _feedbackCount;
    public long QueryCount => _queryCount;
    public bool Stopped => !_thread.IsAlive;

    private void Observe()
    {
        var buffer = Marshal.AllocHGlobal(256);
        var registered = false;
        try
        {
            _observerDispatcher = Dispatcher.CurrentDispatcher;
            uint count = 0;
            var registrationSize = (uint)Marshal.SizeOf<Registration>();
            if (GetRegisteredRawInputDevices(null, ref count, registrationSize) == uint.MaxValue)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (count != 0)
            {
                var prior = new Registration[count];
                if (GetRegisteredRawInputDevices(prior, ref count, registrationSize) == uint.MaxValue)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if (prior.Any(d => d.Page == 1 && d.Usage == 2))
                    throw new InvalidOperationException("Existing raw mouse registration: observation skipped to preserve input behavior.");
            }
            using var source = new HwndSource(new HwndSourceParameters("GlassDesk raw mouse diagnostic")
            { ParentWindow = new nint(-3), Width = 0, Height = 0, WindowStyle = 0 });
            source.AddHook((nint hwnd, int message, nint wParam, nint lParam, ref bool handled) =>
            {
                if (message != 0x00FF) return 0;
                uint size = 256;
                var bytes = GetRawInputData(lParam, 0x10000003, buffer, ref size, (uint)(8 + 2 * IntPtr.Size));
                if (bytes != uint.MaxValue && TryDecodeMouse(buffer, bytes, out var packet))
                {
                    if (!_devices.TryGetValue(packet.Device, out var bins))
                    {
                        if (_devices.Count >= 16) return 0;
                        bins = new RawBin[BinCount]; _devices.Add(packet.Device, bins);
                    }
                    ref var bin = ref bins[Bin()];
                    bin.Packets++;
                    if (packet.X != 0 || packet.Y != 0) bin.Moving++;
                    if (packet.ButtonFlags != 0) bin.ButtonPackets++;
                    bin.MaxQueueAgeMs = Math.Max(bin.MaxQueueAgeMs, Math.Max(0, unchecked(Environment.TickCount - GetMessageTime())));
                }
                return 0; // Leave native input handling and cleanup untouched.
            });
            if (!RegisterRawInputDevices([new Registration { Page = 1, Usage = 2, Flags = 0x100, Target = source.Handle }], 1, registrationSize))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            registered = true;
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
            timer.Tick += (_, _) => { _clock[Bin()].Pulses++; Interlocked.Increment(ref _pulses); };
            timer.Start();
            Ready = true; _ready.Set();
            Dispatcher.Run();
            timer.Stop();
        }
        catch (Exception ex) { _error = ex.Message; }
        finally
        {
            if (registered) RegisterRawInputDevices([new Registration { Page = 1, Usage = 2, Flags = 2 }], 1, (uint)Marshal.SizeOf<Registration>());
            Marshal.FreeHGlobal(buffer);
            _ready.Set();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _window.RemoveHandler(DragDrop.GiveFeedbackEvent, _feedback);
        _window.RemoveHandler(DragDrop.QueryContinueDragEvent, _query);
        _window.RemoveHandler(DragDrop.DragOverEvent, _over);
        InputManager.Current.PreProcessInput -= _input;
        _window.InternalDragStarted -= Started;
        _window.InternalDragFinished -= Finished;
        _observerDispatcher?.BeginInvokeShutdown(DispatcherPriority.Send);
        var stopped = _thread.Join(TimeSpan.FromSeconds(2));
        // Never enumerate mutable background buffers if shutdown timed out.
        var deviceRows = stopped ? _devices.Select((device, index) => new
        {
            device = index + 1, zeroDeviceHandle = device.Key == 0,
            packets = device.Value.Sum(b => b.Packets), movingPackets = device.Value.Sum(b => b.Moving),
            bins = device.Value.Select((b, i) => new { atMilliseconds = i * 100, b.Packets, b.Moving, b.ButtonPackets, b.MaxQueueAgeMs })
                .Where(b => b.Packets != 0).ToArray()
        }).ToArray() : null;
        File.WriteAllText(Path.Combine(_evidence, "input-device-timing.json"), JsonSerializer.Serialize(new
        {
            note = "Raw mouse packets observed on an independent STA message thread; 100ms bins show delivered movement frequency, not configured USB polling rate. Precision touchpad or injected input may have no raw mouse packets or a zero device handle. UI counts observe normal mouse delivery and cross-window OLE without changing them. No device names or mouse positions are saved.",
            startTicks = _start, stopwatchFrequency = Stopwatch.Frequency, binMilliseconds = 100,
            ready = Ready, stopped, error = _error, backgroundPulses = BackgroundPulses,
            sessions = _sessions, devices = deviceRows,
            ui = _ui.Select((b, i) => new { atMilliseconds = i * 100, b.Feedback, b.Query, b.DragOver, b.MouseMove })
                .Where(b => b.Feedback + b.Query + b.DragOver + b.MouseMove != 0).ToArray(),
            backgroundClock = stopped ? _clock.Select((b, i) => new { atMilliseconds = i * 100, b.Pulses }).Where(b => b.Pulses != 0).ToArray() : null
        }, new JsonSerializerOptions { WriteIndented = true }));
        if (stopped) _ready.Dispose();
    }
}
