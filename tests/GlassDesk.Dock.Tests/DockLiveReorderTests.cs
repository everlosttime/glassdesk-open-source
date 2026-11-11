using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GlassDesk.Dock;

namespace GlassDesk.Dock.Tests;

public sealed class DockLiveReorderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InterruptedLongPressRestoresTheIconAndCancelsWithoutSaving(bool hide)
    {
        StaTest.Run(() =>
        {
            var saves = 0;
            var finished = 0;
            var reorder = new DockReorderModel(_ => saves++);
            var strip = CreateStrip(reorder);
            var host = new Window { Content = strip, Width = 400, Height = 100,
                ShowActivated = false, ShowInTaskbar = false };
            try
            {
                host.Show();
                host.UpdateLayout();
                var tiles = (List<Border>)typeof(DockTileStrip).GetField("_tiles", Private)!.GetValue(strip)!;
                var original = tiles[0];
                typeof(DockTileStrip).GetField("_pressedEntryId", Private)!.SetValue(strip, "a");
                typeof(DockTileStrip).GetField("_pressTile", Private)!.SetValue(strip, original);
                strip.ReorderFinished += (_, _) => finished++;
                typeof(DockTileStrip).GetMethod("StartLongPressDrag", Private)!.Invoke(strip, new object[] { original });
                Assert.True(reorder.IsActive);
                reorder.PreviewMove("a", 2);
                ApplyPreview(strip);
                if (hide) host.Hide(); else Mouse.Capture(null);
                Assert.False(reorder.IsActive);
                Assert.Equal(new[] { "a", "b", "c" }, reorder.Order);
                Assert.Equal(1, original.Opacity);
                Assert.True(original.IsHitTestVisible);
                Assert.Equal(0, saves);
                Assert.Equal(1, finished);
            }
            finally { host.Close(); }
        });
    }

    [Fact]
    public void PreviewMovesExistingTilesBeforeDropAndCommitsOnlyOnce()
    {
        StaTest.Run(() =>
        {
            var saves = new List<string[]>();
            var reorder = new DockReorderModel(order => saves.Add(order.ToArray()));
            var strip = CreateStrip(reorder);
            var originalCenter = strip.PaintedTiles[0].Left;
            reorder.Begin(strip.ReorderCandidateIds);
            reorder.PreviewMove("a", 2);
            ApplyPreview(strip);
            Assert.Equal(new[] { "b", "c", "a", "entry:launcher" },
                strip.PaintedTiles.Select(tile => tile.EntryId));
            Assert.Empty(saves);
            Assert.Equal(originalCenter, strip.PaintedTiles[2].Left, 5);
            Advance(strip);
            var halfway = strip.PaintedTiles[2].Left;
            Assert.True(halfway > originalCenter && halfway < 112, $"Left={halfway}");
            for (var frame = 0; frame < 120; frame++) Advance(strip);
            Assert.Equal(112, strip.PaintedTiles[2].Left, 2);
            Assert.Equal(ReorderOutcome.Committed, reorder.Commit());
            Assert.Equal(new[] { "b", "c", "a" }, Assert.Single(saves));
            Assert.Equal(ReorderOutcome.Idle, reorder.Commit());
            Assert.Single(saves);
        });
    }

    [Fact]
    public void CancelRestoresThePreviewWithoutSavingOrRemovingBuiltInEntries()
    {
        StaTest.Run(() =>
        {
            var saves = 0;
            var reorder = new DockReorderModel(_ => saves++);
            var strip = CreateStrip(reorder);
            reorder.Begin(strip.ReorderCandidateIds);
            reorder.PreviewMove("a", 2);
            ApplyPreview(strip);
            Advance(strip);
            Assert.Equal(ReorderOutcome.Cancelled, reorder.Cancel());
            ApplyPreview(strip);
            for (var frame = 0; frame < 120; frame++) Advance(strip);
            Assert.Equal(new[] { "a", "b", "c", "entry:launcher" },
                strip.PaintedTiles.Select(tile => tile.EntryId));
            Assert.Equal(0, strip.PaintedTiles[0].Left, 2);
            Assert.Equal(0, saves);
        });
    }

    private static DockTileStrip CreateStrip(DockReorderModel reorder)
    {
        var strip = new DockTileStrip();
        strip.SetMetrics(48, 8, 1, 8, 0);
        strip.SetTiles(new[] { "a", "b", "c", "entry:launcher" }
            .Select(id => new DockTileModel { EntryId = id, DisplayName = id }).ToArray());
        strip.SetReorderModel(reorder);
        return strip;
    }

    private static void ApplyPreview(DockTileStrip strip)
    {
        var apply = typeof(DockTileStrip).GetMethod("ApplyPreviewOrder", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(apply);
        apply.Invoke(strip, null);
    }

    private static void Advance(DockTileStrip strip)
    {
        typeof(DockTileStrip).GetMethod("AnimateFrame", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(strip, new object?[] { null, .016 });
    }

    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
}
