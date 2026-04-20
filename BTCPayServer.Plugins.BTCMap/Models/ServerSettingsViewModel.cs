namespace BTCPayServer.Plugins.BTCMap.Models;

public class ServerSettingsViewModel
{
    public string OsmClientId { get; set; }
    public string OsmClientSecret { get; set; }
    public bool IsConnected { get; set; }
    public string OsmDisplayName { get; set; }
    public bool IsMainnet { get; set; }
    public string StatusMessage { get; set; }
}
