using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.BTCMap.Data;
using BTCPayServer.Plugins.BTCMap.Models;
using BTCPayServer.Plugins.BTCMap.Services.Osm;
using BTCPayServer.Plugins.BTCMap.Services.Osm.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.BTCMap.Services;

public class BtcMapService : IBtcMapService
{
    private readonly BtcMapDbContextFactory _dbContextFactory;
    private readonly IListingRepository _listingRepository;
    private readonly IPluginBuilderApiClient _apiClient;
    private readonly IOverpassApiClient _overpassApiClient;
    private readonly IOsmApiClient _osmApiClient;
    private readonly IBtcMapStoreOAuthRepository _oauthRepo;
    private readonly ILogger<BtcMapService> _logger;

    public BtcMapService(
        BtcMapDbContextFactory dbContextFactory,
        IListingRepository listingRepository,
        IPluginBuilderApiClient apiClient,
        IOverpassApiClient overpassApiClient,
        IOsmApiClient osmApiClient,
        IBtcMapStoreOAuthRepository oauthRepo,
        ILogger<BtcMapService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _listingRepository = listingRepository;
        _apiClient = apiClient;
        _overpassApiClient = overpassApiClient;
        _osmApiClient = osmApiClient;
        _oauthRepo = oauthRepo;
        _logger = logger;
    }

    private static BtcMapMerchant ToMerchant(BtcMapStoreSettings settings, bool acceptsLightning)
        => new()
        {
            Name = settings.BusinessName,
            OsmCategory = settings.Category,
            Url = settings.Url,
            AcceptsLightning = acceptsLightning,
            Latitude = settings.Latitude,
            Longitude = settings.Longitude,
            HouseNumber = settings.HouseNumber,
            Street = settings.Street,
            City = settings.City,
            PostCode = settings.PostCode,
            Country = settings.Country,
            Phone = settings.Phone
        };

    private static BtcMapMerchant ToMerchant(BtcMapListing listing, BtcMapStoreSettings settings, bool acceptsLightning)
        => new()
        {
            Name = settings.BusinessName,
            OsmCategory = settings.Category ?? listing.Category,
            Url = settings.Url,
            AcceptsLightning = acceptsLightning,
            Latitude = listing.Latitude,
            Longitude = listing.Longitude,
            HouseNumber = settings.HouseNumber,
            Street = settings.Street,
            City = settings.City,
            PostCode = settings.PostCode,
            Country = settings.Country,
            Phone = settings.Phone
        };

    public Task<BtcMapListing> GetListingForStore(string storeId)
        => _listingRepository.GetListingForStoreAsync(storeId);

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
    /// Submit a new listing: write to OSM via the merchant's own OAuth credentials,
    /// optionally submit a directory PR via the plugin-builder API. The OSM write
    /// runs first because it's the irreversible side effect — if the directory leg
    /// fails, the OSM identifiers are persisted before re-throwing so a retry
    /// targets the existing node instead of creating a duplicate.
    /// </summary>
    public async Task<BtcMapListing> SubmitListing(string storeId, BtcMapStoreSettings settings,
        bool acceptsLightning, bool submitToDirectory,
        string osmType = null, long? osmId = null)
    {
        var hasOsmType = !string.IsNullOrWhiteSpace(osmType);
        if (osmId.HasValue != hasOsmType)
            throw new ArgumentException("osmId and osmType must be provided together when linking an existing OSM element.");
        // New-link path is node-only; writer (UpdateNodeAsync/UnlistNodeAsync) stays
        // permissive of way for legacy listings already linked before the cutover.
        if (hasOsmType && osmType != "node")
            throw new ArgumentOutOfRangeException(nameof(osmType), "Only 'node' is supported for new links.");
        if (osmId is <= 0)
            throw new ArgumentOutOfRangeException(nameof(osmId), "osmId must be positive when linking an existing element.");

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
            Phone = settings.Phone,
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

        await DispatchOsmSubmitAsync(storeId, settings, acceptsLightning, listing, osmType, osmId,
            CancellationToken.None);

        listing.Status = ListingStatus.Active;

        if (!submitToDirectory)
        {
            await ctx.SaveChangesAsync();
            return listing;
        }

        var request = new BtcMapSubmitRequest
        {
            Name = settings.BusinessName,
            Url = settings.Url,
            Description = settings.DirectoryDescription,
            Type = settings.DirectoryType,
            SubType = settings.DirectorySubType,
            Country = settings.Country,
            Twitter = settings.DirectoryTwitter,
            Github = settings.DirectoryGithub,
            OnionUrl = settings.DirectoryOnionUrl
        };

        try
        {
            var directoryResponse = await _apiClient.SubmitAsync(request);
            ApplyDirectoryResponse(listing, directoryResponse, settings.Url);
            await ctx.SaveChangesAsync();
            return listing;
        }
        catch (PluginBuilderApiException)
        {
            // OSM is already live with the merchant's identity attached. Persist
            // the OSM identifiers before re-throwing so a retry targets the existing
            // node instead of creating a duplicate. The controller's existing
            // catch(PluginBuilderApiException) block surfaces the error to the
            // merchant — swallowing here would lose that signal.
            await ctx.SaveChangesAsync();
            throw;
        }
    }

