internal readonly record struct PixelBounds(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
}
internal sealed record PixelFrame(PixelBounds Bounds, byte[] Pixels, long StartTicks, long EndTicks, DateTimeOffset StartedUtc);
internal readonly record struct PixelCandidate(int X, int Y, bool Icon, bool Clear);
internal sealed class PixelStats
{
    public int Candidates { get; set; }
    public int Occluded { get; set; }
    public int Unstable { get; set; }
    public int Valid { get; set; }
    public int Mismatched { get; set; }
    public int[] MaxBgrError { get; } = new int[3];
    public bool Passed => Valid >= 50 && Mismatched == 0;
}
internal sealed record PixelResult(PixelStats Wallpaper, PixelStats Icons, IReadOnlyList<(int X, int Y, bool Icon)> Mismatches);
internal static class PixelComparison
{
    internal static PixelResult Compare(PixelFrame before, PixelFrame source, PixelFrame after, IEnumerable<PixelCandidate> points)
    {
        if (before.Bounds != source.Bounds || before.Bounds != after.Bounds)
            throw new InvalidOperationException("Physical capture bounds changed during the triplet.");
        var size = checked(before.Bounds.Width * before.Bounds.Height * 4);
        if (before.Pixels.Length != size || source.Pixels.Length != size || after.Pixels.Length != size)
            throw new InvalidOperationException("Incomplete physical frame.");
        var wallpaper = new PixelStats(); var icons = new PixelStats();
        var mismatches = new List<(int, int, bool)>();
        foreach (var p in points.DistinctBy(p => (p.X, p.Y)))
        {
            var stats = p.Icon ? icons : wallpaper;
            stats.Candidates++;
            if (!p.Clear || !before.Bounds.Contains(p.X, p.Y)) { stats.Occluded++; continue; }
            var offset = ((p.Y - before.Bounds.Top) * before.Bounds.Width + p.X - before.Bounds.Left) * 4;
            if (Enumerable.Range(0,3).Any(c => before.Pixels[offset+c] != after.Pixels[offset+c]))
            { stats.Unstable++; continue; }
            stats.Valid++;
            var over = false;
            for (var c = 0; c < 3; c++)
            {
                // Compare both references explicitly; stable references are exactly equal in BGR.
                var error = Math.Max(Math.Abs(source.Pixels[offset+c] - before.Pixels[offset+c]),
                    Math.Abs(source.Pixels[offset+c] - after.Pixels[offset+c]));
                stats.MaxBgrError[c] = Math.Max(stats.MaxBgrError[c], error);
                over |= error > 2;
            }
            if (over) { stats.Mismatched++; mismatches.Add((p.X,p.Y,p.Icon)); }
        }
        return new(wallpaper, icons, mismatches);
    }
}
