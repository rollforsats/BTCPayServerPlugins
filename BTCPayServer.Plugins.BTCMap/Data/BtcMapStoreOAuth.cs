using System;
using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.Plugins.BTCMap.Data;

/// <summary>
/// Per-store OSM OAuth state. Separate from BtcMapStoreSettings so the form-bound
/// model never overlaps with credential storage.
/// </summary>
public class BtcMapStoreOAuth
{
    public string Id { get; set; }

    [Required]
    public string StoreId { get; set; }

    public string OsmClientId { get; set; }
    public string OsmClientSecretEncrypted { get; set; }
    public string OsmAccessTokenEncrypted { get; set; }
    public string OsmUsername { get; set; }
    public string PendingState { get; set; }
    public DateTimeOffset? PendingStateExpiresAt { get; set; }
    public DateTimeOffset? OsmConnectedAt { get; set; }
    public DateTimeOffset? OsmDisconnectedAt { get; set; }
}
