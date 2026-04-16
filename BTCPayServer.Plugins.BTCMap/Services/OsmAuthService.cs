using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Plugins.BTCMap.Models;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace BTCPayServer.Plugins.BTCMap.Services;

public class OsmAuthService
{
    // Fixed redirect URI registered on the production OSM OAuth app. The plugin is
    // distributed to thousands of BTCPay instances at different domains; OSM
    // enforces exact-match on redirect URIs. The bounce page at this URL reads the
    // state parameter (base64 origin URL), validates it, and redirects the auth
    // code back to the originating instance.
    public const string BouncePageUrl = "https://directory.btcpayserver.org/plugins/btcmap/oauth/callback";

    // Env var holding the OSM OAuth client_id. Eventually baked in via GitHub
    // Actions; set manually for local dev.
    public const string ClientIdEnvVar = "BTCMAP_OSM_CLIENT_ID";

    // Dev-only override: forces the OAuth redirect_uri to a specific URL on
    // non-mainnet networks, so the full chain (BTCPay → OSM → bounce page →
    // BTCPay) can be exercised locally before shipping. Ignored on mainnet,
    // where BouncePageUrl always wins.
    public const string ForceBouncePageEnvVar = "BTCMAP_FORCE_BOUNCE_PAGE";

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

    public string OsmClientId => Environment.GetEnvironmentVariable(ClientIdEnvVar) ?? "";

    public bool IsClientIdConfigured => !string.IsNullOrWhiteSpace(OsmClientId);

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

    // On mainnet, route through the bounce page at directory.btcpayserver.org (the
    // redirect URI registered on OSM's production OAuth app). On non-mainnet, go
    // directly to the local callback — the dev OSM app is registered with the
    // local callback URL — unless BTCMAP_FORCE_BOUNCE_PAGE is set to exercise the
    // bounce page in the full chain.
    public string GetRedirectUri(string localCallbackUrl)
    {
        if (IsMainnet)
            return BouncePageUrl;

        var forced = Environment.GetEnvironmentVariable(ForceBouncePageEnvVar);
        if (!string.IsNullOrWhiteSpace(forced))
        {
            _logger.LogWarning(
                "{EnvVar} ACTIVE — using {Url} as the OAuth redirect_uri instead of the local callback. " +
                "This is a dev-only override for testing the bounce page flow.",
                ForceBouncePageEnvVar, forced);
            return forced;
        }

        return localCallbackUrl;
    }

    public string GetAuthorizationUrl(string redirectUri, string state, string codeChallenge)
    {
        return $"{OsmAuthBaseUrl}/oauth2/authorize" +
               $"?response_type=code" +
               $"&client_id={Uri.EscapeDataString(OsmClientId)}" +
               $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
               $"&scope=write_api+read_prefs" +
               $"&state={Uri.EscapeDataString(state)}" +
               $"&code_challenge={Uri.EscapeDataString(codeChallenge)}" +
               $"&code_challenge_method=S256";
    }

    public async Task<string> ExchangeCodeForToken(string code, string redirectUri, string codeVerifier)
    {
        var client = _httpClientFactory.CreateClient("OsmApi");
        // Public client token exchange: no client_secret. Doorkeeper validates the
        // request by recomputing SHA256(codeVerifier) and matching it against the
        // code_challenge stored from the authorize step.
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["client_id"] = OsmClientId,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = codeVerifier
        });

        var tokenUrl = $"{OsmAuthBaseUrl}/oauth2/token";
        _logger.LogInformation("Token exchange: POST {Url} client_id={ClientId} redirect_uri={RedirectUri}",
            tokenUrl, OsmClientId, redirectUri);

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

    public async Task<string> GetDisplayName(string accessToken)
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

    // PKCE helpers (RFC 7636).

    public static string GenerateCodeVerifier()
    {
        // 96 random bytes → 128 base64url chars. RFC 7636 §4.1 requires the
        // verifier to be between 43 and 128 chars; 128 is the maximum allowed
        // length.
        var bytes = new byte[96];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return Base64UrlEncode(bytes);
    }

    public static string GenerateCodeChallenge(string codeVerifier)
    {
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.ASCII.GetBytes(codeVerifier));
        return Base64UrlEncode(hash);
    }

    private static string Base64UrlEncode(byte[] input)
    {
        return Convert.ToBase64String(input)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
