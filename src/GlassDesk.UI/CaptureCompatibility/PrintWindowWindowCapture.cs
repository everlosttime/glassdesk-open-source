using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using GlassDesk.UI.CaptureCompatibility;

/// <summary>
/// 内部合成窗口的取帧实现：不用 Windows.Graphics.Capture，而是逐帧调用
/// <c>PrintWindow(PW_RENDERFULLCONTENT)</c> 读取载体窗口当前的 DWM 合成结果。
/// </summary>
/// <remarks>
/// 自有合成窗口在 Windows 10/11 上均沿用已有重定向表面，不创建 WGC 会话，
/// 因此这条读取路径不会产生系统捕获指示边框，也不需要申请无边框捕获权限。
/// <para>
/// <c>PW_RENDERFULLCONTENT</c>（Windows 8.1 起）由 DWM 直接返回窗口重定向表面的完整内容，
/// 因此这里既能看到 WPF 绘制的壁纸底色，也能看到注册到载体窗口上的 DWM 缩略图，
/// 也就是“玻璃后面真实的桌面”。整条路径不创建任何捕获会话，所以没有捕获指示器，
/// 也不会影响本进程窗口被录屏软件记录。
/// </para>
/// <para>
/// 该调用在 DWM 侧完成，不经过窗口消息线程，因此不会和 WPF 渲染线程互相等待。
/// </para>
/// </remarks>
internal sealed class PrintWindowWindowCapture : IWindowCapture
{
    private const uint RenderFullContent = 2;
    private const uint BiRgb = 0;
    private const int DibRgbColors = 0;

    /// <summary>上限约 30 fps：载体只承载静态场景与缩略图，没必要按屏幕刷新率重绘。</summary>
    private static readonly TimeSpan MinimumFrameInterval = TimeSpan.FromMilliseconds(1000.0 / 30);

    /// <summary>
    /// 「背景实时更新」打开时的最小取帧间隔。真正的节拍由 DwmFlush 的垂直同步给出，
    /// 这里只是一条下限，免得高刷新率屏幕上把一整个核烧在取帧上。
    /// </summary>
    private static readonly TimeSpan LiveFrameInterval = TimeSpan.FromMilliseconds(8);

    /// <summary>合成提交上限约 10 次/秒：缩略图刷新远慢于屏幕刷新，没必要每帧都等 DWM。</summary>
    private static readonly TimeSpan MinimumFlushInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// 桌面没有变化时的取帧间隔。静止的桌面重绘多少次都是同一张图，
    /// 这里主动降频，避免常驻 Dock 白烧一个核；一旦采样到变化立刻回到满速。
    /// </summary>
    /// <remarks>
    /// 必须明显小于消费端的 120 ms 新鲜度上限：超过它，交出去的帧会被判为过期，
    /// 玻璃反而停在最后一张画面上，静止时看起来就像「不再更新」。
    /// </remarks>
    private static readonly TimeSpan IdleFrameInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>连续多少帧没有变化之后开始降频。</summary>
    private const int IdleFramesBeforeBackOff = 3;

    private readonly nint _window;
    private readonly int _width;
    private readonly int _height;
    private readonly Action<CaptureTrace>? _trace;
    private readonly object _gate = new();

    private nint _memoryContext;
    private nint _bitmap;
    private nint _previousObject;
    private nint _bits;
    private byte[]? _reusablePixels;
    private long _lastCaptureTicks;
    private long _lastFlushTicks;
    private long _lastSignature;
    private int _idleFrames;
    private bool _disposed;

