using System.Collections.Generic;

namespace BTCPayServer.Plugins.BTCMap.Models;

public class OsmElement
{
    public string Type { get; set; }
    public long Id { get; set; }
    public int Version { get; set; }
    public double? Lat { get; set; }
    public double? Lon { get; set; }
    public Dictionary<string, string> Tags { get; set; } = new();
    public List<long> NodeRefs { get; set; } = new();
}
