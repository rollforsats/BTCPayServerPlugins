using System;
using System.Collections.Generic;
using BTCPayServer.Plugins.BTCMap.Data;

namespace BTCPayServer.Plugins.BTCMap.Models;

public class BtcMapListingViewModel
{
    public BtcMapStoreSettings Settings { get; set; } = new();
    public BtcMapListing ExistingListing { get; set; }
    public bool IsMainnet { get; set; }
    public List<OverpassElement> SearchResults { get; set; }
    public string StatusMessage { get; set; }
    public string Address { get; set; }
    public DateTimeOffset? DirectorySubmittedAt { get; set; }
    public string DirectoryPrUrl { get; set; }
    public string DirectoryMergedName { get; set; }
    public string DirectoryMergedType { get; set; }
    public string DirectoryMergedUrl { get; set; }
    public int? DaysUntilVerificationExpires { get; set; }

    // OSM OAuth (per-store)
    public string RedirectUriToShow { get; set; }
    public OsmConnectionState OsmState { get; set; } = OsmConnectionState.NotConfigured;
    public OsmConnectionErrorKind OsmErrorKind { get; set; } = OsmConnectionErrorKind.None;
    public string OsmErrorMessage { get; set; }
    public string OsmUsername { get; set; }
    public DateTimeOffset? OsmConnectedAt { get; set; }
    public string OsmClientIdMasked { get; set; }
    public OsmCredentialsViewModel OsmCredentials { get; set; } = new();
}
