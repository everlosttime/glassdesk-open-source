using System.Text.Json;
using System.Text.Json.Serialization;
using GlassDesk.Core.Models;
using GlassDesk.Core.Models.Settings;

namespace GlassDesk.Core.Serialization;

/// <summary>
/// 我们持久化的每一份文档都在编译期已知。
/// </summary>
/// <remarks>
/// 选项以便于手工编辑为准，因为高级用户会打开这些文件：
/// camelCase 命名、枚举写成单词而不是数字、读取时容忍注释与末尾逗号、
/// 写入时省略 null 成员。
/// </remarks>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(GlassDeskSettings))]
[JsonSerializable(typeof(AppLibraryData))]
[JsonSerializable(typeof(DockLayout))]
[JsonSerializable(typeof(LauncherLayout))]
[JsonSerializable(typeof(BackupManifest))]
public sealed partial class GlassDeskJsonContext : JsonSerializerContext
{
}