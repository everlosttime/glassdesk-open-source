namespace GlassDesk.Dock;
public static class PassiveRevealPolicy
{
    public static bool CanReveal(bool inHotzone,bool edgeEnabled,bool fullscreen,bool suppressFullscreen,bool covered,bool blockCovered)
        => inHotzone && edgeEnabled && !(fullscreen && suppressFullscreen) && !(covered && blockCovered);

    /// <summary>遮挡或全屏期间，关闭唤出锁后允许鼠标移到 Dock 原位置唤出。</summary>
    public static bool CanRevealDuringSuppression(bool pointerOverDock, bool revealLocked)
        => !revealLocked && pointerOverDock;
}
