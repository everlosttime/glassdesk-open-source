using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;

namespace GlassDesk.UI.CaptureCompatibility;

// The backing pixels are shared; each capture owner retains its own DIB handle,
// DC and target. Ownership covers readback, publication and every consumer crop.
internal sealed class SharedWallpaperBuffer : IDisposable
{
    internal const string ScopeEnvironmentVariable="GLASSDESK_WALLPAPER_SOURCE_SCOPE";
    internal const string FrameFanoutEnvironmentVariable="GLASSDESK_SHARED_WALLPAPER_FRAMES";
    internal const uint PixelOffset=64;
    private readonly MemoryMappedFile _mapping;
    private readonly MemoryMappedViewAccessor _control;
    private readonly Mutex _gate;
    internal string Name {get;}
    internal string Identity {get;}
    internal nint Window {get;}
    internal long Bytes {get;}
    internal nint SectionHandle=>_mapping.SafeMemoryMappedFileHandle.DangerousGetHandle();

    internal SharedWallpaperBuffer(string scope,nint window,int width,int height)
    {
        if(!Guid.TryParseExact(scope,"N",out var session))throw new ArgumentException("Invalid wallpaper source scope.",nameof(scope));
        Window=window;Bytes=checked((long)width*height*4+PixelOffset);
        Name=$"Local\\GlassDeskWallpaper-v1-{session:N}-{window:X}-{width}x{height}";
        Identity=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Name)));
        _mapping=MemoryMappedFile.CreateOrOpen(Name,Bytes,MemoryMappedFileAccess.ReadWrite);
        try{_control=_mapping.CreateViewAccessor(0,PixelOffset,MemoryMappedFileAccess.ReadWrite);}
        catch{_mapping.Dispose();throw;}
        try{_gate=new Mutex(false,Name+"-gate");}catch{_control.Dispose();_mapping.Dispose();throw;}
    }
    internal bool Enter()
    {
        try{if(!_gate.WaitOne(100))return false;}
        catch(AbandonedMutexException)
        {
            // A remote WM_PRINT may still be completing when its owner dies.
            // Retire every view instead of racing that write or reusing torn pixels.
            _control.Write(0,1);Thread.MemoryBarrier();_gate.ReleaseMutex();
            throw new SharedWallpaperBufferException();
        }
        if(_control.ReadInt32(0)!=0){_gate.ReleaseMutex();throw new SharedWallpaperBufferException();}
        return true;
    }
    internal void Exit()=>_gate.ReleaseMutex();
    internal readonly record struct Frame(long Sequence,long StartedTicks,long EndedTicks);
    // All metadata access and use of its backing pixels require the named mutex.
    internal bool TryLatest(long previousSequence,long notBeforeTicks,out Frame frame)
    {
        frame=new(_control.ReadInt64(24),_control.ReadInt64(8),_control.ReadInt64(16));
        var age=Stopwatch.GetTimestamp()-frame.EndedTicks;
        return frame.Sequence>previousSequence && frame.StartedTicks>=notBeforeTicks
            && frame.EndedTicks>=frame.StartedTicks && frame.EndedTicks>0
            && age>=0 && age<=Stopwatch.Frequency*120/1000;
    }
    internal void InvalidateFrame()=>_control.Write(24,0L);
    internal Frame Publish(long startedTicks,long endedTicks)
    {
        var sequence=checked(_control.ReadInt64(32)+1);
        _control.Write(8,startedTicks);_control.Write(16,endedTicks);
        _control.Write(32,sequence);_control.Write(24,sequence);
        return new(sequence,startedTicks,endedTicks);
    }
    public void Dispose(){_gate.Dispose();_control.Dispose();_mapping.Dispose();}
}

internal sealed class SharedWallpaperBufferException:InvalidOperationException
{
    internal const string ProtocolErrorCode="GLASSDESK_WALLPAPER_SOURCE_ABANDONED";
    internal SharedWallpaperBufferException():base(ProtocolErrorCode+": Wallpaper source writer was abandoned; retire this source scope before recovery.") { }
}
