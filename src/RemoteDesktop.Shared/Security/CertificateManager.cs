using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace RemoteDesktop.Shared.Security;

/// <summary>
/// Manages the host's self-signed TLS identity and the client's trust-on-first-use (TOFU)
/// pin store. We deliberately do not depend on a public CA: the host mints its own ECDSA
/// certificate, and the client pins the SHA-256 of the public key on first connect. Any later
/// change of key (i.e. a man-in-the-middle) is rejected loudly.
/// </summary>
public static class CertificateManager
{
    /// <summary>Create (or load) the host's persistent self-signed certificate.</summary>
    public static X509Certificate2 GetOrCreateHostCertificate(string pfxPath, string subjectCn = "RemoteDesktopHost")
    {
        if (File.Exists(pfxPath))
        {
            try
            {
                return new X509Certificate2(pfxPath, (string?)null, X509KeyStorageFlags.Exportable);
            }
            catch { /* corrupt / unreadable — regenerate below */ }
        }

        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest($"CN={subjectCn}", ecdsa, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") /* serverAuth */ }, true));

        var now = DateTimeOffset.UtcNow;
        using var cert = req.CreateSelfSigned(now.AddDays(-1), now.AddYears(5));

        var exportable = new X509Certificate2(
            cert.Export(X509ContentType.Pfx),
            (string?)null,
            X509KeyStorageFlags.Exportable);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(pfxPath))!);
        File.WriteAllBytes(pfxPath, exportable.Export(X509ContentType.Pfx));
        return exportable;
    }

    /// <summary>
    /// SHA-256 of the certificate's SubjectPublicKeyInfo, formatted as colon-separated hex.
    /// This is what the user reads aloud / compares out-of-band to trust a new host.
    /// </summary>
    public static string PublicKeyFingerprint(X509Certificate2 cert)
    {
        var spki = cert.PublicKey.ExportSubjectPublicKeyInfo();
        var hash = SHA256.HashData(spki);
        return Convert.ToHexString(hash);
    }

    public static string FormatFingerprint(string hex) =>
        string.Join(":", Enumerable.Range(0, hex.Length / 2).Select(i => hex.Substring(i * 2, 2)));
}
