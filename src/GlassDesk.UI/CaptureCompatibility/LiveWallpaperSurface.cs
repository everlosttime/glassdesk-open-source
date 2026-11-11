using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GlassDesk.UI.CaptureCompatibility;

/// <summary>
/// 逐帧读取动态壁纸窗口（Wallpaper Engine 的 WPEVideoWallpaper 之类）。
/// </summary>
/// <remarks>
/// 为什么不能用 DWM 缩略图：实测在 Windows 10 上
/// <list type="bullet">
///   <item>对壁纸子窗口本身调用 <c>DwmRegisterThumbnail</c> 返回 <c>E_INVALIDARG</c>；</item>
///   <item>对它的父窗口（Progman / WorkerW）能登记成功，但缩略图内容是**冻结**的：
///         连续三秒采样像素级零变化，而屏幕上的壁纸一直在动。</item>
/// </list>
/// 只有 <c>PrintWindow(PW_RENDERFULLCONTENT)</c> 直接读这个窗口，才能拿到实时画面。
/// 这里把整窗读进一张 DIB，再用 StretchBlt 裁到取景区域并缩放到目标尺寸，
/// 只把目标大小的一块拷回托管内存，避免每帧搬运整屏像素。
/// </remarks>
internal sealed class LiveWallpaperSurface : IDisposable
{
    private const uint RenderFullContent = 2;
    private const uint SourceCopy = 0x00CC0020;
    private const int HalfTone = 3;

    private readonly int _sourceWidth;
    private readonly int _sourceHeight;
    private readonly int _targetWidth;
    private readonly int _targetHeight;
    private readonly Func<nint, nint, bool> _readback;
    private readonly Action<CaptureTrace>? _trace;
    private readonly string? _sharedScope;
    private SharedWallpaperBuffer? _sharedSource;
    private readonly bool _shareFrames;
    private readonly ScreenRect[] _displays;
    private long _lastSharedSequence;
    internal long LastSourceQpcTicks {get;private set;}

    private nint _sourceDc, _sourceBitmap, _sourceOld, _sourceBits;
    private nint _targetDc, _targetBitmap, _targetOld, _targetBits;
    private bool _disposed;

    internal LiveWallpaperSurface(int sourceWidth, int sourceHeight, int targetWidth, int targetHeight,
        Action<CaptureTrace>? trace = null)
        : this(sourceWidth, sourceHeight, targetWidth, targetHeight,
            static (window, context) => PrintWindow(window, context, RenderFullContent), trace) { }

    internal LiveWallpaperSurface(int sourceWidth,int sourceHeight,int targetWidth,int targetHeight,
        Action<CaptureTrace>? trace,string? sharedScope)
        :this(sourceWidth,sourceHeight,targetWidth,targetHeight,
            static(window,context)=>PrintWindow(window,context,RenderFullContent),trace,sharedScope,
            Environment.GetEnvironmentVariable(SharedWallpaperBuffer.FrameFanoutEnvironmentVariable)=="1") { }

    internal LiveWallpaperSurface(int sourceWidth, int sourceHeight, int targetWidth, int targetHeight,
        Func<nint, nint, bool> readback, Action<CaptureTrace>? trace)
        :this(sourceWidth,sourceHeight,targetWidth,targetHeight,readback,trace,null) { }

    internal LiveWallpaperSurface(int sourceWidth,int sourceHeight,int targetWidth,int targetHeight,
        Func<nint,nint,bool> readback,Action<CaptureTrace>? trace,string? sharedScope)
        :this(sourceWidth,sourceHeight,targetWidth,targetHeight,readback,trace,sharedScope,false) { }

