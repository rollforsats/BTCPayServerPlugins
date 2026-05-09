namespace BTCPayServer.Plugins.BTCMap.Services.Osm;

/// <summary>
/// Plain DTO carrying the merchant data the OSM tagging path needs. Decouples the
/// OSM services from BtcMapStoreSettings (form-bound) and BtcMapListing (persistence).
/// </summary>
public class BtcMapMerchant
{
    public string Name { get; set; }
    public string OsmCategory { get; set; }
    public string Url { get; set; }
    public bool AcceptsLightning { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string HouseNumber { get; set; }
    public string Street { get; set; }
    public string City { get; set; }
    public string PostCode { get; set; }
    public string Country { get; set; }
}
