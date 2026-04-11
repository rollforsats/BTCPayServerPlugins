using System.Collections.Generic;
using System.Threading.Tasks;
using BTCPayServer.Plugins.BTCMap.Models;

namespace BTCPayServer.Plugins.BTCMap.Services;

public interface IOverpassApiClient
{
    Task<List<OverpassElement>> SearchNearby(double lat, double lon, int radiusMeters, string name);
    Task<List<OverpassElement>> SearchByAddress(double lat, double lon, int radiusMeters, string street, string city);
    Task<List<OverpassElement>> SearchByCoordinates(double lat, double lon, int radiusMeters);
    Task<List<OverpassElement>> CheckExistingBitcoinTags(double lat, double lon);
}
