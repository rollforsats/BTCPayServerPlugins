using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace BTCPayServer.Plugins.BTCMap.Services.Osm;

/// <summary>
/// Narrow HTTP wrapper for OSM API 0.6 calls. Carries Authorization: Bearer + User-Agent
/// per call. Maps non-success responses to typed Osm* exceptions so callers don't see raw
/// HTTP status codes. The XML body return type is the OSM API's native content type for
/// most endpoints; node ID / version responses come back as plain text inside the same
/// HttpResponseMessage and callers parse that themselves.
/// </summary>
public interface IOsmHttpClient
{
    Task<string> PutXmlAsync(string accessToken, string path, string xmlBody, CancellationToken ct);

    Task<string> GetStringAsync(string accessToken, string path, CancellationToken ct);
}
