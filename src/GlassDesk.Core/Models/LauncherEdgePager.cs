namespace GlassDesk.Core.Models;

/// <summary>按时钟计算边缘停留：首次满阈值翻一页，持续停留再按重复间隔继续翻页。</summary>
public sealed class LauncherEdgePager(TimeSpan threshold, TimeSpan? repeatInterval = null)
{
    private int _direction;
    private DateTimeOffset _enteredAt;
    private DateTimeOffset _nextAt;
    private bool _fired;

    public bool Tick(int direction, DateTimeOffset now)
    {
        if (direction is < -1 or > 1) throw new ArgumentOutOfRangeException(nameof(direction));
        if (direction != _direction)
        {
            _direction = direction;
            _enteredAt = now;
            _nextAt = default;
            _fired = false;
        }
        if (direction == 0) return false;
        if (!_fired)
        {
            if (now - _enteredAt < threshold) return false;
            _fired = true;
            _nextAt = now + (repeatInterval ?? threshold);
            return true;
        }
        // Holding at the edge keeps turning pages so a far-away page stays one gesture away.
        if (now < _nextAt) return false;
        _nextAt = now + (repeatInterval ?? threshold);
        return true;
    }

    public void Reset() => _direction = 0;
}
