namespace GlassDesk.Core.Models;

/// <summary>
/// 应用库文档：用户导入的全部条目，由 Dock 与
/// 启动台共用。
/// </summary>
/// <remarks>
/// 包在对象里而不是直接存成裸数组，这样以后加字段
/// （索引、修订计数）不会让既有文件失效。
/// </remarks>
public sealed class AppLibraryData
{
    public int SchemaVersion { get; set; } = 1;

    public List<AppEntry> Items { get; set; } = [];
}

/// <summary>
/// 描述设置中心写出的备份档案，以便还原时确认它
/// 来自本产品，并知道它用的是哪一版架构。
/// </summary>
public sealed class BackupManifest
{
    public string Product { get; set; } = "GlassDesk";

    public int SchemaVersion { get; set; } = GlassDesk.Core.Models.Settings.GlassDeskSettings.CurrentSchemaVersion;

    /// <summary>生成该档案的产品版本。</summary>
    public string? ProductVersion { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>档案内含的文件名，相对于档案根目录。</summary>
    public List<string> Files { get; set; } = [];
}