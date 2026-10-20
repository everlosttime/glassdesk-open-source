using GlassDesk.Core.Storage;

namespace GlassDesk.Core.Tests;

/// <summary>
/// 一次性的数据目录，测试结束时删除。
/// </summary>
/// <remarks>
/// 每个涉及存储的测试都会拿到自己的目录。
/// 若共用一个目录，故意破坏某个文件的测试就会牵连到相邻的测试用例，
/// 而正是这类耦合让数据层测试套件变得不可信。
/// </remarks>
internal sealed class TempDataDirectory : IDisposable
{
    public TempDataDirectory()
    {
        var name = $"GlassDesk-tests-{Guid.NewGuid():n}";
        Root = Path.Combine(Path.GetTempPath(), name);
        Directory.CreateDirectory(Root);
        Paths = new AppPaths(Root);
        Paths.EnsureCreated();
    }

    public string Root { get; }

    public AppPaths Paths { get; }

    public GlassDeskDataStore CreateStore() => new(Paths);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // 残留的临时目录不值得让整轮测试失败。
        }
        catch (UnauthorizedAccessException)
        {
            // 同上。
        }
    }
}