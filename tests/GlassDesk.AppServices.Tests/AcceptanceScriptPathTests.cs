using System.Diagnostics;
using System.Text;

namespace GlassDesk.AppServices.Tests;

/// <summary>只提取并执行路径保护函数；绝不运行构建或发布脚本主体。</summary>
public sealed class AcceptanceScriptPathTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GlassDesk-ScriptGuard-" + Guid.NewGuid().ToString("N"));
    public AcceptanceScriptPathTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void 发布目标必须限制在当前仓库发布目录内()
    {
        var repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(repo);
        var result = RunGuard("publish.ps1", "Assert-SafePublishTarget", repo, Path.Combine(_root, "outside"));
        Assert.Equal(2, result.ExitCode);
        Assert.False(Directory.Exists(Path.Combine(_root, "outside")));
    }

    [Theory]
    [InlineData("build.ps1")]
    [InlineData("publish.ps1")]
    public void 应用进程包装器保留图形程序退出码并正确传递含空格参数(string script)
    {
        var captured = Path.Combine(_root, "captured argument.txt");
        var child = Path.Combine(_root, "child process.ps1");
        File.WriteAllText(child, """
            param([string]$Value)
            [IO.File]::WriteAllText($env:GD_CAPTURE_PATH, $Value)
            exit 17
            """);

        var result = RunInvoker(script, child, captured);

        Assert.Equal(17, result.ExitCode);
        Assert.Equal("argument containing spaces", File.ReadAllText(captured));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void WPF坞测试仅在显式UI验收时进入脚本测试集(bool includeWpf, bool expectedWpf)
    {
        var result = RunBuildPolicy(includeWpf, desktopAcceptance: false, isolatedDesktop: string.Empty);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expectedWpf, result.Output.Contains("GlassDesk.Dock.Tests", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, "", false)]
    [InlineData(true, "", false)]
    [InlineData(true, "1", true)]
    public void 第三四阶段交互验收默认关闭且显式启用必须使用隔离桌面(bool interactiveAcceptance, string isolatedDesktop, bool expectedAllowed)
    {
        var result = RunBuildPolicy(includeWpf: false, desktopAcceptance: false, isolatedDesktop, interactiveAcceptance, checkInteractivePolicy: true);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("|" + expectedAllowed, result.Output, StringComparison.OrdinalIgnoreCase);

        var repository = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(repository.FullName, "build", "build.ps1"));
        var gateIndex = source.IndexOf("if ($InteractiveAcceptance -and -not (Test-InteractiveAcceptanceAllowed", StringComparison.Ordinal);
        var dotnetIndex = source.IndexOf("$dotnet = Resolve-Dotnet", StringComparison.Ordinal);
        Assert.True(gateIndex >= 0 && gateIndex < dotnetIndex, "Interactive acceptance gate must run before build/test/UI setup.");
        Assert.Contains("[switch]$InteractiveAcceptance", source, StringComparison.Ordinal);
        Assert.Contains("if ($InteractiveAcceptance) {", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SkipInteractiveAcceptance", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, "", true)]
    [InlineData(true, "", false)]
    [InlineData(true, "1", true)]
    public void 真实桌面验收在非隔离桌面拒绝启动(bool desktopAcceptance, string isolatedDesktop, bool expectedAllowed)
    {
        var result = RunBuildPolicy(includeWpf: false, desktopAcceptance, isolatedDesktop);
        Assert.Equal(0, result.ExitCode);
        Assert.StartsWith(expectedAllowed.ToString(), result.Output, StringComparison.OrdinalIgnoreCase);

        if (desktopAcceptance)
        {
            var repository = FindRepositoryRoot();
            var source = File.ReadAllText(Path.Combine(repository.FullName, "build", "build.ps1"));
            var gateIndex = source.IndexOf("if (-not (Test-DesktopAcceptanceAllowed", StringComparison.Ordinal);
            var dotnetIndex = source.IndexOf("$dotnet = Resolve-Dotnet", StringComparison.Ordinal);
            Assert.True(gateIndex >= 0 && gateIndex < dotnetIndex, "Desktop gate must run before build/test/UI setup.");
        }
    }

    [Theory]
    [InlineData("build.ps1")]
    [InlineData("publish.ps1")]
    public void 证据目录拒绝仓库父目录的联接(string script)
    {
        var actual = Path.Combine(_root, "actual");
        var junction = Path.Combine(_root, "linked-parent");
        Directory.CreateDirectory(Path.Combine(actual, "repo"));
        RunPowerShell("$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path $env:GD_TEST_JUNCTION -Target $env:GD_TEST_ACTUAL | Out-Null", new() { ["GD_TEST_JUNCTION"] = junction, ["GD_TEST_ACTUAL"] = actual }, expectedExit: 0);
        try
        {
            var repo = Path.Combine(junction, "repo");
            var result = RunGuard(script, "Assert-SafeEvidenceDirectory", repo, Path.Combine(repo, "docs", "evidence", "阶段7"));
            Assert.Equal(2, result.ExitCode);
            Assert.False(Directory.Exists(Path.Combine(actual, "repo", "docs")));
        }
        finally { Directory.Delete(junction); }
    }

    [Theory]
    [InlineData("build.ps1")]
    [InlineData("publish.ps1")]
    public void 安全证据目录可验证且函数本身不创建目录(string script)
    {
        var repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(repo);
        var result = RunGuard(script, "Assert-SafeEvidenceDirectory", repo, Path.Combine(repo, "docs", "evidence", "阶段7"));
        Assert.True(result.ExitCode == 0, result.Output);
        Assert.False(Directory.Exists(Path.Combine(repo, "docs")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 发布目标只允许尚不存在的受控新目录(bool existing)
    {
        var repo = Path.Combine(_root, "repo");
        var parent = Path.Combine(repo, "artifacts", "publish");
        Directory.CreateDirectory(parent);
        var target = Path.Combine(parent, "new-candidate");
        if (existing) Directory.CreateDirectory(target);
        var result = RunGuard("publish.ps1", "Assert-SafePublishTarget", repo, target);
        Assert.Equal(existing ? 2 : 0, result.ExitCode);
        Assert.Equal(existing, Directory.Exists(target));
    }

    [Fact]
    public void 发布版本覆盖必须关闭自动提交号并传递为独立参数()
    {
        var repository = FindRepositoryRoot();
        const string command = """
            $ErrorActionPreference = 'Stop'
            $ProgressPreference = 'SilentlyContinue'
            try {
                $source = [IO.File]::ReadAllText($env:GD_TEST_SCRIPT, [Text.Encoding]::UTF8)
                $tokens = $null; $errors = $null
                $ast = [Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$errors)
                if ($errors.Count -ne 0) { throw 'Publish source has parse errors.' }
                $definition = $ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-VersionOverrideArguments' }, $false) | Select-Object -First 1
                if ($null -eq $definition) { exit 4 }
                Invoke-Expression $definition.Extent.Text
                $versionArguments = @(Get-VersionOverrideArguments -ProductVersion '0.2.0-preview.1' -AssemblyVersion '0.2.0.0' -FileVersion '0.2.0.0')
                Write-Output $versionArguments.Count
                $versionArguments | ForEach-Object { Write-Output $_ }
                exit 0
            } catch { [Console]::Error.WriteLine($_); exit 3 }
            """;
        var result = RunPowerShell(command, new()
        {
            ["GD_TEST_SCRIPT"] = Path.Combine(repository.FullName, "build", "publish.ps1"),
        });

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            ["4", "-p:Version=0.2.0-preview.1", "-p:AssemblyVersion=0.2.0.0", "-p:FileVersion=0.2.0.0", "-p:IncludeSourceRevisionInInformationalVersion=false"],
            result.Output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    [Fact]
    public void 打包器接受并核验自包含发布已携带的Unicode许可证()
    {
        var repository = FindRepositoryRoot();
        var stage = Path.Combine(_root, "package-stage");
        Directory.CreateDirectory(stage);
        File.Copy(
            Path.Combine(repository.FullName, "docs", "third-party", "unicode-license-v3.txt"),
            Path.Combine(stage, "UNICODE-LICENSE.txt"));
        const string command = """
            $ErrorActionPreference = 'Stop'
            $ProgressPreference = 'SilentlyContinue'
            try {
                $source = [IO.File]::ReadAllText($env:GD_TEST_SCRIPT, [Text.Encoding]::UTF8)
                $tokens = $null; $errors = $null
                $ast = [Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$errors)
                if ($errors.Count -ne 0) { throw 'Package source has parse errors.' }
                $definitions = $ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] }, $false)
                foreach ($name in @('Get-Sha256HexFromFile', 'Assert-RequiredLicenseSources')) {
                    $definition = $definitions | Where-Object Name -EQ $name | Select-Object -First 1
                    if ($null -eq $definition) { exit 4 }
                    Invoke-Expression $definition.Extent.Text
                }
                $repoRoot = $env:GD_TEST_REPO
                $required = @('LICENSE.txt', 'ThirdPartyNotices.txt', 'WINDOWS-SDK-LICENSE.txt', 'INNO-SETUP-LICENSE.txt', 'UNICODE-LICENSE.txt', 'THIRD-PARTY-NOTICES.md', 'RELEASE-NOTES.md')
                Assert-RequiredLicenseSources -DotnetRoot $env:GD_TEST_DOTNET_ROOT -Stage $env:GD_TEST_STAGE
                foreach ($name in $required) {
                    if (-not (Test-Path -LiteralPath (Join-Path $env:GD_TEST_STAGE $name) -PathType Leaf)) { throw "Missing staged license or notice: $name" }
                }
                Write-Output 'PASS'
                exit 0
            } catch { [Console]::Error.WriteLine($_); exit 2 }
            """;
        var result = RunPowerShell(command, new()
        {
            ["GD_TEST_SCRIPT"] = Path.Combine(repository.FullName, "build", "package.ps1"),
            ["GD_TEST_REPO"] = repository.FullName,
            ["GD_TEST_DOTNET_ROOT"] = Path.Combine(repository.FullName, "docs", "third-party", "dotnet-10.0.12"),
            ["GD_TEST_STAGE"] = stage,
        });

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Contains("PASS", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Inno编译参数必须逐项传递且保留含空格路径()
    {
        var repository = FindRepositoryRoot();
        const string command = """
            $ErrorActionPreference = 'Stop'
            $ProgressPreference = 'SilentlyContinue'
            try {
                $source = [IO.File]::ReadAllText($env:GD_TEST_SCRIPT, [Text.Encoding]::UTF8)
                $tokens = $null; $errors = $null
                $ast = [Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$errors)
                if ($errors.Count -ne 0) { throw 'Package source has parse errors.' }
                $definition = $ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-InnoCompileArguments' }, $false) | Select-Object -First 1
                if ($null -eq $definition) { exit 4 }
                Invoke-Expression $definition.Extent.Text
                $arguments = @(Get-InnoCompileArguments -AppId 'GlassDesk.Independent' -ProductVersion '0.2.0-preview.1' -FileVersion '0.2.0.0' -PackageBaseName 'GlassDesk-0.2.0-preview.1-win-x64' -OutputDirectory 'D:\candidate packages\GlassDesk' -ScriptPath 'D:\repo with spaces\packaging\GlassDesk.iss')
                Write-Output $arguments.Count
                $arguments | ForEach-Object { Write-Output $_ }
                exit 0
            } catch { [Console]::Error.WriteLine($_); exit 3 }
            """;
        var result = RunPowerShell(command, new()
        {
            ["GD_TEST_SCRIPT"] = Path.Combine(repository.FullName, "build", "package.ps1"),
        });

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Equal(
            ["6", "/DAppIdValue=GlassDesk.Independent", "/DProductVersion=0.2.0-preview.1", "/DFileVersion=0.2.0.0", "/DPackageBaseName=GlassDesk-0.2.0-preview.1-win-x64", "/OD:\\candidate packages\\GlassDesk", "D:\\repo with spaces\\packaging\\GlassDesk.iss"],
            result.Output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private (int ExitCode, string Output) RunGuard(string script, string guard, string repo, string target)
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "GlassDesk.slnx"))) repository = repository.Parent;
        Assert.NotNull(repository);
        const string command = """
            $ErrorActionPreference = 'Stop'
            try {
                $source = [IO.File]::ReadAllText($env:GD_TEST_SCRIPT, [Text.Encoding]::UTF8)
                $tokens = $null; $errors = $null
                $ast = [Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$errors)
                if ($errors.Count -ne 0) { throw 'Guard source has parse errors.' }
                $definitions = $ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] }, $false)
                foreach ($definition in $definitions) { Invoke-Expression $definition.Extent.Text }
            } catch { [Console]::Error.WriteLine($_); exit 3 }
            try {
                if ($env:GD_TEST_GUARD -eq 'Assert-SafePublishTarget') {
                    Assert-SafePublishTarget -Path $env:GD_TEST_TARGET -ProtectedDataRoot $env:GD_TEST_PROTECTED -RepositoryRoot $env:GD_TEST_REPO
                } else {
                    Assert-SafeEvidenceDirectory -Path $env:GD_TEST_TARGET -RepositoryRoot $env:GD_TEST_REPO
                }
                exit 0
            } catch { [Console]::Error.WriteLine($_); exit 2 }
            """;
        return RunPowerShell(command, new()
        {
            ["GD_TEST_SCRIPT"] = Path.Combine(repository!.FullName, "build", script),
            ["GD_TEST_GUARD"] = guard,
            ["GD_TEST_TARGET"] = target,
            ["GD_TEST_PROTECTED"] = Path.Combine(_root, "protected"),
            ["GD_TEST_REPO"] = repo,
        });
    }

    private (int ExitCode, string Output) RunInvoker(string script, string child, string captured)
    {
        var repository = FindRepositoryRoot();
        const string command = """
            $ErrorActionPreference = 'Stop'
            try {
                $source = [IO.File]::ReadAllText($env:GD_TEST_SCRIPT, [Text.Encoding]::UTF8)
                $tokens = $null; $errors = $null
                $ast = [Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$errors)
                if ($errors.Count -ne 0) { throw 'App process helper source has parse errors.' }
                $definitions = $ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] }, $false)
                $requiredNames = @('ConvertTo-ProcessArgument', 'Invoke-WaitingProcess')
                foreach ($name in $requiredNames) {
                    $definition = $definitions | Where-Object Name -EQ $name | Select-Object -First 1
                    if ($null -eq $definition) { exit 4 }
                    Invoke-Expression $definition.Extent.Text
                }
                $arguments = @('-NoProfile', '-NonInteractive', '-File', $env:GD_TEST_CHILD, 'argument containing spaces')
                exit [int](Invoke-WaitingProcess -FilePath 'powershell.exe' -Arguments $arguments)
            } catch { [Console]::Error.WriteLine($_); exit 3 }
            """;
        return RunPowerShell(command, new()
        {
            ["GD_TEST_SCRIPT"] = Path.Combine(repository!.FullName, "build", script),
            ["GD_TEST_CHILD"] = child,
            ["GD_CAPTURE_PATH"] = captured,
        });
    }

    private (int ExitCode, string Output) RunBuildPolicy(bool includeWpf, bool desktopAcceptance, string isolatedDesktop, bool interactiveAcceptance = false, bool checkInteractivePolicy = false)
    {
        var repository = FindRepositoryRoot();
        const string command = """
            $ErrorActionPreference = 'Stop'
            try {
                $source = [IO.File]::ReadAllText($env:GD_TEST_SCRIPT, [Text.Encoding]::UTF8)
                $tokens = $null; $errors = $null
                $ast = [Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$errors)
                if ($errors.Count -ne 0) { throw 'Build policy source has parse errors.' }
                $definitions = $ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] }, $false)
                foreach ($name in @('Get-TestProjects', 'Test-DesktopAcceptanceAllowed')) {
                    $definition = $definitions | Where-Object Name -EQ $name | Select-Object -First 1
                    if ($null -eq $definition) { exit 4 }
                    Invoke-Expression $definition.Extent.Text
                }
                $includeWpf = $env:GD_TEST_INCLUDE_WPF -eq '1'
                $desktopAcceptance = $env:GD_TEST_DESKTOP -eq '1'
                $projects = @(Get-TestProjects -IncludeWpf:$includeWpf)
                $allowed = Test-DesktopAcceptanceAllowed -DesktopAcceptance:$desktopAcceptance -IsolationFlag:$env:GD_TEST_ISOLATED
                $interactiveAllowed = $false
                if ($env:GD_TEST_CHECK_INTERACTIVE -eq '1') {
                    $interactiveDefinition = $definitions | Where-Object Name -EQ 'Test-InteractiveAcceptanceAllowed' | Select-Object -First 1
                    if ($null -eq $interactiveDefinition) { exit 4 }
                    Invoke-Expression $interactiveDefinition.Extent.Text
                    $interactiveAllowed = Test-InteractiveAcceptanceAllowed -InteractiveAcceptance:($env:GD_TEST_INTERACTIVE -eq '1') -IsolationFlag:$env:GD_TEST_ISOLATED
                }
                Write-Output ($allowed.ToString() + '|' + ($projects -join ';') + '|' + $interactiveAllowed.ToString())
                exit 0
            } catch { [Console]::Error.WriteLine($_); exit 3 }
            """;
        return RunPowerShell(command, new()
        {
            ["GD_TEST_SCRIPT"] = Path.Combine(repository.FullName, "build", "build.ps1"),
            ["GD_TEST_INCLUDE_WPF"] = includeWpf ? "1" : "0",
            ["GD_TEST_DESKTOP"] = desktopAcceptance ? "1" : "0",
            ["GD_TEST_ISOLATED"] = isolatedDesktop,
            ["GD_TEST_INTERACTIVE"] = interactiveAcceptance ? "1" : "0",
            ["GD_TEST_CHECK_INTERACTIVE"] = checkInteractivePolicy ? "1" : "0",
        });
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "GlassDesk.slnx"))) repository = repository.Parent;
        Assert.NotNull(repository);
        return repository;
    }

    private static (int ExitCode, string Output) RunPowerShell(string command, Dictionary<string, string> environment, int? expectedExit = null)
    {
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(command)));
        foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(20000));
        var output = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
        if (expectedExit.HasValue) Assert.True(process.ExitCode == expectedExit.Value, output);
        return (process.ExitCode, output);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
