using System.IO;
using System.Text;
using System.Diagnostics;

// Deliberately broken peers exercise the real process/pipe/mapping boundary.
internal static class CaptureProtocolFixture
{
    internal static int Run(string fault)
    {
        if (Environment.GetEnvironmentVariable("GLASSDESK_PROTOCOL_PID_FILE") is { } path)
            File.WriteAllText(path, Environment.ProcessId.ToString());
        using var input = new BinaryReader(Console.OpenStandardInput(), Encoding.UTF8);
        using var output = new BinaryWriter(Console.OpenStandardOutput(), Encoding.UTF8);
        input.ReadInt32(); input.ReadInt32(); input.ReadBoolean();
        input.ReadInt32(); input.ReadInt32(); var width = input.ReadInt32(); var height = input.ReadInt32();
        var name = input.ReadString();var capacity=input.ReadInt32(); Family(input);
        if (fault == "initial-hang") { Thread.Sleep(Timeout.Infinite); return 0; }
        using var pixels = SharedCapturePixels.Open(name, capacity);
        output.Write((byte)1);output.Write(fault=="wrong-protocol" ? 3 : GlassDesk.UI.BackdropCaptureWorker.ProtocolVersion); output.Write(0L); output.Flush();
        if (fault.StartsWith("native-trace", StringComparison.Ordinal))
        {
            input.ReadByte(); var seq = input.ReadInt64(); var gen = input.ReadInt64(); Family(input);
            input.ReadInt64(); input.ReadInt32();
            var x = input.ReadInt32(); var y = input.ReadInt32(); var r = input.ReadInt32(); var b = input.ReadInt32();
            var sw = input.ReadInt32(); var sh = input.ReadInt32();
            pixels.Write(Enumerable.Repeat((byte)99, sw * sh * 4).ToArray());
            output.Write((byte)1); output.Write(seq); output.Write(gen); output.Write(1L);
            output.Write(sw); output.Write(sh); output.Write(Stopwatch.GetTimestamp()); output.Write(sw * sh * 4);
            output.Write(x); output.Write(y); output.Write(r); output.Write(b); output.Flush();
            if (input.ReadByte() != 5) throw new InvalidDataException("Expected trace command.");
            seq = input.ReadInt64(); gen = input.ReadInt64(); Family(input);
            output.Write((byte)0); output.Write(seq); output.Write(gen); output.Write(1L);
            var trace = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new[]
                { new CaptureTrace("VisualSwap", "NewThumbnail", Stopwatch.GetTimestamp()) });
            output.Write(fault == "native-trace-too-large" ? 65537 : trace.Length);
            if (fault == "native-trace") output.Write(trace);
            output.Flush(); Thread.Sleep(Timeout.Infinite); return 0;
        }
        if(fault=="tiled-snapshot")
        {
            var ticks=Stopwatch.GetTimestamp();
            for(var i=0;i<2;i++)
            {
                var command=input.ReadByte();var identity=input.ReadInt64();var version=input.ReadInt64();Family(input);
                input.ReadInt64();input.ReadInt32();
                var x=input.ReadInt32();var y=input.ReadInt32();var r=input.ReadInt32();var b=input.ReadInt32();
                var sampleWidth=input.ReadInt32();var sampleHeight=input.ReadInt32();
                if(command==4 && input.ReadInt64()!=ticks) throw new InvalidDataException("Cached source time mismatch.");
                var sample=Enumerable.Repeat(command==4 || i==0 ? (byte)33 : (byte)77,sampleWidth*sampleHeight*4).ToArray();
                pixels.Write(sample);output.Write((byte)1);output.Write(identity);output.Write(version);output.Write(1L);
                output.Write(sampleWidth);output.Write(sampleHeight);output.Write(ticks);output.Write(sample.Length);
                output.Write(x);output.Write(y);output.Write(r);output.Write(b);output.Flush();
            }
            Thread.Sleep(Timeout.Infinite);return 0;
        }
        input.ReadByte(); var sequence = input.ReadInt64(); var generation = input.ReadInt64(); Family(input);
        var barrier = input.ReadInt64(); input.ReadInt32();
        var left=input.ReadInt32();var top=input.ReadInt32();var right=input.ReadInt32();var bottom=input.ReadInt32();
        width=input.ReadInt32();height=input.ReadInt32();
        if (fault == "read-hang") { Thread.Sleep(Timeout.Infinite); return 0; }
        output.Write((byte)1); output.Write(fault == "old-sequence" ? sequence - 1 : sequence);
        output.Write(fault == "old-generation" ? generation - 1 : generation); output.Write(1L); output.Flush();
        if (fault == "split-hang") { Thread.Sleep(Timeout.Infinite); return 0; }
        pixels.Write(new byte[width * height * 4]);
        output.Write(fault=="wrong-width" ? width+1 : width); output.Write(height);
        output.Write(fault == "old-source" ? barrier - 1 : fault=="future-source" ? Stopwatch.GetTimestamp()+Stopwatch.Frequency : Stopwatch.GetTimestamp());
        output.Write(width * height * 4);
        output.Write(fault=="wrong-crop" ? left+1 : left);output.Write(top);output.Write(right);output.Write(bottom);output.Flush();
        Thread.Sleep(Timeout.Infinite); return 0;
    }
    private static void Family(BinaryReader input)
    { var count = input.ReadInt32(); for (var i = 0; i < count; i++) input.ReadInt32(); }
}