    internal PrintWindowWindowCapture(nint window, int width, int height, Action<CaptureTrace>? trace = null)
    {
        if (window == 0) throw new ArgumentException("A scene carrier window is required.", nameof(window));
        if (width <= 0 || height <= 0 || (long)width * height > 32_000_000)
            throw new ArgumentOutOfRangeException(nameof(width), "The capture extent is invalid.");
        _window = window;
        _width = width;
        _height = height;
        _trace = trace;
        try
        {
            CreateBackBuffer();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public CapturePixels? TryRead(long notBeforeQpcTicks = 0, TimeSpan waitForFrame = default,
        CancellationToken cancellation = default, bool reusePixels = false)
    {
        if (cancellation.IsCancellationRequested) return null;

        // PrintWindow 是同步读取，没有“帧到达”事件。调用方按 16ms 节拍轮询，
        // 这里把节流做在读取侧，避免把 CPU 花在无意义的重绘上。
        if (waitForFrame > TimeSpan.Zero) WaitForNextSlot(waitForFrame);

        lock (_gate)
        {
            if (_disposed || _memoryContext == 0 || _bitmap == 0) return null;
            var started = Stopwatch.GetTimestamp();
            try
            {
                // 先让桌面合成提交一次，缩略图内容才是最新的。缩略图由 DWM 侧合成，
                // 不刷新的话读到的可能是上一轮的画面。实时模式下每帧都等一次合成，
                // 否则视频窗口在玻璃上最多只有 10 fps；省电模式仍然按 10 Hz 提交。
                var live = LiveBackdropMode.Enabled;
                if (live || Stopwatch.GetElapsedTime(_lastFlushTicks) >= MinimumFlushInterval)
                {
                    Marshal.ThrowExceptionForHR(DwmFlush());
                    _lastFlushTicks = Stopwatch.GetTimestamp();
                }
                if (!PrintWindow(_window, _memoryContext, RenderFullContent))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "PrintWindow(PW_RENDERFULLCONTENT) failed.");
            }
            catch (Exception error)
            {
                _trace?.Invoke(new("PrintWindow", "Failed", Stopwatch.GetTimestamp(), Detail: error.Message));
                return null;
            }

            var length = checked(_width * _height * 4);
            var pixels = reusePixels && _reusablePixels is not null && _reusablePixels.Length == length
                ? _reusablePixels
                : new byte[length];
            if (reusePixels) _reusablePixels = pixels;

            // 32bpp DIB 的行跨度恒等于 width * 4，因此整块可以一次拷走。
            Marshal.Copy(_bits, pixels, 0, length);
            MakeOpaque(pixels);
            TrackChange(pixels);
            _lastCaptureTicks = Stopwatch.GetTimestamp();
            _trace?.Invoke(new("PrintWindow", "Delivered", _lastCaptureTicks));
            return new CapturePixels(_width, _height, pixels) { SourceQpcTicks = _lastCaptureTicks };
        }
    }

    private void WaitForNextSlot(TimeSpan budget)
    {
        // 实时模式不降频：只要桌面可能还在动，就一直按满速取帧。
        var live = LiveBackdropMode.Enabled;
        var interval = live
            ? LiveFrameInterval
            : _idleFrames >= IdleFramesBeforeBackOff ? IdleFrameInterval : MinimumFrameInterval;
        var elapsed = Stopwatch.GetElapsedTime(_lastCaptureTicks);
        var remaining = interval - elapsed;
        if (remaining <= TimeSpan.Zero) return;
        if (remaining > budget) remaining = budget;
        if (remaining > TimeSpan.Zero) Thread.Sleep(remaining);
    }

    private void TrackChange(byte[] pixels)
    {
        var signature = Signature(pixels);
        if (signature == _lastSignature)
        {
            if (_idleFrames < int.MaxValue) _idleFrames++;
            return;
        }

        _lastSignature = signature;
        _idleFrames = 0;
    }

    /// <summary>稀疏采样得到画面指纹：512 个点足以判断桌面有没有动，不必整块比较。</summary>
    private static long Signature(byte[] pixels)
    {
        var step = Math.Max(64, (pixels.Length / 512) & ~3);
        var hash = 1469598103934665603L;
        for (var index = 0; index < pixels.Length; index += step)
            hash = unchecked((hash ^ pixels[index]) * 1099511628211L);
        return hash;
    }

    private static void MakeOpaque(byte[] pixels)
    {
        // 分层载体的 alpha 可能是 0，玻璃链路只使用颜色通道，这里统一置为不透明。
        for (var index = 3; index < pixels.Length; index += 4) pixels[index] = 255;
    }

    private void CreateBackBuffer()
    {
        var screen = GetDC(0);
        if (screen == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "A screen device context is required.");
        try
        {
            _memoryContext = CreateCompatibleDC(screen);
            if (_memoryContext == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            var header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                Width = _width,
                Height = -_height, // 负数 = top-down，避免每次读取时翻转
                Planes = 1,
                BitCount = 32,
                Compression = BiRgb
            };
            var info = new BitmapInfo { Header = header };
            _bitmap = CreateDIBSection(_memoryContext, ref info, DibRgbColors, out _bits, 0, 0);
            if (_bitmap == 0 || _bits == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            _previousObject = SelectObject(_memoryContext, _bitmap);
            if (_previousObject == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            ReleaseDC(0, screen);
        }
    }

    public void RefreshSessionAtSceneBoundary()
    {
        // PrintWindow 每次调用都读取当前合成结果，没有需要重建的会话或帧池。
    }

    public void RetireQueuedFramesAtSceneBoundary(long barrierTicks)
    {
        // 没有排队中的原生帧：下一次 TryRead 读到的必然是新场景。
    }

    public void ReleaseReadbackBuffers()
    {
        lock (_gate) _reusablePixels = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _reusablePixels = null;
            if (_memoryContext != 0 && _previousObject != 0) SelectObject(_memoryContext, _previousObject);
            _previousObject = 0;
            if (_bitmap != 0) DeleteObject(_bitmap);
            _bitmap = 0;
            _bits = 0;
            if (_memoryContext != 0) DeleteDC(_memoryContext);
            _memoryContext = 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Colors;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PrintWindow(nint window, nint deviceContext, uint flags);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint window, nint deviceContext);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint CreateCompatibleDC(nint deviceContext);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint CreateDIBSection(nint deviceContext, ref BitmapInfo info, uint usage,
        out nint bits, nint section, uint offset);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint SelectObject(nint deviceContext, nint objectHandle);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(nint objectHandle);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteDC(nint deviceContext);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();
}
