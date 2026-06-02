using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
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
    private readonly ISettingsRepository _settingsRepository;
    private readonly ILogger<BtcMapService> _logger;

    // Settings key BTCPay uses for its server-wide configuration; matches
    // SettingsRepository.KeyNameByType(typeof(BTCPayServer.Services.ServerSettings)).
    private const string ServerSettingsKey = "BTCPayServer.Services.ServerSettings";

    public BtcMapService(
        BtcMapDbContextFactory dbContextFactory,
        IListingRepository listingRepository,
        IPluginBuilderApiClient apiClient,
        IOverpassApiClient overpassApiClient,
        IHttpContextAccessor httpContextAccessor,
        ISettingsRepository settingsRepository,
        ILogger<BtcMapService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _listingRepository = listingRepository;
        _apiClient = apiClient;
        _overpassApiClient = overpassApiClient;
        _httpContextAccessor = httpContextAccessor;
        _settingsRepository = settingsRepository;
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

        var externalId = existing?.BtcMapExternalId ?? await ComposeExternalIdAsync(storeId);
        var now = DateTimeOffset.UtcNow;

        ListingSnapshot? snapshot = existing != null ? ListingSnapshot.Capture(existing) : null;

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

        var request = ToSubmitRequest(settings, externalId, acceptsLightning, acceptsOnchain, submitToDirectory, listing);

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
            if (existing != null)
            {
                snapshot.Value.RestoreInto(existing);
                existing.Status = ListingStatus.Error;
            }
            else
            {
                ctx.Listings.Remove(listing);
            }
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

        var snapshot = ListingSnapshot.Capture(dbListing);
        var externalId = dbListing.BtcMapExternalId ?? await ComposeExternalIdAsync(dbListing.StoreId);

        // Edit view doesn't render directory inputs, so settings.Directory* bind to
        // null on edit POST. Fall back to the stored row so a phone-only edit doesn't
        // wipe previously-published directory metadata.
        var preservedDescription = settings.DirectoryDescription ?? dbListing.Description;
        var preservedTwitter = settings.DirectoryTwitter ?? dbListing.Twitter;
        var preservedGithub = settings.DirectoryGithub ?? dbListing.Github;
        var preservedOnionUrl = settings.DirectoryOnionUrl ?? dbListing.OnionUrl;
        var preservedDirectoryType = settings.DirectoryType ?? dbListing.DirectoryType;
        var preservedDirectorySubType = settings.DirectorySubType ?? dbListing.DirectorySubType;

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
        dbListing.Description = preservedDescription;
        dbListing.Twitter = preservedTwitter;
        dbListing.Github = preservedGithub;
        dbListing.OnionUrl = preservedOnionUrl;
        dbListing.DirectoryType = preservedDirectoryType;
        dbListing.DirectorySubType = preservedDirectorySubType;

        var submitToDirectory = dbListing.DirectorySubmittedAt != null;
        var request = ToSubmitRequest(settings, externalId, acceptsLightning, acceptsOnchain, submitToDirectory, dbListing);

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
            snapshot.RestoreInto(dbListing);
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
        bool acceptsLightning, bool acceptsOnchain, bool submitToDirectory, BtcMapListing fallback)
    {
        var country = NormalizeCountryForBtcMap(settings.Country);
        var directoryCountry = !string.IsNullOrEmpty(settings.Country) ? settings.Country : null;
        var description = settings.DirectoryDescription ?? fallback?.Description;
        var directoryType = settings.DirectoryType ?? fallback?.DirectoryType;
        var directorySubType = settings.DirectorySubType ?? fallback?.DirectorySubType;
        var twitter = settings.DirectoryTwitter ?? fallback?.Twitter;
        var github = settings.DirectoryGithub ?? fallback?.Github;
        var onionUrl = settings.DirectoryOnionUrl ?? fallback?.OnionUrl;
        return new BtcMapSubmitRequest
        {
            Name = settings.BusinessName,
            Url = settings.Url,
            Description = submitToDirectory ? description : null,
            Type = submitToDirectory ? directoryType : null,
            SubType = submitToDirectory ? directorySubType : null,
            Country = submitToDirectory ? directoryCountry : country,
            Twitter = submitToDirectory ? twitter : null,
            Github = submitToDirectory ? github : null,
            OnionUrl = submitToDirectory ? onionUrl : null,
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
            AcceptsLightning = acceptsLightning,
            AcceptsOnchain = acceptsOnchain,
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

    private async Task<string> ComposeExternalIdAsync(string storeId, string hostOverride = null)
    {
        var resolved = hostOverride ?? await ResolveCanonicalHostAsync();
        if (string.IsNullOrEmpty(resolved))
            throw new InvalidOperationException(
                "Cannot compose ExternalId: no host available. Caller must supply a host when running outside an HTTP request context.");
        return $"{resolved.ToLowerInvariant()}:{storeId}";
    }

    // Prefer BTCPay's admin-configured BaseUrl so the ExternalId namespace is stable
    // across alternate hostnames / reverse-proxy paths. Fall back to Request.Host
    // when no BaseUrl is set (fresh installs).
    private async Task<string> ResolveCanonicalHostAsync()
    {
        try
        {
            var settings = await _settingsRepository.GetSettingAsync<BtcPayServerSettingsView>(ServerSettingsKey);
            if (!string.IsNullOrEmpty(settings?.BaseUrl)
                && Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out var uri)
                && !string.IsNullOrEmpty(uri.Host))
                return uri.Host;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to read BTCPay ServerSettings.BaseUrl; falling back to Request.Host for ExternalId composition.");
        }
        return _httpContextAccessor.HttpContext?.Request?.Host.Host;
    }

    // Local projection of BTCPayServer.Services.ServerSettings — only BaseUrl matters
    // to us, and a shape-matching DTO avoids taking a project reference on BTCPay core.
    private sealed class BtcPayServerSettingsView
    {
        public string BaseUrl { get; set; }
    }

    private readonly struct ListingSnapshot
    {
        public string BusinessName { get; init; }
        public string Category { get; init; }
        public double Latitude { get; init; }
        public double Longitude { get; init; }
        public string HouseNumber { get; init; }
        public string Street { get; init; }
        public string City { get; init; }
        public string PostCode { get; init; }
        public string Country { get; init; }
        public string Phone { get; init; }
        public string Email { get; init; }
        public bool AcceptsLightning { get; init; }
        public bool AcceptsOnchain { get; init; }
        public string Url { get; init; }
        public string Description { get; init; }
        public string Twitter { get; init; }
        public string Github { get; init; }
        public string OnionUrl { get; init; }
        public string DirectoryType { get; init; }
        public string DirectorySubType { get; init; }
        public ListingStatus Status { get; init; }

        public static ListingSnapshot Capture(BtcMapListing l) => new()
        {
            BusinessName = l.BusinessName,
            Category = l.Category,
            Latitude = l.Latitude,
            Longitude = l.Longitude,
            HouseNumber = l.HouseNumber,
            Street = l.Street,
            City = l.City,
            PostCode = l.PostCode,
            Country = l.Country,
            Phone = l.Phone,
            Email = l.Email,
            AcceptsLightning = l.AcceptsLightning,
            AcceptsOnchain = l.AcceptsOnchain,
            Url = l.Url,
            Description = l.Description,
            Twitter = l.Twitter,
            Github = l.Github,
            OnionUrl = l.OnionUrl,
            DirectoryType = l.DirectoryType,
            DirectorySubType = l.DirectorySubType,
            Status = l.Status
        };

        public void RestoreInto(BtcMapListing l)
        {
            l.BusinessName = BusinessName;
            l.Category = Category;
            l.Latitude = Latitude;
            l.Longitude = Longitude;
            l.HouseNumber = HouseNumber;
            l.Street = Street;
            l.City = City;
            l.PostCode = PostCode;
            l.Country = Country;
            l.Phone = Phone;
            l.Email = Email;
            l.AcceptsLightning = AcceptsLightning;
            l.AcceptsOnchain = AcceptsOnchain;
            l.Url = Url;
            l.Description = Description;
            l.Twitter = Twitter;
            l.Github = Github;
            l.OnionUrl = OnionUrl;
            l.DirectoryType = DirectoryType;
            l.DirectorySubType = DirectorySubType;
            l.Status = Status;
        }
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
