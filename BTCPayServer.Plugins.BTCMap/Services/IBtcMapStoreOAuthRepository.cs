using System;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.BTCMap.Services;

/// <summary>
/// Decrypted view of a store's OSM OAuth state. Returned from the repository so
/// callers don't see the encrypted column values.
/// </summary>
public class BtcMapStoreOAuthDecrypted
{
    public string OsmClientId { get; set; }
    public string OsmClientSecret { get; set; }
    public string OsmAccessToken { get; set; }
    public string OsmUsername { get; set; }
    public string PendingState { get; set; }
    public DateTimeOffset? PendingStateExpiresAt { get; set; }
    public DateTimeOffset? OsmConnectedAt { get; set; }
    public DateTimeOffset? OsmDisconnectedAt { get; set; }
}

public interface IBtcMapStoreOAuthRepository
{
    /// <summary>Returns null if the store has no OAuth row.</summary>
    Task<BtcMapStoreOAuthDecrypted> GetForStoreAsync(string storeId);

    /// <summary>
    /// Persists the supplied OAuth state, encrypting secrets on write. Creates a new row
    /// if the store doesn't have one yet; otherwise overwrites in place.
    /// </summary>
    Task UpsertAsync(string storeId, BtcMapStoreOAuthDecrypted state);

    /// <summary>
    /// Saves only the merchant-supplied client_id + client_secret. Leaves any existing
    /// access token / username / pending state alone.
    /// </summary>
    Task SaveClientCredentialsAsync(string storeId, string clientId, string clientSecret);

    /// <summary>
    /// Records a successful token exchange: persists the access token, username, and
    /// OsmConnectedAt timestamp; clears pending state and OsmDisconnectedAt.
    /// </summary>
    Task SaveAccessTokenAsync(string storeId, string accessToken, string username);

    /// <summary>
    /// Clears every credential field (client_id, client_secret, access token, username),
    /// stamps OsmDisconnectedAt = now. Used by Disconnect.
    /// </summary>
    Task ClearOAuthAsync(string storeId);

    /// <summary>
    /// Clears only access token + username. Leaves client_id + client_secret intact so the
    /// merchant can reconnect without re-registering the OSM app. Used by 401-from-OSM handling.
    /// </summary>
    Task ClearTokenOnlyAsync(string storeId);

    Task SetPendingStateAsync(string storeId, string state, DateTimeOffset expiresAt);
    Task ClearPendingStateAsync(string storeId);
}