    internal LiveWallpaperSurface(int sourceWidth,int sourceHeight,int targetWidth,int targetHeight,
        Func<nint,nint,bool> readback,Action<CaptureTrace>? trace,string? sharedScope,bool shareFrames)
    {
        _readback = readback ?? throw new ArgumentNullException(nameof(readback));
        _trace = trace;
        _sharedScope=sharedScope;
        _shareFrames=shareFrames && sharedScope is not null;
        _displays = GlassDesk.Platform.Displays.DisplayService.Enumerate()
            .Select(display => new ScreenRect(display.Bounds.X, display.Bounds.Y,
                display.Bounds.Width, display.Bounds.Height)).ToArray();
        if (sourceWidth <= 0 || sourceHeight <= 0 || (long)sourceWidth * sourceHeight > 64_000_000)
            throw new ArgumentOutOfRangeException(nameof(sourceWidth));
        if (targetWidth <= 0 || targetHeight <= 0 || (long)targetWidth * targetHeight > 32_000_000)
            throw new ArgumentOutOfRangeException(nameof(targetWidth));
        _sourceWidth = sourceWidth; _sourceHeight = sourceHeight;
        _targetWidth = targetWidth; _targetHeight = targetHeight;
        try
        {
            _sourceDc = CreateCompatibleDC(0);
            _targetDc = CreateCompatibleDC(0);
            if(sharedScope is null)_sourceBitmap = CreateDib(_sourceDc, sourceWidth, sourceHeight, out _sourceBits);
            _targetBitmap = CreateDib(_targetDc, targetWidth, targetHeight, out _targetBits);
            if (_sourceDc == 0 || _targetDc == 0 || _targetBitmap == 0 || _targetBits == 0
                || sharedScope is null && (_sourceBitmap==0 || _sourceBits==0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Live wallpaper buffers are unavailable.");
            if(sharedScope is null)_sourceOld = SelectObject(_sourceDc, _sourceBitmap);
            _targetOld = SelectObject(_targetDc, _targetBitmap);
            if (sharedScope is null && _sourceOld==0 || _targetOld==0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Live wallpaper buffer selection failed.");
            if (SetStretchBltMode(_targetDc, HalfTone) == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Live wallpaper scaling mode failed.");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void EnsureSource(nint window)
    {
        if(_sharedScope is null || _sharedSource?.Window==window)return;
        ReleaseSource();
        var shared=new SharedWallpaperBuffer(_sharedScope,window,_sourceWidth,_sourceHeight);
        var bitmap=CreateDib(_sourceDc,_sourceWidth,_sourceHeight,out var bits,shared.SectionHandle,SharedWallpaperBuffer.PixelOffset);
        if(bitmap==0 || bits==0){shared.Dispose();throw new Win32Exception(Marshal.GetLastWin32Error(),"Shared wallpaper bitmap unavailable.");}
        var previous=SelectObject(_sourceDc,bitmap);
        if(previous==0 || previous==-1){DeleteObject(bitmap);shared.Dispose();throw new Win32Exception(Marshal.GetLastWin32Error());}
        _sourceBitmap=bitmap;_sourceBits=bits;_sourceOld=previous;_sharedSource=shared;
        _trace?.Invoke(new("WallpaperBuffer","SharedStorageCreated",Stopwatch.GetTimestamp(),WindowHandle:window,
            Width:_sourceWidth,Height:_sourceHeight,Detail:$"bytes={shared.Bytes};identity={shared.Identity}"));
    }

    private void ReleaseSource()
    {
        if(_sourceDc!=0 && _sourceOld!=0)SelectObject(_sourceDc,_sourceOld);
        if(_sourceBitmap!=0)DeleteObject(_sourceBitmap);
        _sourceOld=_sourceBitmap=_sourceBits=0;_sharedSource?.Dispose();_sharedSource=null;
        _lastSharedSequence=0;LastSourceQpcTicks=0;
    }

    /// <summary>
    /// 读一帧壁纸并写入 <paramref name="target"/>（取景区域大小，BGRA 顶朝下）。
    /// 壁纸窗口没有盖满取景区域时只更新交集部分，其余像素保持原样。
    /// </summary>
    internal bool TryDraw(nint window, int regionX, int regionY, int regionWidth, int regionHeight,
        int windowLeft, int windowTop, int windowRight, int windowBottom, byte[] target,long sourceNotBeforeTicks=0)
    {
        if (_disposed || window == 0 || target.Length < (long)_targetWidth * _targetHeight * 4
            || sourceNotBeforeTicks>Stopwatch.GetTimestamp()) return false;
        var left = Math.Max(regionX, windowLeft);
        var top = Math.Max(regionY, windowTop);
        var right = Math.Min(regionX + regionWidth, windowRight);
        var bottom = Math.Min(regionY + regionHeight, windowBottom);
        if (right <= left || bottom <= top) return false;
        try
        {
            EnsureSource(window);
            // 取景区域 -> 目标像素的缩放比例（取景区域可能比目标大，Dock 只取一块）。
            var scaleX = (double)_targetWidth / regionWidth;
            var scaleY = (double)_targetHeight / regionHeight;
            var destinationLeft = (int)Math.Floor((left - regionX) * scaleX);
            var destinationTop = (int)Math.Floor((top - regionY) * scaleY);
            var destinationRight = (int)Math.Ceiling((right - regionX) * scaleX);
            var destinationBottom = (int)Math.Ceiling((bottom - regionY) * scaleY);
            var forceFresh=false;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if(!_shareFrames)Marshal.ThrowExceptionForHR(DwmFlush());
                var shared=_sharedSource;
                if(shared is not null && !shared.Enter())return false;
                var sharedFlushed=false;
                try
                {
                SharedWallpaperBuffer.Frame sharedFrame=default;
                var reused=_shareFrames && !forceFresh && shared!.TryLatest(_lastSharedSequence,sourceNotBeforeTicks,out sharedFrame);
                var started=sharedFrame.StartedTicks;
                var sparseSource = false;
                if(!reused)
                {
                    // Invalidate before any write, including a failed/partial PrintWindow.
                    // A published identity must never describe overwritten source pixels.
                    shared?.InvalidateFrame();
                    if(_shareFrames)Marshal.ThrowExceptionForHR(DwmFlush());
                    started=Stopwatch.GetTimestamp();
                    if (!_readback(window, _sourceDc))
                    {
                        _trace?.Invoke(new("WallpaperReadback", "Failed", Stopwatch.GetTimestamp(),
                            WindowHandle: window, ErrorCode: Marshal.GetLastWin32Error()));
                        return false;
                    }
                    // A shared DIB contains the whole wallpaper, while this owner's
                    // crop may miss a torn/black part that a peer will use. Inspect
                    // the freshly acquired display source before publishing it.
                    if (!GdiFlush()) return false;
                }
                // A peer on another display may have validated a different part
                // of this same spanning source. Check our display on reuse too.
                sparseSource = HasLargeBlackSourceRegion(regionX, regionY, regionWidth, regionHeight, windowLeft, windowTop);
                Marshal.Copy(target, 0, _targetBits, _targetWidth * _targetHeight * 4);
                if (!StretchBlt(_targetDc, destinationLeft, destinationTop,
                        Math.Max(1, destinationRight - destinationLeft), Math.Max(1, destinationBottom - destinationTop),
                        _sourceDc, left - windowLeft, top - windowTop, right - left, bottom - top, SourceCopy))
                    return false;
                if(shared is not null){if(!GdiFlush())return false;sharedFlushed=true;}
                else GdiFlush();
                var ended=reused ? sharedFrame.EndedTicks : Stopwatch.GetTimestamp();
                Marshal.Copy(_targetBits, target, 0, _targetWidth * _targetHeight * 4);
                var sparse = sparseSource || HasLargeBlackRegion(target);
                if (attempt < 2 && sparse)
                {
                    forceFresh=true;
                    // DX11 readback can contain only part of a drawn frame (observed
                    // as a gray/black triangle), even when PrintWindow succeeds.
                    // Reacquire across a fresh DWM boundary, at most twice. Always
                    // deliver the final actual read, including legitimate black or
                    // mixed black content; never substitute a historical frame.
                    _trace?.Invoke(new("WallpaperReadback", sparseSource ? "RetrySparseSource" : "RetrySparseReadback", Stopwatch.GetTimestamp(),
                        WindowHandle: window, Width: _targetWidth, Height: _targetHeight));
                    continue;
                }
                if (attempt != 0)
                    _trace?.Invoke(new("WallpaperReadback", sparse ? "SparseAfterRetry" : "RecoveredAfterRetry",
                        Stopwatch.GetTimestamp(), WindowHandle: window, Width: _targetWidth, Height: _targetHeight));
                if(_shareFrames)
                {
                    if(!reused)sharedFrame=shared!.Publish(started,ended);
                    _lastSharedSequence=sharedFrame.Sequence;
                    _trace?.Invoke(new("WallpaperFrame",reused?"SharedRead":"Acquired",Stopwatch.GetTimestamp(),
                        SourceTicks:ended,Generation:sharedFrame.Sequence));
                }
                LastSourceQpcTicks=ended;
                break; // Persistent real black content is delivered normally.
                }
                finally{if(shared is not null){if(!sharedFlushed)GdiFlush();shared.Exit();}}
            }
            // 玻璃链路只用颜色通道，统一置为不透明，免得分层窗口的 alpha 混出黑边。
            for (var index = 3; index < target.Length; index += 4) target[index] = 255;
            return true;
        }
        catch (Exception error) when(error is not SharedWallpaperBufferException)
        {
            Trace.WriteLine("Live wallpaper frame failed: " + error.Message);
            return false;
        }
    }

    private bool HasLargeBlackRegion(byte[] pixels)
    {
        var black = 0;
        for (var row = 0; row < 16; row++)
            for (var column = 0; column < 32; column++)
            {
                var x = (2 * column + 1) * _targetWidth / 64;
                var y = (2 * row + 1) * _targetHeight / 32;
                var index = (y * _targetWidth + x) * 4;
                if ((pixels[index] | pixels[index + 1] | pixels[index + 2]) == 0) black++;
            }
        return black >= 128; // 25% of 512 samples; a reacquisition cue, never a rejection rule.
    }

    private bool HasLargeBlackSourceRegion(int regionX, int regionY, int regionWidth, int regionHeight,
        int windowLeft, int windowTop)
    {
        var left = 0; var top = 0; var right = _sourceWidth; var bottom = _sourceHeight;
        long bestOverlap = 0;
        foreach (var display in _displays)
        {
            var overlapWidth = Math.Max(0, Math.Min(regionX + regionWidth, display.X + display.Width) - Math.Max(regionX, display.X));
            var overlapHeight = Math.Max(0, Math.Min(regionY + regionHeight, display.Y + display.Height) - Math.Max(regionY, display.Y));
            var overlap = (long)overlapWidth * overlapHeight;
            if (overlap <= bestOverlap) continue;
            var displayLeft = Math.Clamp(display.X - windowLeft, 0, _sourceWidth);
            var displayTop = Math.Clamp(display.Y - windowTop, 0, _sourceHeight);
            var displayRight = Math.Clamp(display.X + display.Width - windowLeft, 0, _sourceWidth);
            var displayBottom = Math.Clamp(display.Y + display.Height - windowTop, 0, _sourceHeight);
            if (displayRight <= displayLeft || displayBottom <= displayTop) continue;
            bestOverlap = overlap;
            left = displayLeft; top = displayTop; right = displayRight; bottom = displayBottom;
        }
        // A multi-monitor source also contains unoccupied virtual-desktop gaps.
        // Inspect the consumer's actual display, not those permanently black gaps.
        var black = 0;
        for (var row = 0; row < 16; row++)
            for (var column = 0; column < 32; column++)
            {
                var x = left + (2 * column + 1) * (right - left) / 64;
                var y = top + (2 * row + 1) * (bottom - top) / 32;
                var pixel = Marshal.ReadInt32(_sourceBits, (y * _sourceWidth + x) * 4);
                if ((pixel & 0x00ffffff) == 0) black++;
            }
        return black >= 128;
    }

    private static nint CreateDib(nint dc, int width, int height, out nint bits,nint section=0,uint offset=0)
    {
        var header = new BitmapInfoHeader
        {
            Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
            Width = width,
            Height = -height, // 负数 = 顶朝下
            Planes = 1,
            BitCount = 32,
            Compression = 0
        };
        var info = new BitmapInfo { Header = header };
        return CreateDIBSection(dc, ref info, 0, out bits, section, offset);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseSource();
        if (_targetDc != 0 && _targetOld != 0) SelectObject(_targetDc, _targetOld);
        if (_targetBitmap != 0) DeleteObject(_targetBitmap);
        if (_sourceDc != 0) DeleteDC(_sourceDc);
        if (_targetDc != 0) DeleteDC(_targetDc);
        _sourceDc = _targetDc = _sourceBitmap = _targetBitmap = _sourceOld = _targetOld = _sourceBits = _targetBits = 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ClrUsed, ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Colors;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool PrintWindow(nint window, nint dc, uint flags);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint SelectObject(nint dc, nint handle);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint handle);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(nint dc, int mode);
    [DllImport("gdi32.dll")] private static extern bool StretchBlt(nint destination, int x, int y, int width, int height,
        nint source, int sourceX, int sourceY, int sourceWidth, int sourceHeight, uint operation);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
}
