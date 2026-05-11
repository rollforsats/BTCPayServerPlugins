using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.BTCMap.Services.Osm.Exceptions;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace BTCPayServer.Plugins.BTCMap.Services.Osm;

// Rationale for not routing through IOsmHttpClient:
//   - /oauth2/token and /oauth2/revoke are unauthenticated (use client_id+client_secret in
//     the form body, not the merchant's bearer token), so IOsmHttpClient's bearer-attaching
//     contract doesn't apply.
//   - Token-exchange errors arrive as a JSON body with {error, error_description} that
//     OsmTokenExchangeException carries — the typed-exception map in IOsmHttpClient.EnsureSuccessAsync
//     would lose that detail.
//   - User-Agent comes from the shared OsmUserAgent static, so the two HTTP surfaces can't drift.
// /api/0.6/user/details.json *is* a bearer call and could move onto IOsmHttpClient if we ever extend
// the interface to expose typed JSON GETs; deferred.
public class OsmAuthService : IOsmAuthService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly BTCPayNetworkProvider _networkProvider;
    private readonly ILogger<OsmAuthService> _logger;

    public OsmAuthService(
        IHttpClientFactory httpClientFactory,
        BTCPayNetworkProvider networkProvider,
        ILogger<OsmAuthService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _networkProvider = networkProvider;
        _logger = logger;
    }

    public bool IsMainnet => _networkProvider.NetworkType == ChainName.Mainnet;

    private string OsmAuthBaseUrl => IsMainnet
        ? "https://www.openstreetmap.org"
        : "https://master.apis.dev.openstreetmap.org";

    private string OsmApiBaseUrl => IsMainnet
        ? "https://api.openstreetmap.org"
        : "https://master.apis.dev.openstreetmap.org";

    public string GetAuthorizationUrl(string clientId, string redirectUri, string state)
    {
        return $"{OsmAuthBaseUrl}/oauth2/authorize" +
               $"?response_type=code" +
               $"&client_id={Uri.EscapeDataString(clientId)}" +
               $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
               $"&scope=write_api+read_prefs" +
               $"&state={Uri.EscapeDataString(state)}";
    }

    public async Task<string> ExchangeCodeForTokenAsync(
        string clientId, string clientSecret, string code, string redirectUri, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient(OsmHttpClient.HttpClientName);
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["redirect_uri"] = redirectUri
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{OsmAuthBaseUrl}/oauth2/token")
        {
            Content = content
        };
        request.Headers.UserAgent.ParseAdd(OsmUserAgent.Value);

        using var response = await client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var (error, description) = TryParseOAuthError(body);
            _logger.LogWarning("OSM token exchange failed status={Status} error={Error} description={Description}",
                (int)response.StatusCode, error, description);
            throw new OsmTokenExchangeException((int)response.StatusCode, error, description, body);
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "OSM token response contains invalid JSON");
            throw new OsmTokenExchangeException(
                (int)response.StatusCode, "invalid_json",
                "Response body is not valid JSON", body);
        }
        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("access_token", out var tokenProp) ||
                tokenProp.ValueKind != JsonValueKind.String)
            {
                _logger.LogWarning("OSM token response missing access_token field");
                throw new OsmTokenExchangeException(
                    (int)response.StatusCode, "missing_token",
                    "Response missing access_token field", body);
            }
            var token = tokenProp.GetString();
            if (string.IsNullOrEmpty(token))
                throw new OsmTokenExchangeException(
                    (int)response.StatusCode, "empty_token",
                    "Empty access_token in response", body);
            return token;
        }
    }

    public async Task<string> GetDisplayNameAsync(string accessToken, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient(OsmHttpClient.HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{OsmApiBaseUrl}/api/0.6/user/details.json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.UserAgent.ParseAdd(OsmUserAgent.Value);

        using var response = await client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("OSM user/details failed status={Status} body={Body}",
                (int)response.StatusCode, body);
            if ((int)response.StatusCode == 401)
                throw new OsmAuthException("/api/0.6/user/details.json", body);
            throw new OsmException((int)response.StatusCode, "/api/0.6/user/details.json",
                $"OSM {(int)response.StatusCode} /api/0.6/user/details.json", body);
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "OSM user/details response contains invalid JSON");
            throw new OsmException((int)response.StatusCode,
                "/api/0.6/user/details.json",
                "Response body is not valid JSON", body);
        }
        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("user", out var userProp) ||
                !userProp.TryGetProperty("display_name", out var nameProp))
            {
                _logger.LogWarning("OSM /api/0.6/user/details.json missing user.display_name");
                throw new OsmException((int)response.StatusCode,
                    "/api/0.6/user/details.json",
                    "Response missing user.display_name", body);
            }
            return nameProp.GetString() ?? "Unknown";
        }
    }

    public async Task RevokeAsync(string clientId, string clientSecret, string accessToken, CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient(OsmHttpClient.HttpClientName);
            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["token"] = accessToken,
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret
            });
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{OsmAuthBaseUrl}/oauth2/revoke")
            {
                Content = content
            };
            request.Headers.UserAgent.ParseAdd(OsmUserAgent.Value);

            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("OSM /oauth2/revoke returned non-success status={Status} body={Body}",
                    (int)response.StatusCode, body);
            }
        }
        catch (Exception ex)
        {
            // Disconnect must complete locally regardless of the OSM round-trip.
            _logger.LogWarning(ex, "OSM revoke best-effort failed; continuing with local Disconnect");
        }
    }

    private static (string error, string description) TryParseOAuthError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var error = root.TryGetProperty("error", out var e) ? e.GetString() ?? "" : "";
            var desc = root.TryGetProperty("error_description", out var d) ? d.GetString() ?? "" : "";
            return (error, desc);
        }
        catch
        {
            return ("", "");
        }
    }
}
