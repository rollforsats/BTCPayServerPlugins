namespace BTCPayServer.Plugins.BTCMap.Models;

public class BtcMapSubmitRequest
{
    public string Name { get; set; }
    public string Url { get; set; }
    public string Description { get; set; }

    // Directory fields (required when SubmitToDirectory = true)
    public string Type { get; set; }
    public string SubType { get; set; }
    public string Country { get; set; }
    public string Twitter { get; set; }
    public string Github { get; set; }
    public string OnionUrl { get; set; }

    // OSM element identification (required when tagging existing; null to create new)
    public long? OsmNodeId { get; set; }
    public string OsmNodeType { get; set; }

    // Required when creating new node (OsmNodeId is null)
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }

    // Maps to OSM amenity= tag (default: "shop" on API side)
    public string OsmCategory { get; set; }

    // Action flags — at least one must be true
    public bool SubmitToDirectory { get; set; }
    public bool TagOnOsm { get; set; }

    // Auto-detected from store's enabled payment methods
    public bool AcceptsLightning { get; set; } = true;

    // Remove bitcoin-related tags from an existing OSM element.
    // Mutually exclusive with TagOnOsm and SubmitToDirectory.
    public bool UnlistFromOsm { get; set; }
}
