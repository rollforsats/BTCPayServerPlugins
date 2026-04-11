using System;
using System.Collections.Generic;
using BTCPayServer.Plugins.BTCMap.Models;

namespace BTCPayServer.Plugins.BTCMap.Services;

/// <summary>
/// Hardcoded Overpass response data used by <see cref="FixtureOverpassApiClient"/> when
/// the plugin is running in dev-only fixture mode. Never registered in production — see
/// the triple-gate in Plugin.cs.
/// </summary>
public record OverpassScenario(
    List<OverpassElement> Duplicates,
    List<OverpassElement> NameSearch,
    List<OverpassElement> AddressSearch,
    List<OverpassElement> CoordinatesSearch);

public static class OverpassFixtureScenarios
{
    // Synthetic "fake Paris" search point. All scenario elements are positioned near
    // this so the distance rendering in SearchResults.cshtml produces sensible values.
    // Enter these exact coordinates in the BTC Map form when testing.
    public const double SearchLat = 48.8566;
    public const double SearchLon = 2.3522;

    public static readonly IReadOnlyList<string> Names = new[]
    {
        "empty-everywhere",
        "fresh-cafe",
        "already-tagged",
        "cascading-mixed",
        "multiple-nearby-untagged",
        "name-mismatch-fallback-to-address"
    };

    public static OverpassScenario Get(string name) => name switch
    {
        "empty-everywhere" => EmptyEverywhere(),
        "fresh-cafe" => FreshCafe(),
        "already-tagged" => AlreadyTagged(),
        "cascading-mixed" => CascadingMixed(),
        "multiple-nearby-untagged" => MultipleNearbyUntagged(),
        "name-mismatch-fallback-to-address" => NameMismatchFallbackToAddress(),
        _ => throw new ArgumentException(
            $"Unknown BTCMAP_OVERPASS_SCENARIO '{name}'. Valid values: {string.Join(", ", Names)}")
    };

    private static OverpassScenario EmptyEverywhere() => new(
        Duplicates: new(),
        NameSearch: new(),
        AddressSearch: new(),
        CoordinatesSearch: new());

    private static OverpassScenario FreshCafe() => new(
        Duplicates: new(),
        NameSearch: new()
        {
            new OverpassElement
            {
                Type = "node",
                Id = 1000001,
                Lat = 48.85720,
                Lon = 2.35310,
                Tags = new Dictionary<string, string>
                {
                    ["name"] = "Café de Flore",
                    ["amenity"] = "cafe",
                    ["addr:housenumber"] = "172",
                    ["addr:street"] = "Boulevard Saint-Germain",
                    ["addr:city"] = "Paris",
                    ["addr:postcode"] = "75006",
                    ["addr:country"] = "FR",
                    ["opening_hours"] = "Mo-Su 07:00-02:00"
                }
            }
        },
        AddressSearch: new(),
        CoordinatesSearch: new());

    private static OverpassScenario AlreadyTagged()
    {
        // Same element returned by both Duplicates and NameSearch so the dedupe
        // logic in UIBtcMapStoreController.Search has something to collapse.
        var satoshisSushi = new OverpassElement
        {
            Type = "node",
            Id = 2000001,
            Lat = 48.85640,
            Lon = 2.35180,
            Tags = new Dictionary<string, string>
            {
                ["name"] = "Satoshi's Sushi",
                ["shop"] = "seafood",
                ["addr:housenumber"] = "12",
                ["addr:street"] = "Rue de Rivoli",
                ["addr:city"] = "Paris",
                ["addr:postcode"] = "75004",
                ["currency:XBT"] = "yes",
                ["payment:onchain"] = "yes",
                ["payment:lightning"] = "yes",
                ["check_date:currency:XBT"] = "2025-08-15"
            }
        };

        return new(
            Duplicates: new() { satoshisSushi },
            NameSearch: new() { satoshisSushi },
            AddressSearch: new(),
            CoordinatesSearch: new());
    }

