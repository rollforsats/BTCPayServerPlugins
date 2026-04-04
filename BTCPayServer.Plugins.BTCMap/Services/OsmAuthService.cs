using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Plugins.BTCMap.Models;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace BTCPayServer.Plugins.BTCMap.Services;

public class OsmAuthService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ISettingsRepository _settingsRepository;
    private readonly BTCPayNetworkProvider _networkProvider;
    private readonly ILogger<OsmAuthService> _logger;

    public OsmAuthService(
        IHttpClientFactory httpClientFactory,
        ISettingsRepository settingsRepository,
        BTCPayNetworkProvider networkProvider,
        ILogger<OsmAuthService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _settingsRepository = settingsRepository;
        _networkProvider = networkProvider;
        _logger = logger;
    }

    public bool IsMainnet => _networkProvider.NetworkType == ChainName.Mainnet;

    public string OsmApiBaseUrl => IsMainnet
        ? "https://api.openstreetmap.org"
        : "https://master.apis.dev.openstreetmap.org";

    public string OsmAuthBaseUrl => IsMainnet
        ? "https://www.openstreetmap.org"
        : "https://master.apis.dev.openstreetmap.org";

    public async Task<OsmServerSettings> GetSettings()
    {
        return await _settingsRepository.GetSettingAsync<OsmServerSettings>() ?? new OsmServerSettings();
    }

    public async Task SaveSettings(OsmServerSettings settings)
    {
        await _settingsRepository.UpdateSetting(settings);
    }

    public string GetAuthorizationUrl(OsmServerSettings settings, string redirectUri, string state)
    {
        return $"{OsmAuthBaseUrl}/oauth2/authorize" +
               $"?response_type=code" +
               $"&client_id={Uri.EscapeDataString(settings.OsmClientId)}" +
               $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
               $"&scope=write_api+read_prefs" +
               $"&state={Uri.EscapeDataString(state)}";
    }

    public async Task<string> ExchangeCodeForToken(OsmServerSettings settings, string code, string redirectUri)
    {
        var client = _httpClientFactory.CreateClient("OsmApi");
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["client_id"] = settings.OsmClientId,
            ["client_secret"] = settings.OsmClientSecret,
            ["redirect_uri"] = redirectUri
        });

        var tokenUrl = $"{OsmAuthBaseUrl}/oauth2/token";
        _logger.LogInformation("Token exchange: POST {Url} client_id={ClientId} redirect_uri={RedirectUri}",
            tokenUrl, settings.OsmClientId, redirectUri);

        var response = await client.PostAsync(tokenUrl, content);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            _logger.LogError("Token exchange failed: {Status} — {Body}", response.StatusCode, errorBody);
        }
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("access_token").GetString();
    }

    public async Task<string> GetDisplayName(OsmServerSettings settings, string accessToken)
    {
        var client = _httpClientFactory.CreateClient("OsmApi");
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"{OsmApiBaseUrl}/api/0.6/user/details.json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await client.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Could not fetch OSM display name: {Status}", response.StatusCode);
            return "OSM User";
        }

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("user").GetProperty("display_name").GetString();
    }

    public async Task<bool> VerifyToken(OsmServerSettings settings)
    {
        if (string.IsNullOrEmpty(settings.OsmAccessToken))
            return false;

        try
        {
            var client = _httpClientFactory.CreateClient("OsmApi");
            var request = new HttpRequestMessage(HttpMethod.Get,
                $"{OsmApiBaseUrl}/api/0.6/permissions");
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", settings.OsmAccessToken);

            var response = await client.SendAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OSM token verification failed");
            return false;
        }
    }
}
