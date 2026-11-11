using GlassDesk.Core.Contracts;

namespace GlassDesk.Nearby;

public sealed record FileSelectionResult(IReadOnlyList<NearbyFile> Files, IReadOnlyList<string> Warnings);

public static class FileSelection
{
    public static FileSelectionResult Expand(IEnumerable<string> sources, CancellationToken cancellationToken = default)
    {
        var files = new List<NearbyFile>(); var warnings = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var full = Path.GetFullPath(source);
            if (!seen.Add(full)) continue;
            var name = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var original = name;
            for (var index = 2; !roots.Add(name); index++) name = original + $" ({index})";
            Visit(full, name.Replace('\\', '/'));
        }
        return new(files.AsReadOnly(), warnings.AsReadOnly());

        void Visit(string path, string name)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                ReceiveBatch.ValidateName(name);
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) { warnings.Add($"已跳过链接：{name}"); return; }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    var children = Directory.GetFileSystemEntries(path);
                    if (children.Length == 0) warnings.Add($"已跳过空文件夹：{name}");
                    foreach (var child in children) Visit(child, name + "/" + Path.GetFileName(child));
                }
                else
                {
                    if (files.Count >= 10000) throw new InvalidOperationException("单次最多发送 10000 个文件，请分批添加。");
                    var info = new FileInfo(path);
                    files.Add(new(Guid.NewGuid().ToString("N"), name, info.Length, ContentType(path), path));
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            { warnings.Add($"无法添加 {name}：{error.Message}"); }
        }
    }

    public static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg", ".png" => "image/png", ".heic" => "image/heic", ".heif" => "image/heif",
        ".gif" => "image/gif", ".webp" => "image/webp", ".bmp" => "image/bmp", ".tif" or ".tiff" => "image/tiff",
        ".mp4" or ".m4v" => "video/mp4", ".mov" => "video/quicktime", ".mkv" => "video/x-matroska",
        ".avi" => "video/x-msvideo", ".webm" => "video/webm", ".mp3" => "audio/mpeg", ".pdf" => "application/pdf",
        ".txt" => "text/plain", ".zip" => "application/zip", _ => "application/octet-stream",
    };
}
