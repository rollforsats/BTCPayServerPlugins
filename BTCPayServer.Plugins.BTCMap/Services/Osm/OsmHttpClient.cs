using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.BTCMap.Services.Osm.Exceptions;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace BTCPayServer.Plugins.BTCMap.Services.Osm;

public class OsmHttpClient : IOsmHttpClient
{
    public const string HttpClientName = "OsmApi";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly BTCPayNetworkProvider _networkProvider;
    private readonly ILogger<OsmHttpClient> _logger;

    public OsmHttpClient(
        IHttpClientFactory httpClientFactory,
        BTCPayNetworkProvider networkProvider,
        ILogger<OsmHttpClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _networkProvider = networkProvider;
        _logger = logger;
    }

    private bool IsMainnet => _networkProvider.NetworkType == ChainName.Mainnet;

    private string ApiBase => IsMainnet
        ? "https://api.openstreetmap.org/api/0.6/"
        : "https://master.apis.dev.openstreetmap.org/api/0.6/";

    public async Task<string> PutXmlAsync(string accessToken, string path, string xmlBody, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(new Uri(ApiBase), path))
        {
            Content = new StringContent(xmlBody, Encoding.UTF8, "text/xml")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.UserAgent.ParseAdd(OsmUserAgent.Value);

        using var response = await client.SendAsync(request, ct);
        await EnsureSuccessAsync(response, $"PUT {path}", ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    public async Task<string> GetStringAsync(string accessToken, string path, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(ApiBase), path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.UserAgent.ParseAdd(OsmUserAgent.Value);

        using var response = await client.SendAsync(request, ct);
        await EnsureSuccessAsync(response, $"GET {path}", ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, string path, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        var truncated = body.Length > 500 ? body[..500] + "...(truncated)" : body;
        _logger.LogError("OSM upstream failure {Status} {Path}: {Body}", (int)response.StatusCode, path, truncated);

        throw response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new OsmAuthException(path, body),
            HttpStatusCode.Conflict => new OsmConflictException(path, body),
            HttpStatusCode.TooManyRequests => new OsmRateLimitException(path, body),
            >= HttpStatusCode.InternalServerError => new OsmServerException((int)response.StatusCode, path, body),
            _ => new OsmException((int)response.StatusCode, path, $"OSM {(int)response.StatusCode} {path}: {body}")
        };
    }
}

internal static class OsmUserAgent
{
    public static readonly string Value = $"BTCPay-BtcMaps-Plugin/{typeof(OsmUserAgent).Assembly.GetName().Version}";
}
