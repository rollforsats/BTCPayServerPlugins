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
}
