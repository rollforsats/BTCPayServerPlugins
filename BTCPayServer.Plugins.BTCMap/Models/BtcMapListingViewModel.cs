using System;
using System.Collections.Generic;
using BTCPayServer.Plugins.BTCMap.Data;

namespace BTCPayServer.Plugins.BTCMap.Models;

public class BtcMapListingViewModel
{
    public BtcMapStoreSettings Settings { get; set; } = new();
    public BtcMapListing ExistingListing { get; set; }
    public bool OsmConnected { get; set; }
    public bool IsMainnet { get; set; }
    public string OsmDisplayName { get; set; }
    public List<OverpassElement> SearchResults { get; set; }
    public string StatusMessage { get; set; }
    public string Address { get; set; }
    public DateTimeOffset? DirectorySubmittedAt { get; set; }
    public string DirectoryExistingUrl { get; set; }
    public string DirectoryExistingName { get; set; }
    public string DirectoryExistingType { get; set; }
}
