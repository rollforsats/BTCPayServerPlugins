using System;
using System.Collections.Generic;
using BTCPayServer.Plugins.BTCMap.Services.Osm;
using Xunit;

namespace BTCPayServer.Plugins.BTCMap.Tests;

public class OsmTagBuilderTests
{
    private static readonly DateTime FixedClock = new(2026, 5, 8, 12, 0, 0, DateTimeKind.Utc);
    private static readonly string FixedDate = "2026-05-08";

    private static OsmTagBuilder BuilderAt(DateTime utc)
        => new(() => utc);

    private static BtcMapMerchant MerchantWith(
        bool acceptsLightning = false,
        string osmCategory = null,
        string url = null,
        string name = "Test Cafe",
        string houseNumber = null,
        string street = null,
        string city = null,
        string postCode = null,
        string country = null)
        => new()
        {
            Name = name,
            OsmCategory = osmCategory,
            Url = url,
            AcceptsLightning = acceptsLightning,
            Latitude = 48.8566,
            Longitude = 2.3522,
            HouseNumber = houseNumber,
            Street = street,
            City = city,
            PostCode = postCode,
            Country = country
        };

    [Fact]
    public void Create_AlwaysWritesCurrencyXbt()
    {
        var merge = BuilderAt(FixedClock).BuildMerge(MerchantWith());
        Assert.Equal("yes", merge.SetTags["currency:XBT"]);
    }

    [Fact]
    public void Create_AlwaysWritesCheckDate()
    {
        var merge = BuilderAt(FixedClock).BuildMerge(MerchantWith());
        Assert.Equal(FixedDate, merge.SetTags["check_date:currency:XBT"]);
    }

    [Fact]
    public void Create_DefaultsAmenityToShop_WhenCategoryMissing()
    {
        var merge = BuilderAt(FixedClock).BuildMerge(MerchantWith(osmCategory: null));
        Assert.Equal("shop", merge.SetTags["amenity"]);
    }

    [Fact]
    public void Create_UsesProvidedAmenity_WhenCategoryProvided()
    {
        var merge = BuilderAt(FixedClock).BuildMerge(MerchantWith(osmCategory: "cafe"));
        Assert.Equal("cafe", merge.SetTags["amenity"]);
    }

    [Fact]
    public void Create_WritesPaymentLightning_WhenAcceptsLightningTrue()
    {
        var merge = BuilderAt(FixedClock).BuildMerge(MerchantWith(acceptsLightning: true));
        Assert.Equal("yes", merge.SetTags["payment:lightning"]);
        Assert.Empty(merge.RemoveTags);
    }

    [Fact]
    public void Create_DoesNotWritePaymentLightning_WhenAcceptsLightningFalse()
    {
        var merge = BuilderAt(FixedClock).BuildMerge(MerchantWith(acceptsLightning: false));
        Assert.False(merge.SetTags.ContainsKey("payment:lightning"));
        Assert.DoesNotContain("payment:lightning", merge.RemoveTags);
    }

    [Fact]
    public void Update_RemovesPaymentLightning_WhenFlippedFalseAndPresent()
    {
        var existing = new Dictionary<string, string> { ["payment:lightning"] = "yes" };
        var merge = BuilderAt(FixedClock).BuildMerge(MerchantWith(acceptsLightning: false), existing);
        Assert.Contains("payment:lightning", merge.RemoveTags);
        Assert.False(merge.SetTags.ContainsKey("payment:lightning"));
    }

    [Fact]
    public void Update_DoesNotRemovePaymentLightning_WhenFlippedFalseAndAbsent()
    {
        var existing = new Dictionary<string, string>();
        var merge = BuilderAt(FixedClock).BuildMerge(MerchantWith(acceptsLightning: false), existing);
        Assert.DoesNotContain("payment:lightning", merge.RemoveTags);
    }

