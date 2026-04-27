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
            AcceptsLightning = acceptsLightning
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

        await using var ctx = _dbContextFactory.CreateContext();
        var existing = await ctx.Listings.FirstOrDefaultAsync(l => l.StoreId == storeId);
        if (existing != null)
            ctx.Listings.Remove(existing);
        ctx.Listings.Add(listing);
        await ctx.SaveChangesAsync();

        try
        {
            var response = await _apiClient.SubmitAsync(request);

            if (response.Osm?.Skipped != null)
            {
                _logger.LogWarning("OSM leg skipped: {Reason}", response.Osm.Skipped);
            }
            else if (response.Osm != null && response.Osm.NodeId.HasValue)
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

            await using var updateCtx = _dbContextFactory.CreateContext();
            var dbListing = await updateCtx.Listings.FirstAsync(l => l.Id == listing.Id);
            dbListing.OsmElementId = listing.OsmElementId;
            dbListing.OsmElementType = listing.OsmElementType;
            dbListing.OsmElementVersion = listing.OsmElementVersion;
            dbListing.Status = ListingStatus.Active;
            dbListing.DirectorySubmittedAt = listing.DirectorySubmittedAt;
            dbListing.DirectorySubmittedUrl = listing.DirectorySubmittedUrl;
            dbListing.DirectoryPrUrl = listing.DirectoryPrUrl;
            await updateCtx.SaveChangesAsync();

            return listing;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "API submission failed for pending listing {ListingId}, store {StoreId}",
                listing.Id, storeId);
            await using var cleanupCtx = _dbContextFactory.CreateContext();
            var pending = await cleanupCtx.Listings.FirstOrDefaultAsync(l => l.Id == listing.Id);
            if (pending != null)
            {
                cleanupCtx.Listings.Remove(pending);
                await cleanupCtx.SaveChangesAsync();
            }
            throw;
        }
    }

    /// <summary>
    /// Link an existing OSM element that already has currency:XBT tags, without
    /// calling the API. Pure local bookkeeping — saves a rate-limited API call.
    /// </summary>
    public async Task<BtcMapListing> AutoLinkExisting(string storeId, BtcMapStoreSettings settings,
        string osmType, long osmId, bool acceptsLightning)
    {
        var listing = new BtcMapListing
        {
            Id = Guid.NewGuid().ToString(),
            StoreId = storeId,
            OsmElementType = osmType,
            OsmElementId = osmId,
            BusinessName = settings.BusinessName,
            Category = settings.Category,
            Latitude = settings.Latitude.Value,
            Longitude = settings.Longitude.Value,
            Street = settings.Street,
            City = settings.City,
            PostCode = settings.PostCode,
            Country = settings.Country,
            AcceptsLightning = acceptsLightning,
            CreatedAt = DateTimeOffset.UtcNow,
            LastVerifiedAt = DateTimeOffset.UtcNow,
            Status = ListingStatus.Active,
            Url = settings.Url
        };

        await using var ctx = _dbContextFactory.CreateContext();
        var existing = await ctx.Listings.FirstOrDefaultAsync(l => l.StoreId == storeId);
        if (existing != null)
            ctx.Listings.Remove(existing);
        ctx.Listings.Add(listing);
        await ctx.SaveChangesAsync();

        return listing;
    }

    public async Task UpdateListing(BtcMapListing listing, BtcMapStoreSettings settings, bool acceptsLightning)
    {
        var request = new BtcMapSubmitRequest
        {
            Name = settings.BusinessName,
            Url = settings.Url,
            Description = listing.Description,
            OsmNodeId = listing.OsmElementId,
            OsmNodeType = listing.OsmElementType,
            TagOnOsm = true,
            SubmitToDirectory = false,
            AcceptsLightning = acceptsLightning
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

        try
        {
            await _apiClient.SubmitAsync(request);
        }
        catch (PluginBuilderApiException ex)
        {
            _logger.LogWarning(ex, "API unlist call failed for store {StoreId}, proceeding with local unlist", storeId);
        }

        await using var ctx = _dbContextFactory.CreateContext();
        var dbListing = await ctx.Listings.FirstAsync(l => l.Id == listing.Id);
        dbListing.Status = ListingStatus.Unlisted;
        // Clear all directory state so a future re-list doesn't inherit stale data.
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
            AcceptsLightning = acceptsLightning
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
        var request = new BtcMapSubmitRequest
        {
            Name = listing.BusinessName,
            Url = listing.Url,
            Description = settings.DirectoryDescription,
            Type = settings.DirectoryType,
            SubType = settings.DirectorySubType,
            Country = listing.Country,
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
            // Persist the directory-specific fields submitted by the merchant so
            // the active listing pre-fills future re-submissions.
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
            // URL already in merchants.json — record submission state so the
            // merchants.json check on next page load surfaces the merged banner.
            await using var ctx = _dbContextFactory.CreateContext();
            var dbListing = await ctx.Listings.FirstAsync(l => l.Id == listing.Id);
            dbListing.DirectorySubmittedAt = DateTimeOffset.UtcNow;
            dbListing.DirectorySubmittedUrl = listing.Url;
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

}
