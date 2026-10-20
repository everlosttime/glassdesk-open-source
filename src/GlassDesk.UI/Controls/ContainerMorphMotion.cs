using System.Windows;
using GlassDesk.Platform.Displays;

namespace GlassDesk.UI.Controls;

public sealed class ContainerMorphMotion
{
    private const double WidthLeadSeconds = .018;
    private const double RevealThreshold = .65;
    private const double RevealSeconds = .1;
    private const double ConcealSeconds = .07;
    private PixelRect _source;
    private readonly PixelRect _destination;
    private readonly double _sourceRoundness;
    private readonly double _destinationRadius;
    private Spring _centerX;
    private Spring _centerY;
    private Spring _width;
    private Spring _height;
    private Spring _roundness;
    private Spring _sourceWidthForProgress;
    private Spring _sourceHeightForProgress;
    private bool _shapeExpanded;
    private double _shapeAge;
    private double _widthDelay;
    private double _closeHold;
    private double _contentOpacity;
    private double _contentFrom;
    private double _contentTime;
    private bool _contentRevealing;

    public ContainerMorphMotion(PixelRect source, PixelRect destination, double sourceRadius, double destinationRadius)
    {
        _source = Normalize(source);
        _destination = Normalize(destination);
        _sourceRoundness = RoundnessFor(_source, NormalizeRadius(sourceRadius));
        _destinationRadius = NormalizeRadius(destinationRadius);
        SnapToTarget();
    }

    /// <summary>Bounds are physical pixels carried as doubles; the host converts to DIP only when drawing.</summary>
    public ContainerMorphFrame CurrentFrame
    {
        get
        {
            var width = Math.Max(1, _width.Value);
            var height = Math.Max(1, _height.Value);
            var openness = Openness;
            var radius = Math.Clamp(_roundness.Value, 0, 1) * Math.Min(width, height) / 2;
            return new ContainerMorphFrame(
                new Rect(_centerX.Value - width / 2, _centerY.Value - height / 2, width, height),
                radius, openness, .8 * (1 - openness), _contentOpacity, .96 + .04 * _contentOpacity,
                1 - SmoothStep(Math.Clamp(openness / .55, 0, 1)));
        }
    }

    public bool IsSettled { get; private set; }
    public bool IsExpandedTarget { get; private set; }
    public double Openness => IsSettled ? IsExpandedTarget ? 1 : 0 :
        OpennessFor(_width.Value, _height.Value, _sourceWidthForProgress.Value, _sourceHeightForProgress.Value);

    /// <summary>Retargets the springs without replacing their current position or velocity.</summary>
    public void Retarget(bool expanded, PixelRect? sourceBounds = null)
    {
        var sourceChanged = sourceBounds is { } bounds && Normalize(bounds) != _source;
        if (sourceChanged) _source = Normalize(sourceBounds!.Value);
        if (expanded == IsExpandedTarget)
        {
            // A moving Dock must not restart an already-running content fade or initial width delay.
            if (sourceChanged && !expanded && IsSettled)
            {
                IsSettled = false;
                _shapeAge = 0;
            }
            return;
        }

        var startFromRest = IsSettled && !IsExpandedTarget;
        IsExpandedTarget = expanded;
        IsSettled = false;
        _contentFrom = _contentOpacity;
        _contentTime = 0;
        _contentRevealing = expanded && Openness >= RevealThreshold;
        _closeHold = !expanded && _contentOpacity > 0 ? ConcealSeconds : 0;
        _widthDelay = expanded && startFromRest ? WidthLeadSeconds : 0;
        if (expanded || _closeHold == 0)
        {
            _shapeExpanded = expanded;
            _shapeAge = 0;
        }
    }

    public void Advance(double deltaSeconds)
    {
        if (IsSettled || !double.IsFinite(deltaSeconds) || deltaSeconds <= 0) return;

        if (IsExpandedTarget)
        {
            var startWidth = _width;
            var startHeight = _height;
            var startSourceWidth = _sourceWidthForProgress;
            var startSourceHeight = _sourceHeightForProgress;
            var startDelay = _widthDelay;
            AdvanceProgressSource(deltaSeconds);
            AdvanceGeometry(deltaSeconds);
            if (!_contentRevealing && Openness >= RevealThreshold)
            {
                // Solve the one reveal crossing, so 30 / 60 / 144 Hz start the fade at the same time.
                var low = 0d;
                var high = deltaSeconds;
                for (var i = 0; i < 24; i++)
                {
                    var middle = (low + high) / 2;
                    var width = startWidth;
                    var height = startHeight;
                    var sourceWidth = startSourceWidth;
                    var sourceHeight = startSourceHeight;
                    width.Advance(Math.Max(0, middle - startDelay), _destination.Width, 25, .76);
                    height.Advance(middle, _destination.Height, 28, .77);
                    sourceWidth.Advance(middle, _source.Width, 48, 1);
                    sourceHeight.Advance(middle, _source.Height, 48, 1);
                    if (OpennessFor(width.Value, height.Value, sourceWidth.Value, sourceHeight.Value) >= RevealThreshold)
                        high = middle;
                    else low = middle;
                }
                _contentRevealing = true;
                _contentTime = deltaSeconds - high;
            }
            else if (_contentRevealing) _contentTime += deltaSeconds;
            if (_contentRevealing)
                _contentOpacity = _contentFrom + (1 - _contentFrom) * SmoothStep(_contentTime / RevealSeconds);
        }
        else
        {
            AdvanceProgressSource(deltaSeconds);
            _contentTime += deltaSeconds;
            _contentOpacity = _contentFrom * (1 - SmoothStep(_contentTime / ConcealSeconds));
            var remaining = deltaSeconds;
            if (_closeHold > 0)
            {
                var held = Math.Min(remaining, _closeHold);
                AdvanceGeometry(held);
                remaining -= held;
                _closeHold -= held;
                if (_closeHold <= 1e-12)
                {
                    _closeHold = 0;
                    _shapeExpanded = false;
                    _shapeAge = 0;
                    _widthDelay = 0;
                }
            }
            if (remaining > 0) AdvanceGeometry(remaining);
        }

        if (_shapeExpanded == IsExpandedTarget && _closeHold == 0 &&
            _shapeAge >= (IsExpandedTarget ? .38 : .2) && GeometryAtRest() &&
            _contentOpacity == (IsExpandedTarget ? 1 : 0))
            SnapToTarget();
    }