    private void ApplyDirectoryResponse(BtcMapListing listing, BtcMapSubmitResponse response, string url)
    {
        if (response.Directory == null) return;
        if (response.Directory.PrUrl != null)
        {
            listing.DirectorySubmittedAt = DateTimeOffset.UtcNow;
            listing.DirectorySubmittedUrl = url;
            listing.DirectoryPrUrl = response.Directory.PrUrl;
        }
        else if (response.Directory.Skipped?.StartsWith("duplicate-url:") == true)
        {
            // URL already merged in merchants.json — track as submitted so the
            // merchants.json page-load check surfaces the merged banner.
            listing.DirectorySubmittedAt = DateTimeOffset.UtcNow;
            listing.DirectorySubmittedUrl = url;
            _logger.LogInformation("Directory submission skipped (already merged): {Reason}", response.Directory.Skipped);
        }
        else if (response.Directory.Skipped != null)
        {
            _logger.LogInformation("Directory submission skipped: {Reason}", response.Directory.Skipped);
        }
    }

    private async Task DispatchOsmSubmitAsync(string storeId, BtcMapStoreSettings settings, bool acceptsLightning,
        BtcMapListing listing, string osmType, long? osmId, CancellationToken ct)
    {
        var merchant = ToMerchant(settings, acceptsLightning);
        try
        {
            if (osmId.HasValue)
            {
                var result = await _osmApiClient.UpdateNodeAsync(storeId, osmId.Value, osmType, merchant, ct);
                listing.OsmElementId = osmId.Value;
                listing.OsmElementType = osmType;
                listing.OsmElementVersion = result.NewVersion;
                // Source-of-truth for the local row's display name is the OSM name tag
                // post-merge: preserves a curator's existing name when linking, falls
                // back to the merchant-supplied name on previously-unnamed nodes.
                if (!string.IsNullOrWhiteSpace(result.ResolvedName))
                    listing.BusinessName = result.ResolvedName;
            }
            else
            {
                var created = await _osmApiClient.CreateNodeAsync(storeId, merchant, ct);
                listing.OsmElementId = created.NodeId;
                listing.OsmElementType = "node";
                listing.OsmElementVersion = created.Version;
            }
        }
        catch (OsmAuthException)
        {
            // 401 from OSM: invalidate the stored token so the merchant sees the
            // Reconnect state on next page load. Re-throw so the controller can
            // surface a clear error to the merchant.
            await _oauthRepo.ClearTokenOnlyAsync(storeId);
            throw;
        }
    }

    public async Task UpdateListing(BtcMapListing listing, BtcMapStoreSettings settings, bool acceptsLightning)
    {
        var merchant = ToMerchant(listing, settings, acceptsLightning);
        int newVersion;
        string resolvedName;
        try
        {
            var result = await _osmApiClient.UpdateNodeAsync(
                listing.StoreId, listing.OsmElementId, listing.OsmElementType, merchant, CancellationToken.None);
            newVersion = result.NewVersion;
            resolvedName = result.ResolvedName;
        }
        catch (OsmAuthException)
        {
            await _oauthRepo.ClearTokenOnlyAsync(listing.StoreId);
            throw;
        }

        await using var ctx = _dbContextFactory.CreateContext();
        var dbListing = await ctx.Listings.FirstAsync(l => l.Id == listing.Id);
        dbListing.OsmElementVersion = newVersion;
        // OSM is source-of-truth for the display name post-merge: preserves a curator's
        // existing name on the node, falls back to merchant-supplied name otherwise.
        dbListing.BusinessName = !string.IsNullOrWhiteSpace(resolvedName)
            ? resolvedName
            : settings.BusinessName;
        dbListing.Category = settings.Category;
        dbListing.Url = settings.Url;
        dbListing.HouseNumber = settings.HouseNumber;
        dbListing.Street = settings.Street;
        dbListing.City = settings.City;
        dbListing.PostCode = settings.PostCode;
        dbListing.Country = settings.Country;
        dbListing.Phone = settings.Phone;
        dbListing.AcceptsLightning = acceptsLightning;
        dbListing.LastVerifiedAt = DateTimeOffset.UtcNow;
        await ctx.SaveChangesAsync();
    }

    public async Task UnlistStore(string storeId)
    {
        var listing = await GetListingForStore(storeId);
        if (listing == null || listing.Status == ListingStatus.Unlisted)
            return;

        try
        {
            await _osmApiClient.UnlistNodeAsync(storeId, listing.OsmElementId, listing.OsmElementType,
                listing.BusinessName, CancellationToken.None);
        }
        catch (OsmAuthException)
        {
            await _oauthRepo.ClearTokenOnlyAsync(storeId);
            throw;
        }

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
        var merchant = new BtcMapMerchant
        {
            Name = listing.BusinessName,
            OsmCategory = listing.Category,
            Url = listing.Url,
            AcceptsLightning = acceptsLightning,
            Latitude = listing.Latitude,
            Longitude = listing.Longitude,
            HouseNumber = listing.HouseNumber,
            Street = listing.Street,
            City = listing.City,
            PostCode = listing.PostCode,
            Country = listing.Country,
            Phone = listing.Phone
        };
        int newVersion;
        try
        {
            newVersion = await _osmApiClient.ReverifyNodeAsync(
                listing.StoreId, listing.OsmElementId, listing.OsmElementType, merchant, CancellationToken.None);
        }
        catch (OsmAuthException)
        {
            await _oauthRepo.ClearTokenOnlyAsync(listing.StoreId);
            throw;
        }

        await using var ctx = _dbContextFactory.CreateContext();
        var dbListing = await ctx.Listings.FirstAsync(l => l.Id == listing.Id);
        dbListing.OsmElementVersion = newVersion;
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
            OnionUrl = settings.DirectoryOnionUrl
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
}
