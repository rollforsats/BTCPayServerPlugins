using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace BTCPayServer.Plugins.BTCMap.Data;

public class BtcMapListing
{
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public string Id { get; set; }

    public string StoreId { get; set; }
    public string OsmElementType { get; set; }
    public long OsmElementId { get; set; }
    public int OsmElementVersion { get; set; }
    public string BusinessName { get; set; }
    public string Category { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string Street { get; set; }
    public string City { get; set; }
    public string PostCode { get; set; }
    public string Country { get; set; }
    public bool AcceptsOnchain { get; set; }
    public bool AcceptsLightning { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastVerifiedAt { get; set; }
    public ListingStatus Status { get; set; }
}

public enum ListingStatus
{
    Active,
    Unlisted,
    Error
}
