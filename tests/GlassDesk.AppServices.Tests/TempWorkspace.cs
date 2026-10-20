using GlassDesk.Core.Storage;

namespace GlassDesk.AppServices.Tests;

/// <summary>
/// 一次性的样本目录，测试结束时删除。
/// </summary>
/// <remarks>
/// 每个用例拿到自己的根目录，因此「删掉目标制造失效」这类操作
/// 不会牵连到相邻用例。
///
/// <para>
/// 目录名刻意带上中文与空格：阶段 3 的验收要求之一就是中文与含空格的路径
/// 能正确导入与启动，把样本放在纯 ASCII 目录里恰好绕开最容易出问题的那一段。
/// </para>
/// </remarks>
internal sealed class TempWorkspace : IDisposable
{
    public TempWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), $"GlassDesk-测试 样本-{Guid.NewGuid():n}");
        Directory.CreateDirectory(Root);

        DataPaths = new AppPaths(Path.Combine(Root, "数据 目录"));
        DataPaths.EnsureCreated();
    }

    /// <summary>样本文件的根目录。</summary>
    public string Root { get; }

    /// <summary>与产品结构一致的一份数据目录，隔离在样本目录之下。</summary>
    public AppPaths DataPaths { get; }

    public GlassDeskDataStore CreateStore() => new(DataPaths);

    /// <summary>造一个内容无害的可执行脚本。</summary>
    public string CreateProgram(string name)
    {
        var path = Path.Combine(Root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "@echo off" + Environment.NewLine);
        return path;
    }

    public string CreateDirectory(string name)
    {
        var path = Path.Combine(Root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    public string CreateFile(string name, string content = "内容")
    {
        var path = Path.Combine(Root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 残留的临时目录不值得让整轮测试失败。
        }
    }
}