    [Fact]
    public void Update_RemovesDeprecatedPaymentBitcoin_WhenPresent()
    {
        var existing = new Dictionary<string, string> { ["payment:bitcoin"] = "yes" };
        var merge = BuilderAt(FixedClock).BuildMerge(MerchantWith(), existing);
        Assert.Contains("payment:bitcoin", merge.RemoveTags);
        Assert.False(merge.SetTags.ContainsKey("payment:bitcoin"));
    }

    [Fact]
    public void Create_NeverWritesPaymentBitcoin()
    {
        var merge = BuilderAt(FixedClock).BuildMerge(MerchantWith());
        Assert.False(merge.SetTags.ContainsKey("payment:bitcoin"));
        Assert.False(merge.SetTags.ContainsKey("payment:onchain"));
        Assert.False(merge.SetTags.ContainsKey("bot"));
    }

    [Fact]
    public void Update_PreservesExistingAmenity()
    {
        var existing = new Dictionary<string, string> { ["amenity"] = "restaurant" };
        var merge = BuilderAt(FixedClock).BuildMerge(MerchantWith(osmCategory: "cafe"), existing);
        Assert.False(merge.SetTags.ContainsKey("amenity"));
    }

    [Fact]
    public void Create_WritesAddressFields_WhenPresent()
    {
        var merge = BuilderAt(FixedClock).BuildMerge(MerchantWith(
            houseNumber: "10", street: "Rue de Rivoli", city: "Paris", postCode: "75001", country: "FR"));
        Assert.Equal("10", merge.SetTags["addr:housenumber"]);
        Assert.Equal("Rue de Rivoli", merge.SetTags["addr:street"]);
        Assert.Equal("Paris", merge.SetTags["addr:city"]);
        Assert.Equal("75001", merge.SetTags["addr:postcode"]);
        Assert.Equal("FR", merge.SetTags["addr:country"]);
    }

    [Fact]
    public void Create_SkipsBlankAddressFields()
    {
        var merge = BuilderAt(FixedClock).BuildMerge(MerchantWith(houseNumber: "", street: "  ", city: null));
        Assert.False(merge.SetTags.ContainsKey("addr:housenumber"));
        Assert.False(merge.SetTags.ContainsKey("addr:street"));
        Assert.False(merge.SetTags.ContainsKey("addr:city"));
    }

    [Fact]
    public void Create_WritesWebsite_WhenUrlProvided()
    {
        var merge = BuilderAt(FixedClock).BuildMerge(MerchantWith(url: "https://example.com"));
        Assert.Equal("https://example.com", merge.SetTags["website"]);
    }

    [Fact]
    public void Create_OmitsWebsite_WhenUrlMissing()
    {
        var merge = BuilderAt(FixedClock).BuildMerge(MerchantWith(url: null));
        Assert.False(merge.SetTags.ContainsKey("website"));
    }

    [Fact]
    public void Update_BumpsCheckDateOnEveryWrite()
    {
        var existing = new Dictionary<string, string>
        {
            ["currency:XBT"] = "yes",
            ["check_date:currency:XBT"] = "2025-01-01"
        };
        var merge = BuilderAt(FixedClock).BuildMerge(MerchantWith(), existing);
        Assert.Equal(FixedDate, merge.SetTags["check_date:currency:XBT"]);
    }

    [Fact]
    public void CheckDate_IsDeterministicAtMidnightUtc()
    {
        var midnight = new DateTime(2026, 12, 31, 23, 59, 59, DateTimeKind.Utc);
        var merge = BuilderAt(midnight).BuildMerge(MerchantWith());
        Assert.Equal("2026-12-31", merge.SetTags["check_date:currency:XBT"]);

        var oneSecondLater = midnight.AddSeconds(1);
        var nextDay = BuilderAt(oneSecondLater).BuildMerge(MerchantWith());
        Assert.Equal("2027-01-01", nextDay.SetTags["check_date:currency:XBT"]);
    }

    [Fact]
    public void BuildMerge_RejectsNullMerchant()
    {
        Assert.Throws<ArgumentNullException>(() => BuilderAt(FixedClock).BuildMerge(null));
    }
}
