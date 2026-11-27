using System.Windows;
using GlassDesk.UI.Controls;

namespace GlassDesk.App;

internal readonly record struct StageMotionPose(Rect Bounds, double Depth, double HandoffOpacity);

internal sealed class StageMotion(Rect initial, bool onStage)
{
    private DampedSpring _x = new(initial.X + initial.Width / 2);
    private DampedSpring _y = new(initial.Y + initial.Height / 2);
    private DampedSpring _width = new(initial.Width);
    private DampedSpring _height = new(initial.Height);
    private DampedSpring _depth = new(onStage ? 0 : 1);
    private DampedSpring _opacity = new(1);
    private Rect _destination = initial;
    private double _targetDepth = onStage ? 0 : 1;
    private double _targetOpacity = 1;

    private double CenterX => _destination.X + _destination.Width / 2;
    private double CenterY => _destination.Y + _destination.Height / 2;

    public StageMotionPose Pose
    {
        get
        {
            var width = Math.Max(1, _width.Value);
            var height = Math.Max(1, _height.Value);
            return new(new Rect(_x.Value - width / 2, _y.Value - height / 2, width, height),
                Math.Clamp(_depth.Value, 0, 1), Math.Clamp(_opacity.Value, 0, 1));
        }
    }

    public bool IsSettled => _x.AtRest(CenterX, 550) && _y.AtRest(CenterY, 550)
        && _width.AtRest(_destination.Width, 400) && _height.AtRest(_destination.Height, 400)
        && _depth.AtRest(_targetDepth, 2);

    public bool CanRestoreNativeWindow
    {
        get
        {
            if (_targetDepth != 0 || _depth.Value > .075) return false;
            var bounds = Pose.Bounds;
            const double edgeTolerance = 6;
            return Math.Abs(bounds.Left - _destination.Left) <= edgeTolerance
                && Math.Abs(bounds.Top - _destination.Top) <= edgeTolerance
                && Math.Abs(bounds.Right - _destination.Right) <= edgeTolerance
                && Math.Abs(bounds.Bottom - _destination.Bottom) <= edgeTolerance;
        }
    }

    public bool NeedsFrames => !IsSettled || !_opacity.AtRest(_targetOpacity, 1);
    public bool HandoffComplete => _targetOpacity == 0 && _opacity.Value <= .01;

    public void Retarget(Rect target, bool opening)
    {
        _destination = target;
        _targetDepth = opening ? 0 : 1;
        _targetOpacity = 1;
    }

    // Maintain the same combined opacity as the outgoing preview reveals the card behind it.
    public static double RailPreviewWeight(double target, double moving) =>
        moving >= target ? 0 : (target - moving) / (target * (1 - moving));

    public void BeginHandoff() => _targetOpacity = 0;
    public void Resume() => _targetOpacity = 1;

    public void Advance(double elapsed)
    {
        // After a late frame, continue along the spring instead of jumping over
        // the missing motion. Transform channels share one clock and response.
        var seconds = Math.Clamp(elapsed, 0, 1.0 / 30);
        const double speed = 32;
        _x.Advance(seconds, CenterX, speed, 1);
        _y.Advance(seconds, CenterY, speed, 1);
        _width.Advance(seconds, _destination.Width, speed, 1);
        _height.Advance(seconds, _destination.Height, speed, 1);
        _depth.Advance(seconds, _targetDepth, speed, 1);
        _opacity.Advance(seconds, _targetOpacity, 84, 1);
    }
}
