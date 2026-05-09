using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.BTCMap.Services.Osm;

public interface IOsmChangesetManager
{
    /// <summary>Opens a changeset on OSM, returns the changeset id.</summary>
    Task<long> OpenAsync(string accessToken, string commentTemplate, string merchantName, CancellationToken ct);

    /// <summary>Best-effort close. Logs on failure, never throws.</summary>
    Task CloseAsync(string accessToken, long changesetId, CancellationToken ct);
}
