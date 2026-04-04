using System.Collections.Generic;
using BTCPayServer.Plugins.BTCMap.Data;

namespace BTCPayServer.Plugins.BTCMap.Models;

public class BtcMapListingViewModel
{
    public BtcMapStoreSettings Settings { get; set; } = new();
    public BtcMapListing ExistingListing { get; set; }
    public bool OsmConnected { get; set; }
    public bool IsMainnet { get; set; }
    public bool IsAdmin { get; set; }
    public string OsmDisplayName { get; set; }
    public string OsmClientId { get; set; }
    public string OsmClientSecret { get; set; }
    public List<OverpassElement> SearchResults { get; set; }
    public string StatusMessage { get; set; }
}
