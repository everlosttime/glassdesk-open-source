internal sealed class DesktopBackgroundComposer
{
    private readonly record struct PixelSpan(int Offset, int Length);
    private readonly record struct CoverageRect(int Left, int Top, int Right, int Bottom);
    private readonly PixelSpan[] _uncovered;
    private readonly int _length;

    // In this case Apply replaces every carrier pixel with the background.
    internal bool IsBackgroundOnly => _uncovered.Length == 1
        && _uncovered[0].Offset == 0 && _uncovered[0].Length == _length;

    internal DesktopBackgroundComposer(int width, int height, IReadOnlyList<ScreenRect> foreground)
    {
        _length = checked(width * height * 4);
        var windows = foreground.Select(bounds => new CoverageRect(
                Math.Clamp(bounds.X, 0, width), Math.Clamp(bounds.Y, 0, height),
                Math.Clamp(bounds.X + bounds.Width, 0, width), Math.Clamp(bounds.Y + bounds.Height, 0, height)))
            .Where(bounds => bounds.Right > bounds.Left && bounds.Bottom > bounds.Top)
            .OrderBy(bounds => bounds.Left).ToArray();
        var spans = new List<PixelSpan>();
        for (var row = 0; row < height; row++)
        {
            var cursor = 0;
            foreach (var bounds in windows)
            {
                if (row < bounds.Top || row >= bounds.Bottom) continue;
                if (bounds.Left > cursor) AddSpan(spans, (row * width + cursor) * 4, (bounds.Left - cursor) * 4);
                cursor = Math.Max(cursor, bounds.Right);
            }
            if (cursor < width) AddSpan(spans, (row * width + cursor) * 4, (width - cursor) * 4);
        }
        _uncovered = spans.ToArray();
    }

    private static void AddSpan(List<PixelSpan> spans, int offset, int length)
    {
        if (spans.Count > 0 && spans[^1].Offset + spans[^1].Length == offset)
        {
            var previous = spans[^1];
            spans[^1] = previous with { Length = previous.Length + length };
        }
        else spans.Add(new(offset, length));
    }

    internal void Apply(byte[] frame, byte[] background)
    {
        if (frame.Length != _length || background.Length != _length)
            throw new ArgumentException("Desktop and scene pixels must have matching geometry.");
        foreach (var span in _uncovered)
            Buffer.BlockCopy(background, span.Offset, frame, span.Offset, span.Length);
    }
}
