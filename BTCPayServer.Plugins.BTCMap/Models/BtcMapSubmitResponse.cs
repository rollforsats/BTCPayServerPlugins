namespace BTCPayServer.Plugins.BTCMap.Models;

public class BtcMapSubmitResponse
{
    public BtcMapDirectoryResult Directory { get; set; }
    public BtcMapOsmResult Osm { get; set; }
}

public class BtcMapDirectoryResult
{
    public string PrUrl { get; set; }
    public int? PrNumber { get; set; }
    public string Branch { get; set; }
    public string Skipped { get; set; }
}

public class BtcMapOsmResult
{
    public long? ChangesetId { get; set; }
    public long? NodeId { get; set; }
    public string NodeType { get; set; }
    public int? NewVersion { get; set; }
    public string Skipped { get; set; }
    public bool Created { get; set; }
    public string[] RemovedTags { get; set; }
}
