using System.Diagnostics;
using System.Windows;

namespace GlassDesk.UI.Controls;

public sealed partial class LiquidGlassSurface
{
    private CaptureTrace? _lastInputPixelTrace, _lastSubmittedPixelTrace;
    private long _lastSubmittedPixelTraceAt;
    private string DiagnosticSurfaceName => Window.GetWindow(this)?.GetType().Name ?? "UnattachedSurface";

    private void RecordSubmittedPixels(byte[] input, byte[] submitted, int width, int height, long sourceTicks)
    {
        var incoming = NativeCaptureDiagnostics.MeasurePixels(new(width, height, input) { SourceQpcTicks = sourceTicks }, BackdropRecovery.Generation);
        var outgoing = NativeCaptureDiagnostics.MeasurePixels(new(width, height, submitted) { SourceQpcTicks = sourceTicks }, BackdropRecovery.Generation);
        var previous = _lastSubmittedPixelTrace;
        var inputPrevious = _lastInputPixelTrace;
        var changed = previous is null || (previous.Value.NearBlackFraction >= .95) != (outgoing.NearBlackFraction >= .95)
            || (previous.Value.TransparentFraction >= .95) != (outgoing.TransparentFraction >= .95);
        var luminanceDrop = previous is { MeanChannel: > 12 } last && inputPrevious is { MeanChannel: > 1 } lastInput
            && incoming.MeanChannel > 20 && incoming.MeanChannel >= lastInput.MeanChannel * .65
            && outgoing.MeanChannel < last.MeanChannel * .5;
        _lastInputPixelTrace = incoming; _lastSubmittedPixelTrace = outgoing;
        var now = Stopwatch.GetTimestamp();
        if (!changed && !luminanceDrop && now - _lastSubmittedPixelTraceAt < Stopwatch.Frequency / 4) return;
        _lastSubmittedPixelTraceAt = now;
        var visible = Window.GetWindow(this)?.IsVisible == true && IsVisible;
        void Record(CaptureTrace sample, string stage) => BackdropRecovery.Record(sample with
        {
            Stage = stage, Reason = luminanceDrop ? "LuminanceDrop" : "StatisticsChanged",
            SurfaceId = _diagnosticId, Surface = DiagnosticSurfaceName, MaterialSource = "Live",
            Visible = visible, Active = IsBackdropActive?.Invoke() != false, Sleeping = _performanceSleeping
        });
        Record(incoming, "MaterialInputPixels");
        Record(outgoing, "MaterialSubmittedPixels");
    }
}
