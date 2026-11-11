using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GlassDesk.AppServices;
using GlassDesk.Core.Models;
using GlassDesk.Core.Serialization;

if (args.Length != 3) throw new ArgumentException("Usage: SearchPerformance <copied-profile> <report.json> <label>");
var profile = Path.GetFullPath(args[0]);
var entries = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(profile, "apps.json")),
    GlassDeskJsonContext.Default.AppLibraryData)!.Items;
var layout = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(profile, "launcher-layout.json")),
    GlassDeskJsonContext.Default.LauncherLayout)!;
var synthetic = Enumerable.Range(0, 500).Select(i => new AppEntry
{
    Id = "fixture-" + i, DisplayName = "示例应用工具 Application " + i, Target = "unused",
    SearchAliases = ["fixture" + i],
}).ToArray();
var queries = new[] { "a", "ap", "app", "application", "w", "we", "wei", "微信", "wx", "code", "工具", "gongju", "gj", "zzzz-no-match" };
var results = new List<object>();
Measure("copied-user-library", entries, layout);
Measure("synthetic-500", synthetic, new LauncherLayout());
var report = new
{
    label = args[2], time = DateTimeOffset.Now, processId = Environment.ProcessId,
    measurement = "In-process production SearchService only, copied user data and labeled synthetic data; not whole-app CPU, GPU FPS or physical input latency. No explicit GC or working-set trim.",
    results,
};
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
File.WriteAllText(args[1], JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(report));

void Measure(string name, IReadOnlyList<AppEntry> items, LauncherLayout order)
{
    var service = new SearchService(() => items, () => order);
    var cold = Stopwatch.StartNew();
    _ = service.SearchApps("zzzz-no-match", int.MaxValue);
    var firstSearchMs = cold.Elapsed.TotalMilliseconds;
    for (var i = 0; i < 300; i++) _ = service.SearchApps(queries[i % queries.Length], int.MaxValue);
    var samples = new List<object>();
    string? signature = null;
    for (var round = 0; round < 5; round++)
    {
        var times = new List<double>();
        var digest = new StringBuilder();
        var bytes = GC.GetAllocatedBytesForCurrentThread();
        var elapsed = Stopwatch.StartNew();
        for (var i = 0; i < 1400; i++)
        {
            var start = Stopwatch.GetTimestamp();
            var hits = service.SearchApps(queries[i % queries.Length], int.MaxValue);
            times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            if (i < queries.Length)
                digest.AppendJoin(',', hits.Select(hit => hit.Item.Id + ":" + hit.MatchKind)).Append(';');
        }
        var totalMs = elapsed.Elapsed.TotalMilliseconds;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - bytes;
        times.Sort();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(digest.ToString())));
        if (signature is not null && signature != hash) throw new InvalidOperationException("Unstable search results.");
        signature = hash;
        samples.Add(new { round, totalMs, meanMs = totalMs / 1400, p50Ms = times[700], p95Ms = times[1330], allocatedBytesPerQuery = allocated / 1400d });
    }
    results.Add(new { name, entries = items.Count, firstSearchMs, resultSha256 = signature, samples });
}
