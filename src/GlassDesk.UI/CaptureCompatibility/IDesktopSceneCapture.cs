internal interface IDesktopSceneCapture : IDisposable
{
    nint CarrierHandle { get; }
    bool AllowsStaticContent => false;
    bool SupportsSampling => false;
    long SceneRevision => 0;
    bool UpdateExclusions();
    CapturePixels? TryRead(TimeSpan waitForFrame = default, CancellationToken cancellation = default,
        long sourceNotBeforeTicks = 0, bool reusePixels = false);
    void ReleaseReadbackBuffers();
    CapturePixels? TryReadSampled(PixelBounds crop,int width,int height,byte[] target,
        TimeSpan waitForFrame=default,CancellationToken cancellation=default,long sourceNotBeforeTicks=0)
        => throw new NotSupportedException("Provider does not support sampled acquisition.");
}
