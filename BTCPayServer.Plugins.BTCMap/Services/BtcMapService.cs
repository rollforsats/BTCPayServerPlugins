using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Plugins.BTCMap.Data;
using BTCPayServer.Plugins.BTCMap.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.BTCMap.Services;

public class BtcMapService
{
    private readonly BtcMapDbContextFactory _dbContextFactory;
    private readonly PluginBuilderApiClient _apiClient;
    private readonly IOverpassApiClient _overpassApiClient;
    private readonly ILogger<BtcMapService> _logger;

    public BtcMapService(
        BtcMapDbContextFactory dbContextFactory,
        PluginBuilderApiClient apiClient,
        IOverpassApiClient overpassApiClient,
        ILogger<BtcMapService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _apiClient = apiClient;
        _overpassApiClient = overpassApiClient;
        _logger = logger;
    }

    public async Task<BtcMapListing> GetListingForStore(string storeId)
    {
        await using var ctx = _dbContextFactory.CreateContext();
        return await ctx.Listings.FirstOrDefaultAsync(l => l.StoreId == storeId && l.Status != ListingStatus.Pending);
    }

    public async Task<List<OverpassElement>> SearchNearby(double lat, double lon, string name,
        string street = null, string city = null)
    {
        // 1. Try name search (200m radius)
        var results = await _overpassApiClient.SearchNearby(lat, lon, 200, name);
        if (results.Count > 0) return results;

        // 2. Try address search if street+city provided (200m radius)
        if (!string.IsNullOrWhiteSpace(street) && !string.IsNullOrWhiteSpace(city))
        {
            results = await _overpassApiClient.SearchByAddress(lat, lon, 200, street, city);
            if (results.Count > 0) return results;
        }

        // 3. Fall back to all named places nearby (100m radius)
        return await _overpassApiClient.SearchByCoordinates(lat, lon, 100);
    }

    public async Task<List<OverpassElement>> CheckDuplicates(double lat, double lon)
    {
        return await _overpassApiClient.CheckExistingBitcoinTags(lat, lon);
    }

    /// <summary>
    /// Submit a new listing via the plugin-builder API. Handles both tagging existing
    /// elements and creating new nodes, depending on whether osmNodeId is provided.
    /// </summary>
    public async Task<BtcMapListing> SubmitListing(string storeId, BtcMapStoreSettings settings,
        bool acceptsLightning, bool submitToDirectory,
        string osmType = null, long? osmId = null)
    {
        var hasOsmType = !string.IsNullOrWhiteSpace(osmType);
        if (osmId.HasValue != hasOsmType)
            throw new ArgumentException("osmId and osmType must be provided together when linking an existing OSM element.");
        if (hasOsmType && osmType is not ("node" or "way"))
            throw new ArgumentOutOfRangeException(nameof(osmType), "Only 'node' and 'way' are supported.");
        if (osmId is <= 0)
            throw new ArgumentOutOfRangeException(nameof(osmId), "osmId must be positive when linking an existing element.");

        var request = new BtcMapSubmitRequest
        {
            Name = settings.BusinessName,
            Url = settings.Url,
            Description = submitToDirectory ? settings.DirectoryDescription : null,
            Type = submitToDirectory ? settings.DirectoryType : null,
            SubType = submitToDirectory ? settings.DirectorySubType : null,
            Country = settings.Country,
            Twitter = submitToDirectory ? settings.DirectoryTwitter : null,
            Github = submitToDirectory ? settings.DirectoryGithub : null,
            OnionUrl = submitToDirectory ? settings.DirectoryOnionUrl : null,
            OsmNodeId = osmId,
            OsmNodeType = osmType,
            Latitude = osmId == null ? settings.Latitude : null,
            Longitude = osmId == null ? settings.Longitude : null,
            OsmCategory = osmId == null ? settings.Category : null,
            SubmitToDirectory = submitToDirectory,
            TagOnOsm = true,
            AcceptsLightning = acceptsLightning,
            Address = BuildAddress(settings.HouseNumber, settings.Street, settings.City, settings.PostCode, settings.Country)
        };

        var listing = new BtcMapListing
        {
            Id = Guid.NewGuid().ToString(),
            StoreId = storeId,
            OsmElementType = osmType ?? "node",
            OsmElementId = osmId ?? 0,
            BusinessName = settings.BusinessName,
            Category = settings.Category,
            Latitude = settings.Latitude.Value,
            Longitude = settings.Longitude.Value,
            HouseNumber = settings.HouseNumber,
            Street = settings.Street,
            City = settings.City,
            PostCode = settings.PostCode,
            Country = settings.Country,
            AcceptsLightning = acceptsLightning,
            CreatedAt = DateTimeOffset.UtcNow,
            LastVerifiedAt = DateTimeOffset.UtcNow,
            Status = ListingStatus.Pending,
            Url = settings.Url,
            Description = submitToDirectory ? settings.DirectoryDescription : null,
            Twitter = submitToDirectory ? settings.DirectoryTwitter : null,
            Github = submitToDirectory ? settings.DirectoryGithub : null,
            OnionUrl = submitToDirectory ? settings.DirectoryOnionUrl : null,
            DirectoryType = submitToDirectory ? settings.DirectoryType : null,
            DirectorySubType = submitToDirectory ? settings.DirectorySubType : null
        };

        // Single context: stage the row in memory, call the API, apply API-derived
        // fields, then commit once. If the API call throws, SaveChangesAsync never
        // runs, so no orphan Pending row gets persisted.
        await using var ctx = _dbContextFactory.CreateContext();
        var existing = await ctx.Listings.FirstOrDefaultAsync(l => l.StoreId == storeId);
        if (existing != null)
            ctx.Listings.Remove(existing);
        ctx.Listings.Add(listing);

        var response = await _apiClient.SubmitAsync(request);

        // Upstream's only OSM-leg skip on a healthy server is `osm-access-token-not-configured` —
        // an operator config error, not a merchant-facing outcome. Surface as an error so the
        // merchant sees the truth and the operator gets paged; nothing persists locally.
        if (response.Osm?.Skipped != null)
            throw new PluginBuilderApiException(503,
                $"BTC Map service is unavailable (OSM leg skipped: {response.Osm.Skipped}). Please contact your administrator.");

        if (response.Osm != null && response.Osm.NodeId.HasValue)
        {
            listing.OsmElementId = response.Osm.NodeId.Value;
            listing.OsmElementType = response.Osm.NodeType ?? listing.OsmElementType;
            listing.OsmElementVersion = response.Osm.NewVersion ?? 1;
        }
        listing.Status = ListingStatus.Active;

        if (response.Directory != null)
        {
            if (response.Directory.PrUrl != null)
            {
                listing.DirectorySubmittedAt = DateTimeOffset.UtcNow;
                listing.DirectorySubmittedUrl = settings.Url;
                listing.DirectoryPrUrl = response.Directory.PrUrl;
            }
            else if (response.Directory.Skipped?.StartsWith("duplicate-url:") == true)
            {
                // URL already merged in merchants.json — track as submitted so the
                // merchants.json page-load check surfaces the merged banner.
                listing.DirectorySubmittedAt = DateTimeOffset.UtcNow;
                listing.DirectorySubmittedUrl = settings.Url;
                _logger.LogInformation("Directory submission skipped (already merged): {Reason}", response.Directory.Skipped);
            }
            else if (response.Directory.Skipped != null)
            {
                _logger.LogInformation("Directory submission skipped: {Reason}", response.Directory.Skipped);
            }
        }

        await ctx.SaveChangesAsync();
        return listing;
    }

