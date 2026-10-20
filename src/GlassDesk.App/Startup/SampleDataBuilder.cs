using System.Globalization;
using System.IO;
using System.Text;
using GlassDesk.Platform.Shell;

namespace GlassDesk.App.Startup;

/// <summary>一个样本来源。</summary>
/// <param name="Key">稳定的英文标识，供断言按种类找到它。</param>
/// <param name="Path">来源路径，或网址。</param>
/// <param name="Description">中文说明，写进报告给人看。</param>
internal sealed record SampleSource(string Key, string Path, string Description);

/// <summary>造出来的样本清单。</summary>
/// <param name="Directory">样本根目录。</param>
/// <param name="ShouldImport">应当被成功导入的来源。</param>
/// <param name="ShouldFail">应当被逐条拒绝的来源。</param>
/// <param name="BrokenTarget">那个「目标被删掉」的文件，用来验证失效保留。</param>
/// <param name="RelocationSource">失效条目的来源，用来在重新定位时找到它。</param>
internal sealed record SampleSet(
    string Directory,
    IReadOnlyList<SampleSource> ShouldImport,
    IReadOnlyList<SampleSource> ShouldFail,
    string BrokenTarget,
    string RelocationSource);

/// <summary>
/// 造出阶段 3 验收需要的代表性样本。
/// </summary>
/// <remarks>
/// 阶段 2 的教训是「边界输入才出问题」，只测干净路径等于没测。所以这里刻意把
/// 中文、空格、失效路径、带参数的快捷方式、指向网址的快捷方式、
/// 以及根本不该被接受的东西各造一份，并且让目录名本身带空格与中文——
/// 把样本放在一个纯 ASCII 的临时目录里，恰好绕开了最容易出问题的那一段。
///
/// <para>
/// 每个样本带一个英文 <c>Key</c>：断言按 Key 找到它，报告里显示中文说明。
/// 这样验收逻辑（在 C# 里）不必去匹配中文文本，样本本身又保持中文。
/// </para>
/// </remarks>
internal static class SampleDataBuilder
{
    /// <summary>在工作目录下造一整套样本。</summary>
    public static SampleSet Build(string? rootOverride = null)
    {
        var root = rootOverride ?? Path.Combine(Path.GetTempPath(), "GlassDesk-样本 测试");

        // 每一类样本放在独立子目录里，这样「删掉目标」只影响那一条。
        var programs = Path.Combine(root, "程序 目录");
        var documents = Path.Combine(root, "文档 目录");
        var shortcuts = Path.Combine(root, "快捷方式 目录");
        var broken = Path.Combine(root, "失效 目录");

        foreach (var directory in new[] { root, programs, documents, shortcuts, broken })
        {
            Directory.CreateDirectory(directory);
        }

        var importable = new List<SampleSource>();
        var failable = new List<SampleSource>();

        // ---- 一、程序：可执行脚本，名字里有中文与空格 ----
        var program = Path.Combine(programs, "记事 本 模拟器.cmd");
        File.WriteAllText(
            program,
            "@echo off" + Environment.NewLine + "echo 这是用来验收的程序样本。" + Environment.NewLine,
            new UTF8Encoding(false));

        importable.Add(new SampleSource("program", program, "程序（中文与空格路径）"));

        // ---- 二、文件夹：中文名 ----
        var folder = Path.Combine(documents, "我的 资料 文件夹");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "里面的文件.txt"), "内容", new UTF8Encoding(false));
        importable.Add(new SampleSource("folder", folder, "文件夹（中文与空格路径）"));

        // ---- 三、文件：中文名的文档 ----
        var document = Path.Combine(documents, "季度 报告 2026.txt");
        File.WriteAllText(document, "样本内容", new UTF8Encoding(false));
        importable.Add(new SampleSource("file", document, "文件（中文与空格路径）"));

        // ---- 四、快捷方式：带参数、中文名、图标指向 shell32 的索引 ----
        var withArguments = Path.Combine(shortcuts, "带 参数 的快捷方式.lnk");

        if (ShortcutWriter.Write(withArguments, new ShortcutDefinition(
                program,
                Arguments: "--flag \"带 空格 的值\" --second",
                WorkingDirectory: programs,
                IconLocation: @"C:\Windows\System32\shell32.dll,3",
                Description: "阶段 3 验收样本：参数与自定义图标位置都要保留")))
        {
            importable.Add(new SampleSource("shortcut-arguments", withArguments, "快捷方式（带参数、中文、自定义图标索引）"));
        }
        else
        {
            failable.Add(new SampleSource("shortcut-arguments", withArguments, "本机无法创建快捷方式样本"));
        }

        // ---- 五、快捷方式：指向网页 ----
        var webShortcut = Path.Combine(shortcuts, "网页 快捷方式.lnk");

        if (ShortcutWriter.Write(webShortcut, new ShortcutDefinition("https://example.com/路径 带空格")))
        {
            importable.Add(new SampleSource("shortcut-url", webShortcut, "快捷方式（指向网页）"));
        }

        // ---- 六、Internet 快捷方式：纯文本 .url ----
        var internetShortcut = Path.Combine(shortcuts, "互联网 快捷方式.url");
        File.WriteAllText(
            internetShortcut,
            "[InternetShortcut]" + Environment.NewLine + "URL=https://example.org/a b" + Environment.NewLine,
            new UTF8Encoding(false));

        importable.Add(new SampleSource("url-file", internetShortcut, "网页（.url 文件）"));

        // ---- 七、网址：直接给，只有它标成 IsUrl ----
        // 由调用方决定哪些来源按网址处理；这里只记录路径形态。

        // 两个只差端口的地址：去重键必须把端口算进去，否则第二个会被当成重复拒绝。
        // 复核时发现键里只折叠了协议与主机，端口被丢掉了。
        importable.Add(new SampleSource("url-port-8001", "http://localhost:8001/app", "网页（端口 8001）"));
        importable.Add(new SampleSource("url-port-8002", "http://localhost:8002/app", "网页（端口 8002）"));

        // ---- 八、失效路径：目标先造出来，导入之后由调用方删掉 ----
        var doomedTarget = Path.Combine(broken, "马上 会被删掉 的程序.cmd");
        File.WriteAllText(doomedTarget, "@echo off" + Environment.NewLine, new UTF8Encoding(false));

        var doomedShortcut = Path.Combine(broken, "指向 将失效目标 的快捷方式.lnk");

        if (ShortcutWriter.Write(doomedShortcut, new ShortcutDefinition(doomedTarget)))
        {
            importable.Add(new SampleSource("broken-later", doomedShortcut, "快捷方式（目标稍后会被删除，用于验证失效保留）"));
        }

        // ---- 九、后缀是 .url 但内容不是网页 —— 应当退回按普通文件处理，而不是失败 ----
        var bogusUrl = Path.Combine(shortcuts, "内容 不完整 的.url");
        File.WriteAllText(bogusUrl, "这不是一个 Internet 快捷方式" + Environment.NewLine, new UTF8Encoding(false));
        importable.Add(new SampleSource("bogus-url-file", bogusUrl, "普通文件（后缀是 .url 但内容不是）"));

        // ---- 十、应当被逐条拒绝的来源 ----
        failable.Add(new SampleSource("missing-path", Path.Combine(root, "根本 不存在 的路径.txt"), "路径不存在"));

        // 一个 0 字节的 .lnk：路径字段与 ID 列表都读不出来，应当逐条报告失败。
        var emptyShortcut = Path.Combine(shortcuts, "空 的快捷方式.lnk");
        File.WriteAllBytes(emptyShortcut, []);
        failable.Add(new SampleSource("empty-shortcut", emptyShortcut, "空文件伪装成快捷方式"));

        return new SampleSet(root, importable, failable, doomedTarget, doomedShortcut);
    }

    /// <summary>把样本目录的样子写进报告，便于人工核对。</summary>
    public static string Describe(SampleSet set)
    {
        ArgumentNullException.ThrowIfNull(set);

        var report = new StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture, $"样本根目录：{set.Directory}");
        report.AppendLine();

        report.AppendLine("应当成功导入：");

        foreach (var source in set.ShouldImport)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"  · [{source.Key}] {source.Description}");
            report.AppendLine(CultureInfo.InvariantCulture, $"      {source.Path}");
        }

        report.AppendLine();
        report.AppendLine("应当被逐条拒绝：");

        foreach (var source in set.ShouldFail)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"  · [{source.Key}] {source.Description}");
            report.AppendLine(CultureInfo.InvariantCulture, $"      {source.Path}");
        }

        report.AppendLine();
        report.AppendLine(CultureInfo.InvariantCulture, $"稍后要删掉的目标：{set.BrokenTarget}");

        return report.ToString();
    }

    /// <summary>删掉一个样本目标，用来制造「条目还在、目标已失效」的局面。</summary>
    public static bool DeleteTarget(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 删不掉就当作没成功，由调用方如实报告。
        }

        return false;
    }
}