using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GlassDesk.AppServices;
using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;
using GlassDesk.Launcher;
using GlassDesk.UI.Controls;

namespace GlassDesk.Dock.Tests;

public sealed class LauncherFolderInteractionTests
{
    [Fact]
    public void ApproachingIconCenterDoesNotSortAwayTheFolderTarget()
    {
        StaTest.Run(() =>
        {
            using var fixture = new Fixture(); fixture.Pickup("a0");
            try
            {
                var edit = Get<LauncherEditSession>(fixture.Window, "_dragEdit");
                var original = LauncherLayoutRules.DeepClone(edit.Draft);
                var center = fixture.Over("a2"); var approach = center; approach.X -= 48;
                Call(fixture.Window, "PlaceIconAtPointer", approach, false, false);
                Assert.True(LauncherLayoutRules.Equivalent(original, edit.Draft), "Entering a cell must not immediately displace its icon.");
                Pump(65);
                Call(fixture.Window, "PlaceIconAtPointer", center, false, false);
                for (var i = 0; i < 11; i++)
                {
                    Pump(50);
                    Call(fixture.Window, "PlaceIconAtPointer", new Point(center.X + (i % 2 == 0 ? 3 : -3), center.Y), false, false);
                    Assert.True(LauncherLayoutRules.Equivalent(original, edit.Draft), "Center hover must retain the target while waiting for folder confirmation.");
                }
                Assert.True(Get<FolderHoverIntent>(fixture.Window, "_folderIntent").IsReady);
                Assert.True((bool)Call(fixture.Window, "ApplyFolderDrop", center, false)!);
                fixture.CommitDrag();
                Assert.Contains(fixture.Slots, s => s.Group?.ItemIds.SequenceEqual(["a2", "a0"]) == true);
            }
            finally
            {
                if (Get<LauncherLayout?>(fixture.Window, "_dragOrigin") is not null) Call(fixture.Window, "FinishIconDrag");
            }
        });
    }