    public async Task UpdateListing(BtcMapListing listing, BtcMapStoreSettings settings, bool acceptsLightning)
    {
        var request = new BtcMapSubmitRequest
        {
            Name = settings.BusinessName,
            Url = settings.Url,
            // Reverify-only path: OSM tags don't carry description, so the stored value
            // is sent purely to satisfy the request shape. Form edits to DirectoryDescription
            // flow through SubmitListing's directory leg, not here.
            Description = listing.Description,
            OsmNodeId = listing.OsmElementId,
            OsmNodeType = listing.OsmElementType,
            TagOnOsm = true,
            SubmitToDirectory = false,
            AcceptsLightning = acceptsLightning,
            Address = BuildAddress(settings.HouseNumber, settings.Street, settings.City, settings.PostCode, settings.Country)
        };

        var response = await _apiClient.SubmitAsync(request);

        await using var ctx = _dbContextFactory.CreateContext();
        var dbListing = await ctx.Listings.FirstAsync(l => l.Id == listing.Id);
        if (response.Osm?.Skipped != null)
            _logger.LogWarning("OSM leg skipped during update: {Reason}", response.Osm.Skipped);
        else if (response.Osm?.NewVersion != null)
            dbListing.OsmElementVersion = response.Osm.NewVersion.Value;
        dbListing.BusinessName = settings.BusinessName;
        dbListing.Category = settings.Category;
        dbListing.Url = settings.Url;
        dbListing.HouseNumber = settings.HouseNumber;
        dbListing.Street = settings.Street;
        dbListing.City = settings.City;
        dbListing.PostCode = settings.PostCode;
        dbListing.Country = settings.Country;
        dbListing.AcceptsLightning = acceptsLightning;
        dbListing.LastVerifiedAt = DateTimeOffset.UtcNow;
        await ctx.SaveChangesAsync();
    }

