using System.Threading.Tasks;
using BTCPayServer.Plugins.BTCMap.Data;
using BTCPayServer.Plugins.BTCMap.Services;
using Xunit;

namespace BTCPayServer.Plugins.BTCMap.Tests;

public class BtcMapServiceTests
{
    [Fact]
    public async Task GetListingForStore_DelegatesToRepository()
    {
        var expected = new BtcMapListing { Id = "abc", StoreId = "store-1", Status = ListingStatus.Active };
        var repo = new StubListingRepository { ToReturn = expected };
        var service = new BtcMapService(
            dbContextFactory: null,
            listingRepository: repo,
            apiClient: null,
            overpassApiClient: null,
            osmApiClient: null,
            oauthRepo: null,
            logger: new NullLogger<BtcMapService>());

        var result = await service.GetListingForStore("store-1");

        Assert.Same(expected, result);
        Assert.Equal("store-1", repo.RequestedStoreId);
    }

    private class StubListingRepository : IListingRepository
    {
        public BtcMapListing ToReturn { get; set; }
        public string RequestedStoreId { get; private set; }

        public Task<BtcMapListing> GetListingForStoreAsync(string storeId)
        {
            RequestedStoreId = storeId;
            return Task.FromResult(ToReturn);
        }
    }
}
