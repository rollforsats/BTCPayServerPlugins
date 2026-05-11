namespace BTCPayServer.Plugins.BTCMap.Models;

public class BtcMapSubmitResponse
{
    public BtcMapDirectoryResult Directory { get; set; }
}

public class BtcMapDirectoryResult
{
    public string PrUrl { get; set; }
    public int? PrNumber { get; set; }
    public string Branch { get; set; }
    public string Skipped { get; set; }
}
