using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

// Only the isolated probe uses this screen reference. Pixels remain in memory.
internal static class DesktopReferenceCapture
{
    internal static void EnsurePhysicalCoordinates()
    {
        if (SetThreadDpiAwarenessContext(-4) == 0 || !AreDpiAwarenessContextsEqual(GetThreadDpiAwarenessContext(), -4))
            throw new InvalidOperationException("PerMonitorV2 physical coordinate context is unavailable.");
    }
    internal sealed record Triplet(PixelFrame Before, PixelFrame Source, PixelFrame After,
        List<PixelBounds> Excluded, int ShadowGuardPixels);

    internal static PixelFrame ReadScreenRegion(PixelBounds region)
    {
        using var dib=new Dib(region);
        return dib.Read(0,false);
    }
    internal static PixelFrame ReadWindowFrame(nint hwnd)
    {
        using var dib=new Dib(Bounds(hwnd));
        return dib.Read(hwnd,true);
    }

    internal static IBackdropCapture CreateBackdropCapture(nint shell)=>new RegionCapture(shell);

    private sealed class RegionCapture : IBackdropCapture
    {
        private readonly nint shell;
        private Dib? source,output;
        private PixelBounds root;
        private (int Width,int Height) outputSize;
        internal RegionCapture(nint shell) { this.shell=shell; EnsurePhysicalCoordinates(); }
        public BackdropFrame Capture(BackdropRequest request,long sequence)
        {
            var current=Bounds(shell);
            if(source is null || current!=root) { source?.Dispose(); source=new Dib(current); root=current; }
            if(!root.Contains(request.Region.Left,request.Region.Top) || !root.Contains(request.Region.Right-1,request.Region.Bottom-1))
                throw new InvalidOperationException("Requested background extends outside the verified shell source.");
            if(output is null || outputSize!=(request.Width,request.Height))
            {
                output?.Dispose(); output=new Dib(new PixelBounds(0,0,request.Width,request.Height));
                outputSize=(request.Width,request.Height);
            }
            var start=Stopwatch.GetTimestamp();
            if(!PrintWindow(shell,source.dc,2) || SetStretchBltMode(output.dc,3)==0 ||
                !StretchBlt(output.dc,0,0,request.Width,request.Height,source.dc,
                    request.Region.Left-root.Left,request.Region.Top-root.Top,request.Region.Width,request.Region.Height,0x00CC0020) || !GdiFlush())
                throw new InvalidOperationException("Background region acquisition failed.");
            if(Bounds(shell)!=root) throw new InvalidOperationException("Shell bounds changed during background acquisition.");
            var pixels=new byte[checked(request.Width*request.Height*4)];
            Marshal.Copy(output.bits,pixels,0,pixels.Length);
            // Root desktop is opaque; GDI's unused alpha bytes are not compositing alpha.
            for(var i=3;i<pixels.Length;i+=4) pixels[i]=255;
            return BackdropFrame.FromOwnedPixels(request,sequence,start,Stopwatch.GetTimestamp(),pixels);
        }
        public void Dispose() { output?.Dispose(); source?.Dispose(); output=source=null; }
    }

    internal static Triplet Capture(nint shell)
    {
        var excluded = Occluders(shell, out var guard);
        var bounds = Bounds(shell);
        using var before = new Dib(bounds);
        using var source = new Dib(bounds);
        using var after = new Dib(bounds);
        // Same process, same physical origin, no per-pixel readback or IPC between these calls.
        var a = before.Read(shell, false);
        excluded.AddRange(Occluders(shell, out _));
        var b = source.Read(shell, true);
        excluded.AddRange(Occluders(shell, out _));
        var c = after.Read(shell, false);
        excluded.AddRange(Occluders(shell, out _));
        if (Bounds(shell) != bounds) throw new InvalidOperationException("Shell moved during sampling.");
        return new(a,b,c,excluded,guard);
    }

    internal static PixelBounds Bounds(nint hwnd)
    {
        if (!GetWindowRect(hwnd, out var r) || r.Width <= 0 || r.Height <= 0 || (long)r.Width*r.Height > 16_000_000)
            throw new InvalidOperationException("Physical shell bounds unavailable or exceed diagnostic budget.");
        return r;
    }

