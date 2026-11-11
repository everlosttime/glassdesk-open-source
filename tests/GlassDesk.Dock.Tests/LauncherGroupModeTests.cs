using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GlassDesk.AppServices;
using GlassDesk.Core.Models;
using GlassDesk.Core.Models.Settings;
using GlassDesk.Core.Storage;
using GlassDesk.Launcher;
using GlassDesk.Settings;
using GlassDesk.UI.Controls;
using GlassDesk.UI.Theming;

namespace GlassDesk.Dock.Tests;

public sealed class LauncherGroupModeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TileMenuAddsToAnotherFilterWithoutMovingTheIcon(bool searching)
    {
        StaTest.Run(() =>
        {
            using var f = new Fixture();
            AddSortingGroups(f);
            Call(f.Window, "ToggleFilterGroup", "work");
            if (searching)
                ((TextBox)f.Window.FindName("SearchBox")).Text = "应用 1";
            var tile = f.Grid.Children.OfType<Button>().Single(b => Equals(b.Tag, "a0"));
            var menu = tile.ContextMenu!;
            var order = f.Host.Layouts.Launcher.FlatOrder.ToArray();
            menu.Resources.MergedDictionaries.Add(f.Window.Resources.MergedDictionaries[0]);
            menu.PlacementTarget = tile;
            menu.IsOpen = true;
            Pump(50);
            var add = Assert.Single(menu.Items.OfType<MenuItem>(), item => Equals(item.Header, "添加到已有分组"));
            Assert.Equal(new[] { "游戏", "媒体" }, add.Items.OfType<MenuItem>().Select(item => (string)item.Header));
            add.IsSubmenuOpen = true;
            Pump(50);
            Assert.True(Assert.IsType<Popup>(add.Template.FindName("PART_Popup", add)).IsOpen);
            var target = Assert.IsType<MenuItem>(add.Items[0]);
            Assert.True(target.IsVisible);
            target.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            menu.IsOpen = false;

            var reloaded = new GlassDeskDataStore(f.Data.Paths); reloaded.LoadAll();
            Assert.Equal(new[] { "a0", "a3" }, reloaded.Layouts.Launcher.FilterGroups.Single(group => group.Id == "work").ItemIds);
            Assert.Equal(new[] { "a5", "a6", "a0" }, reloaded.Layouts.Launcher.FilterGroups.Single(group => group.Id == "games").ItemIds);
            Assert.Equal(order, reloaded.Layouts.Launcher.FlatOrder);
            Assert.Equal(new[] { "a0", "a1" }, f.Folder.ItemIds);
            Assert.Equal("work", Assert.Single(GroupChips(f), chip => chip.IsChecked == true).Tag);
            Assert.Contains("a0", f.VisibleIds());

            menu = f.Grid.Children.OfType<Button>().Single(b => Equals(b.Tag, "a0")).ContextMenu!;
            menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            add = Assert.Single(menu.Items.OfType<MenuItem>(), item => Equals(item.Header, "添加到已有分组"));
            Assert.Equal("媒体", Assert.Single(add.Items.OfType<MenuItem>()).Header);
            menu.RaiseEvent(new RoutedEventArgs(ContextMenu.ClosedEvent));
        });
    }

    [Fact]
    public void GroupButtonDragSavesOnlyOnDropAndRetainsSelectionAndMembers()
    {
        StaTest.Run(() =>
        {
            using var f = new Fixture();
            AddSortingGroups(f);
            Call(f.Window, "ToggleFilterGroup", "work");
            var bar = (ScrollViewer)f.Window.FindName("FilterGroupBar");
            var chips = GroupChips(f);
            var start = chips[2].TranslatePoint(new Point(10, 10), bar);
            var end = chips[0].TranslatePoint(new Point(1, 10), bar);
            var before = File.ReadAllBytes(f.Data.Paths.LauncherLayoutFile);
            Call(f.Window, "BeginFilterGroupPress", chips[2], start);
            Call(f.Window, "UpdateFilterGroupDrag", end);
            Assert.Equal(before, File.ReadAllBytes(f.Data.Paths.LauncherLayoutFile));
            Assert.Equal(new[] { "media", "work", "games" }, GroupChips(f).Select(c => (string)c.Tag));
            Assert.Equal(new[] { "work", "games", "media" }, f.Host.Layouts.Launcher.FilterGroups.Select(g => g.Id));
            Assert.Equal(0, chips[2].Opacity);
            Assert.Equal("媒体", Assert.Single(((Canvas)f.Window.FindName("DragOverlay")).Children.OfType<ToggleButton>()).Content);
            Call(f.Window, "CompleteFilterGroupPress", end);
            Assert.Equal(new[] { "media", "work", "games" }, GroupChips(f).Select(c => (string)c.Tag));
            Assert.Equal("work", Assert.Single(GroupChips(f), c => c.IsChecked == true).Tag);
            Assert.Equal(new[] { "a0", "a3" }, f.VisibleIds());
            var reloaded = new GlassDeskDataStore(f.Data.Paths); reloaded.LoadAll();
            Assert.Equal(new[] { "media", "work", "games" }, reloaded.Layouts.Launcher.FilterGroups.Select(g => g.Id));
            Assert.Equal(new[] { "a0", "a3" }, reloaded.Layouts.Launcher.FilterGroups[1].ItemIds);
            Assert.Equal(new[] { "a0", "a1" }, reloaded.Layouts.Launcher.Pages[0].Slots[0].Group!.ItemIds);
            Assert.Null(Mouse.Captured);
            Assert.All(GroupChips(f), c => Assert.Equal(1, c.Opacity));
            Assert.Empty(((Canvas)f.Window.FindName("DragOverlay")).Children.OfType<ToggleButton>());
            GroupChips(f)[0].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal(new[] { "a7" }, f.VisibleIds());
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GroupButtonDragCanInsertInTheMiddleOrAfterTheLastButton(bool toEnd)
    {
        StaTest.Run(() =>
        {
            using var f = new Fixture(); AddSortingGroups(f);
            var bar = (ScrollViewer)f.Window.FindName("FilterGroupBar");
            var chips = GroupChips(f);
            var target = chips[toEnd ? 2 : 1];
            var end = target.TranslatePoint(new Point(target.ActualWidth * .75, 10), bar);
            Call(f.Window, "BeginFilterGroupPress", chips[0], chips[0].TranslatePoint(new Point(chips[0].ActualWidth / 2, 10), bar));
            Call(f.Window, "UpdateFilterGroupDrag", end);
            Call(f.Window, "CompleteFilterGroupPress", end);
            Assert.Equal(toEnd ? new[] { "games", "media", "work" } : new[] { "games", "work", "media" },
                f.Host.Layouts.Launcher.FilterGroups.Select(g => g.Id));
        });
    }

    [Fact]
    public void SlightGroupButtonMovementStillClicksWithoutSavingLayout()
    {
        StaTest.Run(() =>
        {
            using var f = new Fixture(); AddSortingGroups(f);
            var chip = GroupChips(f)[0];
            var bar = (ScrollViewer)f.Window.FindName("FilterGroupBar");
            var point = chip.TranslatePoint(new Point(10, 10), bar);
            var version = f.Host.Layouts.LauncherVersion;
            Assert.True((bool)Call(f.Window, "BeginFilterGroupPress", chip, point)!,
                $"capture={Mouse.Captured}, visible={f.Window.IsVisible}, loaded={f.Window.IsLoaded}, bar={bar.RenderSize}, chip={chip.RenderSize}");
            Call(f.Window, "UpdateFilterGroupDrag", new Point(point.X + 1, point.Y));
            Call(f.Window, "CompleteFilterGroupPress", new Point(point.X + 1, point.Y));
            Assert.Equal(new[] { "a0", "a3" }, f.VisibleIds());
            Assert.Equal(version, f.Host.Layouts.LauncherVersion);
            Assert.Null(Mouse.Captured);
        });
    }

    [Theory]
    [InlineData("escape")]
    [InlineData("capture")]
    [InlineData("hide")]
    [InlineData("settings")]
    [InlineData("outside")]
    public void InterruptedGroupButtonDragRestoresOrderAndReleasesInput(string interruption)
    {
        StaTest.Run(() =>
        {
            using var f = new Fixture(); AddSortingGroups(f);
            var bar = (ScrollViewer)f.Window.FindName("FilterGroupBar");
            var chips = GroupChips(f);
            var end = chips[0].TranslatePoint(new Point(1, 10), bar);
            var before = File.ReadAllBytes(f.Data.Paths.LauncherLayoutFile);
            Call(f.Window, "BeginFilterGroupPress", chips[2], chips[2].TranslatePoint(new Point(10, 10), bar));
            Call(f.Window, "UpdateFilterGroupDrag", end);
            switch (interruption)
            {
                case "escape":
                    var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(f.Window)!, 0, Key.Escape)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                    f.Window.RaiseEvent(key);
                    Assert.True(key.Handled);
                    break;
                case "capture": Mouse.Capture(null); break;
                case "hide": f.Window.Hide(); break;
                case "settings": f.Window.ApplySettings(new() { GroupModeEnabled = false }); break;
                case "outside": Call(f.Window, "CompleteFilterGroupPress", new Point(-500, -20)); break;
            }
            Assert.Equal(before, File.ReadAllBytes(f.Data.Paths.LauncherLayoutFile));
            Assert.Equal(new[] { "work", "games", "media" }, f.Host.Layouts.Launcher.FilterGroups.Select(g => g.Id));
            Assert.Null(Mouse.Captured);
            Assert.All(chips, c => Assert.Equal(1, c.Opacity));
            Assert.Equal(new[] { "work", "games", "media" }, GroupChips(f).Select(c => (string)c.Tag));
            Assert.Empty(((Canvas)f.Window.FindName("DragOverlay")).Children.OfType<ToggleButton>());
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GroupButtonDragCannotOverwriteConcurrentEditsOrFailedSaves(bool failedSave)
    {
        StaTest.Run(() =>
        {
            using var f = new Fixture(); AddSortingGroups(f);
            var bar = (ScrollViewer)f.Window.FindName("FilterGroupBar");
            var chips = GroupChips(f);
            var end = chips[0].TranslatePoint(new Point(1, 10), bar);
            Call(f.Window, "BeginFilterGroupPress", chips[2], chips[2].TranslatePoint(new Point(10, 10), bar));
            Call(f.Window, "UpdateFilterGroupDrag", end);
            if (!failedSave)
                Assert.True(f.Host.Layouts.CommitLauncher(LauncherFilterRules.RenameGroup(f.Host.Layouts.Launcher, "work", "已更新"), f.Host.Layouts.LauncherVersion));
            var before = File.ReadAllBytes(f.Data.Paths.LauncherLayoutFile);
            using (var blocker = failedSave ? new FileStream(f.Data.Paths.LauncherLayoutFile + AtomicFile.TempSuffix,
                FileMode.Create, FileAccess.ReadWrite, FileShare.None) : null)
                Call(f.Window, "CompleteFilterGroupPress", end);
            Assert.Equal(before, File.ReadAllBytes(f.Data.Paths.LauncherLayoutFile));
            Assert.Equal(new[] { "work", "games", "media" }, f.Host.Layouts.Launcher.FilterGroups.Select(g => g.Id));
            Assert.Equal(failedSave ? "工作" : "已更新", f.Host.Layouts.Launcher.FilterGroups[0].Name);
            Assert.False(string.IsNullOrWhiteSpace(Get<string?>(f.Window, "_notice")));
            Assert.Null(Mouse.Captured);
        });
    }

    [Fact]
    public void GroupButtonGhostKeepsGrabPointAndExistingNeighboursSlideWithoutJumping()
    {
        StaTest.Run(() =>
        {
            using var f = new Fixture(animate: true); AddSortingGroups(f);
            var bar = (ScrollViewer)f.Window.FindName("FilterGroupBar");
            var panel = (StackPanel)f.Window.FindName("FilterGroupButtons");
            var overlay = (Canvas)f.Window.FindName("DragOverlay");
            var chips = GroupChips(f);
            var transforms = chips.Select(c => c.RenderTransform).ToArray();
            var before = chips[0].TranslatePoint(new Point(), bar).X;
            var end = chips[0].TranslatePoint(new Point(1, 10), bar);
            Call(f.Window, "BeginFilterGroupPress", chips[2], chips[2].TranslatePoint(new Point(10, 10), bar));
            Call(f.Window, "UpdateFilterGroupDrag", end);
            Assert.Equal(new[] { "media", "work", "games" }, GroupChips(f).Select(c => (string)c.Tag));
            Assert.Equal(before, chips[0].TranslatePoint(new Point(), bar).X, 3);
            var target = panel.TranslatePoint(new Point(LayoutInformation.GetLayoutSlot(chips[0]).X, 0), bar).X;
            Call(f.Window, "AdvanceFilterGroupAnimations", .016);
            var halfway = chips[0].TranslatePoint(new Point(), bar).X;
            Assert.True(halfway > before && halfway < target, $"before={before}, halfway={halfway}, target={target}");
            for (var i = 0; i < 80; i++) Call(f.Window, "AdvanceFilterGroupAnimations", .016);
            Assert.Equal(target, chips[0].TranslatePoint(new Point(), bar).X, 2);
            var ghost = Assert.Single(overlay.Children.OfType<ToggleButton>());
            Assert.Equal("媒体", ghost.Content);
            var motion = Get<TranslateTransform>(f.Window, "_filterGhostMotion");
            var expected = bar.TranslatePoint(end, overlay);
            Assert.Equal(expected.X - 10, motion.X, 3);
            Assert.Equal(expected.Y - 10, motion.Y, 3);
            Call(f.Window, "UpdateFilterGroupDrag", new Point(end.X + 12, end.Y + 3));
            Assert.Equal(expected.X + 2, motion.X, 3);
            Assert.Equal(expected.Y - 10, motion.Y, 3);
            Call(f.Window, "CancelDrag");
            Assert.Empty(overlay.Children.OfType<ToggleButton>());
            for (var i = 0; i < chips.Length; i++) Assert.Same(transforms[i], chips[i].RenderTransform);
            Assert.Equal(new[] { "work", "games", "media" }, GroupChips(f).Select(c => (string)c.Tag));
        });
    }

    [Theory]
    [InlineData(-120)]
    [InlineData(120)]
    public void GroupButtonIgnoresVerticalMovementAndKeepsGhostOnItsOriginalRow(double verticalDelta)
    {
        StaTest.Run(() =>
        {
            using var f = new Fixture(); AddSortingGroups(f);
            var bar = (ScrollViewer)f.Window.FindName("FilterGroupBar");
            var overlay = (Canvas)f.Window.FindName("DragOverlay");
            var chip = GroupChips(f)[1];
            var start = chip.TranslatePoint(new Point(10, 10), bar);
            var rowY = chip.TranslatePoint(new Point(), overlay).Y;
            Call(f.Window, "BeginFilterGroupPress", chip, start);
            Call(f.Window, "UpdateFilterGroupDrag", new Point(start.X, start.Y + verticalDelta));
            Assert.Empty(overlay.Children.OfType<ToggleButton>());
            Assert.False(Get<bool>(f.Window, "_filterGroupDragging"));
            Call(f.Window, "UpdateFilterGroupDrag", new Point(start.X + 20, start.Y + verticalDelta));
            Assert.Single(overlay.Children.OfType<ToggleButton>());
            var motion = Get<TranslateTransform>(f.Window, "_filterGhostMotion");
            Assert.Equal(rowY, motion.Y, 3);
            Call(f.Window, "UpdateFilterGroupDrag", new Point(start.X + 35, start.Y - verticalDelta));
            Assert.Equal(rowY, motion.Y, 3);
            Call(f.Window, "CancelDrag");
        });
    }

    [Fact]
    public void LiveGroupPreviewCanReturnToItsOriginalSlotWithoutSaving()
    {
        StaTest.Run(() =>
        {
            using var f = new Fixture(); AddSortingGroups(f);
            var bar = (ScrollViewer)f.Window.FindName("FilterGroupBar");
            var chips = GroupChips(f);
            var original = File.ReadAllBytes(f.Data.Paths.LauncherLayoutFile);
            var version = f.Host.Layouts.LauncherVersion;
            Call(f.Window, "BeginFilterGroupPress", chips[2], chips[2].TranslatePoint(new Point(chips[2].ActualWidth / 2, 10), bar));
            var left = new Point(1, 10);
            Call(f.Window, "UpdateFilterGroupDrag", left);
            Assert.Equal(new[] { "media", "work", "games" }, GroupChips(f).Select(c => (string)c.Tag));
            var right = new Point(bar.ActualWidth - 2, 10);
            Call(f.Window, "UpdateFilterGroupDrag", right);
            Assert.Equal(new[] { "work", "games", "media" }, GroupChips(f).Select(c => (string)c.Tag));
            Call(f.Window, "CompleteFilterGroupPress", right);
            Assert.Equal(original, File.ReadAllBytes(f.Data.Paths.LauncherLayoutFile));
            Assert.Equal(version, f.Host.Layouts.LauncherVersion);
        });
    }

    [Fact]
    public void UnequalWidthGroupButtonsDoNotOscillateUnderAStationaryPointer()
    {
        StaTest.Run(() =>
        {
            using var f = new Fixture(); AddSortingGroups(f);
            Call(f.Window, "CommitFilterEdit", (Func<LauncherLayout, LauncherLayout>)(layout =>
                LauncherFilterRules.RenameGroup(layout, "work", "这是一个明显更长的工作分组名称")));
            f.Window.UpdateLayout();
            var chips = GroupChips(f);
            var bar = (ScrollViewer)f.Window.FindName("FilterGroupBar");
            var point = chips[0].TranslatePoint(new Point(chips[0].ActualWidth * .55, 10), bar);
            var original = File.ReadAllBytes(f.Data.Paths.LauncherLayoutFile);
            Call(f.Window, "BeginFilterGroupPress", chips[2], chips[2].TranslatePoint(new Point(chips[2].ActualWidth / 2, 10), bar));
            for (var i = 0; i < 12; i++)
            {
                Call(f.Window, "UpdateFilterGroupDrag", point);
                Assert.Equal(new[] { "work", "media", "games" }, GroupChips(f).Select(c => (string)c.Tag));
            }
            Assert.Equal(original, File.ReadAllBytes(f.Data.Paths.LauncherLayoutFile));
            Call(f.Window, "CancelDrag");
        });
    }

    [Theory]
    [InlineData(0, -80, .1)]
    [InlineData(0, 80, .9)]
    [InlineData(2, -80, .1)]
    [InlineData(2, 80, .9)]
    public void HorizontalGroupPreviewAndDropUseTheFloatingButtonWhenPointerLeavesTheRow(int sourceIndex, double yOffset, double grabFraction)
    {
        StaTest.Run(() =>
        {
            using var f = new Fixture(); AddSortingGroups(f);
            Call(f.Window, "CommitFilterEdit", (Func<LauncherLayout, LauncherLayout>)(layout =>
                LauncherFilterRules.RenameGroup(layout, "work", "这是一个明显更长的工作分组名称")));
            f.Window.UpdateLayout();
            var chips = GroupChips(f);
            var source = chips[sourceIndex];
            var others = chips.Where(chip => chip != source).ToArray();
            var bar = (ScrollViewer)f.Window.FindName("FilterGroupBar");
            var overlay = (Canvas)f.Window.FindName("DragOverlay");
            var gap = (others[0].TranslatePoint(new Point(others[0].ActualWidth, 0), bar).X
                + others[1].TranslatePoint(new Point(), bar).X) / 2;
            var grabX = source.ActualWidth * grabFraction;
            var start = source.TranslatePoint(new Point(grabX, 10), bar);
            var rowY = source.TranslatePoint(new Point(), overlay).Y;
            var destination = new Point(gap + grabX - source.ActualWidth / 2, start.Y + yOffset);
            var before = File.ReadAllBytes(f.Data.Paths.LauncherLayoutFile);
            Call(f.Window, "BeginFilterGroupPress", source, start);
            Call(f.Window, "UpdateFilterGroupDrag", destination);
            var expected = sourceIndex == 0 ? new[] { "games", "work", "media" } : new[] { "work", "media", "games" };
            Assert.Equal(expected, GroupChips(f).Select(chip => (string)chip.Tag));
            Assert.Equal(rowY, Get<TranslateTransform>(f.Window, "_filterGhostMotion").Y, 3);
            Assert.Equal(before, File.ReadAllBytes(f.Data.Paths.LauncherLayoutFile));
            Call(f.Window, "CompleteFilterGroupPress", destination);
            var reload = new GlassDeskDataStore(f.Data.Paths); reload.LoadAll();
            Assert.Equal(expected, reload.Layouts.Launcher.FilterGroups.Select(group => group.Id));
            Assert.Null(Mouse.Captured);
        });
    }

    [Fact]
    public void GroupButtonDragScrollsAtBothEdgesRegardlessOfPointerHeightAndStopsOutsideHorizontally()
    {
        StaTest.Run(() =>
        {
            using var f = new Fixture();
            Call(f.Window, "CommitFilterEdit", (Func<LauncherLayout, LauncherLayout>)(layout =>
            {
                for (var i = 0; i < 20; i++)
                    layout.FilterGroups.Add(new() { Id = "g" + i, Name = "用于滚动排序的分组" + i, ItemIds = ["a7"] });
                return layout;
            }));
            f.Window.UpdateLayout();
            var bar = (ScrollViewer)f.Window.FindName("FilterGroupBar");
            var chip = GroupChips(f)[0];
            Call(f.Window, "BeginFilterGroupPress", chip, chip.TranslatePoint(new Point(10, 10), bar));
            var right = new Point(bar.ActualWidth - 2, 10);
            Call(f.Window, "UpdateFilterGroupDrag", right);
            for (var i = 0; i < 8; i++) Call(f.Window, "UpdateFilterGroupAutoScroll", right);
            f.Window.UpdateLayout();
            Assert.True(bar.HorizontalOffset > 0);
            var offset = bar.HorizontalOffset;
            Call(f.Window, "UpdateFilterGroupAutoScroll", new Point(-500, -20));
            f.Window.UpdateLayout(); Assert.Equal(offset, bar.HorizontalOffset);
            Call(f.Window, "UpdateFilterGroupAutoScroll", new Point(right.X, -80));
            f.Window.UpdateLayout(); Assert.True(bar.HorizontalOffset > offset);
            offset = bar.HorizontalOffset;
            for (var i = 0; i < 8; i++) Call(f.Window, "UpdateFilterGroupAutoScroll", new Point(2, 10));
            f.Window.UpdateLayout(); Assert.True(bar.HorizontalOffset < offset);
            Call(f.Window, "CancelDrag");
            Assert.Null(Mouse.Captured);
        });
    }

    private static ToggleButton[] GroupChips(Fixture f)
        => ((StackPanel)f.Window.FindName("FilterGroupButtons")).Children.OfType<ToggleButton>().ToArray();

    private static void AddSortingGroups(Fixture f)
    {
        Call(f.Window, "CommitFilterEdit", (Func<LauncherLayout, LauncherLayout>)(layout =>
            LauncherFilterRules.AddGroup(LauncherFilterRules.AddGroup(layout, "games", "游戏", ["a5", "a6"]), "media", "媒体", ["a7"])));
        f.Window.UpdateLayout();
    }

    [Fact]
    public void RenderingClockCompletesSwitchAndRestoresOriginalTileTransforms()
    {
        StaTest.Run(() =>
        {
            using var f = new Fixture(animate: true);
            Call(f.Window, "CommitFilterEdit", (Func<LauncherLayout, LauncherLayout>)(layout =>
                LauncherFilterRules.AddGroup(layout, "games", "游戏", new[] { "a5", "a6" })));
            Call(f.Window, "ToggleFilterGroup", "work");
            Pump(450);
            Assert.True(f.Window.CanNavigatePages);
            Assert.All(f.Grid.Children.OfType<Button>(), tile => Assert.IsType<ScaleTransform>(tile.RenderTransform));
            Call(f.Window, "ToggleFilterGroup", "games");
            var selectedChip = Assert.Single(((StackPanel)f.Window.FindName("FilterGroupButtons")).Children.OfType<ToggleButton>(), chip => chip.IsChecked == true);
            Assert.Equal("games", selectedChip.Tag);
            Pump(750);
            Assert.Equal(new[] { "a5", "a6" }, f.VisibleIds());
            Assert.True(f.Window.CanNavigatePages);
            Call(f.Window, "ToggleFilterGroup", "games");
            Pump(450);
            Assert.Equal(8, f.VisibleIds().Length);
            Assert.True(f.Window.CanNavigatePages);
            Assert.All(f.Grid.Children.OfType<Button>(), tile =>
            { Assert.Equal(1, tile.Opacity); Assert.IsType<ScaleTransform>(tile.RenderTransform); });
        });
    }

    [Fact]
    public void GroupRowsRevealTopDownAndCollapseBottomUpBeforeRestoringAll()
    {
        StaTest.Run(() =>
        {
            using var f = new Fixture(animate: true);
            f.Window.ApplySettings(new() { GroupModeEnabled = true, Rows = 4, Columns = 2 });
            Call(f.Window, "CommitFilterEdit", (Func<LauncherLayout, LauncherLayout>)(layout =>
                LauncherFilterRules.UpdateGroupMembers(layout, "work", new[] { "a0", "a1", "a2", "a3", "a4", "a5" })));
            Call(f.Window, "ToggleFilterGroup", "work");
            Assert.False(f.Window.CanNavigatePages);
            Assert.All(f.Grid.Children.OfType<Button>(), tile => Assert.Equal(0, tile.Opacity));
            Call(f.Window, "UpdateFilterTransition", 70d);
            var tiles = f.Grid.Children.OfType<Button>().ToArray();
            Assert.True(tiles[0].Opacity > tiles[2].Opacity);
            Assert.True(tiles[2].Opacity > tiles[4].Opacity);
            f.Capture("group-mode-enter-70ms.png");
            Call(f.Window, "UpdateFilterTransition", 1000d);
            Assert.All(tiles, tile => Assert.Equal(1, tile.Opacity));
            Assert.True(f.Window.CanNavigatePages);

            Call(f.Window, "ToggleFilterGroup", "work");
            Assert.Equal(6, f.VisibleIds().Length);
            Call(f.Window, "UpdateFilterTransition", 70d);
            Assert.True(tiles[4].Opacity < tiles[2].Opacity);
            Assert.True(tiles[2].Opacity < tiles[0].Opacity);
            f.Capture("group-mode-exit-70ms.png");
            Call(f.Window, "UpdateFilterTransition", 1000d);
            Assert.Equal(8, f.VisibleIds().Length);
            Assert.All(f.Grid.Children.OfType<Button>(), tile => Assert.Equal(1, tile.Opacity));
            Assert.True(f.Window.CanNavigatePages);
            Assert.True(((ScrollViewer)f.Window.FindName("PageScroll")).IsHitTestVisible);
        });
    }

    [Fact]
    public void RapidRetargetAndSearchOrHideCannotLeaveGhostTransformsOrBlockedInput()
    {
        StaTest.Run(() =>
        {
            using var f = new Fixture(animate: true);
            Call(f.Window, "CommitFilterEdit", (Func<LauncherLayout, LauncherLayout>)(layout =>
                LauncherFilterRules.AddGroup(layout, "games", "游戏", new[] { "a5", "a6" })));
            Call(f.Window, "ToggleFilterGroup", "work");
            Call(f.Window, "UpdateFilterTransition", 70d);
            Call(f.Window, "ToggleFilterGroup", "games");
            Call(f.Window, "ToggleFilterGroup", "work");
            ((TextBox)f.Window.FindName("SearchBox")).Text = "应用";
            Assert.Equal(new[] { "a0", "a3" }, f.VisibleIds());
            Assert.All(f.Grid.Children.OfType<Button>(), tile => Assert.Equal(1, tile.Opacity));
            Assert.True(f.Window.CanNavigatePages);
            Call(f.Window, "ToggleFilterGroup", "games");
            f.Window.Hide();
            Assert.All(f.Grid.Children.OfType<Button>(), tile => Assert.Equal(1, tile.Opacity));
            Assert.False(Get<bool>(f.Window, "_filterTransitionActive"));
            f.Window.Show(); f.Window.UpdateLayout();
            Assert.Equal(new[] { "a5", "a6" }, f.VisibleIds());
            Assert.True(f.Window.CanNavigatePages);
        });
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void EditingGroupPrechecksMembersAndCanSaveOrCancelChanges(bool save, bool clearAll)
    {
        StaTest.Run(() =>
        {
            using var f = new Fixture();
            Exception? failure = null;
            var edited = false;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            timer.Tick += (_, _) =>
            {
                var dialog = f.Window.OwnedWindows.Cast<Window>().FirstOrDefault(w => w.IsVisible);
                if (dialog is null) return;
                timer.Stop();
                try
                {
                    Assert.False(f.Window.CanNavigatePages);
                    var choices = Descendants(dialog).OfType<CheckBox>().ToList();
                    Assert.Equal(8, choices.Count);
                    Assert.Equal(new[] { "a0", "a3" }, choices.Where(c => c.IsChecked == true).Select(c => (string)c.Tag));
                    foreach (var choice in choices) choice.IsChecked = false;
                    if (!clearAll)
                    {
                        choices.Single(c => Equals(c.Tag, "a1")).IsChecked = true;
                        choices.Single(c => Equals(c.Tag, "a7")).IsChecked = true;
                        Descendants(dialog).OfType<TextBox>().Single().Text = "应用 8";
                    }
                    var accept = Descendants(dialog).OfType<Button>().Single(b => Equals(b.Content, "保存更改"));
                    Assert.True(accept.IsEnabled);
                    AssertMenuBackground(f.Window, dialog);
                    edited = true;
                    if (save) accept.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    else dialog.DialogResult = false;
                }
                catch (Exception ex) { failure = ex; dialog.DialogResult = false; }
            };
            timer.Start();
            try { Call(f.Window, "EditFilterGroup", "work"); }
            finally { timer.Stop(); }
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            Assert.True(edited);
            Assert.Equal(!save ? new[] { "a0", "a3" } : clearAll ? Array.Empty<string>() : new[] { "a1", "a7" }, f.Host.Layouts.Launcher.FilterGroups.Single().ItemIds);
            Assert.Equal(8, LauncherLayoutRules.AppIds(f.Host.Layouts.Launcher).Count);
            Assert.Equal(new[] { "a0", "a1" }, f.Folder.ItemIds);
            var persisted = new GlassDeskDataStore(f.Data.Paths); persisted.LoadAll();
            Assert.Equal(f.Host.Layouts.Launcher.FilterGroups.Single().ItemIds, persisted.Layouts.Launcher.FilterGroups.Single().ItemIds);
        });
    }

    [Fact]
    public void CrossPageDragStaysOnDestinationPageAfterCommitAndFilterResetsPage()
    {
        StaTest.Run(() =>
        {
            using var f = new Fixture();
            f.Window.ApplySettings(new() { GroupModeEnabled = true, Rows = 2, Columns = 2 });
            f.Window.UpdateLayout();
            var source = f.Grid.Children.OfType<Button>().Single(b => Equals(b.Tag, "a0"));
            Call(f.Window, "PrepareIconDrag", source, new Point(source.ActualWidth / 2, source.ActualHeight / 2));
            Call(f.Window, "NavigateDragPage", 1);
            Call(f.Window, "StopPageSlide"); f.Window.UpdateLayout();
            Call(f.Window, "PlaceIconAtPointer", new Point(f.Grid.ActualWidth / 4, f.Grid.ActualHeight / 4), false, true);
            Assert.True(new LauncherWorkspace(f.Host).Commit(Get<LauncherEditSession>(f.Window, "_dragEdit")));
            typeof(LauncherWindow).GetField("_dropCommitted", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(f.Window, true);
            Call(f.Window, "FinishIconDrag"); f.Window.UpdateLayout();
            Assert.Equal(1, Get<LauncherViewState>(f.Window, "_view").Page);
            Assert.Equal("a0", f.VisibleIds()[0]);
            Assert.Equal(new[] { "a0", "a1" }, f.Folder.ItemIds);
            Call(f.Window, "ToggleFilterGroup", "work");
            Assert.Equal(0, Get<LauncherViewState>(f.Window, "_view").Page);
            Assert.Equal(2, f.VisibleIds().Length);
        });
    }

    [Fact]
    public void AddFlowNamesFirstThenOffersAllIconsAndKeepsSelectionWhenFiltering()
    {
        StaTest.Run(() =>
        {
            using var f = new Fixture();
            // Start with an active filter to prove the picker still offers the full launcher.
            Call(f.Window, "ToggleFilterGroup", "work");
            var stage = 0;
            Exception? failure = null;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            timer.Tick += (_, _) =>
            {
                var dialog = f.Window.OwnedWindows.Cast<Window>().FirstOrDefault(w => w.IsVisible);
                if (dialog is null) return;
                try
                {
                    Assert.False(f.Window.CanNavigatePages);
                    if (stage == 0 && dialog.Title == "添加分组")
                    {
                        AssertMenuBackground(f.Window, dialog);
                        var input = Descendants(dialog).OfType<TextBox>().Single();
                        input.Text = "创作";
                        f.CaptureDialog(dialog, "group-mode-name.png");
                        stage++;
                        Descendants(dialog).OfType<Button>().Single(b => Equals(b.Content, "保存")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    }
                    else if (stage == 1 && dialog.Title == "选择分组图标")
                    {
                        AssertMenuBackground(f.Window, dialog);
                        var choices = Descendants(dialog).OfType<CheckBox>().ToList();
                        Assert.Equal(8, choices.Count);
                        var create = Descendants(dialog).OfType<Button>().Single(b => Equals(b.Content, "创建分组"));
                        Assert.False(create.IsEnabled);
                        choices.Single(b => Equals(b.Tag, "a1")).IsChecked = true;
                        var search = Descendants(dialog).OfType<TextBox>().Single();
                        search.Text = "应用 8";
                        Assert.Single(choices, b => b.Visibility == Visibility.Visible);
                        choices.Single(b => Equals(b.Tag, "a7")).IsChecked = true;
                        Assert.True(create.IsEnabled);
                        search.Text = "";
                        f.CaptureDialog(dialog, "group-mode-picker.png");
                        stage++;
                        create.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    }
                    else if (stage == 2 && dialog.Title == "重命名分组")
                    {
                        AssertMenuBackground(f.Window, dialog);
                        var input = Descendants(dialog).OfType<TextBox>().Single();
                        Assert.Equal("创作", input.Text);
                        input.Text = "创作工具";
                        f.CaptureDialog(dialog, "group-mode-rename.png");
                        stage++;
                        Descendants(dialog).OfType<Button>().Single(b => Equals(b.Content, "保存")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    }
                }
                catch (Exception ex) { failure = ex; timer.Stop(); dialog.DialogResult = false; }
            };
            timer.Start();
            try
            {
                Call(f.Window, "AddFilterGroup");
                if (failure is null)
                    Call(f.Window, "RenameFilterGroup", f.Host.Layouts.Launcher.FilterGroups.Single(g => g.Name == "创作").Id);
            }
            finally { timer.Stop(); }
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            Assert.Equal(3, stage);
            var added = f.Host.Layouts.Launcher.FilterGroups.Single(g => g.Name == "创作工具");
            Assert.Equal(new[] { "a1", "a7" }, added.ItemIds);
            Assert.Equal(new[] { "a0", "a1" }, f.Folder.ItemIds);
            Assert.Equal(8, f.Data.Library.Items.Count);
            Call(f.Window, "ToggleFilterGroup", added.Id);
            Assert.Equal(new[] { "a1", "a7" }, f.VisibleIds());
        });
    }

    [Fact]
    public void SettingsModePersistsAcrossGridChangesAndSwitchesWithoutLosingFolders()
    {
        StaTest.Run(() =>
        {
            using var f = new Fixture(false);
            var settings = new SettingsWindow();
            try
            {
                settings.AttachDataStore(f.Data, null);
                settings.LauncherSettingsChanged += (_, value) => f.Window.ApplySettings(value);
                var mode = Assert.IsType<ComboBox>(settings.FindName("LauncherModeCombo"));
                Assert.Equal(0, mode.SelectedIndex);
                mode.SelectedIndex = 1;
                Assert.IsType<Slider>(settings.FindName("LauncherRowsSlider")).Value = 6;
                var store = new SettingsStore(f.Data.Settings.FilePath); store.Load();
                Assert.True(store.Current.Launcher.GroupModeEnabled);
                Assert.Equal(8, f.Grid.Children.Count);
                Assert.Equal(new[] { "a0", "a1" }, f.Folder.ItemIds);
                mode.SelectedIndex = 0;
                Assert.Equal(7, f.Grid.Children.Count);
                Assert.Contains(f.Grid.Children.OfType<Button>(), b => Equals(b.Tag, "folder"));
            }
            finally { settings.Close(); }
        });
    }

    [Fact]
    public void ButtonsToggleFilterAndSearchStaysInsideTheSelectedGroup()
    {
        StaTest.Run(() =>
        {
            using var f = new Fixture();
            var buttons = Assert.IsType<StackPanel>(f.Window.FindName("FilterGroupButtons"));
            Assert.Equal(Visibility.Visible, ((FrameworkElement)f.Window.FindName("FilterGroupBar")).Visibility);
            var button = Assert.Single(buttons.Children.OfType<ToggleButton>());
            Assert.Equal("工作", button.Content);
            Assert.Equal(new[] { "重命名", "编辑图标…", "删除分组" }, button.ContextMenu!.Items.OfType<MenuItem>().Select(x => (string)x.Header));
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal(new[] { "a0", "a3" }, f.VisibleIds());
            Assert.True(buttons.Children.OfType<ToggleButton>().Single().IsChecked);
            var search = (TextBox)f.Window.FindName("SearchBox");
            search.Text = "应用 2";
            Assert.Empty(f.VisibleIds());
            search.Text = "应用 1";
            Assert.Equal(new[] { "a0" }, f.VisibleIds());
            search.Text = "";
            buttons.Children.OfType<ToggleButton>().Single().RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal(8, f.VisibleIds().Length);
            f.Capture("group-mode-all.png");
            buttons.Children.OfType<ToggleButton>().Single().RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            f.Capture("group-mode-filtered.png");
        });
    }

    [Fact]
    public void CenterDragSortsImmediatelyAndCannotCreateFolderInGroupMode()
    {
        StaTest.Run(() =>
        {
            using var f = new Fixture();
            var source = f.Grid.Children.OfType<Button>().Single(b => Equals(b.Tag, "a0"));
            Call(f.Window, "PrepareIconDrag", source, new Point(source.ActualWidth / 2, source.ActualHeight / 2));
            try
            {
                var target = f.Grid.Children.OfType<Button>().Single(b => Equals(b.Tag, "a3"));
                var point = target.TranslatePoint(new Point(target.ActualWidth / 2, target.ActualHeight / 2), f.Grid);
                Call(f.Window, "PlaceIconAtPointer", point, false, false);
                var edit = Get<LauncherEditSession>(f.Window, "_dragEdit");
                Assert.Equal("a1", LauncherLayoutRules.AppIds(edit.Draft)[0]);
                Assert.Null(Get<FolderHoverIntent>(f.Window, "_folderIntent").TargetId);
                Assert.False((bool)Call(f.Window, "ApplyFolderDrop", point, false)!);
                Assert.True(new LauncherWorkspace(f.Host).Commit(edit));
                Assert.Equal(new[] { "a0", "a1" }, f.Folder.ItemIds);
                Assert.Equal("a1", f.Host.Layouts.Launcher.FlatOrder[0]);
            }
            finally { Call(f.Window, "CancelDrag"); }
        });
    }

    [Fact]
    public void DeletingSelectedFilterRestoresAllIconsAndRetainsFolders()
    {
        StaTest.Run(() =>
        {
            using var f = new Fixture();
            Call(f.Window, "ToggleFilterGroup", "work");
            Assert.Equal(2, f.Grid.Children.Count);
            Call(f.Window, "CommitFilterEdit", (Func<LauncherLayout, LauncherLayout>)(layout => LauncherFilterRules.RemoveGroup(layout, "work")));
            Assert.Empty(f.Host.Layouts.Launcher.FilterGroups);
            Assert.Equal(8, f.Grid.Children.Count);
            Assert.Equal(new[] { "a0", "a1" }, f.Folder.ItemIds);
        });
    }

    private static void AssertMenuBackground(LauncherWindow owner, Window dialog)
    {
        // Production dialogs receive these implicit control styles from Application resources.
        dialog.Resources.MergedDictionaries.Add(owner.Resources.MergedDictionaries[0]);
        Assert.Empty(Descendants(dialog).OfType<LiquidGlassSurface>());
        var panel = Assert.IsType<PhoneBorder>(dialog.Content);
        var background = Assert.IsType<SolidColorBrush>(panel.Background);
        Assert.Equal(((SolidColorBrush)owner.FindResource("LiquidControlPopup")).Color, background.Color);
        Assert.Equal(((SolidColorBrush)owner.FindResource("LiquidControlEdge")).Color,
            Assert.IsType<SolidColorBrush>(panel.BorderBrush).Color);
        dialog.UpdateLayout();
        var frame = new RenderTargetBitmap((int)dialog.ActualWidth, (int)dialog.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        frame.Render(dialog);
        var pixel = new byte[4];
        frame.CopyPixels(new Int32Rect((int)dialog.ActualWidth / 2, 8, 1, 1), pixel, 4, 0);
        Assert.Equal(background.Color.A, pixel[3]);
    }

    private static object? Call(object target, string name, params object[] args)
    {
        var method = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return method.Invoke(target, args);
    }
    private static T Get<T>(object target, string name)
        => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => frame.Continue = false;
        timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ThemeService _theme;
        private readonly string _root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "GlassDesk-group-mode-" + Guid.NewGuid().ToString("N")));
        public GlassDeskDataStore Data { get; }
        public AppServiceHost Host { get; }
        public LauncherWindow Window { get; }
        public UniformGrid Grid => (UniformGrid)Window.FindName("TileGrid");
        public LauncherGroup Folder => Host.Layouts.Launcher.Pages[0].Slots[0].Group!;
        public Fixture(bool enabled = true, bool animate = false)
        {
            _theme = ThemeService.Initialize(Dispatcher.CurrentDispatcher);
            _theme.Update(new AppearanceSettings { ReduceMotion = !animate });
            Data = new GlassDeskDataStore(new AppPaths(_root)); Data.LoadAll();
            for (var i = 0; i < 8; i++) Data.Library.Add(new() { Id = "a" + i, DisplayName = "应用 " + (i + 1), Target = "missing-" + i, IsTargetMissing = true });
            Host = new AppServiceHost(Data);
            var layout = LauncherLayoutEditor.AddShortcuts(Host.Layouts.Launcher, Enumerable.Range(0, 8).Select(i => "a" + i), 35);
            layout = LauncherLayoutEditor.CreateGroup(layout, "a1", "a0", "folder", 35);
            layout.FilterGroups.Add(new() { Id = "work", Name = "工作", ItemIds = ["a0", "a3"] });
            Assert.True(Host.Layouts.CommitLauncher(layout, Host.Layouts.LauncherVersion));
            Data.Settings.Current.Launcher.GroupModeEnabled = enabled;
            Window = new LauncherWindow { ShowActivated = false };
            Window.Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri("pack://application:,,,/GlassDesk.UI;component/Theming/LiquidGlassTheme.xaml") });
            ((LiquidGlassSurface)Window.FindName("GlassSurface")).SetPerformanceSleeping(true);
            Window.ApplySettings(Data.Settings.Current.Launcher);
            Window.AttachServices(Host, true); Window.Show(); Window.UpdateLayout();
        }
        public string[] VisibleIds() => Grid.Children.OfType<Button>().Select(b => (string)b.Tag).ToArray();
        public void Capture(string name)
            => CaptureDialog(Window, name);
        public void CaptureDialog(Window window, string name)
        {
            var output = Environment.GetEnvironmentVariable("GLASSDESK_GROUP_EVIDENCE");
            if (string.IsNullOrWhiteSpace(output)) return;
            window.UpdateLayout(); Directory.CreateDirectory(output);
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(output, name)); png.Save(stream);
        }
        public void Dispose()
        {
            Window.Close(); Host.Dispose(); _theme.Dispose();
            if (Path.GetDirectoryName(_root) != Path.TrimEndingDirectorySeparator(Path.GetTempPath())) throw new IOException("Unexpected cleanup boundary.");
            Directory.Delete(_root, true);
        }
    }
}
