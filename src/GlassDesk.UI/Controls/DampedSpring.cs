namespace GlassDesk.UI.Controls;

public struct DampedSpring(double initial)
{
    public double Value = initial;
    private double _velocity;

    public void Advance(double seconds, double target, double frequency, double damping)
    {
        if (seconds <= 0) return;
        var displacement = Value - target;
        if (displacement == 0 && _velocity == 0) return;
        var decay = Math.Exp(-damping * frequency * seconds);
        if (damping == 1)
        {
            var impulse = _velocity + frequency * displacement;
            Value = target + decay * (displacement + impulse * seconds);
            _velocity = decay * (_velocity - frequency * impulse * seconds);
            return;
        }
        var dampedFrequency = frequency * Math.Sqrt(1 - damping * damping);
        var sine = Math.Sin(dampedFrequency * seconds);
        var cosine = Math.Cos(dampedFrequency * seconds);
        var coefficient = (_velocity + damping * frequency * displacement) / dampedFrequency;
        var wave = displacement * cosine + coefficient * sine;
        Value = target + decay * wave;
        _velocity = decay * (dampedFrequency * (-displacement * sine + coefficient * cosine) -
            damping * frequency * wave);
    }

    public readonly bool AtRest(double target, double span) =>
        Math.Abs(Value - target) <= Math.Max(.001, Math.Abs(span) * .001) &&
        Math.Abs(_velocity) <= Math.Max(.01, Math.Abs(span) * .01);
}
