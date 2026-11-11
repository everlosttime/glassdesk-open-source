internal interface IDesktopSceneCapture : IDisposable
{
    nint CarrierHandle { get; }
    bool AllowsStaticContent => false;
    bool SupportsSampling => false;
    bool SupportsCroppedReadback => false;
    long SceneRevision => 0;
    bool UpdateExclusions();
    CapturePixels? TryRead(TimeSpan waitForFrame = default, CancellationToken cancellation = default,
        long sourceNotBeforeTicks = 0, bool reusePixels = false);
    void ReleaseReadbackBuffers();
    CapturePixels? TryReadCropped(PixelBounds crop,TimeSpan waitForFrame=default,CancellationToken cancellation=default,
        long sourceNotBeforeTicks=0,bool reusePixels=false)
        => throw new NotSupportedException("Provider does not support cropped raw readback.");
    CapturePixels? TryReadSampled(PixelBounds crop,int width,int height,byte[] target,
        TimeSpan waitForFrame=default,CancellationToken cancellation=default,long sourceNotBeforeTicks=0)
        => throw new NotSupportedException("Provider does not support sampled acquisition.");
}
