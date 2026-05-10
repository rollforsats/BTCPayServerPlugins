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
    // Synthetic Coronado, CA search point (938 Ocean Blvd). All scenario elements are
    // positioned near this so the distance rendering in SearchResults.cshtml produces
    // sensible values. Enter these exact coordinates in the BTC Map form when testing.
    public const double SearchLat = 32.6838298;
    public const double SearchLon = -117.1839771;

    public static readonly IReadOnlyList<string> Names = new[]
    {
        "empty-everywhere",
        "fresh-cafe",
        "already-tagged",
        "cascading",
        "multiple-nearby-untagged",
        "name-mismatch-fallback-to-address"
    };

    public static OverpassScenario Get(string name) => name switch
    {
        "empty-everywhere" => EmptyEverywhere(),
        "fresh-cafe" => FreshCafe(),
        "already-tagged" => AlreadyTagged(),
        "cascading" => Cascading(),
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
                Lat = 32.6839570,
                Lon = -117.1838260,
                Tags = new Dictionary<string, string>
                {
                    ["name"] = "Bitcoin Burgers",
                    ["amenity"] = "cafe",
                    ["addr:housenumber"] = "1100",
                    ["addr:street"] = "Orange Ave",
                    ["addr:city"] = "Coronado",
                    ["addr:postcode"] = "92118",
                    ["addr:country"] = "US",
                    ["opening_hours"] = "Mo-Su 07:00-22:00"
                }
            }
        },
        AddressSearch: new(),
        CoordinatesSearch: new());

    private static OverpassScenario AlreadyTagged()
    {
        // Same element returned by both Duplicates and NameSearch so the dedupe
        // logic in UIBtcMapStoreController.Search has something to collapse.
        var bitcoinSushi = new OverpassElement
        {
            Type = "node",
            Id = 2000001,
            Lat = 32.6878558,
            Lon = -117.1792045,
            Tags = new Dictionary<string, string>
            {
                ["name"] = "Bitcoin Sushi",
                ["shop"] = "seafood",
                ["addr:housenumber"] = "868",
                ["addr:street"] = "Orange Ave",
                ["addr:city"] = "San Diego",
                ["addr:postcode"] = "92107",
                ["addr:country"] = "US",
                ["currency:XBT"] = "yes",
                ["payment:onchain"] = "yes",
                ["payment:lightning"] = "yes",
                ["check_date:currency:XBT"] = "2025-08-15"
            }
        };

        return new(
            Duplicates: new() { bitcoinSushi },
            NameSearch: new() { bitcoinSushi },
            AddressSearch: new(),
            CoordinatesSearch: new());
    }

    private static OverpassScenario Cascading() => new(
        Duplicates: new()
        {
            new OverpassElement
            {
                Type = "node",
                Id = 3000001,
                Lat = 32.6839570,
                Lon = -117.1838260,
                Tags = new Dictionary<string, string>
                {
                    ["name"] = "Bitcoin Beach Bar",
                    ["amenity"] = "bar",
                    ["addr:city"] = "Coronado",
                    ["addr:country"] = "US",
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
                Lat = 32.6836949,
                Lon = -117.1836995,
                Tags = new Dictionary<string, string>
                {
                    ["name"] = "Bitcoin Brewery",
                    ["amenity"] = "restaurant",
                    ["addr:housenumber"] = "1134",
                    ["addr:street"] = "Orange Ave",
                    ["addr:city"] = "Coronado",
                    ["addr:postcode"] = "92118",
                    ["addr:country"] = "US"
                }
            }
        },
        CoordinatesSearch: new());

    // Four different untagged businesses within ~170m of the search point. Tests the
    // realistic dense-city case where multiple nearby POIs match a business-name query
    // and the merchant has to pick the right one. Coordinates and bearings are real;
    // distances render on the picker as ~20 m, ~30 m, ~100 m, ~170 m.
    private static OverpassScenario MultipleNearbyUntagged() => new(
        Duplicates: new(),
        NameSearch: new()
        {
            new OverpassElement
            {
                Type = "node",
                Id = 4000001,
                Lat = 32.6839570,
                Lon = -117.1838260,
                Tags = new Dictionary<string, string>
                {
                    ["name"] = "Bitcoin Pizza",
                    ["shop"] = "bakery",
                    ["addr:housenumber"] = "1100",
                    ["addr:street"] = "Orange Ave",
                    ["addr:city"] = "Coronado",
                    ["addr:postcode"] = "92118",
                    ["addr:country"] = "US"
                }
            },
            new OverpassElement
            {
                Type = "node",
                Id = 4000002,
                Lat = 32.6836949,
                Lon = -117.1836995,
                Tags = new Dictionary<string, string>
                {
                    ["name"] = "Bitcoin Coffee",
                    ["amenity"] = "restaurant",
                    ["addr:housenumber"] = "1134",
                    ["addr:street"] = "Orange Ave",
                    ["addr:city"] = "Coronado",
                    ["addr:postcode"] = "92118",
                    ["addr:country"] = "US"
                }
            },
            new OverpassElement
            {
                Type = "node",
                Id = 4000003,
                Lat = 32.6829847,
                Lon = -117.1843425,
                Tags = new Dictionary<string, string>
                {
                    ["name"] = "Bitcoin Barbecue",
                    ["amenity"] = "restaurant",
                    ["addr:housenumber"] = "1107",
                    ["addr:street"] = "Orange Ave",
                    ["addr:city"] = "Coronado",
                    ["addr:postcode"] = "92118",
                    ["addr:country"] = "US"
                }
            },
            new OverpassElement
            {
                Type = "node",
                Id = 4000004,
                Lat = 32.6848125,
                Lon = -117.1853686,
                Tags = new Dictionary<string, string>
                {
                    ["name"] = "Bitcoin Tacos",
                    ["shop"] = "pastry",
                    ["addr:housenumber"] = "1031",
                    ["addr:street"] = "Orange Ave",
                    ["addr:city"] = "Coronado",
                    ["addr:postcode"] = "92118",
                    ["addr:country"] = "US"
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
                Lat = 32.6836949,
                Lon = -117.1836995,
                Tags = new Dictionary<string, string>
                {
                    // Merchant typed "Bitcoin Cafe" in BTCPay; OSM still has the
                    // pre-rebrand name. Name search missed — address cascade found it.
                    ["name"] = "Bitcoin Diner",
                    ["amenity"] = "cafe",
                    ["addr:housenumber"] = "1134",
                    ["addr:street"] = "Orange Ave",
                    ["addr:city"] = "Coronado",
                    ["addr:postcode"] = "92118",
                    ["addr:country"] = "US",
                    ["opening_hours"] = "Mo-Fr 07:00-19:00; Sa-Su 08:00-18:00"
                }
            }
        },
        CoordinatesSearch: new());
}
