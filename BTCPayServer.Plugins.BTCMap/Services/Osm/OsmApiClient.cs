using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using BTCPayServer.Plugins.BTCMap.Services.Osm.Exceptions;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.BTCMap.Services.Osm;

public class OsmApiClient : IOsmApiClient
{
    private const int MaxUpdateAttempts = 3;

    /// <summary>
    /// Bitcoin-acceptance tags this service removes when un-listing. Mirrors plugin-builder
    /// BtcMapsService.cs:461-467. Keeps `website`, `name`, `amenity`, and address tags
    /// intact since those are not bitcoin-specific (a venue may remain on OSM after it
    /// stops accepting bitcoin). payment:bitcoin is included for historical nodes tagged
    /// before the deprecation-vs-currency:XBT switch.
    /// </summary>
    public static readonly string[] BitcoinAcceptanceTagKeys =
    {
        "currency:XBT",
        "payment:bitcoin",
        "payment:lightning",
        "payment:onchain"
    };

    private readonly IOsmHttpClient _http;
    private readonly IOsmChangesetManager _changesets;
    private readonly IOsmTagBuilder _tagBuilder;
    private readonly IBtcMapStoreOAuthRepository _oauthRepo;
    private readonly ILogger<OsmApiClient> _logger;

    public OsmApiClient(
        IOsmHttpClient http,
        IOsmChangesetManager changesets,
        IOsmTagBuilder tagBuilder,
        IBtcMapStoreOAuthRepository oauthRepo,
        ILogger<OsmApiClient> logger)
    {
        _http = http;
        _changesets = changesets;
        _tagBuilder = tagBuilder;
        _oauthRepo = oauthRepo;
        _logger = logger;
    }

    public async Task<OsmCreateResult> CreateNodeAsync(string storeId, BtcMapMerchant merchant, CancellationToken ct)
    {
        var token = await GetTokenAsync(storeId);
        var changesetId = await _changesets.OpenAsync(token, OsmChangesetManager.CommentCreate, merchant.Name, ct);
        try
        {
            var merge = _tagBuilder.BuildMerge(merchant, existingTags: null);

            var newNode = new XElement("node",
                new XAttribute("changeset", changesetId),
                new XAttribute("lat", merchant.Latitude!.Value.ToString("R", CultureInfo.InvariantCulture)),
                new XAttribute("lon", merchant.Longitude!.Value.ToString("R", CultureInfo.InvariantCulture)));
            foreach (var (k, v) in merge.SetTags)
                newNode.Add(new XElement("tag", new XAttribute("k", k), new XAttribute("v", v)));

            var doc = new XDocument(new XElement("osm", newNode));
            var body = await _http.PutXmlAsync(token, "node/create", doc.ToString(), ct);
            var nodeId = long.Parse(body.Trim());
            _logger.LogInformation("Created OSM node id={NodeId} in changeset={ChangesetId}", nodeId, changesetId);

            return new OsmCreateResult { NodeId = nodeId, ChangesetId = changesetId, Version = 1 };
        }
        finally
        {
            await _changesets.CloseAsync(token, changesetId, ct);
        }
    }

    public Task<int> UpdateNodeAsync(string storeId, long nodeId, string nodeType, BtcMapMerchant merchant, CancellationToken ct)
        => UpdateNodeInternal(storeId, nodeId, nodeType, merchant, OsmChangesetManager.CommentUpdate, ct);

    public Task<int> ReverifyNodeAsync(string storeId, long nodeId, string nodeType, BtcMapMerchant merchant, CancellationToken ct)
        => UpdateNodeInternal(storeId, nodeId, nodeType, merchant, OsmChangesetManager.CommentUpdate, ct);

    private async Task<int> UpdateNodeInternal(
        string storeId, long nodeId, string nodeType, BtcMapMerchant merchant, string commentTemplate, CancellationToken ct)
    {
        var token = await GetTokenAsync(storeId);
        nodeType = NormalizeNodeType(nodeType);
        var elementPath = $"{nodeType}/{nodeId}";

        var changesetId = await _changesets.OpenAsync(token, commentTemplate, merchant.Name, ct);
        try
        {
            for (var attempt = 1; attempt <= MaxUpdateAttempts; attempt++)
            {
                var elementXml = await _http.GetStringAsync(token, elementPath, ct);
                var elementDoc = XDocument.Parse(elementXml);
                var elementEl = elementDoc.Root?.Element(nodeType)
                    ?? throw new InvalidOperationException($"OSM element <{nodeType}> not found in response");

                elementEl.SetAttributeValue("changeset", changesetId);

                var existingTags = ReadExistingTags(elementEl);
                var merge = _tagBuilder.BuildMerge(merchant, existingTags);
                ApplyMergeToElement(elementEl, merge);

                try
                {
                    var body = await _http.PutXmlAsync(token, elementPath, elementDoc.ToString(), ct);
                    var newVersion = int.Parse(body.Trim());
                    _logger.LogInformation(
                        "Updated OSM element {ElementPath} -> newVersion={NewVersion} in changeset={ChangesetId} (attempt {Attempt})",
                        elementPath, newVersion, changesetId, attempt);
                    return newVersion;
                }
                catch (OsmConflictException) when (attempt < MaxUpdateAttempts)
                {
                    _logger.LogWarning(
                        "OSM 409 on PUT {ElementPath} attempt {Attempt}/{Max}; refetching and retrying within same changeset",
                        elementPath, attempt, MaxUpdateAttempts);
                }
            }

            // Fall-through is unreachable: attempts < MaxUpdateAttempts retry, attempt
            // == MaxUpdateAttempts re-throws.
            throw new InvalidOperationException("Unreachable: update retry loop exhausted without resolution.");
        }
        finally
        {
            await _changesets.CloseAsync(token, changesetId, ct);
        }
    }

