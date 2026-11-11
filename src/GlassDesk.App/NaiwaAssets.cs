using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace GlassDesk.App;

internal static class NaiwaAssets
{
    private const string ArchiveSha256 = "4529f445e6b75f3f39a50c338ef54e6bbddb620db9772ca025fd88597e10b13b";

    private static readonly string[] RequiredFiles =
    [
        "素材.nwp", "idle_gif.nwp", "drag.nwp", "dance.nwp", "eye_art.nwp", "fat.nwp", "feed.nwp",
        "head.nwp", "hold.nwp", "hunger.nwp", "rest.nwp", "rest2.nwp", "sad.nwp", "seq.nwp", "walk.nwp",
        "dance.mp3", "原版笑声.wav", "cry.wav",
    ];

    internal static bool AreReady(string directory)
        => RequiredFiles.All(name => new FileInfo(Path.Combine(directory, name)) is { Exists: true, Length: > 0 });

    internal static async Task ImportAsync(string archivePath, string directory)
    {
        await using var input = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 128 * 1024, useAsync: true);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(input).ConfigureAwait(false));
        if (!hash.Equals(ArchiveSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("素材包校验失败。请选择官方发布的 naiwa-pet-v2.5.1-portable.zip。");

        input.Position = 0;
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        var entries = RequiredFiles.Select(name => archive.GetEntry(name)
            ?? throw new InvalidDataException($"素材包缺少 {name}。")).ToArray();

        // Complete every extraction before promoting assets into the active directory.
        var stage = directory + ".import-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(stage);
        try
        {
            foreach (var entry in entries)
            {
                using var source = entry.Open();
                await using var target = new FileStream(Path.Combine(stage, entry.Name), FileMode.CreateNew,
                    FileAccess.Write, FileShare.None, bufferSize: 128 * 1024, useAsync: true);
                await source.CopyToAsync(target).ConfigureAwait(false);
            }

            if (!Directory.Exists(directory))
                Directory.Move(stage, directory);
            else
                foreach (var entry in entries)
                    File.Move(Path.Combine(stage, entry.Name), Path.Combine(directory, entry.Name), overwrite: true);
        }
        finally
        {
            // The stage contains only the enumerated filenames created above.
            if (Directory.Exists(stage))
            {
                foreach (var entry in entries) File.Delete(Path.Combine(stage, entry.Name));
                Directory.Delete(stage);
            }
        }
    }
}