    public void SnapToTarget()
    {
        var bounds = IsExpandedTarget ? _destination : _source;
        _centerX = new Spring(bounds.X + bounds.Width / 2d);
        _centerY = new Spring(bounds.Y + bounds.Height / 2d);
        _width = new Spring(bounds.Width);
        _height = new Spring(bounds.Height);
        _roundness = new Spring(IsExpandedTarget ? RoundnessFor(bounds, _destinationRadius) : _sourceRoundness);
        _sourceWidthForProgress = new Spring(_source.Width);
        _sourceHeightForProgress = new Spring(_source.Height);
        _shapeExpanded = IsExpandedTarget;
        _widthDelay = 0;
        _closeHold = 0;
        _contentOpacity = IsExpandedTarget ? 1 : 0;
        _contentFrom = _contentOpacity;
        _contentTime = 0;
        _contentRevealing = IsExpandedTarget;
        IsSettled = true;
    }

    private void AdvanceGeometry(double seconds)
    {
        _shapeAge += seconds;
        var target = _shapeExpanded ? _destination : _source;
        if (_shapeExpanded)
        {
            _centerX.Advance(seconds, target.X + target.Width / 2d, 26, .84);
            _centerY.Advance(seconds, target.Y + target.Height / 2d, 29, .82);
            var widthSeconds = Math.Max(0, seconds - _widthDelay);
            _widthDelay = Math.Max(0, _widthDelay - seconds);
            _width.Advance(widthSeconds, target.Width, 25, .76);
            _height.Advance(seconds, target.Height, 28, .77);
            _roundness.Advance(seconds, RoundnessFor(target, _destinationRadius), 27, 1);
        }
        else
        {
            // Critical damping keeps a collapsing shell from undershooting into a negative small size.
            _centerX.Advance(seconds, target.X + target.Width / 2d, 48, 1);
            _centerY.Advance(seconds, target.Y + target.Height / 2d, 48, 1);
            _width.Advance(seconds, target.Width, 48, 1);
            _height.Advance(seconds, target.Height, 48, 1);
            _roundness.Advance(seconds, _sourceRoundness, 48, 1);
        }
    }

    private void AdvanceProgressSource(double seconds)
    {
        _sourceWidthForProgress.Advance(seconds, _source.Width, 48, 1);
        _sourceHeightForProgress.Advance(seconds, _source.Height, 48, 1);
    }

    private bool GeometryAtRest()
    {
        var target = IsExpandedTarget ? _destination : _source;
        return _centerX.AtRest(target.X + target.Width / 2d,
                _destination.X + _destination.Width / 2d - _source.X - _source.Width / 2d) &&
            _centerY.AtRest(target.Y + target.Height / 2d,
                _destination.Y + _destination.Height / 2d - _source.Y - _source.Height / 2d) &&
            _width.AtRest(target.Width, _destination.Width - _source.Width) &&
            _height.AtRest(target.Height, _destination.Height - _source.Height) &&
            _roundness.AtRest(IsExpandedTarget ? RoundnessFor(target, _destinationRadius) : _sourceRoundness, 1) &&
            _sourceWidthForProgress.AtRest(_source.Width, _destination.Width - _source.Width) &&
            _sourceHeightForProgress.AtRest(_source.Height, _destination.Height - _source.Height);
    }

    private double OpennessFor(double width, double height, double sourceWidth, double sourceHeight)
    {
        var widthSpan = _destination.Width - sourceWidth;
        var heightSpan = _destination.Height - sourceHeight;
        var w = Math.Abs(widthSpan) < 1e-9 ? 1 : (width - sourceWidth) / widthSpan;
        var h = Math.Abs(heightSpan) < 1e-9 ? 1 : (height - sourceHeight) / heightSpan;
        return Math.Clamp(Math.Min(w, h), 0, 1);
    }

    private static PixelRect Normalize(PixelRect bounds) => bounds with
        { Width = Math.Max(1, bounds.Width), Height = Math.Max(1, bounds.Height) };
    private static double NormalizeRadius(double radius) => double.IsFinite(radius) ? Math.Max(0, radius) : 0;
    private static double RoundnessFor(PixelRect bounds, double radius) =>
        Math.Clamp(radius * 2 / Math.Min(bounds.Width, bounds.Height), 0, 1);
    private static double SmoothStep(double value)
    {
        var t = Math.Clamp(value, 0, 1);
        return t * t * (3 - 2 * t);
    }

    private struct Spring(double initial)
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
}

public readonly record struct ContainerMorphFrame(Rect Bounds, double CornerRadius, double GlassStrength,
    double BlurRadius, double ContentOpacity, double ContentScale, double GlyphOpacity);
