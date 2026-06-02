namespace BTCPayServer.Plugins.BTCMap.Models;

public class BtcMapSubmitRequest
{
    public string Name { get; set; }
    public string Url { get; set; }
    public string Description { get; set; }

    public string Type { get; set; }
    public string SubType { get; set; }
    public string Country { get; set; }
    public string Twitter { get; set; }
    public string Github { get; set; }
    public string OnionUrl { get; set; }
    public string Phone { get; set; }

    public double? Lat { get; set; }
    public double? Lon { get; set; }
    public string Category { get; set; }
    public string ExternalId { get; set; }

    public string HouseNumber { get; set; }
    public string Street { get; set; }
    public string City { get; set; }
    public string Postcode { get; set; }

    public string Email { get; set; }

    public bool? AcceptsOnchain { get; set; }
    public bool? AcceptsLightning { get; set; }

    public bool SubmitToDirectory { get; set; } = true;
    public bool SubmitToBtcMap { get; set; }
}
