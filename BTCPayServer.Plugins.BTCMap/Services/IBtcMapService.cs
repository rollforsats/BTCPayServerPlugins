using System.Collections.Generic;
using System.Threading.Tasks;
using BTCPayServer.Plugins.BTCMap.Data;
using BTCPayServer.Plugins.BTCMap.Models;

namespace BTCPayServer.Plugins.BTCMap.Services;

public interface IBtcMapService
{
    Task<BtcMapListing> GetListingForStore(string storeId);

    Task<List<OverpassElement>> SearchNearby(double lat, double lon, string name,
        string street = null, string city = null);

    Task<List<OverpassElement>> CheckDuplicates(double lat, double lon);

    Task<BtcMapListing> SubmitListing(string storeId, BtcMapStoreSettings settings,
        bool acceptsLightning, bool acceptsOnchain, bool submitToDirectory);

    Task UpdateListing(BtcMapListing listing, BtcMapStoreSettings settings,
        bool acceptsLightning, bool acceptsOnchain);

    Task<BtcMapSubmitResponse> SubmitToDirectoryOnly(BtcMapListing listing, BtcMapStoreSettings settings);
}
