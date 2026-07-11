using System;
using System.IO;
using System.Security.Cryptography;
using RemoteDesktop.Shared.Security;
using Xunit;

namespace RemoteDesktop.Shared.Tests;

/// <summary>
/// Audit M-A0 gelombang G2: proteksi kunci host at-rest via delegate (AUD-011).
/// Delegate palsu meniru DPAPI: protect membungkus dengan magic, unprotect MELEMPAR untuk data yang
/// tak terbungkus (persis seperti ProtectedData.Unprotect menolak data asing) — sehingga jalur migrasi
/// file lama tak-terenkripsi teruji tanpa perlu DPAPI Windows.
/// </summary>
public class CertificateManagerTests
{
    private static readonly byte[] Magic = { 0xDE, 0xAD };

    private static byte[] FakeProtect(byte[] d)
    {
        var r = new byte[d.Length + 2];
        Magic.CopyTo(r, 0);
        d.CopyTo(r, 2);
        return r;
    }

    private static byte[] FakeUnprotect(byte[] d)
    {
        if (d.Length < 2 || d[0] != Magic[0] || d[1] != Magic[1])
            throw new CryptographicException("not protected"); // mimic DPAPI rejecting foreign data
        return d[2..];
    }

    [Fact]
    public void Protected_WritesEncryptedFile_AndReloadsSameFingerprint()
    {
        var path = TempPath();
        try
        {
            var c1 = CertificateManager.GetOrCreateHostCertificate(path, protect: FakeProtect, unprotect: FakeUnprotect);
            var fp1 = CertificateManager.PublicKeyFingerprint(c1);

            // On disk it must be wrapped (magic header), not a raw PFX.
            var onDisk = File.ReadAllBytes(path);
            Assert.True(onDisk.Length >= 2 && onDisk[0] == Magic[0] && onDisk[1] == Magic[1]);

            var c2 = CertificateManager.GetOrCreateHostCertificate(path, protect: FakeProtect, unprotect: FakeUnprotect);
            Assert.Equal(fp1, CertificateManager.PublicKeyFingerprint(c2));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void LegacyPlaintextFile_UpgradesInPlace_WithoutChangingFingerprint()
    {
        var path = TempPath();
        try
        {
            // 1) Legacy host: written with no protection.
            var legacy = CertificateManager.GetOrCreateHostCertificate(path);
            var fpLegacy = CertificateManager.PublicKeyFingerprint(legacy);
            var beforeUpgrade = File.ReadAllBytes(path);
            Assert.False(beforeUpgrade.Length >= 2 && beforeUpgrade[0] == Magic[0] && beforeUpgrade[1] == Magic[1]);

            // 2) After the AUD-011 update, the host opens the same file WITH protection.
            var upgraded = CertificateManager.GetOrCreateHostCertificate(path, protect: FakeProtect, unprotect: FakeUnprotect);

            // Same key -> same pinned fingerprint (no client re-pin), and file is now protected.
            Assert.Equal(fpLegacy, CertificateManager.PublicKeyFingerprint(upgraded));
            var afterUpgrade = File.ReadAllBytes(path);
            Assert.True(afterUpgrade.Length >= 2 && afterUpgrade[0] == Magic[0] && afterUpgrade[1] == Magic[1]);
        }
        finally { Cleanup(path); }
    }

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "literemote-certtest-" + Guid.NewGuid().ToString("N") + ".pfx");

    private static void Cleanup(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
