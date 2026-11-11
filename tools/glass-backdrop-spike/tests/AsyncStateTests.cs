internal static class AsyncStateTests
{
    internal static void Run()
    {
        using var state = new LatestBackdropState();
        var region = new PixelBounds(-10,20,-8,21);
        var request = state.Request(region,2,1);
        Check(state.Request(region,2,1).Generation == request.Generation,"unchanged geometry should preserve its generation");
        var bytes = new byte[] {1,2,3,255,4,5,6,255};
        var old = new BackdropFrame(request,1,10,20,bytes);
        bytes[0]=99;
        Check(old.Pixels.Span[0]==1,"capture frame must own immutable pixels");
        Check(state.Publish(old),"valid current capture accepted");
        Check(state.TryRead(25,10,out var first) && ReferenceEquals(old,first),"fresh matching frame returned without copying on the consumer");
        Check(!state.TryRead(31,10,out _),"stale frame rejected");
        Check(!state.Publish(new BackdropFrame(request,0,10,20,new byte[8])),"out-of-order capture rejected");
        Check(state.Publish(new BackdropFrame(request,2,30,40,new byte[8])),"newest capture replaces previous");
        Check(state.TryRead(42,10,out var latest) && latest!.Sequence==2 && old.Pixels.Span[0]==1,"latest-only delivery does not mutate previous frames");
        var moved = state.Request(new PixelBounds(-8,20,-6,21),2,1);
        Check(moved.Generation>request.Generation && !state.TryRead(42,10,out _),"move invalidates previous coordinates immediately");
        Check(!state.Publish(new BackdropFrame(request,3,40,50,new byte[8])),"capture finishing after a move is rejected");
        Check(state.Publish(new BackdropFrame(moved,4,50,60,new byte[8])),"new geometry capture accepted");
        state.Dispose();
        Check(!state.TryRead(61,10,out _) && !state.Publish(new BackdropFrame(moved,5,60,70,new byte[8])),"shutdown invalidates reads and publication");
        var invalid = false;
        try { _ = new BackdropFrame(request,6,80,70,new byte[8]); } catch (ArgumentException) { invalid=true; }
        Check(invalid,"invalid capture timing rejected");
    }
    private static void Check(bool value,string reason) { if (!value) throw new Exception(reason); }
}