    internal static List<PixelCandidate> Candidates(nint shell, Triplet frames, IReadOnlyList<PixelBounds> iconBounds, int phase)
    {
        var result = new List<PixelCandidate>();
        var b = frames.Before.Bounds;
        var monitors = new List<PixelBounds>();
        if (!EnumDisplayMonitors(0,0,(nint m,nint dc, ref PixelBounds r,nint state) => { monitors.Add(r); return true; },0))
            throw new InvalidOperationException("Monitor enumeration failed.");
        bool Clear(int x, int y) => monitors.Any(r => r.Contains(x,y)) && !frames.Excluded.Any(r => r.Contains(x,y)) &&
            GetAncestor(WindowFromPoint(new NativePoint(x,y)),2) == shell;
        // Fixed coordinates across repeats let the report detect recurring errors at the same pixel.
        for (var y = b.Top + 8 + phase%4; y < b.Bottom; y += 16)
        for (var x = b.Left + 8 + phase%7; x < b.Right; x += 16)
        {
            if (iconBounds.Any(r => r.Contains(x,y))) continue;
            result.Add(new(x,y,false,Clear(x,y)));
        }
        // Dense sampling in item bounds (icon plus label), explicitly separate from wallpaper.
        foreach (var r in iconBounds)
        for (var y = Math.Max(r.Top,b.Top)+phase%3; y < Math.Min(r.Bottom,b.Bottom); y += 3)
        for (var x = Math.Max(r.Left,b.Left)+phase%3; x < Math.Min(r.Right,b.Right); x += 3)
            result.Add(new(x,y,true,Clear(x,y)));
        return result;
    }

