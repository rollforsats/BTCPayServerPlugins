using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.BTCMap.Services.Osm;

/// <summary>
/// High-level OSM tagging operations. Each method is store-scoped: it loads the
/// access token from the OAuth repository and surfaces auth/connection errors
/// as typed exceptions (OsmAuthException, OsmNotConnectedException).
/// </summary>
public interface IOsmApiClient
{
    Task<OsmCreateResult> CreateNodeAsync(string storeId, BtcMapMerchant merchant, CancellationToken ct);

    Task<int> UpdateNodeAsync(string storeId, long nodeId, string nodeType, BtcMapMerchant merchant, CancellationToken ct);

    Task<int> ReverifyNodeAsync(string storeId, long nodeId, string nodeType, BtcMapMerchant merchant, CancellationToken ct);

    Task<OsmUnlistResult> UnlistNodeAsync(string storeId, long nodeId, string nodeType, string merchantName, CancellationToken ct);
}

public class OsmCreateResult
{
    public long NodeId { get; set; }
    public long ChangesetId { get; set; }
    public int Version { get; set; }
}

public class OsmUnlistResult
{
    public int? NewVersion { get; set; }
    public string[] RemovedTags { get; set; }
    public bool AlreadyUnlisted { get; set; }
}
