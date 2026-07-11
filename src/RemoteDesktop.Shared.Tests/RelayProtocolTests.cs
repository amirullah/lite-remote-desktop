using RemoteDesktop.Shared.Relay;
using Xunit;

namespace RemoteDesktop.Shared.Tests;

/// <summary>Audit M-A0 gelombang G1c: normalisasi id relay.</summary>
public class RelayProtocolTests
{
    [Fact]
    public void NormalizeId_KeepsOnlyAsciiDigits()
    {
        Assert.Equal("123456789", RelayProtocol.NormalizeId("123 456 789"));
        Assert.Equal("123456789", RelayProtocol.NormalizeId("123-456-789"));
        Assert.Equal("123", RelayProtocol.NormalizeId("abc123def"));
    }

    [Fact]
    public void NormalizeId_StripsNonAsciiUnicodeDigits()
    {
        // Arabic-Indic digits ٤٥٦ are Unicode category Nd (char.IsDigit == true) but must be dropped.
        Assert.Equal("789", RelayProtocol.NormalizeId("٤٥٦789"));
    }

    [Fact]
    public void FormatId_GroupsNineDigits()
    {
        Assert.Equal("123 456 789", RelayProtocol.FormatId("123456789"));
        Assert.Equal("12345", RelayProtocol.FormatId("12345")); // non-9-digit passes through
    }
}
