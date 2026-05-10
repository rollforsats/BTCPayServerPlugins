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

    private async Task<bool> StoreHasOsmToken(string storeId)
    {
        var oauth = await _oauthRepo.GetForStoreAsync(storeId);
        return oauth != null && !string.IsNullOrEmpty(oauth.OsmAccessToken);
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
        // New-link path is node-only; writer (UpdateNodeAsync/UnlistNodeAsync) stays
        // permissive of way for legacy listings already linked before the cutover.
        if (hasOsmType && osmType != "node")
            throw new ArgumentOutOfRangeException(nameof(osmType), "Only 'node' is supported for new links.");
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

        // Single context: stage the row in memory, call the API, apply API-derived
        // fields, then commit once. If the API call throws, SaveChangesAsync never
        // runs, so no orphan Pending row gets persisted.
        await using var ctx = _dbContextFactory.CreateContext();
        var existing = await ctx.Listings.FirstOrDefaultAsync(l => l.StoreId == storeId);
        if (existing != null)
            ctx.Listings.Remove(existing);
        ctx.Listings.Add(listing);

        if (await StoreHasOsmToken(storeId))
        {
            // Per-store OAuth path: tag OSM directly using the merchant's own
            // credentials, then route any directory leg through plugin-builder
            // with TagOnOsm=false so the bot doesn't double-tag.
            await DispatchOsmSubmitAsync(storeId, settings, acceptsLightning, listing, osmType, osmId,
                CancellationToken.None);

            if (submitToDirectory)
            {
                request.TagOnOsm = false;
                request.OsmNodeId = listing.OsmElementId;
                request.OsmNodeType = listing.OsmElementType;
                var directoryResponse = await _apiClient.SubmitAsync(request);
                ApplyDirectoryResponse(listing, directoryResponse, settings.Url);
            }
        }
        else
        {
            // Legacy plugin-builder bot path.
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
            ApplyDirectoryResponse(listing, response, settings.Url);
        }

        listing.Status = ListingStatus.Active;
        await ctx.SaveChangesAsync();
        return listing;
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
        int? newVersion = null;
        string resolvedName = null;

        if (await StoreHasOsmToken(listing.StoreId))
        {
            var merchant = ToMerchant(listing, settings, acceptsLightning);
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
        }
        else
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
            if (response.Osm?.Skipped != null)
                _logger.LogWarning("OSM leg skipped during update: {Reason}", response.Osm.Skipped);
            else
                newVersion = response.Osm?.NewVersion;
        }

        await using var ctx = _dbContextFactory.CreateContext();
        var dbListing = await ctx.Listings.FirstAsync(l => l.Id == listing.Id);
        if (newVersion.HasValue)
            dbListing.OsmElementVersion = newVersion.Value;
        // OAuth path: OSM is source-of-truth (preserves curator name). Legacy bot
        // path: no resolved name returned, so fall back to form value.
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

        if (await StoreHasOsmToken(storeId))
        {
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
        }
        else
        {
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
        int? newVersion = null;

        if (await StoreHasOsmToken(listing.StoreId))
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
        }
        else
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
            if (response.Osm?.Skipped != null)
                _logger.LogWarning("OSM leg skipped during reverify: {Reason}", response.Osm.Skipped);
            else
                newVersion = response.Osm?.NewVersion;
        }

        await using var ctx = _dbContextFactory.CreateContext();
        var dbListing = await ctx.Listings.FirstAsync(l => l.Id == listing.Id);
        if (newVersion.HasValue)
            dbListing.OsmElementVersion = newVersion.Value;
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