    private static OverpassScenario CascadingMixed() => new(
        Duplicates: new()
        {
            new OverpassElement
            {
                Type = "node",
                Id = 3000001,
                Lat = 48.85700,
                Lon = 2.35150,
                Tags = new Dictionary<string, string>
                {
                    ["name"] = "Bitcoin Beach Bar",
                    ["amenity"] = "bar",
                    ["addr:city"] = "Paris",
                    ["currency:XBT"] = "yes",
                    ["payment:lightning"] = "yes"
                }
            }
        },
        NameSearch: new(),
        AddressSearch: new()
        {
            new OverpassElement
            {
                Type = "node",
                Id = 3000002,
                Lat = 48.85680,
                Lon = 2.35230,
                Tags = new Dictionary<string, string>
                {
                    ["name"] = "Le Procope",
                    ["amenity"] = "restaurant",
                    ["addr:housenumber"] = "13",
                    ["addr:street"] = "Rue de l'Ancienne Comédie",
                    ["addr:city"] = "Paris",
                    ["addr:postcode"] = "75006"
                }
            },
            new OverpassElement
            {
                Type = "way",
                Id = 3000003,
                // No direct Lat/Lon — Center is used via OverpassElement.EffectiveLat/EffectiveLon
                Center = new OverpassCenter { Lat = 48.85590, Lon = 2.35080 },
                Tags = new Dictionary<string, string>
                {
                    ["name"] = "Jardin du Luxembourg",
                    ["tourism"] = "park",
                    ["addr:city"] = "Paris"
                }
            }
        },
        CoordinatesSearch: new());

    // Four different untagged businesses within ~150m of the search point. Tests the
    // realistic dense-city case where multiple nearby POIs match a business-name query
    // and the merchant has to pick the right one.
    private static OverpassScenario MultipleNearbyUntagged() => new(
        Duplicates: new(),
        NameSearch: new()
        {
            new OverpassElement
            {
                Type = "node",
                Id = 4000001,
                Lat = 48.85680,
                Lon = 2.35240,
                Tags = new Dictionary<string, string>
                {
                    ["name"] = "Boulangerie Poilâne",
                    ["shop"] = "bakery",
                    ["addr:housenumber"] = "8",
                    ["addr:street"] = "Rue du Cherche-Midi",
                    ["addr:city"] = "Paris",
                    ["addr:postcode"] = "75006"
                }
            },
            new OverpassElement
            {
                Type = "node",
                Id = 4000002,
                Lat = 48.85720,
                Lon = 2.35190,
                Tags = new Dictionary<string, string>
                {
                    ["name"] = "Le Relais de l'Entrecôte",
                    ["amenity"] = "restaurant",
                    ["addr:housenumber"] = "20",
                    ["addr:street"] = "Rue Saint-Benoît",
                    ["addr:city"] = "Paris",
                    ["addr:postcode"] = "75006"
                }
            },
            new OverpassElement
            {
                Type = "node",
                Id = 4000003,
                Lat = 48.85610,
                Lon = 2.35300,
                Tags = new Dictionary<string, string>
                {
                    ["name"] = "Brasserie Lipp",
                    ["amenity"] = "restaurant",
                    ["addr:housenumber"] = "151",
                    ["addr:street"] = "Boulevard Saint-Germain",
                    ["addr:city"] = "Paris",
                    ["addr:postcode"] = "75006"
                }
            },
            new OverpassElement
            {
                Type = "node",
                Id = 4000004,
                Lat = 48.85590,
                Lon = 2.35100,
                Tags = new Dictionary<string, string>
                {
                    ["name"] = "Patisserie Stohrer",
                    ["shop"] = "pastry",
                    ["addr:housenumber"] = "51",
                    ["addr:street"] = "Rue Montorgueil",
                    ["addr:city"] = "Paris",
                    ["addr:postcode"] = "75002"
                }
            }
        },
        AddressSearch: new(),
        CoordinatesSearch: new());

    // The merchant's BTCPay store name doesn't match the OSM record (business renamed,
    // or OSM data is out of date). Name search returns empty, the address cascade kicks
    // in, and finds the actual element via matching street + city. Tests the whole
    // reason the cascading search exists.
    private static OverpassScenario NameMismatchFallbackToAddress() => new(
        Duplicates: new(),
        NameSearch: new(), // empty — forces cascade to address step
        AddressSearch: new()
        {
            new OverpassElement
            {
                Type = "node",
                Id = 5000001,
                Lat = 48.85650,
                Lon = 2.35200,
                Tags = new Dictionary<string, string>
                {
                    // Merchant typed "Satoshi Coffee Co" in BTCPay; OSM still has the
                    // pre-rebrand name. Name search missed — address cascade found it.
                    ["name"] = "Coffee on 5th",
                    ["amenity"] = "cafe",
                    ["addr:housenumber"] = "5",
                    ["addr:street"] = "Rue de Rivoli",
                    ["addr:city"] = "Paris",
                    ["addr:postcode"] = "75004",
                    ["addr:country"] = "FR",
                    ["opening_hours"] = "Mo-Fr 07:00-19:00; Sa-Su 08:00-18:00"
                }
            }
        },
        CoordinatesSearch: new());
}
