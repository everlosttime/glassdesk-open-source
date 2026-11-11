using GlassDesk.Core.Storage;
using GlassDesk.Nearby;

namespace GlassDesk.AppServices.Tests;

public sealed class NearbyIdentityTests
{
    [Fact]
    public void IdentityIsLazyProtectedStableAndExcludedFromBackup()
    {
        var root = Path.Combine(Path.GetTempPath(), "GlassDesk-identity-" + Guid.NewGuid().ToString("N"));
        try
        {
            var data = new GlassDeskDataStore(new AppPaths(Path.Combine(root, "Data"))); data.LoadAll();
            using var host = new AppServiceHost(data);
            Assert.False(Directory.Exists(Path.Combine(data.Paths.RootDirectory, "runtime", "nearby")));
            var identity = new NearbyIdentityStore(Path.Combine(data.Paths.RootDirectory, "runtime", "nearby"));
            string fingerprint;
            using (var first = identity.LoadOrCreate()) fingerprint = CertificateIdentity.Fingerprint(first);
            using (var second = identity.LoadOrCreate()) Assert.Equal(fingerprint, CertificateIdentity.Fingerprint(second));
            var stored = File.ReadAllBytes(Path.Combine(data.Paths.RootDirectory, "runtime", "nearby", "identity.dpapi"));
            Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12(stored, null));
            var backup = data.Backup.BackupTo(Path.Combine(root, "backup"));
            Assert.True(backup.Succeeded);
            Assert.DoesNotContain(backup.Files, f => f.Contains("runtime", StringComparison.OrdinalIgnoreCase));
            Assert.False(host.Nearby.State.Running);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
