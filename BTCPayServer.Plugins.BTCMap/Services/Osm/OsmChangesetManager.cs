using System;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.BTCMap.Services.Osm;

public class OsmChangesetManager : IOsmChangesetManager
{
    public const string SourceTag = "BTCPay Server BTCMap plugin";

    public const string CommentCreate = "Add {name} as a bitcoin-accepting place via BTCPay Server #btcmap";
    public const string CommentUpdate = "Tag {name} as accepting bitcoin via BTCPay Server #btcmap";
    public const string CommentReverify = "Re-verify {name} still accepts bitcoin via BTCPay Server #btcmap";
    public const string CommentUnlist = "Un-list {name} from bitcoin-accepting places via BTCPay Server #btcmap";

    private readonly IOsmHttpClient _http;
    private readonly ILogger<OsmChangesetManager> _logger;

    public OsmChangesetManager(IOsmHttpClient http, ILogger<OsmChangesetManager> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<long> OpenAsync(string accessToken, string commentTemplate, string merchantName, CancellationToken ct)
    {
        var comment = (commentTemplate ?? string.Empty).Replace("{name}", merchantName ?? string.Empty);
        var xml = new XDocument(
            new XElement("osm",
                new XElement("changeset",
                    new XElement("tag", new XAttribute("k", "created_by"), new XAttribute("v", OsmUserAgent.Value)),
                    new XElement("tag", new XAttribute("k", "comment"), new XAttribute("v", comment)),
                    new XElement("tag", new XAttribute("k", "source"), new XAttribute("v", SourceTag)))));

        var body = await _http.PutXmlAsync(accessToken, "changeset/create", xml.ToString(), ct);
        var changesetId = long.Parse(body.Trim());
        _logger.LogInformation("Opened OSM changeset id={ChangesetId}", changesetId);
        return changesetId;
    }

    public async Task CloseAsync(string accessToken, long changesetId, CancellationToken ct)
    {
        try
        {
            await _http.PutXmlAsync(accessToken, $"changeset/{changesetId}/close", string.Empty, ct);
            _logger.LogInformation("Closed OSM changeset {ChangesetId}", changesetId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to close OSM changeset {ChangesetId}", changesetId);
        }
    }
}
