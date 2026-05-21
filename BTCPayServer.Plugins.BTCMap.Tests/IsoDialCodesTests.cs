using System.Text.RegularExpressions;
using BTCPayServer.Plugins.BTCMap.Controllers;
using BTCPayServer.Plugins.BTCMap.Models;
using Xunit;

namespace BTCPayServer.Plugins.BTCMap.Tests;

public class IsoDialCodesByIso2Tests
{
    [Fact]
    public void ByIso2_IsNonEmpty()
    {
        Assert.True(IsoDialCodes.ByIso2.Count >= 200,
            $"Expected at least 200 ISO-2 → dial-code entries, got {IsoDialCodes.ByIso2.Count}");
    }

    [Fact]
    public void ByIso2_EveryValueIsPlusThenDigits()
    {
        var pattern = new Regex(@"^\+\d{1,4}$");
        foreach (var (iso2, code) in IsoDialCodes.ByIso2)
            Assert.True(pattern.IsMatch(code),
                $"{iso2} dial code {code} is not '+' followed by 1-4 digits");
    }

    [Theory]
    [InlineData("GB", "+44")]
    [InlineData("US", "+1")]
    [InlineData("BS", "+1242")]
    [InlineData("DE", "+49")]
    public void ForCountry_KnownIso_ReturnsDialCode(string iso2, string expected)
    {
        Assert.Equal(expected, IsoDialCodes.ForCountry(iso2));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("XX")]
    [InlineData("ZZ")]
    public void ForCountry_NullOrUnknown_ReturnsNull(string iso2)
    {
        Assert.Null(IsoDialCodes.ForCountry(iso2));
    }

    [Theory]
    [InlineData("gb", "+44")]
    [InlineData("Us", "+1")]
    [InlineData("dE", "+49")]
    public void ForCountry_IsCaseInsensitive(string iso2, string expected)
    {
        Assert.Equal(expected, IsoDialCodes.ForCountry(iso2));
    }
}

public class PhoneIsWellFormedTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("+44 20 8452 7891", true)]
    [InlineData("  +44 20 8452 7891", true)]
    [InlineData("+1", true)]
    [InlineData("555-1234", false)]
    [InlineData("44 20 8452 7891", false)]
    public void Cases(string input, bool expected)
        => Assert.Equal(expected, UIBtcMapStoreController.PhoneIsWellFormed(input));
}
