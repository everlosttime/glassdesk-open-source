using System.Diagnostics;

/// <summary>Worker-owned bounded mailbox. Never holds pixels or window titles.</summary>
internal sealed class NativeCaptureDiagnostics
{
    private readonly Queue<CaptureTrace> _events = new();
    private (bool Black, bool Transparent)? _pixelState;
    internal void Observe(CaptureTrace trace)
    {
        if (!CaptureTraceLog.IsStateEvent(trace)) return;
        if (_events.Count == 64) _events.Dequeue();
        _events.Enqueue(trace);
    }
    internal CaptureTrace[] Drain()
    {
        var events = _events.ToArray(); _events.Clear(); return events;
    }
    internal void ObservePixels(CapturePixels frame, long generation)
    {
        var pixels = frame.Pixels;
        var count = pixels.Length / 4; var step = Math.Max(1, count / 512);
        var samples = 0; var black = 0; var transparent = 0; long sum = 0;
        for (var pixel = 0; pixel < count; pixel += step)
        {
            var i = pixel * 4;
            samples++;
            if (Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2])) <= 3) black++;
            if (pixels[i + 3] == 0) transparent++;
            sum += pixels[i] + pixels[i + 1] + pixels[i + 2];
        }
        var blackFraction = black / (double)Math.Max(1, samples);
        var transparentFraction = transparent / (double)Math.Max(1, samples);
        var state = (blackFraction >= .95, transparentFraction > 0);
        if (_pixelState == state) return;
        _pixelState = state;
        // Statistics mark evidence only. Opaque black wallpaper/video is valid and
        // must continue through the exact same acquisition and sampling pipeline.
        Observe(new("NativePixels", "StatisticsChanged", Stopwatch.GetTimestamp(), frame.SourceQpcTicks,
            Generation: generation, Width: frame.Width, Height: frame.Height, NearBlackFraction: blackFraction,
            TransparentFraction: transparentFraction, MeanChannel: sum / (double)Math.Max(1, samples * 3)));
    }
}