    private static List<PixelBounds> Occluders(nint shell, out int guard)
    {
        var margin = Math.Max(128,(int)Math.Ceiling(128d*GetDpiForWindow(shell)/96));
        guard = margin;
        var list = new List<PixelBounds>();
        var unavailableBounds = false;
        if (!EnumWindows((hwnd,_) =>
        {
            if (hwnd == shell || !IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
            if (DwmGetWindowAttribute(hwnd,14,out int cloaked,4) == 0 && cloaked != 0) return true;
            // WS_VISIBLE alone also includes globally alpha-zero layered utility windows.
            // These contribute neither window pixels nor their shadow to the composed reference.
            if (GetLayeredWindowAttributes(hwnd,out var colorKey,out var alpha,out var flags) && (flags & 2) != 0 && alpha == 0)
                return true;
            var name = new StringBuilder(256); GetClassName(hwnd,name,name.Capacity);
            // Explorer background hosts are not foreground overlays. Root ownership is checked per point too.
            if (name.ToString() is "WorkerW" or "Progman") return true;
            if (!GetWindowRect(hwnd,out var r)) { unavailableBounds = true; return true; }
            if (DwmGetWindowAttribute(hwnd,9,out PixelBounds extended,16) == 0)
                r = new(Math.Min(r.Left,extended.Left),Math.Min(r.Top,extended.Top),Math.Max(r.Right,extended.Right),Math.Max(r.Bottom,extended.Bottom));
            list.Add(new(r.Left-margin,r.Top-margin,r.Right+margin,r.Bottom+margin));
            return true;
        },0) || unavailableBounds) throw new InvalidOperationException("Occlusion enumeration or window bounds unavailable.");
        var cursor = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        if (GetCursorInfo(ref cursor) && (cursor.Flags & 1) != 0)
            list.Add(new(cursor.Position.X-64,cursor.Position.Y-64,cursor.Position.X+64,cursor.Position.Y+64));
        return list;
    }

    internal static List<PixelBounds> IconBounds(nint shell)
    {
        nint view = 0;
        EnumChildWindows(shell,(hwnd,_) =>
        {
            var name = new StringBuilder(256); GetClassName(hwnd,name,name.Capacity);
            if (name.ToString() != "SysListView32") return true;
            view = hwnd; return false;
        },0);
        if (view == 0) throw new InvalidOperationException("Desktop list view is unavailable.");
        var count = SendMessage(view,0x1004,0,0).ToInt32();
        if (count <= 0 || count > 10000) throw new InvalidOperationException("Desktop icon item count is unavailable.");
        GetWindowThreadProcessId(view,out var pid);
        var process = OpenProcess(0x38,false,pid);
        if (process == 0) throw new InvalidOperationException("Cannot read anonymous desktop item geometry.");
        nint remote = 0;
        try
        {
            remote = VirtualAllocEx(process,0,16,0x3000,4);
            if (remote == 0) throw new InvalidOperationException("Remote rectangle allocation failed.");
            var result = new List<PixelBounds>(); var data = new byte[16];
            for (var i = 0; i < count; i++)
            {
                Array.Clear(data); // LVIR_BOUNDS = 0, includes the icon and its text without reading names.
                if (!WriteProcessMemory(process,remote,data,16,out var written) || written != 16)
                    throw new InvalidOperationException("Item geometry request failed.");
                if (SendMessage(view,0x100E,i,remote) == 0 || !ReadProcessMemory(process,remote,data,16,out var read) || read != 16)
                    throw new InvalidOperationException("Item geometry read failed.");
                var left = new NativePoint(BitConverter.ToInt32(data,0),BitConverter.ToInt32(data,4));
                var right = new NativePoint(BitConverter.ToInt32(data,8),BitConverter.ToInt32(data,12));
                if (!ClientToScreen(view,ref left) || !ClientToScreen(view,ref right))
                    throw new InvalidOperationException("Item physical coordinate conversion failed.");
                result.Add(new(left.X,left.Y,right.X,right.Y));
            }
            return result;
        }
        finally { if (remote != 0) VirtualFreeEx(process,remote,0,0x8000); CloseHandle(process); }
    }

    internal static object MeasureContinuous(nint shell, TimeSpan duration, int targetHz)
    {
        EnsurePhysicalCoordinates();
        var bounds = Bounds(shell);
        using var dib = new Dib(bounds);
        var buffer = new byte[checked(bounds.Width*bounds.Height*4)];
        for (var i=0;i<10;i++) dib.Read(shell,true,buffer);
        using var own = Process.GetCurrentProcess();
        GetWindowThreadProcessId(shell,out var pid);
        using var explorer = Process.GetProcessById((int)pid);
        var ownCpu = own.TotalProcessorTime; var explorerCpu = explorer.TotalProcessorTime;
        var gc = Enumerable.Range(0,3).Select(GC.CollectionCount).ToArray();
        var samples = new List<double>();
        var captureSamples = new List<double>();
        var watch = Stopwatch.StartNew(); var peakPrivate = own.PrivateMemorySize64;
        var missedSlots = 0; var nextMs = 0d; var budgetMs = 1000d/targetHz;
        while (watch.Elapsed < duration)
        {
            var start = Stopwatch.GetTimestamp();
            var frame = dib.Read(shell,true,buffer);
            samples.Add((Stopwatch.GetTimestamp()-start)*1000d/Stopwatch.Frequency);
            captureSamples.Add((frame.EndTicks-frame.StartTicks)*1000d/Stopwatch.Frequency);
            nextMs += budgetMs;
            var now = watch.Elapsed.TotalMilliseconds;
            if (now > nextMs)
            {
                var dropped = (int)Math.Floor((now-nextMs)/budgetMs)+1;
                missedSlots += dropped; nextMs += dropped*budgetMs;
            }
            var wait = nextMs-watch.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)Math.Floor(wait));
            while (watch.Elapsed.TotalMilliseconds < nextMs) Thread.SpinWait(100);
            if (samples.Count%30==0) { own.Refresh(); peakPrivate = Math.Max(peakPrivate,own.PrivateMemorySize64); }
        }
        var elapsed = watch.Elapsed.TotalSeconds;
        own.Refresh(); explorer.Refresh();
        static object Distribution(List<double> values, double budget)
        {
            values.Sort();
            double Percentile(double p) => values[Math.Clamp((int)Math.Ceiling(values.Count*p)-1,0,values.Count-1)];
            return new { meanMs=values.Average(), p50Ms=Percentile(.5),p95Ms=Percentile(.95),p99Ms=Percentile(.99),
                maxMs=values[^1], framesOverTargetBudget=values.Count(v=>v>budget) };
        }
        return new { physicalScreenBounds=bounds,targetHz,targetBudgetMs=budgetMs,requestedSeconds=duration.TotalSeconds,
            elapsedSeconds=elapsed,frames=samples.Count,achievedFps=samples.Count/elapsed,missedSlots,
            frameAcquisition=Distribution(samples,budgetMs), printWindowAndFlush=Distribution(captureSamples,budgetMs),
            bytesPerFrame=buffer.Length, readbackMiBPerSecond=DiagnosticMath.ReadbackMiBPerSecond(buffer.Length,samples.Count,elapsed),
            probeCpuMs=(own.TotalProcessorTime-ownCpu).TotalMilliseconds,
            explorerCpuMs=(explorer.TotalProcessorTime-explorerCpu).TotalMilliseconds,
            peakProbePrivateMiB=peakPrivate/1024d/1024,
            gcCollections=Enumerable.Range(0,3).Select(i=>GC.CollectionCount(i)-gc[i]).ToArray(),
            note="30-second isolated in-process PrintWindow + BGRA readback, reusable DIB and byte buffer, 10 warmup frames. 60Hz is a measurement target, not a changed product parameter. No blur, GPU upload, Dock/Launcher composition, screenshot or recording included; performance acceptance for integration remains unverified." };
    }

    private sealed class Dib : IDisposable
    {
        internal nint dc,bits;
        private nint screen,bitmap,previous;
        private readonly PixelBounds bounds;
        internal Dib(PixelBounds b)
        {
            bounds = b;
            try
            {
                screen = GetDC(0); dc = CreateCompatibleDC(screen);
                var info = new BitmapInfo { Size=40,Width=b.Width,Height=-b.Height,Planes=1,Bits=32 };
                bitmap = CreateDIBSection(screen,ref info,0,out bits,0,0);
                if (screen==0 || dc==0 || bitmap==0 || bits==0) throw new InvalidOperationException("Reference DIB allocation failed.");
                previous = SelectObject(dc,bitmap);
                if (previous==0 || previous==-1) throw new InvalidOperationException("Reference DIB selection failed.");
            }
            catch { Dispose(); throw; }
        }
        internal PixelFrame Read(nint shell, bool print, byte[]? buffer = null)
        {
            if (shell!=0 && Bounds(shell) != bounds) throw new InvalidOperationException("Shell bounds changed before capture.");
            var utc = DateTimeOffset.UtcNow; var start = Stopwatch.GetTimestamp();
            var ok = print ? PrintWindow(shell,dc,2) : BitBlt(dc,0,0,bounds.Width,bounds.Height,screen,bounds.Left,bounds.Top,0x40CC0020);
            if (!ok || !GdiFlush()) throw new InvalidOperationException(print ? "PrintWindow failed." : "Screen reference BitBlt failed.");
            var end = Stopwatch.GetTimestamp();
            var pixels = buffer ?? new byte[checked(bounds.Width*bounds.Height*4)];
            Marshal.Copy(bits,pixels,0,pixels.Length);
            return new(bounds,pixels,start,end,utc);
        }
        public void Dispose()
        {
            if (previous!=0 && previous!=-1 && dc!=0) SelectObject(dc,previous);
            if (bitmap!=0) DeleteObject(bitmap);
            if (dc!=0) DeleteDC(dc);
            if (screen!=0) ReleaseDC(0,screen);
            previous=bitmap=dc=screen=0;
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint(int x,int y) { public int X=x,Y=y; }
    [StructLayout(LayoutKind.Sequential)] private struct CursorInfo { public int Size,Flags; public nint Cursor; public NativePoint Position; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public uint Size; public int Width,Height; public ushort Planes,Bits; public uint Compression,ImageSize; public int XPels,YPels; public uint Colors,Important; }
    private delegate bool EnumProc(nint hwnd,nint state);
    private delegate bool MonitorProc(nint monitor,nint dc,ref PixelBounds bounds,nint state);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern nint GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")] private static extern bool AreDpiAwarenessContextsEqual(nint a,nint b);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd,out PixelBounds rect);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd,nint dc);
    [DllImport("user32.dll")] private static extern bool PrintWindow(nint hwnd,nint dc,uint flags);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback,nint state);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint hwnd,EnumProc callback,nint state);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint dc,nint clip,MonitorProc callback,nint state);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CursorInfo info);
    [DllImport("user32.dll")] private static extern bool GetLayeredWindowAttributes(nint hwnd,out uint colorKey,out byte alpha,out uint flags);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd,uint flags);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetClassName(nint hwnd,StringBuilder text,int size);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd,out uint pid);
    [DllImport("user32.dll")] private static extern nint SendMessage(nint hwnd,uint message,nint wp,nint lp);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint hwnd,ref NativePoint point);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd,int attribute,out int value,int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd,int attribute,out PixelBounds value,int size);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc,ref BitmapInfo info,uint usage,out nint bits,nint section,uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc,nint value);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint dc,int x,int y,int width,int height,nint source,int sx,int sy,uint rop);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(nint dc,int mode);
    [DllImport("gdi32.dll")] private static extern bool StretchBlt(nint dc,int x,int y,int width,int height,nint source,int sx,int sy,int sw,int sh,uint rop);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("kernel32.dll")] private static extern nint OpenProcess(uint access,bool inherit,uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll")] private static extern nint VirtualAllocEx(nint process,nint address,nuint size,uint allocation,uint protection);
    [DllImport("kernel32.dll")] private static extern bool VirtualFreeEx(nint process,nint address,nuint size,uint type);
    [DllImport("kernel32.dll")] private static extern bool WriteProcessMemory(nint process,nint address,byte[] data,nuint size,out nuint written);
    [DllImport("kernel32.dll")] private static extern bool ReadProcessMemory(nint process,nint address,byte[] data,nuint size,out nuint read);
}
