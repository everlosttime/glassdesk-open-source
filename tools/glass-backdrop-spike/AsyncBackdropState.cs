internal sealed record BackdropRequest(PixelBounds Region, int Width, int Height, long Generation);
internal sealed class BackdropFrame
{
    public BackdropRequest Request { get; }
    public long Sequence { get; }
    public long StartTicks { get; }
    public long EndTicks { get; }
    public ReadOnlyMemory<byte> Pixels { get; }
    internal BackdropFrame(BackdropRequest request, long sequence, long start, long end, byte[] pixels)
        : this(request,sequence,start,end,pixels,false) { }
    private BackdropFrame(BackdropRequest request,long sequence,long start,long end,byte[] pixels,bool ownsPixels)
    {
        if (request.Width<=0 || request.Height<=0 || request.Region.Width<=0 || request.Region.Height<=0 ||
            (long)request.Width*request.Height>500_000 || pixels.Length!=(long)request.Width*request.Height*4 || start>end)
            throw new ArgumentException("Invalid backdrop extent, timing or pixels.");
        Request=request; Sequence=sequence; StartTicks=start; EndTicks=end;
        Pixels=ownsPixels ? pixels : (byte[])pixels.Clone();
    }
    internal static BackdropFrame FromOwnedPixels(BackdropRequest request,long sequence,long start,long end,byte[] pixels)
        => new(request,sequence,start,end,pixels,true);
}
internal sealed class LatestBackdropState : IDisposable
{
    private readonly object gate = new();
    private BackdropRequest? current;
    private BackdropFrame? latest;
    private long generation;
    private bool disposed;
    internal BackdropRequest Request(PixelBounds region, int width, int height)
    {
        if (region.Width<=0 || region.Height<=0 || width<=0 || height<=0 || (long)width*height>500_000)
            throw new ArgumentException("Invalid backdrop request.");
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            if (current is not null && current.Region==region && current.Width==width && current.Height==height) return current;
            latest=null;
            return current=new(region,width,height,++generation);
        }
    }
    internal BackdropRequest? CurrentRequest { get { lock(gate) return disposed ? null : current; } }
    internal bool Publish(BackdropFrame frame)
    {
        lock(gate)
        {
            if (disposed || frame.Request!=current || latest is not null && frame.Sequence<=latest.Sequence) return false;
            latest=frame; return true;
        }
    }
    internal bool TryRead(long now, long maximumAgeTicks, out BackdropFrame? frame)
    {
        lock(gate)
        {
            frame=null;
            if (disposed || latest is null || latest.Request!=current || now<latest.EndTicks ||
                maximumAgeTicks<0 || now-latest.EndTicks>maximumAgeTicks) return false;
            frame=latest; return true;
        }
    }
    public void Dispose() { lock(gate) { disposed=true; latest=null; current=null; } }
}
