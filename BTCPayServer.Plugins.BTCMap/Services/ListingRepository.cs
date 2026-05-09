using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Plugins.BTCMap.Data;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.BTCMap.Services;

public class ListingRepository : IListingRepository
{
    private readonly BtcMapDbContextFactory _dbContextFactory;

    public ListingRepository(BtcMapDbContextFactory dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task<BtcMapListing> GetListingForStoreAsync(string storeId)
    {
        await using var ctx = _dbContextFactory.CreateContext();
        return await ctx.Listings
            .FirstOrDefaultAsync(l => l.StoreId == storeId && l.Status != ListingStatus.Pending);
    }
}