    public async Task UnlistStore(string storeId)
    {
        var listing = await GetListingForStore(storeId);
        if (listing == null || listing.Status == ListingStatus.Unlisted)
            return;

        var request = new BtcMapSubmitRequest
        {
            Name = listing.BusinessName,
            Url = listing.Url,
            Description = listing.Description,
            OsmNodeId = listing.OsmElementId,
            OsmNodeType = listing.OsmElementType,
            UnlistFromOsm = true
        };

        await _apiClient.SubmitAsync(request);

        await using var ctx = _dbContextFactory.CreateContext();
        var dbListing = await ctx.Listings.FirstAsync(l => l.Id == listing.Id);
        dbListing.Status = ListingStatus.Unlisted;
        dbListing.Url = null;
        dbListing.Description = null;
        dbListing.Twitter = null;
        dbListing.Github = null;
        dbListing.OnionUrl = null;
        dbListing.DirectoryType = null;
        dbListing.DirectorySubType = null;
        dbListing.DirectorySubmittedAt = null;
        dbListing.DirectorySubmittedUrl = null;
        dbListing.DirectoryPrUrl = null;
        await ctx.SaveChangesAsync();
    }

    public async Task ReverifyListing(BtcMapListing listing, bool acceptsLightning)
    {
        var request = new BtcMapSubmitRequest
        {
            Name = listing.BusinessName,
            Url = listing.Url,
            Description = listing.Description,
            OsmNodeId = listing.OsmElementId,
            OsmNodeType = listing.OsmElementType,
            TagOnOsm = true,
            SubmitToDirectory = false,
            AcceptsLightning = acceptsLightning,
            Address = BuildAddress(listing.HouseNumber, listing.Street, listing.City, listing.PostCode, listing.Country)
        };

        var response = await _apiClient.SubmitAsync(request);

        await using var ctx = _dbContextFactory.CreateContext();
        var dbListing = await ctx.Listings.FirstAsync(l => l.Id == listing.Id);
        if (response.Osm?.Skipped != null)
            _logger.LogWarning("OSM leg skipped during reverify: {Reason}", response.Osm.Skipped);
        else if (response.Osm?.NewVersion != null)
            dbListing.OsmElementVersion = response.Osm.NewVersion.Value;
        dbListing.LastVerifiedAt = DateTimeOffset.UtcNow;
        await ctx.SaveChangesAsync();
    }

    public async Task<BtcMapSubmitResponse> SubmitToDirectoryOnly(BtcMapListing listing, BtcMapStoreSettings settings)
    {
        var country = !string.IsNullOrEmpty(settings.Country) ? settings.Country : listing.Country;
        var request = new BtcMapSubmitRequest
        {
            Name = listing.BusinessName,
            Url = listing.Url,
            Description = settings.DirectoryDescription,
            Type = settings.DirectoryType,
            SubType = settings.DirectorySubType,
            Country = country,
            Twitter = settings.DirectoryTwitter,
            Github = settings.DirectoryGithub,
            OnionUrl = settings.DirectoryOnionUrl,
            SubmitToDirectory = true
        };

        var response = await _apiClient.SubmitAsync(request);

        if (response.Directory?.PrUrl != null)
        {
            await using var ctx = _dbContextFactory.CreateContext();
            var dbListing = await ctx.Listings.FirstAsync(l => l.Id == listing.Id);
            dbListing.DirectorySubmittedAt = DateTimeOffset.UtcNow;
            dbListing.DirectorySubmittedUrl = listing.Url;
            dbListing.DirectoryPrUrl = response.Directory.PrUrl;
            dbListing.Country = country;
            dbListing.Description = settings.DirectoryDescription;
            dbListing.Twitter = settings.DirectoryTwitter;
            dbListing.Github = settings.DirectoryGithub;
            dbListing.OnionUrl = settings.DirectoryOnionUrl;
            dbListing.DirectoryType = settings.DirectoryType;
            dbListing.DirectorySubType = settings.DirectorySubType;
            await ctx.SaveChangesAsync();
        }
        else if (response.Directory?.Skipped?.StartsWith("duplicate-url:") == true)
        {
            await using var ctx = _dbContextFactory.CreateContext();
            var dbListing = await ctx.Listings.FirstAsync(l => l.Id == listing.Id);
            dbListing.DirectorySubmittedAt = DateTimeOffset.UtcNow;
            dbListing.DirectorySubmittedUrl = listing.Url;
            dbListing.Country = country;
            dbListing.Description = settings.DirectoryDescription;
            dbListing.Twitter = settings.DirectoryTwitter;
            dbListing.Github = settings.DirectoryGithub;
            dbListing.OnionUrl = settings.DirectoryOnionUrl;
            dbListing.DirectoryType = settings.DirectoryType;
            dbListing.DirectorySubType = settings.DirectorySubType;
            await ctx.SaveChangesAsync();
        }

        return response;
    }

    private static BtcMapSubmitAddress BuildAddress(string houseNumber, string street, string city, string postcode, string country)
    {
        if (string.IsNullOrWhiteSpace(houseNumber) && string.IsNullOrWhiteSpace(street) &&
            string.IsNullOrWhiteSpace(city) && string.IsNullOrWhiteSpace(postcode) &&
            string.IsNullOrWhiteSpace(country))
            return null;
        return new BtcMapSubmitAddress
        {
            HouseNumber = houseNumber,
            Street = street,
            City = city,
            Postcode = postcode,
            Country = country
        };
    }
}
