namespace GlassDesk.Platform.Shell;

public sealed record DesktopItem(string Path, string DisplayName, bool IsDirectory, bool FromCurrentUser);

public sealed record DesktopScanResult(IReadOnlyList<DesktopItem> Items, IReadOnlyList<string> Warnings);

/// <summary>合并个人与公共桌面上的可见文件和文件夹，不递归展开文件夹。</summary>
public static class DesktopScanner
{
    public static DesktopScanResult Scan() => Scan(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory));

    public static DesktopScanResult Scan(string? currentUserDesktop, string? commonDesktop)
    {
        var items = new Dictionary<string, DesktopItem>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        // 个人桌面上的同名项优先，与 Windows 合并桌面时的行为一致。
        Collect(commonDesktop, false, items, warnings);
        Collect(currentUserDesktop, true, items, warnings);
        return new DesktopScanResult(items.Values
            .OrderBy(item => item.DisplayName, StringComparer.CurrentCulture)
            .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToArray(), warnings);
    }

    private static void Collect(string? root, bool fromCurrentUser,
        Dictionary<string, DesktopItem> items, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;
        try
        {
            foreach (var path in Directory.GetFileSystemEntries(root))
            {
                try
                {
                    var attributes = File.GetAttributes(path);
                    if ((attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                    var isDirectory = (attributes & FileAttributes.Directory) != 0;
                    var name = ShellDisplayNames.GetFileName(path, removeExtension: !isDirectory
                        && Path.GetExtension(path).ToLowerInvariant() is ".lnk" or ".url" or ".exe" or ".com" or ".bat" or ".cmd" or ".ps1");
                    items[Path.GetFileName(path)] = new DesktopItem(path, name, isDirectory, fromCurrentUser);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    warnings.Add($"无法读取 {path}：{ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"无法读取桌面 {root}：{ex.Message}");
        }
    }
}
