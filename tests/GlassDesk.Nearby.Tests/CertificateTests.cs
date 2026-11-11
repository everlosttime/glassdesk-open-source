using System.Security.Cryptography.X509Certificates;

namespace GlassDesk.Nearby.Tests;

public sealed class CertificateTests
{
    [Fact]
    public void CertificateWithTamperedSelfSignatureIsRejected()
    {
        using var original = CertificateIdentity.Create();
        var bytes = original.Export(X509ContentType.Cert); bytes[^1] ^= 1;
        using var tampered = X509CertificateLoader.LoadCertificate(bytes);
        Assert.True(CertificateIdentity.Validate(original));
        Assert.False(CertificateIdentity.Validate(tampered));
    }
}
