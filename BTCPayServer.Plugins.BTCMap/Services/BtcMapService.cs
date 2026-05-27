using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BTCPayServer.Plugins.BTCMap.Data;
using BTCPayServer.Plugins.BTCMap.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.BTCMap.Services;

public class BtcMapService : IBtcMapService
{
    private readonly BtcMapDbContextFactory _dbContextFactory;
    private readonly IListingRepository _listingRepository;
    private readonly IPluginBuilderApiClient _apiClient;
    private readonly IOverpassApiClient _overpassApiClient;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<BtcMapService> _logger;

    public BtcMapService(
        BtcMapDbContextFactory dbContextFactory,
        IListingRepository listingRepository,
        IPluginBuilderApiClient apiClient,
        IOverpassApiClient overpassApiClient,
        IHttpContextAccessor httpContextAccessor,
        ILogger<BtcMapService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _listingRepository = listingRepository;
        _apiClient = apiClient;
        _overpassApiClient = overpassApiClient;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    public Task<BtcMapListing> GetListingForStore(string storeId)
        => _listingRepository.GetListingForStoreAsync(storeId);

    public async Task<List<OverpassElement>> SearchNearby(double lat, double lon, string name,
        string street = null, string city = null)
    {
        var results = await _overpassApiClient.SearchNearby(lat, lon, 200, name);
        if (results.Count > 0) return results;

        if (!string.IsNullOrWhiteSpace(street) && !string.IsNullOrWhiteSpace(city))
        {
            results = await _overpassApiClient.SearchByAddress(lat, lon, 200, street, city);
            if (results.Count > 0) return results;
        }

        return await _overpassApiClient.SearchByCoordinates(lat, lon, 100);
    }

    public async Task<List<OverpassElement>> CheckDuplicates(double lat, double lon)
    {
        return await _overpassApiClient.CheckExistingBitcoinTags(lat, lon);
    }

    public async Task<BtcMapListing> SubmitListing(string storeId, BtcMapStoreSettings settings,
        bool acceptsLightning, bool acceptsOnchain, bool submitToDirectory)
    {
        await using var ctx = _dbContextFactory.CreateContext();
        var existing = await ctx.Listings.FirstOrDefaultAsync(l => l.StoreId == storeId);

        var externalId = existing?.BtcMapExternalId ?? ComposeExternalId(storeId);
        var now = DateTimeOffset.UtcNow;

        var listing = existing ?? new BtcMapListing
        {
            Id = Guid.NewGuid().ToString(),
            StoreId = storeId,
            CreatedAt = now,
            BtcMapExternalId = externalId
        };

        listing.BusinessName = settings.BusinessName;
        listing.Category = settings.Category;
        listing.Latitude = settings.Latitude.Value;
        listing.Longitude = settings.Longitude.Value;
        listing.HouseNumber = settings.HouseNumber;
        listing.Street = settings.Street;
        listing.City = settings.City;
        listing.PostCode = settings.PostCode;
        listing.Country = settings.Country;
        listing.Phone = settings.Phone;
        listing.Email = settings.Email;
        listing.AcceptsLightning = acceptsLightning;
        listing.AcceptsOnchain = acceptsOnchain;
        listing.Url = settings.Url;
        listing.Status = ListingStatus.Pending;
        listing.Description = submitToDirectory ? settings.DirectoryDescription : null;
        listing.Twitter = submitToDirectory ? settings.DirectoryTwitter : null;
        listing.Github = submitToDirectory ? settings.DirectoryGithub : null;
        listing.OnionUrl = submitToDirectory ? settings.DirectoryOnionUrl : null;
        listing.DirectoryType = submitToDirectory ? settings.DirectoryType : null;
        listing.DirectorySubType = submitToDirectory ? settings.DirectorySubType : null;

        if (existing == null)
            ctx.Listings.Add(listing);

        var request = ToSubmitRequest(settings, externalId, acceptsLightning, acceptsOnchain, submitToDirectory);

        try
        {
            var response = await _apiClient.SubmitAsync(request);
            ApplyBtcMapResponse(listing, response, isFirstSubmission: existing == null || listing.BtcMapSubmittedAt == null);
            ApplyDirectoryResponse(listing, response, settings.Url);
            listing.Status = ListingStatus.Active;
            await ctx.SaveChangesAsync();
            return listing;
        }
        catch (PluginBuilderApiException)
        {
            listing.Status = ListingStatus.Error;
            try
            {
                await ctx.SaveChangesAsync();
            }
            catch (Exception saveEx)
            {
                _logger.LogError(saveEx,
                    "Failed to persist listing error state after submission failure for store {StoreId}.",
                    storeId);
            }
            throw;
        }
    }

    public async Task UpdateListing(BtcMapListing listing, BtcMapStoreSettings settings,
        bool acceptsLightning, bool acceptsOnchain)
    {
        await using var ctx = _dbContextFactory.CreateContext();
        var dbListing = await ctx.Listings.FirstAsync(l => l.Id == listing.Id);

        var externalId = dbListing.BtcMapExternalId ?? ComposeExternalId(dbListing.StoreId);
        dbListing.BusinessName = settings.BusinessName;
        dbListing.Category = settings.Category;
        dbListing.Latitude = settings.Latitude.Value;
        dbListing.Longitude = settings.Longitude.Value;
        dbListing.HouseNumber = settings.HouseNumber;
        dbListing.Street = settings.Street;
        dbListing.City = settings.City;
        dbListing.PostCode = settings.PostCode;
        dbListing.Country = settings.Country;
        dbListing.Phone = settings.Phone;
        dbListing.Email = settings.Email;
        dbListing.AcceptsLightning = acceptsLightning;
        dbListing.AcceptsOnchain = acceptsOnchain;
        dbListing.Url = settings.Url;
        dbListing.BtcMapExternalId = externalId;

        var submitToDirectory = dbListing.DirectorySubmittedAt == null;
        var request = ToSubmitRequest(settings, externalId, acceptsLightning, acceptsOnchain, submitToDirectory);

        try
        {
            var response = await _apiClient.SubmitAsync(request);
            ApplyBtcMapResponse(dbListing, response, isFirstSubmission: dbListing.BtcMapSubmittedAt == null);
            ApplyDirectoryResponse(dbListing, response, settings.Url);
            dbListing.Status = ListingStatus.Active;
            await ctx.SaveChangesAsync();
        }
        catch (PluginBuilderApiException)
        {
            dbListing.Status = ListingStatus.Error;
            try { await ctx.SaveChangesAsync(); } catch (Exception saveEx)
            {
                _logger.LogError(saveEx,
                    "Failed to persist listing error state during update for store {StoreId}.", dbListing.StoreId);
            }
            throw;
        }
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
            SubmitToDirectory = true,
            SubmitToBtcMap = false
        };

        var response = await _apiClient.SubmitAsync(request);

        if (response.Directory?.PrUrl != null || response.Directory?.Skipped?.StartsWith("duplicate-url:") == true)
        {
            await using var ctx = _dbContextFactory.CreateContext();
            var dbListing = await ctx.Listings.FirstAsync(l => l.Id == listing.Id);
            dbListing.DirectorySubmittedAt = DateTimeOffset.UtcNow;
            dbListing.DirectorySubmittedUrl = listing.Url;
            if (response.Directory.PrUrl != null)
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
        else if (response.Directory?.Skipped != null)
        {
            _logger.LogInformation("Directory submission skipped: {Reason}", response.Directory.Skipped);
        }

        return response;
    }

    private BtcMapSubmitRequest ToSubmitRequest(BtcMapStoreSettings settings, string externalId,
        bool acceptsLightning, bool acceptsOnchain, bool submitToDirectory)
    {
        var country = NormalizeCountryForBtcMap(settings.Country);
        var directoryCountry = !string.IsNullOrEmpty(settings.Country) ? settings.Country : null;
        return new BtcMapSubmitRequest
        {
            Name = settings.BusinessName,
            Url = settings.Url,
            Description = submitToDirectory ? settings.DirectoryDescription : null,
            Type = submitToDirectory ? settings.DirectoryType : null,
            SubType = submitToDirectory ? settings.DirectorySubType : null,
            Country = submitToDirectory ? directoryCountry : country,
            Twitter = submitToDirectory ? settings.DirectoryTwitter : null,
            Github = submitToDirectory ? settings.DirectoryGithub : null,
            OnionUrl = submitToDirectory ? settings.DirectoryOnionUrl : null,
            Phone = settings.Phone,
            Email = settings.Email,
            Lat = settings.Latitude,
            Lon = settings.Longitude,
            Category = NormalizeCategory(settings.Category),
            ExternalId = externalId,
            HouseNumber = settings.HouseNumber,
            Street = settings.Street,
            City = settings.City,
            Postcode = settings.PostCode,
            AcceptsLightning = acceptsLightning ? true : null,
            AcceptsOnchain = acceptsOnchain ? true : null,
            SubmitToDirectory = submitToDirectory,
            SubmitToBtcMap = true
        };

        static string NormalizeCountryForBtcMap(string country)
        {
            if (string.IsNullOrEmpty(country)) return null;
            if (string.Equals(country, "GLOBAL", StringComparison.OrdinalIgnoreCase)) return null;
            return country;
        }
    }

    internal static string NormalizeCategory(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var idx = raw.IndexOf('=');
        if (idx < 0) return raw.ToLowerInvariant();
        var key = raw.Substring(0, idx);
        var value = raw.Substring(idx + 1);
        var picked = string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase) ? key : value;
        return picked.ToLowerInvariant();
    }

