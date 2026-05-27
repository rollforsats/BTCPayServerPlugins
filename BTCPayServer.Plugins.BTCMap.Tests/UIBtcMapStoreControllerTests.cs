using BTCPayServer.Plugins.BTCMap.Controllers;
using Xunit;

namespace BTCPayServer.Plugins.BTCMap.Tests;

public class UIBtcMapStoreControllerTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("+44 20 8452 7891", true)]
    [InlineData("+1 5551234567", true)]
    [InlineData("5551234567", false)]
    [InlineData("020 8452 7891", false)]
    public void PhoneIsWellFormed_AcceptsLeadingPlusOnly(string input, bool expected)
    {
        Assert.Equal(expected, UIBtcMapStoreController.PhoneIsWellFormed(input));
    }
}
