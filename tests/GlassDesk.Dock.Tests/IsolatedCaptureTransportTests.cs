using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace GlassDesk.Dock.Tests;

public sealed class IsolatedCaptureTransportTests
{
    [Fact]
    public void LargeNativeFramesDoNotTravelThroughTheControlPipe()
    {
        StaTest.Run(() =>
        {
            var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
            var executable = Path.Combine(repo, "tests/GlassDesk.CompatibilityProbe/bin/Release/net10.0-windows10.0.26100.0/GlassCaptureProbe.exe");
            var bounds = GlassDesk.Platform.Displays.DisplayService.Primary()!.Bounds;
            var region = new ScreenRect(bounds.X, bounds.Y, Math.Min(1600, bounds.Width), Math.Min(900, bounds.Height));
            using var capture = new IsolatedDesktopSceneCapture(region, executable: executable);
            var output = typeof(IsolatedDesktopSceneCapture).GetField("_output", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var count = new CountingReadStream((Stream)output.GetValue(capture)!);
            output.SetValue(capture, count);
            CapturePixels? frame = null;
            var timer = Stopwatch.StartNew();
            while (frame is null && timer.Elapsed < TimeSpan.FromSeconds(6))
                frame = capture.TryRead(TimeSpan.FromMilliseconds(30));
            Assert.NotNull(frame);
            Assert.Equal(region.Width * region.Height * 4, frame.Pixels.Length);
            Assert.True(count.BytesRead < 1024, $"A {frame.Pixels.Length}-byte background consumed {count.BytesRead} control-pipe bytes.");
        });
    }

    private sealed class CountingReadStream(Stream inner) : Stream
    {
        internal long BytesRead;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        { var read = inner.Read(buffer, offset, count); BytesRead += read; return read; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { var read = await inner.ReadAsync(buffer, cancellationToken); BytesRead += read; return read; }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
