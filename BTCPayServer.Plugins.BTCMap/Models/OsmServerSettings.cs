namespace BTCPayServer.Plugins.BTCMap.Models;

public class OsmServerSettings
{
    public string OsmAccessToken { get; set; }
    public string OsmDisplayName { get; set; }

    // PKCE in-flight state. Set when ConnectOsm kicks off the OAuth flow, cleared
    // after the token exchange completes (or fails). These are only populated
    // between the authorize redirect and the callback.
    public string PendingCodeVerifier { get; set; }
    public string PendingRedirectUri { get; set; }
    public string PendingStoreId { get; set; }
}
