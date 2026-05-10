using System;
using System.Collections.Generic;

namespace BTCPayServer.Plugins.BTCMap.Services.Osm;

/// <summary>
/// Pure-function tag merge for OSM elements. No I/O, no state. Deterministic given
/// (merchant, existingTags). Easiest possible thing to test exhaustively.
/// </summary>
public interface IOsmTagBuilder
{
    /// <summary>
    /// Returns the per-key intent of the tag merge. SetTags entries should be written
    /// to the element (overwrite existing or add new); RemoveTags should be removed
    /// if present. Existing tags not mentioned in either collection are left alone.
    /// </summary>
    OsmTagMerge BuildMerge(BtcMapMerchant merchant, IDictionary<string, string> existingTags = null);
}

public class OsmTagMerge
{
    public Dictionary<string, string> SetTags { get; } = new();
    public List<string> RemoveTags { get; } = new();
}
