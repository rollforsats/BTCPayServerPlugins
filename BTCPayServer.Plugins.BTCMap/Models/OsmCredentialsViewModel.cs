using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.Plugins.BTCMap.Models;

/// <summary>
/// Form-bound model for the merchant's OSM client_id + client_secret. Lives separate
/// from BtcMapStoreSettings so the existing settings form's [Bind(Prefix=...)] never
/// overlaps with credential storage.
/// </summary>
public class OsmCredentialsViewModel
{
    [Required]
    public string OsmClientId { get; set; }

    [Required]
    public string OsmClientSecret { get; set; }
}

/// <summary>One of the six UI states for the OSM Connect section.</summary>
public enum OsmConnectionState
{
    NotConfigured,
    ConfiguredNotConnected,
    Connected,
    PendingExpired,
    ConnectionError,
    TokenRevoked
}

/// <summary>Discriminator for the State 5 connection-error variants.</summary>
public enum OsmConnectionErrorKind
{
    None,
    RedirectUriMismatch,
    InvalidClient,
    PublicAppNotConfidential,
    AccessDenied,
    Other
}
