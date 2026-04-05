using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.Plugins.BTCMap.Models;

public class BtcMapStoreSettings
{
    [Required]
    public string BusinessName { get; set; }

    [Required]
    public string Category { get; set; }

    public string Street { get; set; }
    public string City { get; set; }
    public string PostCode { get; set; }
    public string Country { get; set; }

    [Required]
    [Range(-90, 90)]
    public double? Latitude { get; set; }

    [Required]
    [Range(-180, 180)]
    public double? Longitude { get; set; }

    public bool AcceptsOnchain { get; set; } = true;
    public bool AcceptsLightning { get; set; } = true;
}
