using System;
using System.Collections.Generic;

namespace BTCPayServer.Plugins.BTCMap.Models;

public class OsmServerSettings
{
    public string OsmAccessToken { get; set; }
    public string OsmDisplayName { get; set; }

    // Keyed by server-generated nonce. Each OAuth flow gets its own entry so
    // concurrent browser tabs or multiple admins don't clobber each other.
    public Dictionary<string, PendingOAuthFlow> PendingFlows { get; set; } = new();
}

public class PendingOAuthFlow
{
    public string CodeVerifier { get; set; }
    public string RedirectUri { get; set; }
    public string StoreId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
