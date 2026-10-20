namespace GlassDesk.Core.Models;

/// <summary>Monotonic, continuous overlap intent; visual previews never mutate layout.</summary>
public sealed class FolderHoverIntent
{
    private TimeSpan _entered;
    public string? TargetId { get; private set; }
    public bool IsReady { get; private set; }
    public double Progress { get; private set; }

    public bool Update(string? targetId, bool overlapping, TimeSpan timestamp)
    {
        if (!overlapping || targetId is null) { Reset(); return false; }
        if (targetId != TargetId) { TargetId = targetId; _entered = timestamp; }
        Progress = Math.Clamp((timestamp - _entered).TotalMilliseconds / 500, 0, 1);
        return IsReady = Progress >= 1;
    }

    public void Reset() { TargetId = null; Progress = 0; IsReady = false; }
}

/// <summary>Small underdamped spring with bounded integration after suspended frames.</summary>
public sealed class FolderSpring
{
    private double _velocity;
    public double Value { get; private set; }
    public double Target { get; set; }
    public bool IsMoving => Math.Abs(Value - Target) > .0005 || Math.Abs(_velocity) > .002;
    public void Snap(double value) { Value = Target = value; _velocity = 0; }
    public double Advance(double seconds)
    {
        var remaining = double.IsFinite(seconds) ? Math.Clamp(seconds, 0, .05) : 0;
        while (remaining > 0)
        {
            var dt = Math.Min(remaining, 1d / 120);
            _velocity += ((Target - Value) * 260 - _velocity * 25) * dt;
            Value += _velocity * dt;
            remaining -= dt;
        }
        if (!IsMoving) Snap(Target);
        return Value;
    }
}
