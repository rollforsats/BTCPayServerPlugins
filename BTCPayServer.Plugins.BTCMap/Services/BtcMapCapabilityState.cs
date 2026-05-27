using System;

namespace BTCPayServer.Plugins.BTCMap.Services;

public class BtcMapCapabilityState
{
    public bool PluginBuilderReachable { get; private set; }
    public DateTimeOffset? LastProbedAt { get; private set; }

    public void Record(bool reachable)
    {
        PluginBuilderReachable = reachable;
        LastProbedAt = DateTimeOffset.UtcNow;
    }
}
