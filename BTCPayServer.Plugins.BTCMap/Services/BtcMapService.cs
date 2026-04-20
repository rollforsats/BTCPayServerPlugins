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
    private readonly OsmApiClient _osmApiClient;
    private readonly IOverpassApiClient _overpassApiClient;
    private readonly OsmAuthService _osmAuthService;
    private readonly ILogger<BtcMapService> _logger;

    public BtcMapService(
        BtcMapDbContextFactory dbContextFactory,
        OsmApiClient osmApiClient,
        IOverpassApiClient overpassApiClient,
        OsmAuthService osmAuthService,
        ILogger<BtcMapService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _osmApiClient = osmApiClient;
        _overpassApiClient = overpassApiClient;
        _osmAuthService = osmAuthService;
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

    public async Task<BtcMapListing> CreateNewListing(string storeId, BtcMapStoreSettings settings)
    {
        var listing = new BtcMapListing
        {
            Id = Guid.NewGuid().ToString(),
            StoreId = storeId,
            OsmElementType = "node",
            BusinessName = settings.BusinessName,
            Category = settings.Category,
            Latitude = settings.Latitude.Value,
            Longitude = settings.Longitude.Value,
            Street = settings.Street,
            City = settings.City,
            PostCode = settings.PostCode,
            Country = settings.Country,
            AcceptsOnchain = settings.AcceptsOnchain,
            AcceptsLightning = settings.AcceptsLightning,
            CreatedAt = DateTimeOffset.UtcNow,
            LastVerifiedAt = DateTimeOffset.UtcNow,
            Status = ListingStatus.Pending
        };

        await using var ctx = _dbContextFactory.CreateContext();
        var existing = await ctx.Listings.FirstOrDefaultAsync(l => l.StoreId == storeId);
        if (existing != null)
            ctx.Listings.Remove(existing);
        ctx.Listings.Add(listing);
        await ctx.SaveChangesAsync();

        try
        {
            var osmSettings = await _osmAuthService.GetSettings();
            var tags = BuildAllTags(settings, isNewNode: true);
            var comment = $"Add Bitcoin payment tags for {settings.BusinessName} #btcmap";
            var changesetId = await _osmApiClient.CreateChangeset(osmSettings, comment);
            try
            {
                var nodeId = await _osmApiClient.CreateNode(osmSettings, changesetId,
                    settings.Latitude.Value, settings.Longitude.Value, tags);

                listing.OsmElementId = nodeId;
                listing.OsmElementVersion = 1;
                listing.Status = ListingStatus.Active;
            }
            finally
            {
                await _osmApiClient.CloseChangeset(osmSettings, changesetId);
            }

            await using var updateCtx = _dbContextFactory.CreateContext();
            var dbListing = await updateCtx.Listings.FirstAsync(l => l.Id == listing.Id);
            dbListing.OsmElementId = listing.OsmElementId;
            dbListing.OsmElementVersion = listing.OsmElementVersion;
            dbListing.Status = ListingStatus.Active;
            await updateCtx.SaveChangesAsync();

            return listing;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OSM operation failed for pending listing {ListingId}, store {StoreId}", listing.Id, storeId);
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

    public async Task<BtcMapListing> LinkToExistingElement(string storeId, BtcMapStoreSettings settings,
        string osmType, long osmId)
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
            AcceptsOnchain = settings.AcceptsOnchain,
            AcceptsLightning = settings.AcceptsLightning,
            CreatedAt = DateTimeOffset.UtcNow,
            LastVerifiedAt = DateTimeOffset.UtcNow,
            Status = ListingStatus.Pending
        };

        await using var ctx = _dbContextFactory.CreateContext();
        var existing = await ctx.Listings.FirstOrDefaultAsync(l => l.StoreId == storeId);
        if (existing != null)
            ctx.Listings.Remove(existing);
        ctx.Listings.Add(listing);
        await ctx.SaveChangesAsync();

        try
        {
            var osmSettings = await _osmAuthService.GetSettings();
            var element = await _osmApiClient.GetElement(osmSettings, osmType, osmId);

            var bitcoinTags = OsmApiClient.BuildBitcoinTags(settings.AcceptsOnchain, settings.AcceptsLightning);
            foreach (var tag in bitcoinTags)
                element.Tags[tag.Key] = tag.Value;

            var comment = $"Add Bitcoin payment tags for {settings.BusinessName} #btcmap";
            var changesetId = await _osmApiClient.CreateChangeset(osmSettings, comment);
            int newVersion;
            try
            {
                newVersion = await _osmApiClient.UpdateElement(osmSettings, changesetId, element);
            }
            finally
            {
                await _osmApiClient.CloseChangeset(osmSettings, changesetId);
            }

            await using var updateCtx = _dbContextFactory.CreateContext();
            var dbListing = await updateCtx.Listings.FirstAsync(l => l.Id == listing.Id);
            dbListing.OsmElementVersion = newVersion;
            dbListing.Status = ListingStatus.Active;
            await updateCtx.SaveChangesAsync();

            listing.OsmElementVersion = newVersion;
            listing.Status = ListingStatus.Active;
            return listing;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OSM operation failed for pending listing {ListingId}, store {StoreId}", listing.Id, storeId);
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

    public async Task UpdateListing(BtcMapListing listing, BtcMapStoreSettings settings)
    {
        var osmSettings = await _osmAuthService.GetSettings();
        const int maxRetries = 3;

        for (var attempt = 0; attempt < maxRetries; attempt++)
        {
            var element = await _osmApiClient.GetElement(osmSettings,
                listing.OsmElementType, listing.OsmElementId);

            var bitcoinTags = OsmApiClient.BuildBitcoinTags(settings.AcceptsOnchain, settings.AcceptsLightning);
            foreach (var tag in bitcoinTags)
                element.Tags[tag.Key] = tag.Value;

            // Remove payment methods that are no longer enabled
            if (!settings.AcceptsOnchain)
                element.Tags.Remove("payment:onchain");
            if (!settings.AcceptsLightning)
                element.Tags.Remove("payment:lightning");

            var comment = $"Update Bitcoin payment tags for {settings.BusinessName} #btcmap";
            var changesetId = await _osmApiClient.CreateChangeset(osmSettings, comment);
            try
            {
                var newVersion = await _osmApiClient.UpdateElement(osmSettings, changesetId, element);

                await using var ctx = _dbContextFactory.CreateContext();
                var dbListing = await ctx.Listings.FirstAsync(l => l.Id == listing.Id);
                dbListing.OsmElementVersion = newVersion;
                dbListing.BusinessName = settings.BusinessName;
                dbListing.Category = settings.Category;
                dbListing.AcceptsOnchain = settings.AcceptsOnchain;
                dbListing.AcceptsLightning = settings.AcceptsLightning;
                dbListing.LastVerifiedAt = DateTimeOffset.UtcNow;
                await ctx.SaveChangesAsync();
                return;
            }
            catch (OsmVersionConflictException) when (attempt < maxRetries - 1)
            {
                _logger.LogWarning("Version conflict on attempt {Attempt}, retrying", attempt + 1);
            }
            finally
            {
                await _osmApiClient.CloseChangeset(osmSettings, changesetId);
            }
        }
    }

    public async Task UnlistStore(string storeId)
    {
        var listing = await GetListingForStore(storeId);
        if (listing == null || listing.Status == ListingStatus.Unlisted)
            return;

        var osmSettings = await _osmAuthService.GetSettings();
        var element = await _osmApiClient.GetElement(osmSettings,
            listing.OsmElementType, listing.OsmElementId);

        // Remove only Bitcoin-related tags
        foreach (var key in OsmApiClient.BitcoinTagKeys)
            element.Tags.Remove(key);

        var comment = $"Remove Bitcoin payment tags for {listing.BusinessName} #btcmap";
        var changesetId = await _osmApiClient.CreateChangeset(osmSettings, comment);
        try
        {
            await _osmApiClient.UpdateElement(osmSettings, changesetId, element);
        }
        finally
        {
            await _osmApiClient.CloseChangeset(osmSettings, changesetId);
        }

        await using var ctx = _dbContextFactory.CreateContext();
        var dbListing = await ctx.Listings.FirstAsync(l => l.Id == listing.Id);
        dbListing.Status = ListingStatus.Unlisted;
        await ctx.SaveChangesAsync();
    }

    public async Task ReverifyListing(BtcMapListing listing)
    {
        var osmSettings = await _osmAuthService.GetSettings();

        var element = await _osmApiClient.GetElement(osmSettings,
            listing.OsmElementType, listing.OsmElementId);

        element.Tags["check_date:currency:XBT"] = DateTime.UtcNow.ToString("yyyy-MM-dd");

        var comment = $"Re-verify Bitcoin acceptance for {listing.BusinessName} #btcmap";
        var changesetId = await _osmApiClient.CreateChangeset(osmSettings, comment);
        try
        {
            var newVersion = await _osmApiClient.UpdateElement(osmSettings, changesetId, element);

            await using var ctx = _dbContextFactory.CreateContext();
            var dbListing = await ctx.Listings.FirstAsync(l => l.Id == listing.Id);
            dbListing.OsmElementVersion = newVersion;
            dbListing.LastVerifiedAt = DateTimeOffset.UtcNow;
            await ctx.SaveChangesAsync();
        }
        finally
        {
            await _osmApiClient.CloseChangeset(osmSettings, changesetId);
        }
    }

    private static Dictionary<string, string> BuildAllTags(BtcMapStoreSettings settings, bool isNewNode)
    {
        var tags = OsmApiClient.BuildBitcoinTags(settings.AcceptsOnchain, settings.AcceptsLightning);

        if (isNewNode)
        {
            tags["name"] = settings.BusinessName;

            // Common amenity/shop categories
            var shopCategories = new HashSet<string>
            {
                "supermarket", "convenience", "clothes", "electronics", "jewelry",
                "hardware", "books", "gift", "general", "mall"
            };

            if (shopCategories.Contains(settings.Category))
                tags["shop"] = settings.Category;
            else
                tags["amenity"] = settings.Category;

            if (!string.IsNullOrWhiteSpace(settings.Street))
                tags["addr:street"] = settings.Street;
            if (!string.IsNullOrWhiteSpace(settings.City))
                tags["addr:city"] = settings.City;
            if (!string.IsNullOrWhiteSpace(settings.PostCode))
                tags["addr:postcode"] = settings.PostCode;
            if (!string.IsNullOrWhiteSpace(settings.Country))
                tags["addr:country"] = settings.Country;
        }

        return tags;
    }
}
