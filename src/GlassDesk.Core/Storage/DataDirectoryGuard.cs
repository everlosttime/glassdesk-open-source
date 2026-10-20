namespace GlassDesk.Core.Storage;

/// <summary>检查两个数据目录是否相同或互为父子目录。</summary>
public static class DataDirectoryGuard
{
    public static bool Overlaps(string candidateDirectory, string protectedDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedDirectory);

        var candidate = Normalize(candidateDirectory);
        var protectedPath = Normalize(protectedDirectory);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(candidate, protectedPath, comparison)
            || IsWithin(candidate, protectedPath, comparison)
            || IsWithin(protectedPath, candidate, comparison);
    }

    private static string Normalize(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        return fullPath.Length > (root?.Length ?? 0)
            ? Path.TrimEndingDirectorySeparator(fullPath)
            : fullPath;
    }

    private static bool IsWithin(string candidate, string parent, StringComparison comparison)
    {
        var prefix = Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, comparison);
    }
}
