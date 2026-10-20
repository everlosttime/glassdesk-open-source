using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GlassDesk.AppServices.Tests;

public sealed class PackageVerificationTests : IDisposable
{
    private static readonly FileVersionInfo TestVersionInfo = FileVersionInfo.GetVersionInfo(typeof(PackageVerificationTests).Assembly.Location);
    private static readonly string ProductVersion = TestVersionInfo.ProductVersion?.Trim() ?? "0.0.0.0";
    private static readonly string FileVersion = TestVersionInfo.FileVersion?.Trim() ?? "0.0.0.0";
    private static readonly string AssemblyVersion = typeof(PackageVerificationTests).Assembly.GetName().Version?.ToString(4) ?? "0.0.0.0";
    private const string AppId = "GlassDesk.Independent";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GlassDesk-PackageVerification-" + Guid.NewGuid().ToString("N"));

    public PackageVerificationTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void 未知发行状态仍然拒绝()
    {
        var fixture = CandidateFixture.Create(_root);
        var manifest = JsonNode.Parse(File.ReadAllText(fixture.ReleaseManifest))!;
        manifest["status"] = "external-unverified";
        File.WriteAllText(fixture.ReleaseManifest, manifest.ToJsonString());
        var result = RunVerifier(fixture);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Unsupported", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 有效的未签名候选包通过且校验不改动输入()
    {
        var fixture = CandidateFixture.Create(_root);
        var before = Directory.GetFiles(fixture.PackageDirectory).ToDictionary(path => Path.GetFileName(path)!, HashFile, StringComparer.OrdinalIgnoreCase);

        var result = RunVerifier(fixture);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("PASS", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, Directory.GetFiles(fixture.PackageDirectory).ToDictionary(path => Path.GetFileName(path)!, HashFile, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void 含中文路径的候选清单由WindowsPowerShell按UTF8读取()
    {
        var localizedRoot = Path.Combine(_root, "阶段七 本地候选");
        Directory.CreateDirectory(localizedRoot);
        var fixture = CandidateFixture.Create(localizedRoot);
        fixture.WriteBuildReportWithPackagePath();

        var result = RunVerifier(fixture);

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Contains("PASS", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("fileVersion", "9.8.7.6")]
    [InlineData("productVersion", "9.8.7-preview.6")]
    public void SetupPE版本必须与发行清单一致(string property, string mismatchedVersion)
    {
        var fixture = CandidateFixture.Create(_root);
        fixture.SetExpectedSetupMetadata(property, mismatchedVersion);

        var result = RunVerifier(fixture);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Setup", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Setup缺少PE版本资源时必须拒绝()
    {
        var fixture = CandidateFixture.Create(_root);
        fixture.ReplaceSetupWithInvalidBytes();

        var result = RunVerifier(fixture);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Setup", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 未指定发行清单时从脚本目录解析默认清单()
    {
        var standaloneRoot = Path.Combine(_root, "standalone-verifier");
        Directory.CreateDirectory(Path.Combine(standaloneRoot, "build"));
        Directory.CreateDirectory(Path.Combine(standaloneRoot, "packaging"));
        var fixture = CandidateFixture.Create(standaloneRoot);
        var script = Path.Combine(standaloneRoot, "build", "verify-package.ps1");
        File.Copy(Path.Combine(FindRepositoryRoot().FullName, "build", "verify-package.ps1"), script);
        File.Copy(fixture.ReleaseManifest, Path.Combine(standaloneRoot, "packaging", "release.json"));

        var result = RunVerifier(fixture, script, includeReleaseManifest: false);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("PASS", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("missing-record")]
    [InlineData("extra-record")]
    [InlineData("length")]
    [InlineData("hash")]
    public void 交付校验和必须与实际文件双向一致(string mutation)
    {
        var fixture = CandidateFixture.Create(_root);
        fixture.MutateDeliveryChecksums(mutation);

        var result = RunVerifier(fixture);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("checksum", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("missing-record")]
    [InlineData("extra-record")]
    [InlineData("length")]
    [InlineData("hash")]
    public void ZIP载荷清单必须与归档内容双向一致(string mutation)
    {
        var fixture = CandidateFixture.Create(_root);
        fixture.MutatePayloadManifest(mutation);

        var result = RunVerifier(fixture);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("payload", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("GlassDesk.Tests.dll")]
    [InlineData("GlassDesk.AppServices.Tests.dll")]
    [InlineData("GlassDesk.pdb")]
    [InlineData("src/GlassDeskWindow.cs")]
    [InlineData("sdk/hostpolicy.dll")]
    public void 即使清单与哈希匹配也拒绝测试SDK和源码内容(string path)
    {
        var fixture = CandidateFixture.Create(_root);
        fixture.AddPayloadFile(path, [0x4D, 0x5A, 0x01]);

        var result = RunVerifier(fixture);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("forbidden", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("C:/outside.txt")]
    [InlineData("folder\\outside.txt")]
    [InlineData("folder/../../outside.txt")]
    public void ZIP拒绝绝对路径和越界载荷路径(string path)
    {
        var fixture = CandidateFixture.Create(_root);
        fixture.AddPayloadFile(path, [0x01]);

        var result = RunVerifier(fixture);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("path", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ZIP拒绝重复的大小写不敏感路径()
    {
        var fixture = CandidateFixture.Create(_root);
        fixture.AddPayloadFile("glassdesk.exe", [0x4D, 0x5A, 0x02]);

        var result = RunVerifier(fixture);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("duplicate", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("LICENSE.txt")]
    [InlineData("ThirdPartyNotices.txt")]
    [InlineData("WINDOWS-SDK-LICENSE.txt")]
    [InlineData("INNO-SETUP-LICENSE.txt")]
    [InlineData("UNICODE-LICENSE.txt")]
    [InlineData("THIRD-PARTY-NOTICES.md")]
    public void 必须携带运行时与应用数据许可材料(string requiredFile)
    {
        var fixture = CandidateFixture.Create(_root);
        fixture.RemovePayloadFile(requiredFile);

        var result = RunVerifier(fixture);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("license", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 必须随候选载荷提供发行说明()
    {
        var fixture = CandidateFixture.Create(_root);
        fixture.RemovePayloadFile("RELEASE-NOTES.md");

        var result = RunVerifier(fixture);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("release-notes.md", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 候选目录拒绝未登记的额外文件()
    {
        var fixture = CandidateFixture.Create(_root);
        File.WriteAllText(Path.Combine(fixture.PackageDirectory, "unexpected.dll"), "extra");

        var result = RunVerifier(fixture);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("extra", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 发布身份必须与单一发行配置一致()
    {
        var fixture = CandidateFixture.Create(_root);
        fixture.WriteReleaseManifest(productVersion: "0.3.0-preview.1");

        var result = RunVerifier(fixture);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("version", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    private (int ExitCode, string Output) RunVerifier(CandidateFixture fixture, string? scriptPath = null, bool includeReleaseManifest = true)
    {
        var repository = FindRepositoryRoot();
        var script = scriptPath ?? Path.Combine(repository.FullName, "build", "verify-package.ps1");
        if (!File.Exists(script)) return (127, "verify-package.ps1 is not implemented.");
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(script);
        start.ArgumentList.Add("-PackageDirectory");
        start.ArgumentList.Add(fixture.PackageDirectory);
        if (includeReleaseManifest)
        {
            start.ArgumentList.Add("-ReleaseManifest");
            start.ArgumentList.Add(fixture.ReleaseManifest);
        }
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(30000), "Package verification timed out.");
        return (process.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "GlassDesk.slnx"))) repository = repository.Parent;
        Assert.NotNull(repository);
        return repository;
    }

    private static string HashFile(string? path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path!)));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class CandidateFixture
    {
        private readonly List<PayloadFile> _payload = [];

        private CandidateFixture(string root)
        {
            PackageDirectory = Path.Combine(root, "candidate-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(PackageDirectory);
            ReleaseManifest = Path.Combine(root, "release-" + Guid.NewGuid().ToString("N") + ".json");
            WriteReleaseManifest();
            _payload.AddRange(
            [
                new("GlassDesk.exe", [0x4D, 0x5A, 0x01]),
                new("LICENSE.txt", Encoding.UTF8.GetBytes("Microsoft .NET Library License")),
                new("ThirdPartyNotices.txt", Encoding.UTF8.GetBytes(".NET third-party notices")),
                new("WINDOWS-SDK-LICENSE.txt", Encoding.UTF8.GetBytes("Microsoft Windows SDK license")),
                new("INNO-SETUP-LICENSE.txt", Encoding.UTF8.GetBytes("Inno Setup license")),
                new("UNICODE-LICENSE.txt", Encoding.UTF8.GetBytes("Unicode license")),
                new("THIRD-PARTY-NOTICES.md", Encoding.UTF8.GetBytes("GlassDesk notices")),
                new("RELEASE-NOTES.md", Encoding.UTF8.GetBytes("Local preview candidate")),
                new("D3DCompiler_47_cor3.dll", [0x4D, 0x5A, 0x03]),
            ]);
            WriteCandidate();
        }

        public string PackageDirectory { get; }
        public string ReleaseManifest { get; }
        private string PackageBaseName => "GlassDesk-" + AssemblyVersion + "-win-x64";
        private string ZipPath => Path.Combine(PackageDirectory, PackageBaseName + ".zip");
        private string SetupPath => Path.Combine(PackageDirectory, PackageBaseName + "-Setup.exe");
        private string PayloadManifestPath => Path.Combine(PackageDirectory, "payload-manifest.json");
        private string ToolchainPath => Path.Combine(PackageDirectory, "toolchain.json");
        private string BuildReportPath => Path.Combine(PackageDirectory, "build-report.json");
        private string InnoLicensePath => Path.Combine(PackageDirectory, "INNO-SETUP-LICENSE.txt");
        private string ChecksumsPath => Path.Combine(PackageDirectory, "SHA256SUMS.json");

        public static CandidateFixture Create(string root) => new(root);

        public void WriteReleaseManifest(string? productVersion = null)
        {
            File.WriteAllText(ReleaseManifest, JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                productName = "GlassDesk",
                productVersion = productVersion ?? ProductVersion,
                assemblyVersion = AssemblyVersion,
                fileVersion = FileVersion,
                appId = AppId,
                rid = "win-x64",
                status = "local-candidate",
                packageBaseName = PackageBaseName,
                dotnetSdkVersion = "10.0.401",
                runtimeVersion = "10.0.12",
                innoVersion = "7.1.0",
                innoSetupInstallerFile = "innosetup-7.1.0-x64.exe",
                innoSetupInstallerSha256 = "0362a383ed217d4c4239b5933866dd96d3eb2102737da92f80f6057a4b40df2f",
            }));
        }

        public void WriteBuildReportWithPackagePath()
        {
            var options = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            File.WriteAllText(BuildReportPath, JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                productName = "GlassDesk",
                productVersion = ProductVersion,
                assemblyVersion = AssemblyVersion,
                fileVersion = FileVersion,
                appId = AppId,
                rid = "win-x64",
                status = "Built",
                packageDirectory = PackageDirectory,
            }, options), new UTF8Encoding(false));
            RebuildChecksums();
        }

        public void SetExpectedSetupMetadata(string property, string version)
        {
            foreach (var path in new[] { ReleaseManifest, PayloadManifestPath, BuildReportPath })
            {
                var document = JsonNode.Parse(File.ReadAllText(path))!;
                document[property] = version;
                File.WriteAllText(path, document.ToJsonString());
            }
            RebuildChecksums();
        }

        public void ReplaceSetupWithInvalidBytes()
        {
            File.WriteAllBytes(SetupPath, [0x4D, 0x5A, 0x00]);
            RebuildChecksums();
        }

        public void AddPayloadFile(string path, byte[] bytes)
        {
            _payload.Add(new(path, bytes));
            WriteCandidate();
        }

        public void RemovePayloadFile(string path)
        {
            _payload.RemoveAll(item => string.Equals(item.RelativePath, path, StringComparison.OrdinalIgnoreCase));
            WriteCandidate();
        }

        public void MutatePayloadManifest(string mutation)
        {
            var manifest = ReadObject(PayloadManifestPath);
            var files = manifest.RootElement.GetProperty("files").EnumerateArray().Select(item => JsonDocument.Parse(item.GetRawText()).RootElement.Clone()).ToList();
            switch (mutation)
            {
                case "missing-record": files.RemoveAt(0); break;
                case "extra-record": files.Add(JsonDocument.Parse("{\"relativePath\":\"ghost.txt\",\"length\":1,\"sha256\":\"00\"}").RootElement.Clone()); break;
                case "length": files[0] = Replace(files[0], "length", 999); break;
                case "hash": files[0] = Replace(files[0], "sha256", new string('0', 64)); break;
            }
            File.WriteAllText(PayloadManifestPath, JsonSerializer.Serialize(new
            {
                schemaVersion = manifest.RootElement.GetProperty("schemaVersion").GetInt32(),
                productName = manifest.RootElement.GetProperty("productName").GetString(),
                productVersion = manifest.RootElement.GetProperty("productVersion").GetString(),
                assemblyVersion = AssemblyVersion,
                fileVersion = manifest.RootElement.GetProperty("fileVersion").GetString(),
                appId = manifest.RootElement.GetProperty("appId").GetString(),
                rid = manifest.RootElement.GetProperty("rid").GetString(),
                files,
            }));
            RebuildChecksums();
        }

        public void MutateDeliveryChecksums(string mutation)
        {
            var manifest = ReadObject(ChecksumsPath);
            var files = manifest.RootElement.GetProperty("files").EnumerateArray().Select(item => JsonDocument.Parse(item.GetRawText()).RootElement.Clone()).ToList();
            switch (mutation)
            {
                case "missing-record": files.RemoveAt(0); break;
                case "extra-record": files.Add(JsonDocument.Parse("{\"relativePath\":\"ghost.bin\",\"length\":1,\"sha256\":\"00\"}").RootElement.Clone()); break;
                case "length": files[0] = Replace(files[0], "length", 999); break;
                case "hash": files[0] = Replace(files[0], "sha256", new string('0', 64)); break;
            }
            File.WriteAllText(ChecksumsPath, JsonSerializer.Serialize(new { schemaVersion = 1, files }));
        }

        private void WriteCandidate()
        {
            using (var stream = File.Create(ZipPath))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                foreach (var item in _payload)
                {
                    var entry = archive.CreateEntry(item.RelativePath);
                    using var output = entry.Open();
                    output.Write(item.Bytes);
                }
            }
            File.Copy(typeof(PackageVerificationTests).Assembly.Location, SetupPath, overwrite: true);
            File.WriteAllText(PayloadManifestPath, JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                productName = "GlassDesk",
                productVersion = ProductVersion,
                assemblyVersion = AssemblyVersion,
                fileVersion = FileVersion,
                appId = AppId,
                rid = "win-x64",
                files = _payload.Select(item => new { relativePath = item.RelativePath, length = item.Bytes.Length, sha256 = Convert.ToHexString(SHA256.HashData(item.Bytes)) }).ToArray(),
            }));
            File.WriteAllText(ToolchainPath, JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                sdkVersion = "10.0.401",
                runtimeVersion = "10.0.12",
                innoVersion = "7.1.0",
                innoPublisher = "Pyrsys B.V.",
                innoInstallerSignatureStatus = "Valid",
                innoInstallerAttestationStatus = "Verified",
                innoCompilerSignatureStatus = "Valid",
                innoInstallerSha256 = "0362a383ed217d4c4239b5933866dd96d3eb2102737da92f80f6057a4b40df2f",
                innoCompilerSha256 = new string('A', 64),
            }));
            File.WriteAllText(BuildReportPath, JsonSerializer.Serialize(new { schemaVersion = 1, productName = "GlassDesk", productVersion = ProductVersion, assemblyVersion = AssemblyVersion, fileVersion = FileVersion, appId = AppId, rid = "win-x64", status = "Built" }));
            File.WriteAllText(InnoLicensePath, "Inno Setup license notice");
            RebuildChecksums();
        }

        private void RebuildChecksums()
        {
            var files = Directory.GetFiles(PackageDirectory)
                .Where(path => !string.Equals(path, ChecksumsPath, StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .Select(path => new { relativePath = Path.GetFileName(path), length = new FileInfo(path).Length, sha256 = HashFile(path) })
                .ToArray();
            File.WriteAllText(ChecksumsPath, JsonSerializer.Serialize(new { schemaVersion = 1, files }));
        }

        private static JsonDocument ReadObject(string path) => JsonDocument.Parse(File.ReadAllText(path));

        private static JsonElement Replace(JsonElement element, string property, object value)
        {
            var values = element.EnumerateObject().ToDictionary(item => item.Name, item => item.NameEquals(property) ? value : (object)item.Value.Clone());
            return JsonDocument.Parse(JsonSerializer.Serialize(values)).RootElement.Clone();
        }

        private sealed record PayloadFile(string RelativePath, byte[] Bytes);
    }
}
