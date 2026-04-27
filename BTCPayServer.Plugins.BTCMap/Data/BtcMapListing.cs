using System;
using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.Plugins.BTCMap.Data;

public class BtcMapListing
{
    public string Id { get; set; }

    [Required]
    public string StoreId { get; set; }
    public string OsmElementType { get; set; }
    public long OsmElementId { get; set; }
    public int OsmElementVersion { get; set; }
    public string BusinessName { get; set; }
    public string Category { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string HouseNumber { get; set; }
    public string Street { get; set; }
    public string City { get; set; }
    public string PostCode { get; set; }
    public string Country { get; set; }
    public bool AcceptsLightning { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastVerifiedAt { get; set; }
    public ListingStatus Status { get; set; }
    public DateTimeOffset? DirectorySubmittedAt { get; set; }
    public string DirectorySubmittedUrl { get; set; }
    public string DirectoryPrUrl { get; set; }
    public string Url { get; set; }
    public string Description { get; set; }
    public string Twitter { get; set; }
    public string Github { get; set; }
    public string OnionUrl { get; set; }
    public string DirectoryType { get; set; }
    public string DirectorySubType { get; set; }
}

public enum ListingStatus
{
    Active,
    Unlisted,
    Error,
    Pending
}