    public async Task<OsmUnlistResult> UnlistNodeAsync(
        string storeId, long nodeId, string nodeType, string merchantName, CancellationToken ct)
    {
        var token = await GetTokenAsync(storeId);
        nodeType = NormalizeNodeType(nodeType);
        var elementPath = $"{nodeType}/{nodeId}";

        var elementXml = await _http.GetStringAsync(token, elementPath, ct);
        var elementDoc = XDocument.Parse(elementXml);
        var elementEl = elementDoc.Root?.Element(nodeType)
            ?? throw new InvalidOperationException($"OSM element <{nodeType}> not found in response");

        var removableKeys = BitcoinAcceptanceTagKeys
            .Where(k => elementEl.Elements("tag").Any(t => (string)t.Attribute("k") == k))
            .ToArray();

        if (removableKeys.Length == 0)
        {
            _logger.LogInformation("OSM unlist short-circuit on {ElementPath}: already-unlisted, no changeset opened",
                elementPath);
            return new OsmUnlistResult { AlreadyUnlisted = true };
        }

        var changesetId = await _changesets.OpenAsync(token, OsmChangesetManager.CommentUnlist, merchantName, ct);
        try
        {
            elementEl.SetAttributeValue("changeset", changesetId);
            foreach (var key in removableKeys)
            {
                var existing = elementEl.Elements("tag").FirstOrDefault(t => (string)t.Attribute("k") == key);
                existing?.Remove();
            }

            var body = await _http.PutXmlAsync(token, elementPath, elementDoc.ToString(), ct);
            var newVersion = int.Parse(body.Trim());
            _logger.LogInformation(
                "Unlisted OSM element {ElementPath} -> newVersion={NewVersion} removedTags=[{RemovedKeys}] in changeset={ChangesetId}",
                elementPath, newVersion, string.Join(",", removableKeys), changesetId);

            return new OsmUnlistResult
            {
                NewVersion = newVersion,
                RemovedTags = removableKeys
            };
        }
        finally
        {
            await _changesets.CloseAsync(token, changesetId, ct);
        }
    }

    private async Task<string> GetTokenAsync(string storeId)
    {
        var oauth = await _oauthRepo.GetForStoreAsync(storeId);
        if (oauth == null || string.IsNullOrEmpty(oauth.OsmAccessToken))
            throw new OsmNotConnectedException(storeId);
        return oauth.OsmAccessToken;
    }

    private static string NormalizeNodeType(string nodeType)
    {
        if (string.IsNullOrWhiteSpace(nodeType))
            throw new ArgumentException("nodeType is required", nameof(nodeType));
        var normalized = nodeType.Trim().ToLowerInvariant();
        if (normalized is not ("node" or "way" or "relation"))
            throw new ArgumentOutOfRangeException(nameof(nodeType), "nodeType must be one of: node, way, relation");
        return normalized;
    }

    private static Dictionary<string, string> ReadExistingTags(XElement elementEl)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tag in elementEl.Elements("tag"))
        {
            var k = (string)tag.Attribute("k");
            var v = (string)tag.Attribute("v");
            if (!string.IsNullOrEmpty(k))
                dict[k] = v ?? string.Empty;
        }
        return dict;
    }

    private static void ApplyMergeToElement(XElement elementEl, OsmTagMerge merge)
    {
        foreach (var (k, v) in merge.SetTags)
        {
            var existing = elementEl.Elements("tag").FirstOrDefault(t => (string)t.Attribute("k") == k);
            if (existing != null)
                existing.SetAttributeValue("v", v);
            else
                elementEl.Add(new XElement("tag", new XAttribute("k", k), new XAttribute("v", v)));
        }
        foreach (var k in merge.RemoveTags)
        {
            var existing = elementEl.Elements("tag").FirstOrDefault(t => (string)t.Attribute("k") == k);
            existing?.Remove();
        }
    }
}
