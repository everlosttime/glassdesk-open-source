namespace GlassDesk.Core.Models;

/// <summary>按时钟计算边缘停留，每次持续停留最多翻一页。</summary>
public sealed class LauncherEdgePager(TimeSpan threshold)
{
    private int _direction;
    private DateTimeOffset _enteredAt;
    private bool _fired;

    public bool Tick(int direction, DateTimeOffset now)
    {
        if (direction is < -1 or > 1) throw new ArgumentOutOfRangeException(nameof(direction));
        if (direction != _direction)
        {
            _direction = direction;
            _enteredAt = now;
            _fired = false;
        }
        if (direction == 0 || _fired || now - _enteredAt < threshold) return false;
        _fired = true;
        return true;
    }

    public void Reset() => _direction = 0;
}
