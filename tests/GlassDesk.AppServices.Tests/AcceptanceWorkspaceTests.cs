using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using GlassDesk.Platform.Storage;
using Microsoft.Win32.SafeHandles;

namespace GlassDesk.AppServices.Tests;

/// <summary>真实文件系统边界：任何拒绝都不能覆盖既有资料。</summary>
public sealed class AcceptanceWorkspaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GlassDesk-AcceptanceTests-" + Guid.NewGuid().ToString("N"));

    public AcceptanceWorkspaceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void 新建数据和具名输出且持有独占输出直到释放()
    {
        var request = Request();
        using (var workspace = AcceptanceWorkspace.Create(request))
        {
            Assert.Equal(request.NewDataRoot, workspace.DataRoot);
            Assert.True(Directory.Exists(workspace.DataRoot));
            workspace.GetOutput("report").Write(Encoding.UTF8.GetBytes("验收报告"));
            Assert.Throws<IOException>(() => File.WriteAllText(request.Outputs[0].Path, "覆盖"));
            Assert.Throws<IOException>(() => File.Delete(request.Outputs[0].Path));
        }
        Assert.Equal("验收报告", File.ReadAllText(request.Outputs[0].Path));
        Assert.False(Directory.Exists(request.ProtectedDataRoot));
    }

    [Fact]
    public void 子数据根从已持有工作区句柄下新建且释放前不能改名()
    {
        var request = Request();
        using var workspace = AcceptanceWorkspace.Create(request);

        var childRoot = workspace.CreateChildDataRoot("cross-directory-restore");

        Assert.Equal(Path.Combine(workspace.DataRoot, "cross-directory-restore"), childRoot);
        Assert.True(Directory.Exists(childRoot));
        Assert.Throws<IOException>(() => Directory.Move(childRoot, childRoot + "-moved"));
        Assert.Throws<IOException>(() => { workspace.CreateChildDataRoot("cross-directory-restore"); });
        Assert.Throws<IOException>(() => { workspace.CreateChildDataRoot("..\\outside"); });
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(workspace.DataRoot)!, "outside")));
    }

    [Theory]
    [InlineData("same")]
    [InlineData("child")]
    [InlineData("parent")]
    [InlineData("case")]
    public void 数据根与受保护目录重叠时拒绝且没有副作用(string relationship)
    {
        var request = Request();
        var candidate = relationship switch
        {
            "same" => request.ProtectedDataRoot,
            "child" => Path.Combine(request.ProtectedDataRoot, "child"),
            "parent" => _root,
            _ => request.ProtectedDataRoot.ToUpperInvariant(),
        };
        RejectWithoutChanges(request with { NewDataRoot = candidate });
    }

    [Fact]
    public void 相邻同名前缀允许而不创建受保护目录()
    {
        var request = Request();
        using var workspace = AcceptanceWorkspace.Create(request with { NewDataRoot = request.ProtectedDataRoot + "-test" });
        Assert.True(Directory.Exists(workspace.DataRoot));
        Assert.False(Directory.Exists(request.ProtectedDataRoot));
    }

    [Theory]
    [InlineData("protected")]
    [InlineData("data")]
    [InlineData("duplicate")]
    [InlineData("ancestor")]
    public void 所有输出在创建数据根之前检查(string target)
    {
        var request = Request();
        var outputs = target switch
        {
            "protected" => new[] { new AcceptanceOutput("report", Path.Combine(request.ProtectedDataRoot, "report.txt")) },
            "data" => [new AcceptanceOutput("report", Path.Combine(request.NewDataRoot, "report.txt"))],
            "duplicate" => [new AcceptanceOutput("report", Path.Combine(_root, "a")), new AcceptanceOutput("image", Path.Combine(_root, "a"))],
            _ => [new AcceptanceOutput("report", Path.Combine(_root, "a")), new AcceptanceOutput("image", Path.Combine(_root, "a", "b"))],
        };
        RejectWithoutChanges(request with { Outputs = outputs });
    }

    [Theory]
    [InlineData("data-directory")]
    [InlineData("data-file")]
    [InlineData("report-file")]
    [InlineData("report-directory")]
    public void 已有数据根和输出永不复用或覆盖(string kind)
    {
        var request = Request();
        var target = kind.StartsWith("data", StringComparison.Ordinal) ? request.NewDataRoot : request.Outputs[0].Path;
        if (kind.EndsWith("directory", StringComparison.Ordinal)) Directory.CreateDirectory(target);
        else File.WriteAllText(target, "保留原件");
        RejectWithoutChanges(request);
    }

    [Fact]
    public void 既有硬链接输出拒绝且外部哨兵不变()
    {
        var request = Request();
        var sentinel = Path.Combine(_root, "sentinel.txt");
        File.WriteAllText(sentinel, "外部哨兵");
        Assert.True(CreateHardLink(request.Outputs[0].Path, sentinel, IntPtr.Zero), Marshal.GetLastPInvokeError().ToString());
        RejectWithoutChanges(request);
        Assert.Equal("外部哨兵", File.ReadAllText(sentinel));
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("dotdot")]
    [InlineData("dot")]
    [InlineData("trailing-dot")]
    [InlineData("trailing-space")]
    [InlineData("ads")]
    [InlineData("device")]
    [InlineData("reserved")]
    [InlineData("missing-parent")]
    public void 非规范或不安全路径在任何写入前拒绝(string kind)
    {
        var request = Request();
        var path = kind switch
        {
            "relative" => "relative-report.txt",
            "dotdot" => Path.Combine(_root, "a", "..", "report.txt"),
            "dot" => Path.Combine(_root, ".", "report.txt"),
            "trailing-dot" => Path.Combine(_root, "report."),
            "trailing-space" => Path.Combine(_root, "report "),
            "ads" => Path.Combine(_root, "report:stream"),
            "device" => @"\\?\" + Path.Combine(_root, "report.txt"),
            "reserved" => Path.Combine(_root, "CON.txt"),
            _ => Path.Combine(_root, "missing", "report.txt"),
        };
        RejectWithoutChanges(request with { Outputs = [new AcceptanceOutput("report", path)] });
    }

    [Fact]
    public void 联接祖先被拒绝而外部目录没有新增资料()
    {
        var request = Request();
        var target = Path.Combine(_root, "external");
        var junction = Path.Combine(_root, "junction");
        Directory.CreateDirectory(target);
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '" + junction.Replace("'", "''", StringComparison.Ordinal) + "' -Target '" + target.Replace("'", "''", StringComparison.Ordinal) + "' | Out-Null");
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(15000));
        Assert.True(process.ExitCode == 0, error);
        try
        {
            RejectWithoutChanges(request with { Outputs = [new AcceptanceOutput("report", Path.Combine(junction, "report.txt"))] });
            Assert.Empty(Directory.EnumerateFileSystemEntries(target));
        }
        finally { Directory.Delete(junction); }
    }

    [Fact]
    public void 运行期间数据根及输出祖先不能被替换()
    {
        var parent = Path.Combine(_root, "output-parent");
        Directory.CreateDirectory(parent);
        var request = Request() with { Outputs = [new AcceptanceOutput("report", Path.Combine(parent, "report.txt"))] };
        using (var workspace = AcceptanceWorkspace.Create(request))
        {
            Assert.Throws<IOException>(() => Directory.Move(workspace.DataRoot, workspace.DataRoot + "-moved"));
            Assert.Throws<IOException>(() => Directory.Move(parent, parent + "-moved"));
            File.WriteAllText(Path.Combine(workspace.DataRoot, "settings.json"), "{}");
            workspace.GetOutput("report").WriteByte(42);
        }
        Directory.Move(parent, parent + "-moved");
        Assert.Equal(new byte[] { 42 }, File.ReadAllBytes(Path.Combine(parent + "-moved", "report.txt")));
    }

    [Fact]
    public void 持有工作区时既有保护根仍可写入文件并创建子目录()
    {
        var request = Request();
        Directory.CreateDirectory(request.ProtectedDataRoot);
        var settings = Path.Combine(request.ProtectedDataRoot, "settings.json");
        File.WriteAllText(settings, "旧设置");
        using var workspace = AcceptanceWorkspace.Create(request);
        File.WriteAllText(settings, "新设置");
        Directory.CreateDirectory(Path.Combine(request.ProtectedDataRoot, "icons"));
        Directory.CreateDirectory(Path.Combine(request.ProtectedDataRoot, "logs"));
        File.WriteAllText(Path.Combine(request.ProtectedDataRoot, "logs", "active.log"), "继续运行");
        Directory.CreateDirectory(Path.Combine(workspace.DataRoot, "icons"));
        Directory.CreateDirectory(Path.Combine(workspace.DataRoot, "logs"));
        Assert.Equal("新设置", File.ReadAllText(settings));
        Assert.Equal("继续运行", File.ReadAllText(Path.Combine(request.ProtectedDataRoot, "logs", "active.log")));
        Assert.True(Directory.Exists(Path.Combine(workspace.DataRoot, "icons")));
        Assert.True(Directory.Exists(Path.Combine(workspace.DataRoot, "logs")));
        Assert.Throws<IOException>(() => Directory.Move(request.ProtectedDataRoot, request.ProtectedDataRoot + "-moved"));
    }

    [Fact]
    public void 持有保护根时不拒绝其它目录写句柄但仍拒绝改名()
    {
        var request = Request();
        Directory.CreateDirectory(request.ProtectedDataRoot);
        using var workspace = AcceptanceWorkspace.Create(request);
        using var writer = OpenDirectoryForWrite(request.ProtectedDataRoot, 0x40000000, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        Assert.False(writer.IsInvalid, "保护根目录写句柄被拒绝，Win32=" + Marshal.GetLastPInvokeError());
        Assert.Throws<IOException>(() => Directory.Move(request.ProtectedDataRoot, request.ProtectedDataRoot + "-moved"));
    }

    [Fact]
    public void 重复输出名拒绝且释放后不能获取输出()
    {
        var request = Request();
        RejectWithoutChanges(request with { Outputs = [new AcceptanceOutput("report", Path.Combine(_root, "a.txt")), new AcceptanceOutput("report", Path.Combine(_root, "b.txt"))] });
        var workspace = AcceptanceWorkspace.Create(request);
        Assert.Throws<KeyNotFoundException>(() => workspace.GetOutput("absent"));
        workspace.Dispose();
        workspace.Dispose();
        Assert.Throws<ObjectDisposedException>(() => workspace.GetOutput("report"));
    }

    private AcceptanceRequest Request() => new(Path.Combine(_root, "protected"), Path.Combine(_root, "data"), [new AcceptanceOutput("report", Path.Combine(_root, "report.txt"))]);

    private void RejectWithoutChanges(AcceptanceRequest request)
    {
        var before = Snapshot();
        Assert.Throws<IOException>(() => AcceptanceWorkspace.Create(request));
        Assert.Equal(before, Snapshot());
    }

    private string[] Snapshot() => Directory.EnumerateFileSystemEntries(_root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
        .Select(path => Directory.Exists(path) ? path + "|directory" : path + "|" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))))
        .Order(StringComparer.Ordinal).ToArray();

    public void Dispose() => Directory.Delete(_root, true);

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenDirectoryForWrite(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flags, IntPtr templateFile);
}