    private string ComposeExternalId(string storeId)
    {
        var host = _httpContextAccessor.HttpContext?.Request?.Host.Host;
        if (string.IsNullOrEmpty(host))
            throw new InvalidOperationException("Cannot compose ExternalId: HttpContext has no host.");
        return $"{host.ToLowerInvariant()}:{storeId}";
    }

    private void ApplyBtcMapResponse(BtcMapListing listing, BtcMapSubmitResponse response, bool isFirstSubmission)
    {
        if (response.BtcMap == null) return;
        if (response.BtcMap.Id.HasValue)
            listing.BtcMapSubmissionId = response.BtcMap.Id;
        if (!string.IsNullOrEmpty(response.BtcMap.ExternalId))
            listing.BtcMapExternalId = response.BtcMap.ExternalId;
        var now = DateTimeOffset.UtcNow;
        if (isFirstSubmission)
            listing.BtcMapSubmittedAt = now;
        else
            listing.BtcMapLastEditedAt = now;
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
            listing.DirectorySubmittedAt = DateTimeOffset.UtcNow;
            listing.DirectorySubmittedUrl = url;
            _logger.LogInformation("Directory submission skipped (already merged): {Reason}", response.Directory.Skipped);
        }
        else if (response.Directory.Skipped != null)
        {
            _logger.LogInformation("Directory submission skipped: {Reason}", response.Directory.Skipped);
        }
    }
}
