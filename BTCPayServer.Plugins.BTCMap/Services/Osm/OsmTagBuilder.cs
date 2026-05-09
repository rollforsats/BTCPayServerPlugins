using System;
using System.Collections.Generic;
using System.Globalization;

namespace BTCPayServer.Plugins.BTCMap.Services.Osm;

public class OsmTagBuilder : IOsmTagBuilder
{
    /// <summary>
    /// Mandatory date stamp on every write. Bumped to today UTC every time, which is
    /// also how reverify works (no merchant-data changes, the merge is a no-op for
    /// everything else, the date bumps).
    /// </summary>
    public const string CheckDateKey = "check_date:currency:XBT";
    public const string CurrencyXbtKey = "currency:XBT";
    public const string PaymentLightningKey = "payment:lightning";
    public const string PaymentBitcoinKey = "payment:bitcoin";
    public const string PhoneKey = "phone";

    private readonly Func<DateTime> _utcNow;

    public OsmTagBuilder(Func<DateTime> utcNow = null)
    {
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public OsmTagMerge BuildMerge(BtcMapMerchant merchant, IDictionary<string, string> existingTags = null)
    {
        if (merchant == null) throw new ArgumentNullException(nameof(merchant));
        var merge = new OsmTagMerge();

        // Always-write tags.
        merge.SetTags[CurrencyXbtKey] = "yes";
        merge.SetTags[CheckDateKey] = _utcNow().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(merchant.Name))
            merge.SetTags["name"] = merchant.Name.Trim();

        // amenity: write on create (no existing tags) or only if the existing element
        // doesn't already carry one. Don't overwrite a curator's choice.
        var amenity = string.IsNullOrWhiteSpace(merchant.OsmCategory) ? "shop" : merchant.OsmCategory.Trim();
        if (existingTags == null || !existingTags.ContainsKey("amenity"))
            merge.SetTags["amenity"] = amenity;

        if (!string.IsNullOrWhiteSpace(merchant.Url))
            merge.SetTags["website"] = merchant.Url.Trim();

        // payment:lightning is gated on AcceptsLightning. When the merchant flips it
        // off we *remove* the tag if present (close the gap from plugin-builder which
        // only adds, never removes).
        if (merchant.AcceptsLightning)
            merge.SetTags[PaymentLightningKey] = "yes";
        else if (existingTags != null && existingTags.ContainsKey(PaymentLightningKey))
            merge.RemoveTags.Add(PaymentLightningKey);

        // payment:bitcoin is deprecated in favor of currency:XBT. Never write; remove
        // if present on existing nodes.
        if (existingTags != null && existingTags.ContainsKey(PaymentBitcoinKey))
            merge.RemoveTags.Add(PaymentBitcoinKey);

        // Address tags: per-field, only if non-null/whitespace.
        TryAdd(merge, "addr:housenumber", merchant.HouseNumber);
        TryAdd(merge, "addr:street", merchant.Street);
        TryAdd(merge, "addr:city", merchant.City);
        TryAdd(merge, "addr:postcode", merchant.PostCode);
        TryAdd(merge, "addr:country", merchant.Country);

        // phone is optional; when the merchant clears it on an existing element
        // we remove the tag (mirrors payment:lightning gating).
        if (!string.IsNullOrWhiteSpace(merchant.Phone))
            merge.SetTags[PhoneKey] = merchant.Phone.Trim();
        else if (existingTags != null && existingTags.ContainsKey(PhoneKey))
            merge.RemoveTags.Add(PhoneKey);

        return merge;
    }

    private static void TryAdd(OsmTagMerge merge, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        merge.SetTags[key] = value.Trim();
    }
}
