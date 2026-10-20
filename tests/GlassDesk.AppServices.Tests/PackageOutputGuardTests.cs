using System.Diagnostics;
using System.Text;

namespace GlassDesk.AppServices.Tests;

/// <summary>Runs only Assert-SafeOutputTarget extracted from package.ps1 against temporary paths.</summary>
public sealed class PackageOutputGuardTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GlassDesk-PackageOutputGuard-" + Guid.NewGuid().ToString("N"));

    public PackageOutputGuardTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Allows_nonexistent_child_under_controlled_root()
    {
        var allowedRoot = MakeAllowedRoot();
        var target = Path.Combine(allowedRoot, "candidate-new");

        var result = RunGuard(target, allowedRoot, Path.Combine(_root, "protected-data"));

        Assert.Equal(0, result.ExitCode);
        Assert.False(Directory.Exists(target));
        Assert.False(File.Exists(target));
    }

    [Theory]
    [InlineData("outside")]
    [InlineData("controlled-evil")]
    [InlineData(".")]
    public void Rejects_target_outside_or_equal_to_controlled_root(string relativeTarget)
    {
        var allowedRoot = MakeAllowedRoot();
        var target = relativeTarget == "." ? allowedRoot : Path.Combine(_root, relativeTarget);

        var result = RunGuard(target, allowedRoot, Path.Combine(_root, "protected-data"));

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(relativeTarget == ".", Directory.Exists(target));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rejects_existing_directory_or_file_without_changing_contents(bool existingFile)
    {
        var allowedRoot = MakeAllowedRoot();
        var target = Path.Combine(allowedRoot, "already-there");
        string sentinel;
        if (existingFile)
        {
            File.WriteAllText(target, "existing-output-sentinel");
            sentinel = target;
        }
        else
        {
            Directory.CreateDirectory(target);
            sentinel = Path.Combine(target, "keep.txt");
            File.WriteAllText(sentinel, "existing-output-sentinel");
        }

        var result = RunGuard(target, allowedRoot, Path.Combine(_root, "protected-data"));

        Assert.Equal(2, result.ExitCode);
        Assert.Equal("existing-output-sentinel", File.ReadAllText(sentinel));
        Assert.Equal(existingFile, File.Exists(target));
        Assert.Equal(!existingFile, Directory.Exists(target));
    }

    [Theory]
    [InlineData("equal")]
    [InlineData("target-under-data")]
    [InlineData("data-under-target")]
    public void Rejects_targets_that_overlap_protected_data(string relation)
    {
        var allowedRoot = MakeAllowedRoot();
        var protectedRoot = Path.Combine(allowedRoot, "protected-data");
        string target;
        switch (relation)
        {
            case "equal":
                target = protectedRoot;
                break;
            case "target-under-data":
                target = Path.Combine(protectedRoot, "child-output");
                break;
            default:
                target = protectedRoot;
                protectedRoot = Path.Combine(target, "user-data");
                break;
        }

        var result = RunGuard(target, allowedRoot, protectedRoot);

        Assert.Equal(2, result.ExitCode);
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public void Rejects_target_reached_through_junction_ancestor_without_creating_in_real_target()
    {
        var realAllowedRoot = Path.Combine(_root, "real-allowed");
        Directory.CreateDirectory(realAllowedRoot);
        var junction = Path.Combine(_root, "linked-allowed");
        CreateDirectoryJunction(junction, realAllowedRoot);
        try
        {
            var target = Path.Combine(junction, "new-output");
            var result = RunGuard(target, junction, Path.Combine(_root, "protected-data"));

            Assert.Equal(2, result.ExitCode);
            Assert.False(Directory.Exists(Path.Combine(realAllowedRoot, "new-output")));
            Assert.Empty(Directory.EnumerateFileSystemEntries(realAllowedRoot));
        }
        finally
        {
            Directory.Delete(junction);
        }
    }

    private string MakeAllowedRoot()
    {
        var path = Path.Combine(_root, "controlled");
        Directory.CreateDirectory(path);
        return path;
    }

    private (int ExitCode, string Output) RunGuard(string target, string allowedRoot, string protectedDataRoot)
    {
        var repository = FindRepositoryRoot();
        const string command = """
            $ErrorActionPreference = 'Stop'
            try {
                $source = [IO.File]::ReadAllText($env:GD_TEST_SCRIPT, [Text.Encoding]::UTF8)
                $tokens = $null; $errors = $null
                $ast = [Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$errors)
                if ($errors.Count -ne 0) { throw 'Guard source has parse errors.' }
                $definitions = $ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] }, $false)
                foreach ($definition in $definitions) {
                    if ($definition.Name -in @('Test-PathOverlap', 'Assert-SafeOutputTarget')) {
                        Invoke-Expression $definition.Extent.Text
                    }
                }
            } catch { [Console]::Error.WriteLine($_); exit 3 }
            try {
                Assert-SafeOutputTarget -Path $env:GD_TEST_TARGET -AllowedRoot $env:GD_TEST_ALLOWED_ROOT -ProtectedDataRoot $env:GD_TEST_PROTECTED
                exit 0
            } catch { [Console]::Error.WriteLine($_); exit 2 }
            """;

        return RunPowerShell(command, new()
        {
            ["GD_TEST_SCRIPT"] = Path.Combine(repository.FullName, "build", "package.ps1"),
            ["GD_TEST_TARGET"] = target,
            ["GD_TEST_ALLOWED_ROOT"] = allowedRoot,
            ["GD_TEST_PROTECTED"] = protectedDataRoot,
        });
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "GlassDesk.slnx"))) repository = repository.Parent;
        Assert.NotNull(repository);
        return repository;
    }

    private static void CreateDirectoryJunction(string junction, string target)
    {
        const string command = "New-Item -ItemType Junction -Path $env:GD_TEST_JUNCTION -Target $env:GD_TEST_TARGET | Out-Null";
        var result = RunPowerShell(command, new()
        {
            ["GD_TEST_JUNCTION"] = junction,
            ["GD_TEST_TARGET"] = target,
        });
        Assert.True(result.ExitCode == 0, result.Output);
    }

    private static (int ExitCode, string Output) RunPowerShell(string command, Dictionary<string, string> environment)
    {
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(command)));
        foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(20000));
        return (process.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