    [Fact]
    public void OrdinarySortingWaitsForABriefDwellOutsideTheIconCenter()
    {
        StaTest.Run(() =>
        {
            using var fixture = new Fixture(); fixture.Pickup("a0");
            try
            {
                var edit = Get<LauncherEditSession>(fixture.Window, "_dragEdit");
                var original = LauncherLayoutRules.DeepClone(edit.Draft);
                var point = fixture.Over("a3"); point.Y += 38;
                Call(fixture.Window, "PlaceIconAtPointer", point, false, false);
                Assert.Null(Get<FolderHoverIntent>(fixture.Window, "_folderIntent").TargetId);
                Assert.True(LauncherLayoutRules.Equivalent(original, edit.Draft), "Sorting must not react to the first frame in a new cell.");
                Pump(110);
                Call(fixture.Window, "PlaceIconAtPointer", new Point(-1, -1), false, false);
                Pump(80);
                Call(fixture.Window, "PlaceIconAtPointer", point, false, false);
                Assert.True(LauncherLayoutRules.Equivalent(original, edit.Draft), "Leaving and reentering must restart the sorting dwell.");
                Pump(210);
                Call(fixture.Window, "PlaceIconAtPointer", point, false, false);
                Assert.False(LauncherLayoutRules.Equivalent(original, edit.Draft));
                Assert.Equal("a0", edit.Draft.Pages.SelectMany(p => p.Slots).ElementAt(3).ItemId);
            }
            finally { Call(fixture.Window, "FinishIconDrag"); }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FolderHoverNeverMovesOrReplacesTheTargetIcon(bool ready)
    {
        StaTest.Run(() =>
        {
            using var fixture = new Fixture(); fixture.Pickup("a0");
            try
            {
                var target = fixture.Tile("a1");
                var host = (FrameworkElement)target.GetType().GetProperty("IconHost", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;
                var overlay = (Canvas)fixture.Window.FindName("DragOverlay");
                Point Center() => host.TranslatePoint(new Point(host.ActualWidth / 2, host.ActualHeight / 2), overlay);
                var before = Center(); var point = fixture.Over("a1"); point.X += 16;
                Call(fixture.Window, "UpdateFolderHover", point, false);
                var intent = Get<FolderHoverIntent>(fixture.Window, "_folderIntent");
                intent.Reset(); intent.Update("a1", true, Stopwatch.GetElapsedTime(0, Stopwatch.GetTimestamp())
                    - TimeSpan.FromMilliseconds(ready ? 550 : 220));
                Call(fixture.Window, "UpdateFolderHover", point, false);
                Pump(130);
                Assert.Equal(ready, intent.IsReady);
                Assert.Equal(1, host.Opacity);
                Assert.True((Center() - before).Length < .01, "Folder hover must not translate the target icon.");
                Assert.Single(overlay.Children.OfType<Button>());
                Assert.All(overlay.Children.Cast<UIElement>(), child => Assert.True(child is Button or SharedGlassLens,
                    "Folder confirmation must not add a floating copy of the target icon."));
                if (!ready) Assert.Empty(overlay.Children.OfType<SharedGlassLens>());
            }
            finally { Call(fixture.Window, "FinishIconDrag"); }
        });
    }

    [Fact]
    public void ExtractedMemberKeepsContinuousDesktopHoverAcrossPreviewFrames()
    {
        StaTest.Run(() =>
        {
            using var fixture = new Fixture(); fixture.MakeGroup(2); fixture.OpenGroup(); Pump(650);
            var items = (UniformGrid)fixture.Window.FindName("GroupItems");
            var source = items.Children.OfType<Button>().First(t => Equals(t.Tag, "a1"));
            Call(fixture.Window, "PrepareIconDrag", source, new Point(source.ActualWidth / 2, source.ActualHeight / 2));
            try
            {
                Call(fixture.Window, "PlaceIconAtPointer", new Point(8, 8), false, false);
                Get<FolderSpring>(fixture.Window, "_folderOpen").Snap(0);
                Call(fixture.Window, "ApplyFolderFrame"); fixture.Window.UpdateLayout();
                var point = fixture.Over("a4");
                Call(fixture.Window, "PlaceIconAtPointer", point, false, false);
                fixture.Mature("a4");
                // A second preview must retain the hover begun after extraction,
                // rather than clearing it as if the member were still in its folder.
                Call(fixture.Window, "PlaceIconAtPointer", point, false, false);
                Assert.True(Get<FolderHoverIntent>(fixture.Window, "_folderIntent").IsReady);
                Assert.True((bool)Call(fixture.Window, "ApplyFolderDrop", point, false)!);
                fixture.CommitDrag();
                Assert.Contains(fixture.Slots, s => s.Group?.ItemIds.SequenceEqual(["a4", "a1"]) == true);
            }
            finally
            {
                if (Get<LauncherLayout?>(fixture.Window, "_dragOrigin") is not null) Call(fixture.Window, "FinishIconDrag");
            }
        });
    }

    [Fact]
    public void ExtractedMemberLandingAnimationUsesArrangedDestination()
    {
        StaTest.Run(() =>
        {
            using var fixture = new Fixture(); fixture.MakeGroup(2); fixture.OpenGroup(); Pump(650);
            var items = (UniformGrid)fixture.Window.FindName("GroupItems");
            var source = items.Children.OfType<Button>().First(t => Equals(t.Tag, "a1"));
            Call(fixture.Window, "PrepareIconDrag", source, new Point(source.ActualWidth / 2, source.ActualHeight / 2));
            var point = new Point(fixture.Grid.ActualWidth * .8, fixture.Grid.ActualHeight * .8);
            Call(fixture.Window, "PlaceIconAtPointer", point, true, true);
            Pump(1000);
            var overlay = (Canvas)fixture.Window.FindName("DragOverlay");
            var ghost = Get<Button>(fixture.Window, "_dragGhost");
            var motion = Get<TranslateTransform>(fixture.Window, "_dragGhostMotion");
            var grab = Get<Point>(fixture.Window, "_grabPoint");
            var cursor = fixture.Grid.TranslatePoint(point, overlay);
            var start = new Point(cursor.X - grab.X, cursor.Y - grab.Y);
            motion.X = start.X - Canvas.GetLeft(ghost); motion.Y = start.Y - Canvas.GetTop(ghost);
            fixture.CommitDrag();
            var landing = fixture.Tile("a1");
            var end = landing.TranslatePoint(new Point(), overlay);
            Pump(75);
            var actual = new Point(Canvas.GetLeft(ghost) + motion.X, Canvas.GetTop(ghost) + motion.Y);
            var path = end - start;
            var t = Math.Clamp(Vector.Multiply(actual - start, path) / path.LengthSquared, 0, 1);
            var error = (actual - (start + path * t)).Length;
            Assert.True(error < 4, $"Landing left the direct path by {error:F1} DIP: start={start}, end={end}, actual={actual}.");
            Pump(220);
            Assert.Equal(1, landing.Opacity);
            Assert.Empty(overlay.Children);
        });
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void ClosingFolderDuringMemberDragKeepsDesktopPlaceholderHidden(int members)
    {
        StaTest.Run(() =>
        {
            using var fixture = new Fixture(); fixture.MakeGroup(members); fixture.OpenGroup(); Pump(650);
            var items = (UniformGrid)fixture.Window.FindName("GroupItems");
            var source = items.Children.OfType<Button>().First(t => Equals(t.Tag, "a1"));
            Call(fixture.Window, "PrepareIconDrag", source, new Point(source.ActualWidth / 2, source.ActualHeight / 2));
            var active = typeof(LauncherWindow).GetField("_logicalIconDrag", BindingFlags.NonPublic | BindingFlags.Instance)!;
            active.SetValue(fixture.Window, true);
            try
            {
                Call(fixture.Window, "PlaceIconAtPointer", new Point(8, 8), false, false);
                Pump(210);
                Call(fixture.Window, "PlaceIconAtPointer", new Point(8, 8), false, false);
                Pump(1000); // Keep dragging after the entire close animation completes.
                Assert.Equal(Visibility.Collapsed, ((Grid)fixture.Window.FindName("GroupPanel")).Visibility);
                var placeholder = fixture.Tile("a1");
                Assert.Equal(0, placeholder.Opacity);
                Assert.False(placeholder.IsHitTestVisible);
                Assert.Single(((Canvas)fixture.Window.FindName("DragOverlay")).Children.OfType<Button>());
                Assert.All(fixture.Grid.Children.OfType<Button>().Where(t => !Equals(t.Tag, "a1")), t => Assert.Equal(1, t.Opacity));
            }
            finally
            {
                active.SetValue(fixture.Window, false);
                Call(fixture.Window, "FinishIconDrag");
            }
            Assert.All(fixture.Grid.Children.OfType<Button>(), t => Assert.Equal(1, t.Opacity));
            Assert.Equal(members, fixture.Slots.Single(s => s.Group is not null).Group!.ItemIds.Count);
        });
    }

    [Theory]
    [InlineData(80)]
    [InlineData(280)]
    public void ReenteringDisplacedTargetUsesItsCurrentVisiblePosition(int settleMilliseconds)
    {
        StaTest.Run(() =>
        {
            using var fixture = new Fixture(); fixture.Pickup("a0");
            Call(fixture.Window, "PlaceIconAtPointer", fixture.Over("a1"), false, true);
            Call(fixture.Window, "ClearFolderHover", false);
            Pump(settleMilliseconds); // Reenter during sorting as well as after it settles.
            var target = fixture.Tile("a1");
            var host = (FrameworkElement)target.GetType().GetProperty("IconHost", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;
            var overlay = (Canvas)fixture.Window.FindName("DragOverlay");
            var visible = host.TranslatePoint(new Point(host.ActualWidth / 2, host.ActualHeight / 2), overlay);
            Call(fixture.Window, "UpdateFolderHover", fixture.Over("a1"), false);
            var anchor = Get<Rect>(fixture.Window, "_folderHoverBounds");
            var distance = (new Point(anchor.X + anchor.Width / 2, anchor.Y + anchor.Height / 2) - visible).Length;
            Assert.True(distance < 2, $"Reentered visible target was displaced by {distance:F1} DIP.");
            fixture.Mature("a1");
            Assert.True((bool)Call(fixture.Window, "ApplyFolderDrop", fixture.Over("a1"), false)!);
        });
    }

    [Fact]
    public void FolderConfirmationStaysSquareWhenPointerIsOffCenter()
    {
        StaTest.Run(() =>
        {
            using var fixture = new Fixture();
            fixture.Pickup("a0");
            var point = fixture.Over("a1"); point.X += 16;
            Call(fixture.Window, "UpdateFolderHover", point, false);
            fixture.Mature("a1");
            Call(fixture.Window, "UpdateFolderHover", point, false);
            Assert.True(Get<FolderHoverIntent>(fixture.Window, "_folderIntent").IsReady);
            Get<FolderSpring>(fixture.Window, "_fusionSpring").Snap(1);
            Call(fixture.Window, "ApplyFusionFrame");
            fixture.Window.UpdateLayout();
            var frame = Get<SharedGlassLens>(fixture.Window, "_fusionLens");
            Assert.Equal(frame.Width, frame.Height, 3);
            Assert.InRange(frame.Radius / frame.Width, .34, .5);
            Assert.Null(frame.Effect);
        });
    }

    [Fact]
    public void FolderIconAndOpenPanelUseSharedBlurWithoutOpticalEffects()
    {
        StaTest.Run(() =>
        {
            using var fixture = new Fixture(); fixture.MakeGroup(2);
            var icon = Descendants(fixture.Tile("g")).OfType<SharedGlassLens>().Single();
            Assert.Null(icon.Effect);
            Assert.InRange(icon.Radius / icon.ActualWidth, .34, .5);
            fixture.OpenGroup(); Pump(650);
            var panel = (SharedGlassLens)fixture.Window.FindName("FolderGlass");
            Assert.Same(icon.Source, panel.Source);
            Assert.Null(panel.Effect);
            Assert.True(panel.Radius >= 40);
        });
    }

    [Fact]
    public void ContinuousOverlapCreatesFolderButQuickDropKeepsReorder()
    {
        StaTest.Run(() =>
        {
            using var fixture = new Fixture();
            fixture.Pickup("a0");
            var point = fixture.Over("a1");
            Call(fixture.Window, "PlaceIconAtPointer", point, false, false);
            Assert.False((bool)Call(fixture.Window, "ApplyFolderDrop", point, false)!);
            Call(fixture.Window, "PlaceIconAtPointer", point, false, true);
            fixture.CommitDrag();
            Assert.DoesNotContain(fixture.Slots, s => s.Group is not null);
            Assert.Equal("a1", fixture.Slots[0].ItemId);

            fixture.Pickup("a0"); point = fixture.Over("a2");
            Call(fixture.Window, "PlaceIconAtPointer", point, false, false);
            fixture.Mature("a2");
            Assert.True((bool)Call(fixture.Window, "ApplyFolderDrop", point, false)!);
            fixture.CommitDrag();
            var group = fixture.Slots.Single(s => s.Group is not null).Group!;
            Assert.Equal(["a2", "a0"], group.ItemIds);
            Assert.Equal(6, LauncherLayoutRules.AppIds(fixture.Services.Layouts.Launcher).Distinct().Count());
        });
    }

    [Fact]
    public void LeavingOverlapAndAltBothCancelFusionWithoutSaving()
    {
        StaTest.Run(() =>
        {
            using var fixture = new Fixture();
            var original = LauncherLayoutRules.DeepClone(fixture.Services.Layouts.Launcher);
            fixture.Pickup("a0");
            var point = fixture.Over("a1");
            Call(fixture.Window, "PlaceIconAtPointer", point, false, false);
            fixture.Mature("a1");
            Call(fixture.Window, "UpdateFolderHover", new Point(0, 0), false);
            Assert.False(Get<FolderHoverIntent>(fixture.Window, "_folderIntent").IsReady);
            Assert.False((bool)Call(fixture.Window, "ApplyFolderDrop", point, false)!);
            fixture.Mature("a1");
            Assert.False((bool)Call(fixture.Window, "ApplyFolderDrop", point, true)!);
            Call(fixture.Window, "FinishIconDrag");
            Assert.True(LauncherLayoutRules.Equivalent(original, fixture.Services.Layouts.Launcher));
        });
    }

    [Fact]
    public void ExistingFolderAcceptsAnotherAppAndKeepsPreviewBoundedAtNine()
    {
        StaTest.Run(() =>
        {
            using var fixture = new Fixture(12);
            fixture.MakeGroup(10);
            fixture.Pickup("a10"); var point = fixture.Over("g");
            Call(fixture.Window, "PlaceIconAtPointer", point, false, false);
            fixture.Mature("g");
            Assert.True((bool)Call(fixture.Window, "ApplyFolderDrop", point, false)!);
            fixture.CommitDrag();
            var group = fixture.Slots.Single(s => s.Group is not null).Group!;
            Assert.Equal(11, group.ItemIds.Count);
            var tile = fixture.Tile("g");
            Assert.Equal(9, Descendants(tile).OfType<UniformGrid>().Single().Children.Count);
            Assert.All(Descendants(tile).OfType<SharedGlassLens>(), lens => Assert.Same(fixture.Window.FindName("GlassSurface"), lens.Source));
        });
    }

    [Fact]
    public void FolderCanBeReorderedAsAUnitWithoutNesting()
    {
        StaTest.Run(() =>
        {
            using var fixture = new Fixture(); fixture.MakeGroup(2);
            fixture.Pickup("g"); var point = fixture.Over("a3");
            Assert.False((bool)Call(fixture.Window, "UpdateFolderHover", point, false)!);
            Call(fixture.Window, "PlaceIconAtPointer", point, false, true);
            fixture.CommitDrag();
            Assert.Single(fixture.Slots, s => s.Group is not null);
            Assert.Equal(2, fixture.Slots.Single(s => s.Group is not null).Group!.ItemIds.Count);
        });
    }

    [Fact]
    public void OpenCloseUsesOriginalIconAndRestoresHitTestingAfterReverseAnimation()
    {
        StaTest.Run(() =>
        {
            using var fixture = new Fixture(18); fixture.MakeGroup(15); fixture.OpenGroup();
            var card = (FrameworkElement)fixture.Window.FindName("FolderCard");
            var before = card.Width;
            Assert.InRange(before, 1, 100);
            Pump(650);
            Assert.True(card.Width > before * 2);
            Assert.Equal(Visibility.Visible, ((Grid)fixture.Window.FindName("GroupPanel")).Visibility);
            Assert.True(((ScrollViewer)fixture.Window.FindName("GroupScroll")).ScrollableHeight > 0);
            fixture.Capture("folder-expanded.png");
            Call(fixture.Window, "CloseFolder");
            Assert.Equal(Visibility.Visible, ((Grid)fixture.Window.FindName("GroupPanel")).Visibility);
            Pump(700);
            Assert.Equal(Visibility.Collapsed, ((Grid)fixture.Window.FindName("GroupPanel")).Visibility);
            Assert.Equal(1, fixture.Grid.Opacity);
            Assert.Null(Get<LauncherViewState>(fixture.Window, "_view").OpenGroupId);
            Assert.False(Get<bool>(fixture.Window, "_folderFramesAttached"));
            fixture.Capture("folder-collapsed.png");
        });
    }

    [Fact]
    public void MemberDragReordersAndCanLeaveFolderWithAutomaticDissolve()
    {
        StaTest.Run(() =>
        {
            using var fixture = new Fixture(); fixture.MakeGroup(3); fixture.OpenGroup(); Pump(650);
            var groupItems = (UniformGrid)fixture.Window.FindName("GroupItems");
            var source = groupItems.Children.OfType<Button>().First(t => Equals(t.Tag, "a0"));
            Call(fixture.Window, "PrepareIconDrag", source, new Point(source.ActualWidth / 2, source.ActualHeight / 2));
            var destination = groupItems.Children.OfType<Button>().Last();
            var point = destination.TranslatePoint(new Point(destination.ActualWidth / 2, destination.ActualHeight / 2), fixture.Grid);
            Assert.True((bool)Call(fixture.Window, "PreviewFolderMemberInside", point)!);
            fixture.CommitDrag();
            Assert.Equal("a0", fixture.Slots.Single(s => s.Group is not null).Group!.ItemIds.Last());

            groupItems = (UniformGrid)fixture.Window.FindName("GroupItems");
            source = groupItems.Children.OfType<Button>().First(t => Equals(t.Tag, "a1"));
            Call(fixture.Window, "PrepareIconDrag", source, new Point(source.ActualWidth / 2, source.ActualHeight / 2));
            Call(fixture.Window, "PlaceIconAtPointer", new Point(8, 8), false, true);
            fixture.CommitDrag();
            Assert.Contains(fixture.Slots, s => s.ItemId == "a1");
            Assert.Equal(2, fixture.Slots.Single(s => s.Group is not null).Group!.ItemIds.Count);
            Pump(700); fixture.OpenGroup(); Pump(650);
            groupItems = (UniformGrid)fixture.Window.FindName("GroupItems");
            source = groupItems.Children.OfType<Button>().First();
            Call(fixture.Window, "PrepareIconDrag", source, new Point(source.ActualWidth / 2, source.ActualHeight / 2));
            Call(fixture.Window, "PlaceIconAtPointer", new Point(8, 8), false, true);
            fixture.CommitDrag();
            Assert.DoesNotContain(fixture.Slots, s => s.Group is not null);
            Assert.Equal(6, LauncherLayoutRules.AppIds(fixture.Services.Layouts.Launcher).Distinct().Count());
        });
    }

    private static object? Call(object target, string name, params object[] arguments)
        => target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, arguments);
    private static T Get<T>(object target, string name)
        => (T)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => frame.Continue = false;
        timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "GlassDesk-folder-ui-" + Guid.NewGuid().ToString("N")));
        public LauncherWindow Window { get; }
        public AppServiceHost Services { get; }
        public UniformGrid Grid => (UniformGrid)Window.FindName("TileGrid");
        public List<LauncherSlot> Slots => Services.Layouts.Launcher.Pages.SelectMany(p => p.Slots).ToList();
        public Fixture(int count = 6)
        {
            var data = new GlassDeskDataStore(new AppPaths(_root)); data.LoadAll();
            for (var i = 0; i < count; i++)
            {
                var icon = Path.Combine(_root, $"custom-{i}.png");
                File.WriteAllBytes(icon, GlassDesk.Core.Imaging.PngWriter.EncodeBgra([(byte)(60 + i * 9), (byte)(140 + i * 3), (byte)(220 - i * 7), 255], 1, 1));
                data.Library.Add(new AppEntry { Id = "a" + i, DisplayName = "应用 " + (i + 1), Target = Path.Combine(_root, "missing-" + i), IsTargetMissing = true, CustomIconPath = icon });
            }
            Services = new AppServiceHost(data);
            Assert.True(Services.Layouts.CommitLauncher(
                LauncherLayoutEditor.AddShortcuts(Services.Layouts.Launcher, Enumerable.Range(0, count).Select(i => "a" + i), 35), Services.Layouts.LauncherVersion));
            Window = new LauncherWindow { ShowActivated = false };
            ((LiquidGlassSurface)Window.FindName("GlassSurface")).SetPerformanceSleeping(true);
            Window.AttachServices(Services, true); Window.Show(); Window.UpdateLayout();
        }
        public Button Tile(string id) => Grid.Children.OfType<Button>().First(t => Equals(t.Tag, id));
        public void Pickup(string id)
        {
            var tile = Tile(id);
            Call(Window, "PrepareIconDrag", tile, new Point(tile.ActualWidth / 2, tile.ActualHeight / 2));
            Window.UpdateLayout();
        }
        public Point Over(string id)
        {
            var tile = Tile(id);
            var host = (FrameworkElement)tile.GetType().GetProperty("IconHost", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(tile)!;
            var center = host.TranslatePoint(new Point(host.ActualWidth / 2, host.ActualHeight / 2), Grid);
            var grab = Get<Point>(Window, "_grabPoint"); var offset = Get<Point>(Window, "_ghostIconCenterOffset");
            return new Point(center.X + grab.X - offset.X, center.Y + grab.Y - offset.Y);
        }
        public void Mature(string id)
        {
            var intent = Get<FolderHoverIntent>(Window, "_folderIntent");
            intent.Reset(); intent.Update(id, true, Stopwatch.GetElapsedTime(0, Stopwatch.GetTimestamp()) - TimeSpan.FromMilliseconds(550));
        }
        public void CommitDrag()
        {
            var edit = Get<LauncherEditSession>(Window, "_dragEdit");
            Assert.True(new LauncherWorkspace(Services).Commit(edit));
            typeof(LauncherWindow).GetField("_dropCommitted", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Window, true);
            Call(Window, "FinishIconDrag"); Window.UpdateLayout();
        }
        public void MakeGroup(int count)
        {
            var edit = new LauncherWorkspace(Services).BeginEdit();
            edit.Draft = LauncherLayoutEditor.CreateGroup(edit.Draft, "a1", "a0", "g", 35);
            for (var i = 2; i < count; i++) edit.Draft = LauncherLayoutEditor.AddToGroup(edit.Draft, "a" + i, "g", 35);
            Assert.True(new LauncherWorkspace(Services).Commit(edit)); Window.RefreshLibrary(); Window.UpdateLayout();
        }
        public void OpenGroup() { Call(Window, "OnTileActivated", Slots.Single(s => s.Group is not null)); Window.UpdateLayout(); }
        public void Capture(string name)
        {
            var output = Environment.GetEnvironmentVariable("GLASSDESK_FOLDER_EVIDENCE");
            if (string.IsNullOrWhiteSpace(output)) return;
            Directory.CreateDirectory(output);
            var bitmap = new RenderTargetBitmap((int)Window.ActualWidth, (int)Window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(Window);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(output, name)); png.Save(stream);
        }
        public void Dispose()
        {
            Window.Close(); Services.Dispose();
            if (Path.GetDirectoryName(_root) != Path.TrimEndingDirectorySeparator(Path.GetTempPath())) throw new IOException("Unexpected cleanup boundary.");
            Directory.Delete(_root, true);
        }
    }
}
