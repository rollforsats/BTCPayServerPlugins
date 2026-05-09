using System.Threading.Tasks;

namespace BTCPayServer.Plugins.BTCMap.Services;

public interface INominatimApiClient
{
    Task<(double lat, double lon)?> Geocode(string street, string city, string postcode, string country);
}
