using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.BTCMap.Services.Osm;

/// <summary>
/// OSM OAuth 2.0 token-endpoint helper. Each call carries the merchant-supplied
/// client_id + client_secret (confidential client; per-store registration). No
/// PKCE — the secret is the auth.
/// </summary>
public interface IOsmAuthService
{
    string GetAuthorizationUrl(string clientId, string redirectUri, string state);

    Task<string> ExchangeCodeForTokenAsync(
        string clientId, string clientSecret, string code, string redirectUri, CancellationToken ct);

    Task<string> GetDisplayNameAsync(string accessToken, CancellationToken ct);

    /// <summary>
    /// POSTs to OSM /oauth2/revoke. Best-effort: failures are swallowed (no exception
    /// thrown), since Disconnect must complete locally regardless of the OSM round-trip.
    /// </summary>
    Task RevokeAsync(string clientId, string clientSecret, string accessToken, CancellationToken ct);
}
