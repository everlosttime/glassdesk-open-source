using GlassDesk.Core.Models.Settings;

namespace GlassDesk.Core.Input;

/// <summary>单调毫秒时钟驱动；触发一次后需要离开角落才能重新触发。</summary>
public sealed class HotCornerDwell
{
    private string? _token;
    private long _entered;
    private bool _fired;

    public bool Update(string? token, long nowMs, int dwellMs, bool suppressed)
    {
        if (suppressed || token is null) { _token = null; _fired = false; return false; }
        if (_token != token || nowMs < _entered) { _token = token; _entered = nowMs; _fired = false; }
        if (_fired || nowMs - _entered < dwellMs) return false;
        _fired = true;
        return true;
    }

    public static bool IsInCorner(int x, int y, int left, int top, int width, int height, HotCorner corner, int thickness)
    {
        if (width <= 0 || height <= 0 || thickness <= 0 || x < left || y < top || x >= left + width || y >= top + height) return false;
        var l = x < left + thickness;
        var r = x >= left + width - thickness;
        var t = y < top + thickness;
        var b = y >= top + height - thickness;
        return corner switch { HotCorner.TopLeft => l && t, HotCorner.TopRight => r && t,
            HotCorner.BottomLeft => l && b, HotCorner.BottomRight => r && b, _ => false };
    }
}
