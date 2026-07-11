using System;
using System.IO;
using System.Threading.Tasks;
using RemoteDesktop.Shared.Security;
using Xunit;

namespace RemoteDesktop.Shared.Tests;

/// <summary>Audit M-A0 gelombang G1b: ketahanan auth & pin store.</summary>
public class SecurityRobustnessTests
{
    // ---------- AUD-006: GoogleIdTokenVerifier ----------

    [Fact]
    public async Task Google_MalformedToken_ReturnsNull_NotThrow()
    {
        var v = new GoogleIdTokenVerifier("my-audience");
        // These all fail before any JWKS network call and must return null, not throw.
        Assert.Null(await v.VerifyAsync("onlyonepart"));
        Assert.Null(await v.VerifyAsync("two.parts"));
        Assert.Null(await v.VerifyAsync("a.b.c"));          // parts[0]="a" is invalid base64 length
        Assert.Null(await v.VerifyAsync("!!!.@@@.###"));    // non-base64 chars
    }

    // ---------- AUD-005: PinStore fail-closed ----------

    [Fact]
    public void PinStore_MissingFile_IsFirstUse()
    {
        var path = TempPath();
        try
        {
            var store = new PinStore(path);
            Assert.False(store.IsCorrupt);
            Assert.Equal(PinCheck.FirstUse, store.Check("host:7443", "AA:BB"));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void PinStore_CorruptFile_FailsClosed_NotFirstUse()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path, "{ this is not valid json ]["); // present but unparseable
            var store = new PinStore(path);
            Assert.True(store.IsCorrupt);
            // Must NOT silently become FirstUse (which would drop MITM protection).
            Assert.Equal(PinCheck.Mismatch, store.Check("host:7443", "AA:BB"));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void PinStore_ValidFile_MatchesAndMismatches()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path, "{\"host:7443\":\"AA:BB:CC\"}");
            var store = new PinStore(path);
            Assert.False(store.IsCorrupt);
            Assert.Equal(PinCheck.Match, store.Check("host:7443", "aa:bb:cc"));   // case-insensitive
            Assert.Equal(PinCheck.Mismatch, store.Check("host:7443", "ZZ:ZZ"));
            Assert.Equal(PinCheck.FirstUse, store.Check("other:9999", "any"));
        }
        finally { Cleanup(path); }
    }

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "literemote-pintest-" + Guid.NewGuid().ToString("N") + ".json");

    private static void Cleanup(string path)
    {
        try { File.Delete(path); } catch { }
        try { File.Delete(path + ".corrupt"); } catch { }
    }
}
