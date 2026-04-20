using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using BTCPayServer.Plugins.BTCMap.Models;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.BTCMap.Services;

public class OsmVersionConflictException : Exception
{
    public OsmVersionConflictException(string message) : base(message) { }
}

public class OsmApiClient
{
    private const string PluginVersion = "1.0.0";
    private const string CreatedBy = "BTCPayServer BTC Map Plugin v" + PluginVersion;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly OsmAuthService _osmAuthService;
    private readonly ILogger<OsmApiClient> _logger;

    public OsmApiClient(
        IHttpClientFactory httpClientFactory,
        OsmAuthService osmAuthService,
        ILogger<OsmApiClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _osmAuthService = osmAuthService;
        _logger = logger;
    }

    private string ApiBaseUrl => _osmAuthService.OsmApiBaseUrl;

    public async Task<OsmElement> GetElement(OsmServerSettings settings, string type, long id)
    {
        var client = _httpClientFactory.CreateClient("OsmApi");
        var response = await client.GetAsync($"{ApiBaseUrl}/api/0.6/{type}/{id}");
        response.EnsureSuccessStatusCode();

        var xml = await response.Content.ReadAsStringAsync();
        var doc = XDocument.Parse(xml);
        var element = doc.Root!.Element(type)!;

        var osmElement = new OsmElement
        {
            Type = type,
            Id = long.Parse(element.Attribute("id")!.Value),
            Version = int.Parse(element.Attribute("version")!.Value),
            Lat = element.Attribute("lat") != null
                ? double.Parse(element.Attribute("lat")!.Value, CultureInfo.InvariantCulture)
                : null,
            Lon = element.Attribute("lon") != null
                ? double.Parse(element.Attribute("lon")!.Value, CultureInfo.InvariantCulture)
                : null
        };

        foreach (var tag in element.Elements("tag"))
            osmElement.Tags[tag.Attribute("k")!.Value] = tag.Attribute("v")!.Value;

        foreach (var nd in element.Elements("nd"))
            osmElement.NodeRefs.Add(long.Parse(nd.Attribute("ref")!.Value));

        return osmElement;
    }

    public async Task<long> CreateChangeset(OsmServerSettings settings, string comment)
    {
        var xml = new XDocument(
            new XElement("osm",
                new XElement("changeset",
                    new XElement("tag", new XAttribute("k", "comment"), new XAttribute("v", comment)),
                    new XElement("tag", new XAttribute("k", "created_by"), new XAttribute("v", CreatedBy)),
                    new XElement("tag", new XAttribute("k", "source"),
                        new XAttribute("v", "Merchant self-report via BTCPay Server"))
                )
            )
        );

        var response = await SendAuthenticatedRequest(settings, HttpMethod.Put,
            $"{ApiBaseUrl}/api/0.6/changeset/create", xml.ToString());
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync();
        return long.Parse(body.Trim());
    }

    public async Task CloseChangeset(OsmServerSettings settings, long changesetId)
    {
        var response = await SendAuthenticatedRequest(settings, HttpMethod.Put,
            $"{ApiBaseUrl}/api/0.6/changeset/{changesetId}/close", null);

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            _logger.LogWarning("Changeset {ChangesetId} was already closed", changesetId);
            return;
        }

        response.EnsureSuccessStatusCode();
    }

    public async Task<long> CreateNode(OsmServerSettings settings, long changesetId, double lat, double lon,
        Dictionary<string, string> tags)
    {
        var nodeElement = new XElement("node",
            new XAttribute("changeset", changesetId),
            new XAttribute("lat", lat.ToString(CultureInfo.InvariantCulture)),
            new XAttribute("lon", lon.ToString(CultureInfo.InvariantCulture))
        );

        foreach (var tag in tags)
            nodeElement.Add(new XElement("tag", new XAttribute("k", tag.Key), new XAttribute("v", tag.Value)));

        var xml = new XDocument(new XElement("osm", nodeElement));

        var response = await SendAuthenticatedRequest(settings, HttpMethod.Put,
            $"{ApiBaseUrl}/api/0.6/node/create", xml.ToString());
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync();
        return long.Parse(body.Trim());
    }

    public async Task<int> UpdateElement(OsmServerSettings settings, long changesetId, OsmElement element)
    {
        XElement xmlElement;

        if (element.Type == "node")
        {
            xmlElement = new XElement("node",
                new XAttribute("id", element.Id),
                new XAttribute("changeset", changesetId),
                new XAttribute("version", element.Version),
                new XAttribute("lat", element.Lat!.Value.ToString(CultureInfo.InvariantCulture)),
                new XAttribute("lon", element.Lon!.Value.ToString(CultureInfo.InvariantCulture))
            );
        }
        else
        {
            xmlElement = new XElement("way",
                new XAttribute("id", element.Id),
                new XAttribute("changeset", changesetId),
                new XAttribute("version", element.Version)
            );

            foreach (var nodeRef in element.NodeRefs)
                xmlElement.Add(new XElement("nd", new XAttribute("ref", nodeRef)));
        }

        foreach (var tag in element.Tags)
            xmlElement.Add(new XElement("tag", new XAttribute("k", tag.Key), new XAttribute("v", tag.Value)));

        var xml = new XDocument(new XElement("osm", xmlElement));

        var response = await SendAuthenticatedRequest(settings, HttpMethod.Put,
            $"{ApiBaseUrl}/api/0.6/{element.Type}/{element.Id}", xml.ToString());

        if (response.StatusCode == HttpStatusCode.Conflict)
            throw new OsmVersionConflictException(
                $"Version conflict updating {element.Type}/{element.Id} at version {element.Version}");

        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync();
        return int.Parse(body.Trim());
    }

    private async Task<HttpResponseMessage> SendAuthenticatedRequest(
        OsmServerSettings settings, HttpMethod method, string url, string xmlBody)
    {
        var client = _httpClientFactory.CreateClient("OsmApi");
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", settings.OsmAccessToken);

        if (xmlBody != null)
            request.Content = new StringContent(xmlBody, Encoding.UTF8, "text/xml");

        return await client.SendAsync(request);
    }

    public static Dictionary<string, string> BuildBitcoinTags(bool acceptsOnchain, bool acceptsLightning)
    {
        var tags = new Dictionary<string, string>
        {
            ["currency:XBT"] = "yes",
            ["check_date:currency:XBT"] = DateTime.UtcNow.ToString("yyyy-MM-dd")
        };

        if (acceptsOnchain)
            tags["payment:onchain"] = "yes";
        if (acceptsLightning)
            tags["payment:lightning"] = "yes";

        return tags;
    }

    public static readonly HashSet<string> BitcoinTagKeys = new()
    {
        "currency:XBT",
        "payment:bitcoin",
        "payment:onchain",
        "payment:lightning",
        "payment:lightning_contactless",
        "check_date:currency:XBT"
    };
}
