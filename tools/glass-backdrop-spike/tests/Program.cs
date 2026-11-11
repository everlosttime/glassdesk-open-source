// Negative physical origin catches missing coordinate offsets; literal BGR values catch channel errors.
var bounds = new PixelBounds(-2, 10, 2, 11);
PixelFrame Frame(byte[] pixels, long start) => new(bounds, pixels, start, start + 1, DateTimeOffset.UnixEpoch);
var before = Frame([10,20,30,0, 10,20,30,0, 10,20,30,0, 10,20,30,0], 1);
var source = Frame([12,22,32,255, 10,20,33,255, 80,80,80,0, 90,90,90,0], 3);
var after = Frame([10,20,30,255, 10,20,30,255, 11,20,30,0, 10,20,30,0], 5);
var points = new[] { new PixelCandidate(-2,10,false,true), new PixelCandidate(-1,10,true,true),
    new PixelCandidate(0,10,false,true), new PixelCandidate(1,10,true,false) };
try
{
    var result = PixelComparison.Compare(before, source, after, points);
    Require(result.Wallpaper.Valid == 1 && result.Wallpaper.Unstable == 1 && result.Wallpaper.Mismatched == 0,
        "exact reference stability, alpha ignored, <=2 allowed");
    Require(result.Wallpaper.MaxBgrError.SequenceEqual(new[] {2,2,2}), "physical origin and BGR mapping");
    Require(result.Icons.Valid == 1 && result.Icons.Mismatched == 1 && result.Icons.Occluded == 1,
        "icon grouping, >2 rejected, occlusion excluded");
    Require(!result.Wallpaper.Passed && !result.Icons.Passed, "fewer than 50 cannot pass");
    var fifty = Enumerable.Range(0,50).Select(x => new PixelCandidate(x,0,false,true)).ToArray();
    var large = new PixelFrame(new PixelBounds(0,0,50,1), new byte[200],1,2,DateTimeOffset.UnixEpoch);
    Require(PixelComparison.Compare(large,large,large,fifty).Wallpaper.Passed, "50 stable matches pass");
    var invalid = source with { Bounds = new PixelBounds(-1,10,3,11) };
    var rejected = false;
    try { PixelComparison.Compare(before,invalid,after,points); } catch (InvalidOperationException) { rejected = true; }
    Require(rejected,"changed capture bounds rejected");
    Require(DiagnosticMath.ReadbackMiBPerSecond(16384000,861,30) == 448.4375, "full-resolution sustained readback must not overflow int32");
    AsyncStateTests.Run();
    AsyncProducerTests.Run();
    Console.WriteLine("PASS: pixel comparison, arithmetic and asynchronous frame delivery checks."); return 0;
}
catch (Exception error) { Console.Error.WriteLine("FAIL: " + error.Message); return 1; }
static void Require(bool value, string name) { if (!value) throw new Exception(name); }
