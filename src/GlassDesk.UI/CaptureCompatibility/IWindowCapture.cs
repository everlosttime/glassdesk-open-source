/// <summary>
/// 载体窗口的取帧接口。WGC 会话（<see cref="PersistentWindowCapture"/>）与
/// PrintWindow 读取（<see cref="PrintWindowWindowCapture"/>）共享同一组语义：
/// 场景只关心“给我一帧当前合成结果”，不关心底层用哪种抓取机制。
/// </summary>
internal interface IWindowCapture : IDisposable
{
    CapturePixels? TryRead(long notBeforeQpcTicks = 0, TimeSpan waitForFrame = default,
        CancellationToken cancellation = default, bool reusePixels = false);

    void RefreshSessionAtSceneBoundary();

    void RetireQueuedFramesAtSceneBoundary(long barrierTicks);

    void ReleaseReadbackBuffers();
}
