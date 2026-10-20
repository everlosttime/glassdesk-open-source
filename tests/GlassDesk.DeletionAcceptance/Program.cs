using System.Security.Cryptography;
using System.Text.Json;
using GlassDesk.AppServices;
using GlassDesk.Core.Storage;
using GlassDesk.Platform.Storage;

var report = Path.GetFullPath(args.Single());
var results = new List<object>();
var failed = false;
try
{
    var paths = AppPaths.ForCurrentUser();
    // Run only inside a fresh package acceptance directory. Never delete an existing profile.
    if (!Path.GetFileName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)).Equals("packaged-deletion-test")
        || Directory.Exists(paths.RootDirectory))
        throw new InvalidOperationException("Deletion acceptance requires a fresh isolated packaged-deletion-test directory.");
    var binaries = Directory.GetFiles(AppContext.BaseDirectory, "*.dll")
        .ToDictionary(path => path, path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
    var service = UserDataDeletionService.ForCurrentUser(AppContext.BaseDirectory);
    Check("Fresh portable Data returns NotFound", service.DeleteAll(true).Status == UserDataDeletionStatus.NotFound);
    var store = new GlassDeskDataStore(paths);
    store.LoadAll();
    store.Settings.Save();
    var privateFile = Path.Combine(paths.RootDirectory, "private.bin");
    byte[] bytes = [0, 255, 42, 19];
    File.WriteAllBytes(privateFile, bytes);
    var nested = Path.Combine(paths.IconCacheDirectory, "nested");
    Directory.CreateDirectory(nested);
    File.WriteAllBytes(Path.Combine(nested, "icon.bin"), bytes);
    var neighbor = Path.Combine(AppContext.BaseDirectory, "Data-neighbor");
    Directory.CreateDirectory(neighbor);
    var sentinel = Path.Combine(neighbor, "keep.bin");
    File.WriteAllBytes(sentinel, bytes);
    Check("Cancel leaves the profile intact", service.DeleteAll(false).Status == UserDataDeletionStatus.Cancelled
        && File.ReadAllBytes(privateFile).SequenceEqual(bytes));
    if (!UserDataLease.TryAcquireShared(paths.RootDirectory, out var lease))
        throw new InvalidOperationException("Cannot acquire the test runtime lease.");
    using (lease)
        Check("Active runtime lease blocks deletion", service.DeleteAll(true).Status == UserDataDeletionStatus.Busy
            && File.Exists(privateFile));
    var result = service.DeleteAll(true);
    Check("Production factory deletes portable Data", result.Status == UserDataDeletionStatus.Deleted,
        result.Error ?? result.ResidualDirectory);
    Check("Data and operation quarantine are removed", !Directory.Exists(paths.RootDirectory)
        && Directory.GetDirectories(AppContext.BaseDirectory, "GlassDesk.deleted-*").Length == 0);
    Check("Neighbor files and every packaged DLL retain their bytes", File.ReadAllBytes(sentinel).SequenceEqual(bytes)
        && binaries.All(pair => File.Exists(pair.Key)
            && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pair.Key))) == pair.Value));
    store = new GlassDeskDataStore(paths);
    var load = store.LoadAll();
    store.Settings.Save();
    Check("Next data load creates clean defaults", !load.HasNotices && File.Exists(paths.SettingsFile)
        && !File.Exists(privateFile) && store.Library.Items.Count == 0);
}
catch (Exception ex)
{
    failed = true;
    results.Add(new { name = "Unexpected exception", passed = false, details = ex.ToString() });
}
Directory.CreateDirectory(Path.GetDirectoryName(report)!);
File.WriteAllText(report, JsonSerializer.Serialize(new { passed = !failed, results }, new JsonSerializerOptions { WriteIndented = true }));
return failed ? 1 : 0;

void Check(string name, bool passed, string? details = null)
{
    results.Add(new { name, passed, details });
    if (!passed) failed = true;
}
