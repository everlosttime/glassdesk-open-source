using System.IO;
using System.Text;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GlassDesk.UI;

/// <summary>Both native scene providers run outside the application on every Windows build.</summary>
public static class BackdropCaptureWorker
{
    internal const int ProtocolMagic = 0x47444333;
    internal const int ProtocolVersion = 5;
    public static bool RunIfRequested(string[] arguments, out int exitCode)
    {
        exitCode = 0;
        if (arguments.Length != 2 || arguments[0] != "--glass-backdrop-worker") return false;
        if (!int.TryParse(arguments[1], out var parentId) || parentId <= 0) { exitCode = 2; return true; }
        using var input = new BinaryReader(Console.OpenStandardInput(), Encoding.UTF8);
        using var output = new BinaryWriter(Console.OpenStandardOutput(), Encoding.UTF8);
        try
        {
            using var parent = Process.GetProcessById(parentId);
            // Input can block forever after a parent crash. A watchdog owns that lifetime,
            // even when the capture thread is blocked in a driver or native destructor.
            using var lifetime = new CancellationTokenSource();
            _ = Task.Run(async () =>
            {
                while (!lifetime.IsCancellationRequested)
                {
                    if (parent.HasExited) Environment.Exit(0);
                    try { await Task.Delay(500, lifetime.Token); }
                    catch (OperationCanceledException) { break; }
                }
            });
            SetThreadDpiAwarenessContext(-4);
            if (input.ReadInt32() != ProtocolMagic || input.ReadInt32() != ProtocolVersion)
                throw new InvalidDataException("Invalid capture protocol.");
            var provider = input.ReadByte();
            if (provider > 2) throw new InvalidDataException("Invalid capture provider.");
            var region = new ScreenRect(input.ReadInt32(), input.ReadInt32(), input.ReadInt32(), input.ReadInt32());
            if (region.Width <= 0 || region.Height <= 0 || (long)region.Width * region.Height > 32_000_000)
                throw new InvalidDataException("Invalid capture extent.");
            using var pixels = SharedCapturePixels.Open(input.ReadString(), input.ReadInt32());
            using var resize=new NativeFrameResize();
            byte[]? sampled=null;
            CapturePixels? cachedNative=null;long cachedGeneration=0;
            var excluded = ReadFamily(input, parentId);
            var diagnostics = new NativeCaptureDiagnostics();
            using IDesktopSceneCapture capture = provider switch
            {
                0 => new SharedDesktopSceneCapture(region, trace: diagnostics.Observe, isolatedParentProcess: parentId, excludedProcesses: () => excluded),
                1 => new DocumentedDesktopSceneCapture(region, trace: diagnostics.Observe, excludedProcesses: () => excluded),
                _ => new WallpaperSceneCapture(region)
            };
            output.Write((byte)1);output.Write(ProtocolVersion); output.Write(capture.CarrierHandle.ToInt64()); output.Flush();
            while (!parent.HasExited)
            {
                var command = input.ReadByte();
                if (command == 0) break;
                var sequence = input.ReadInt64(); var generation = input.ReadInt64();
                if (sequence <= 0 || generation <= 0) throw new InvalidDataException("Invalid capture request identity.");
                excluded = ReadFamily(input, parentId);
                if (command == 5)
                {
                    var events = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(diagnostics.Drain());
                    if (events.Length > 65536) throw new InvalidDataException("Native trace envelope exceeds its budget.");
                    output.Write((byte)0); output.Write(sequence); output.Write(generation); output.Write(capture.SceneRevision);
                    output.Write(events.Length); output.Write(events); output.Flush(); continue;
                }
                if (command == 3)
                {
                    var changed=capture.UpdateExclusions();if(changed) cachedNative=null;
                    output.Write(changed ? (byte)1 : (byte)0);
                    output.Write(sequence); output.Write(generation); output.Write(capture.SceneRevision); output.Flush(); continue;
                }
                if (command == 2)
                {
                    capture.ReleaseReadbackBuffers();resize.Dispose();sampled=null;cachedNative=null; output.Write((byte)0);
                    output.Write(sequence); output.Write(generation); output.Write(capture.SceneRevision); output.Flush(); continue;
                }
                if (command is not (1 or 4)) throw new InvalidDataException("Unknown capture command.");
                var notBefore = input.ReadInt64();
                var wait = Math.Clamp(input.ReadInt32(), 0, 100);
                var crop=new PixelBounds(input.ReadInt32(),input.ReadInt32(),input.ReadInt32(),input.ReadInt32());
                var width=input.ReadInt32();var height=input.ReadInt32();
                if(crop.Left<0 || crop.Top<0 || crop.Right>region.Width || crop.Bottom>region.Height || crop.Width<=0 || crop.Height<=0
                    || width<=0 || height<=0 || (long)width*height>1_000_000 || (long)width*height*4>pixels.Capacity)
                    throw new InvalidDataException("Invalid sampled capture geometry.");
                var cachedSource=command==4 ? input.ReadInt64() : 0;
                var frame=command==4
                    ? cachedNative is not null && cachedGeneration==generation && cachedNative.SourceQpcTicks==cachedSource
                        && cachedSource>=notBefore ? cachedNative : null
                    : capture.TryRead(TimeSpan.FromMilliseconds(wait),sourceNotBeforeTicks:notBefore,reusePixels:true);
                if(command==1 && frame is not null) {cachedNative=frame;cachedGeneration=generation;}
                if (frame is null)
                { output.Write((byte)0); output.Write(sequence); output.Write(generation); output.Write(capture.SceneRevision); }
                else
                {
                    diagnostics.ObservePixels(frame, generation);
                    var bytes=checked(width*height*4);
                    if(sampled?.Length!=bytes) sampled=new byte[bytes];
                    resize.ResizeInto(frame,width,height,crop,sampled);
                    pixels.Write(sampled);
                    output.Write((byte)1); output.Write(sequence); output.Write(generation);
                    output.Write(capture.SceneRevision);
                    output.Write(width); output.Write(height);
                    output.Write(frame.SourceQpcTicks); output.Write(bytes);
                    output.Write(crop.Left);output.Write(crop.Top);output.Write(crop.Right);output.Write(crop.Bottom);
                }
                output.Flush();
                global::Program.Pump(TimeSpan.FromMilliseconds(1));
            }
            lifetime.Cancel();
        }
        catch (EndOfStreamException) { }
        catch (IOException) { exitCode = 1; }
        catch (Exception error)
        {
            exitCode = 1;
            try
            {
                var bytes = Encoding.UTF8.GetBytes(error.Message);
                if (bytes.Length > 8192) bytes = bytes[..8192];
                output.Write((byte)2); output.Write(bytes.Length); output.Write(bytes); output.Flush();
            }
            catch (IOException) { }
        }
        return true;
    }
    private static int[] ReadFamily(BinaryReader input, int parent)
    {
        var count = input.ReadInt32();
        if (count < 1 || count > 256) throw new InvalidDataException("Invalid exclusion family.");
        var family = new HashSet<int> { Environment.ProcessId, parent };
        for (var i = 0; i < count; i++)
        {
            var pid = input.ReadInt32();
            if (pid <= 0) throw new InvalidDataException("Invalid exclusion process.");
            family.Add(pid);
        }
        return family.ToArray();
    }
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
}
