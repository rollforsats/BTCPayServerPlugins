using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.Plugins.BTCMap.Models;

public class BtcMapStoreSettings
{
    [Required]
    public string BusinessName { get; set; }

    [Required]
    public string Category { get; set; }

    public string HouseNumber { get; set; }
    public string Street { get; set; }
    public string City { get; set; }
    public string PostCode { get; set; }
    public string Country { get; set; }

    [Phone]
    public string Phone { get; set; }

    [Required]
    [Range(-90, 90)]
    public double? Latitude { get; set; }

    [Required]
    [Range(-180, 180)]
    public double? Longitude { get; set; }

    // Website URL — always required, used as OSM website= tag and (when selected) in directory submission
    [Required]
    public string Url { get; set; }

    // Directory submission — checkbox on form controls whether these are sent
    public bool SubmitToDirectory { get; set; } = true;
    public string DirectoryDescription { get; set; }
    public string DirectoryTwitter { get; set; }
    public string DirectoryGithub { get; set; }
    public string DirectoryOnionUrl { get; set; }
    public string DirectoryType { get; set; } = "merchants";
    public string DirectorySubType { get; set; }
}
