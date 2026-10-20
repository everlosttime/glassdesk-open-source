internal sealed record BackdropRequest(PixelBounds Region, int Width, int Height, long Generation)
{
    internal long SourceNotBeforeTicks { get; init; }
}
internal sealed class BackdropFrame : IDisposable
{
    public BackdropRequest Request { get; }
    public long Sequence { get; }
    internal long ContentRevision {get;set;}
    public long StartTicks { get; }
    public long EndTicks { get; }
    public long SourceTicks { get; }
    public ReadOnlyMemory<byte> Pixels { get; }
    private SamplePixelLease? _lease;
    private bool _pooled;
    internal bool IsStatic { get; init; }
    internal GlassDesk.UI.Controls.GlassBackdropSource Source { get; init; } = GlassDesk.UI.Controls.GlassBackdropSource.LiveDesktop;
    internal BackdropFrame(BackdropRequest request, long sequence, long start, long end, byte[] pixels,long? sourceTicks=null)
        : this(request,sequence,start,end,pixels,false,sourceTicks) { }
    private BackdropFrame(BackdropRequest request,long sequence,long start,long end,byte[] pixels,bool ownsPixels,long? sourceTicks)
    {
        if (request.Width<=0 || request.Height<=0 || request.Region.Width<=0 || request.Region.Height<=0 ||
            (long)request.Width*request.Height>1_000_000 || pixels.Length!=(long)request.Width*request.Height*4 || start>end
            || sourceTicks is <0)
            throw new ArgumentException($"Invalid backdrop extent, timing or pixels: {request.Width}x{request.Height}, start={start}, end={end}, source={sourceTicks}.");
        Request=request; Sequence=sequence;ContentRevision=sequence; StartTicks=start; EndTicks=end;
        SourceTicks=sourceTicks ?? end;
        Pixels=ownsPixels ? pixels : (byte[])pixels.Clone();
    }
    internal static BackdropFrame FromOwnedPixels(BackdropRequest request,long sequence,long start,long end,byte[] pixels,long? sourceTicks=null,
        bool isStatic=false, GlassDesk.UI.Controls.GlassBackdropSource source=GlassDesk.UI.Controls.GlassBackdropSource.LiveDesktop)
        => new(request,sequence,start,end,pixels,true,sourceTicks) { IsStatic=isStatic, Source=source };
    internal static BackdropFrame FromLease(BackdropRequest request,long sequence,long start,long end,SamplePixelLease lease,
        long? sourceTicks=null,bool isStatic=false,GlassDesk.UI.Controls.GlassBackdropSource source=GlassDesk.UI.Controls.GlassBackdropSource.LiveDesktop)
    {
        try {return new(request,sequence,start,end,lease.Pixels,true,sourceTicks) {_lease=lease,_pooled=true,IsStatic=isStatic,Source=source};}
        catch {lease.Dispose();throw;}
    }
    // Snapshots remain independently immutable; pooled frames give each reader a lease.
    internal BackdropFrame Acquire()
    {
        if(!_pooled) return this;
        var reader=FromLease(Request,Sequence,StartTicks,EndTicks,(_lease ?? throw new ObjectDisposedException(nameof(BackdropFrame))).Acquire(),SourceTicks,IsStatic,Source);
        reader.ContentRevision=ContentRevision;return reader;
    }
    public void Dispose() => Interlocked.Exchange(ref _lease,null)?.Dispose();
}
internal sealed class LatestBackdropState : IDisposable
{
    private static long nextGeneration;
    internal Action<CaptureTrace>? TraceObserved {get;init;}
    private readonly object gate = new();
    private BackdropRequest? current;
    private BackdropFrame? latest;
    private long generation;
    private long NextGeneration() => generation=Interlocked.Increment(ref nextGeneration);
    private long contentRevision;
    private bool disposed;
    private bool resumed;
    private long sourceNotBeforeTicks;
    private AutoResetEvent? _changed;
    internal WaitHandle Changed { get { lock(gate) return _changed ??= new(false); } }
    internal void RetireWaitHandle() { lock(gate) {_changed?.Dispose();_changed=null;} }
    private void ClearLatest() { latest?.Dispose(); latest=null; }
    internal BackdropRequest Request(PixelBounds region, int width, int height)
    {
        if (region.Width<=0 || region.Height<=0 || width<=0 || height<=0 || (long)width*height>1_000_000)
            throw new ArgumentException("Invalid backdrop request.");
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            if (current is not null && current.Region==region && current.Width==width && current.Height==height) return current;
            ClearLatest();
            // A resumed request cannot consume textures queued before this reveal.
            if (resumed) { sourceNotBeforeTicks=System.Diagnostics.Stopwatch.GetTimestamp(); resumed=false; }
            current=new(region,width,height,NextGeneration()) {SourceNotBeforeTicks=sourceNotBeforeTicks};
            _changed?.Set();
            TraceObserved?.Invoke(new("Geometry","Changed",System.Diagnostics.Stopwatch.GetTimestamp(),Generation:generation));
            return current;
        }
    }
    internal BackdropRequest? CurrentRequest { get { lock(gate) return disposed ? null : current; } }
    internal void InvalidateScene()
    {
        lock(gate)
        {
            if (disposed) return;
            ClearLatest();
            sourceNotBeforeTicks=System.Diagnostics.Stopwatch.GetTimestamp();
            if (current is not null) current = current with {Generation=NextGeneration(),SourceNotBeforeTicks=sourceNotBeforeTicks};
            _changed?.Set();
        }
    }
    internal void Suspend()
    {
        lock(gate)
        {
            if(disposed) return;
            resumed=true;
            if(current is null) return;
            current=null;ClearLatest();NextGeneration();_changed?.Set();
        }
    }
    internal object InspectTiming(long now)
    {
        lock(gate) return new {disposed,generation,sequence=latest?.Sequence,
            currentMatches=latest is not null && latest.Request==current,
            readAgeTicks=latest is null ? (long?)null : now-latest.EndTicks,
            sourceAgeTicks=latest is null ? (long?)null : now-latest.SourceTicks};
    }
    internal bool Publish(BackdropFrame frame)
    {
        lock(gate)
        {
            var reason=disposed ? "WorkerStopped" : frame.Request!=current ? "GenerationChanged"
                : frame.SourceTicks<frame.Request.SourceNotBeforeTicks ? "BeforeResume"
                : latest is not null && frame.Sequence<=latest.Sequence ? "OutOfOrder" : "Delivered";
            TraceObserved?.Invoke(new("Publish",reason,System.Diagnostics.Stopwatch.GetTimestamp(),frame.SourceTicks,
                frame.EndTicks,Generation:frame.Request.Generation));
            if(reason!="Delivered") {if(!ReferenceEquals(latest,frame)) frame.Dispose();return false;}
            frame.ContentRevision=latest is not null && latest.Source==frame.Source && latest.Request==frame.Request
                && latest.Pixels.Span.SequenceEqual(frame.Pixels.Span) ? latest.ContentRevision : ++contentRevision;
            if(!ReferenceEquals(latest,frame)) ClearLatest();
            latest=frame; return true;
        }
    }
    internal bool TryRead(long now, long maximumAgeTicks, out BackdropFrame? frame)
    {
        lock(gate)
        {
            frame=null;
            var reason=disposed ? "WorkerStopped" : latest is null ? "NoFrame" : latest.Request!=current ? "GeometryMismatch"
                : now<latest.EndTicks ? "FutureReadback" : maximumAgeTicks<0 ? "InvalidAgeBudget"
                : !latest.IsStatic && now-latest.EndTicks>maximumAgeTicks ? "ExpiredReadback" : now<latest.SourceTicks ? "FutureSource"
                : !latest.IsStatic && now-latest.SourceTicks>maximumAgeTicks ? "ExpiredSource" : "Delivered";
            TraceObserved?.Invoke(new("Consumer",reason,now,latest?.SourceTicks ?? 0,latest?.EndTicks ?? 0,Generation:generation));
            if(reason!="Delivered") return false;
            frame=latest!.Acquire(); return true;
        }
    }
    public void Dispose() { lock(gate) { disposed=true; ClearLatest(); current=null;_changed?.Set(); } }
}
