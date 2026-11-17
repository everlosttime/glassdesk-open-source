using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using GlassDesk.Platform.Displays;
using GlassDesk.UI;

namespace GlassDesk.Dock.Tests;

[Collection("Window topmost UI")]
public sealed class WindowTopmostPickerTests
{
    [Fact]
    public void ClosingAnyOverlayCancelsTheWholeSelection()
    {
        StaTest.Run(() =>
        {
            using var picker = new WindowTopmostPicker();
            picker.Begin(_ => Assert.Fail("Closing selection must not commit."));
            SelectionWindows().First().Close();
            Assert.False(picker.IsSelecting);
            Assert.Empty(SelectionWindows());
        });
    }

    [Fact]
    public void CancelStopsSelectionWithoutCommittingAndDisposalIsIdempotent()
    {
        StaTest.Run(() =>
        {
            var picker = new WindowTopmostPicker();
            var committed = 0;
            try
            {
                picker.Cancel();
                picker.Begin(_ => committed++);
                Assert.True(picker.IsSelecting);
                picker.Cancel();
                Assert.False(picker.IsSelecting);
                Assert.Equal(0, committed);
                picker.Begin(_ => committed++);
                picker.Dispose();
                Assert.False(picker.IsSelecting);
                picker.Dispose();
                Assert.Equal(0, committed);
                Assert.Empty(SelectionWindows());
            }
            finally { picker.Dispose(); }
        });
    }

    [Fact]
    public void WindowOutlineUsesDisplayLocalDipsWithoutClippingCrossScreenEdges()
    {
        var display = new DisplayInfo("left", false, new(-1920, 0, 1920, 1080), new(-1920, 0, 1920, 1040), 144);
        var bounds = new PixelRect(-1700, -100, 2000, 900);
        var actual = WindowTopmostPickerLayout.ToDisplayRect(bounds, display);
        Assert.Equal(146.6666667, actual.X, 5);
        Assert.Equal(-66.6666667, actual.Y, 5);
        Assert.Equal(1333.3333333, actual.Width, 5);
        Assert.Equal(600, actual.Height, 5);
    }

    [Fact]
    public void RightClickIsConsumedUntilReleaseThenCancelsWithoutCommitting()
    {
        StaTest.Run(() =>
        {
            using var picker = new WindowTopmostPicker();
            var committed = 0;
            picker.Begin(_ => committed++);
            var overlay = SelectionWindows().First();
            var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right) { RoutedEvent = Mouse.PreviewMouseDownEvent };
            overlay.RaiseEvent(down);
            Assert.True(down.Handled);
            Assert.True(picker.IsSelecting);
            var up = new MouseButtonEventArgs(Mouse.PrimaryDevice, 1, MouseButton.Right) { RoutedEvent = Mouse.PreviewMouseUpEvent };
            overlay.RaiseEvent(up);
            Assert.True(up.Handled);
            Assert.False(picker.IsSelecting);
            Assert.Equal(0, committed);
            Assert.Empty(SelectionWindows());
        });
    }

    [Fact]
    public void EscapeAndRepeatedBeginCancelTheWholeSelection()
    {
        StaTest.Run(() =>
        {
            using var picker = new WindowTopmostPicker();
            picker.Begin(_ => Assert.Fail("Cancellation must not select a window."));
            var overlay = SelectionWindows().First();
            var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(overlay)!, 0, Key.Escape)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            overlay.RaiseEvent(escape);
            Assert.True(escape.Handled);
            Assert.False(picker.IsSelecting);
            picker.Begin(_ => Assert.Fail("Cancellation must not select a window."));
            picker.Begin(_ => Assert.Fail("A repeated hotkey must cancel, not replace the callback."));
            Assert.False(picker.IsSelecting);
            Assert.Empty(SelectionWindows());
        });
    }

    [Fact]
    public void FeedbackDoesNotActivateOrCaptureInputAndDisposeClosesIt()
    {
        StaTest.Run(() =>
        {
            var picker = new WindowTopmostPicker();
            try
            {
                picker.ShowFeedback("已置顶");
                var window = PresentationSource.CurrentSources.Cast<PresentationSource>().Select(s => s.RootVisual)
                    .OfType<Window>().Single(w => w.Title == "GlassDesk 窗口置顶提示");
                Assert.False(window.ShowActivated);
                Assert.False(window.IsHitTestVisible);
                picker.Dispose();
                Assert.False(window.IsVisible);
            }
            finally { picker.Dispose(); }
        });
    }

    private static Window[] SelectionWindows() => PresentationSource.CurrentSources.Cast<PresentationSource>()
        .Select(s => s.RootVisual).OfType<Window>().Where(w => w.Title == "GlassDesk 窗口置顶选择").ToArray();
}
