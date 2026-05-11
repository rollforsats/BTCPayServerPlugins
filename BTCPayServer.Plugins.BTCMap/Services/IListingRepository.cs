using System.Threading.Tasks;
using BTCPayServer.Plugins.BTCMap.Data;

namespace BTCPayServer.Plugins.BTCMap.Services;

public interface IListingRepository
{
    Task<BtcMapListing> GetListingForStoreAsync(string storeId);
}
