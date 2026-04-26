using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Plugins.BTCMap.Data;
using BTCPayServer.Plugins.BTCMap.Models;
using BTCPayServer.Plugins.BTCMap.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace BTCPayServer.Plugins.BTCMap.Controllers;

[Route("~/plugins/btcmap/stores/{storeId}")]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyStoreSettings)]
public class UIBtcMapStoreController : Controller
{
    private readonly BtcMapService _btcMapService;
    private readonly NominatimApiClient _nominatimApiClient;
    private readonly DirectoryListingChecker _directoryListingChecker;
    private readonly BTCPayNetworkProvider _networkProvider;
    private readonly ILogger<UIBtcMapStoreController> _logger;

    public UIBtcMapStoreController(
        BtcMapService btcMapService,
        NominatimApiClient nominatimApiClient,
        DirectoryListingChecker directoryListingChecker,
        BTCPayNetworkProvider networkProvider,
        ILogger<UIBtcMapStoreController> logger)
    {
        _btcMapService = btcMapService;
        _nominatimApiClient = nominatimApiClient;
        _directoryListingChecker = directoryListingChecker;
        _networkProvider = networkProvider;
        _logger = logger;
    }

    private bool IsMainnet => _networkProvider.NetworkType == ChainName.Mainnet;

    private bool StoreAcceptsLightning()
    {
        var storeData = HttpContext.GetStoreData();
        if (storeData == null) return true;
        return storeData.GetEnabledPaymentIds()
            .Select(p => p.ToString())
            .Any(id => string.Equals(id, "BTC-LN", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<BtcMapListingViewModel> BuildViewModel(string storeId, BtcMapStoreSettings settings = null)
    {
        await PluginMigrationRunner.WaitForMigration;
        var listing = await _btcMapService.GetListingForStore(storeId);
        var storeData = HttpContext.GetStoreData();

        var vm = new BtcMapListingViewModel
        {
            IsMainnet = IsMainnet,
            ExistingListing = listing,
            StatusMessage = TempData["StatusMessage"]?.ToString(),
            DirectorySubmittedAt = listing?.DirectorySubmittedAt,
            DirectoryPrUrl = listing?.DirectoryPrUrl
        };

        if (listing != null)
        {
            var parts = new[] { listing.Street, listing.City, listing.PostCode, listing.Country }
                .Where(p => !string.IsNullOrWhiteSpace(p));
            vm.Address = string.Join(", ", parts);

            if (listing.DirectorySubmittedAt.HasValue && !string.IsNullOrEmpty(listing.Url))
            {
                var entry = await _directoryListingChecker.FindByUrl(listing.Url);
                if (entry != null)
                {
                    vm.DirectoryMergedName = entry.Name;
                    vm.DirectoryMergedType = entry.Type;
                    vm.DirectoryMergedUrl = entry.Url;
                }
            }

            if (listing.Status == ListingStatus.Active)
            {
                var verifiedAt = listing.LastVerifiedAt;

                var ageOverride = Environment.GetEnvironmentVariable("BTCMAP_VERIFICATION_AGE");
                if (!string.IsNullOrEmpty(ageOverride) && !IsMainnet)
                {
                    verifiedAt = ageOverride switch
                    {
                        "expiring" => DateTimeOffset.UtcNow.AddDays(-320),
                        "expired" => DateTimeOffset.UtcNow.AddMonths(-12),
                        _ => verifiedAt
                    };
                }

                var expiresAt = verifiedAt.AddMonths(11);
                vm.DaysUntilVerificationExpires = (int)(expiresAt - DateTimeOffset.UtcNow).TotalDays;
            }
        }

        if (settings != null)
        {
            vm.Settings = settings;
        }
        else if (listing != null)
        {
            vm.Settings = new BtcMapStoreSettings
            {
                BusinessName = listing.BusinessName,
                Category = listing.Category,
                Latitude = listing.Latitude,
                Longitude = listing.Longitude,
                Street = listing.Street,
                City = listing.City,
                PostCode = listing.PostCode,
                Country = listing.Country,
                AcceptsLightning = listing.AcceptsLightning,
                Url = listing.Url ?? storeData?.StoreWebsite,
                DirectoryDescription = listing.Description,
                DirectoryTwitter = listing.Twitter,
                DirectoryGithub = listing.Github,
                DirectoryOnionUrl = listing.OnionUrl,
                DirectoryType = listing.DirectoryType ?? "merchants",
                DirectorySubType = listing.DirectorySubType
            };
        }
        else if (storeData != null)
        {
            var enabledIds = storeData.GetEnabledPaymentIds().Select(p => p.ToString()).ToArray();
            vm.Settings = new BtcMapStoreSettings
            {
                BusinessName = storeData.StoreName,
                AcceptsLightning = enabledIds.Any(id => string.Equals(id, "BTC-LN", StringComparison.OrdinalIgnoreCase)),
                Url = storeData.StoreWebsite
            };
        }

        return vm;
    }

    [HttpGet]
    public async Task<IActionResult> Index(string storeId)
    {
        return View(await BuildViewModel(storeId));
    }

    [HttpPost("search")]
    public async Task<IActionResult> Search(string storeId, [Bind(Prefix = "Settings")] BtcMapStoreSettings model)
    {
        if (!ModelState.IsValid)
            return View("Index", await BuildViewModel(storeId, model));

        try
        {
            var duplicates = await _btcMapService.CheckDuplicates(model.Latitude.Value, model.Longitude.Value);
            var nearby = await _btcMapService.SearchNearby(
                model.Latitude.Value, model.Longitude.Value, model.BusinessName, model.Street, model.City);

            var seen = new HashSet<(string Type, long Id)>();
            var merged = new List<OverpassElement>();
            foreach (var el in duplicates.Concat(nearby))
            {
                if (seen.Add((el.Type, el.Id)))
                    merged.Add(el);
            }

            return View("SearchResults", new BtcMapListingViewModel
            {
                Settings = model,
                SearchResults = merged,
                IsMainnet = IsMainnet
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BTC Map search failed for store {StoreId}", storeId);
            var vm = await BuildViewModel(storeId, model);
            vm.StatusMessage = "Error: Search failed. Please try again or check the server logs.";
            return View("Index", vm);
        }
    }

    [HttpPost("link")]
    public async Task<IActionResult> LinkExisting(string storeId, BtcMapStoreSettings model,
        string osmType, long osmId, bool alreadyTagged = false, bool submitToDirectory = false)
    {
        if (!ModelState.IsValid)
        {
            TempData["StatusMessage"] = "Error: Please fill in all required fields.";
            return RedirectToAction(nameof(Index), new { storeId });
        }

        if (osmId <= 0 || osmType is not ("node" or "way"))
        {
            TempData["StatusMessage"] = "Error: Invalid OSM element.";
            return RedirectToAction(nameof(Index), new { storeId });
        }

        try
        {
            var acceptsLightning = StoreAcceptsLightning();

            if (alreadyTagged && !submitToDirectory)
            {
                // Element already has Bitcoin tags and no directory submission requested.
                // Pure local bookkeeping — no API call needed.
                await _btcMapService.AutoLinkExisting(storeId, model, osmType, osmId, acceptsLightning);
                TempData["StatusMessage"] = "Your store has been linked to the existing BTC Map listing.";
            }
            else
            {
                await _btcMapService.SubmitListing(storeId, model, acceptsLightning, submitToDirectory, osmType, osmId);
                TempData["StatusMessage"] = alreadyTagged
                    ? "Your store has been linked and submitted to the BTCPay Directory."
                    : "Bitcoin acceptance tags added! It may take up to 10 minutes to appear on BTC Map.";
            }
            return RedirectToAction(nameof(Index), new { storeId });
        }
        catch (PluginBuilderApiException ex)
        {
            _logger.LogError(ex, "API call failed for store {StoreId}", storeId);
            var vm = await BuildViewModel(storeId, model);
            vm.StatusMessage = $"Error: {ex.Message}";
            return View("Index", vm);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to link OSM element for store {StoreId}", storeId);
            var vm = await BuildViewModel(storeId, model);
            vm.StatusMessage = "Error: Failed to update listing. Please try again or check the server logs.";
            return View("Index", vm);
        }
    }

    [HttpPost("create-new")]
    public async Task<IActionResult> CreateNew(string storeId, [Bind(Prefix = "Settings")] BtcMapStoreSettings model,
        bool submitToDirectory = false)
    {
        if (!ModelState.IsValid)
        {
            TempData["StatusMessage"] = "Error: Please fill in all required fields.";
            return RedirectToAction(nameof(Index), new { storeId });
        }

        try
        {
            var acceptsLightning = StoreAcceptsLightning();
            await _btcMapService.SubmitListing(storeId, model, acceptsLightning, submitToDirectory);
            TempData["StatusMessage"] = "Your business has been listed on BTC Map! It may take up to 10 minutes to appear.";
            return RedirectToAction(nameof(Index), new { storeId });
        }
        catch (PluginBuilderApiException ex)
        {
            _logger.LogError(ex, "API call failed for store {StoreId}", storeId);
            var vm = await BuildViewModel(storeId, model);
            vm.StatusMessage = $"Error: {ex.Message}";
            return View("Index", vm);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create BTC Map listing for store {StoreId}", storeId);
            var vm = await BuildViewModel(storeId, model);
            vm.StatusMessage = "Error: Failed to create listing. Please try again or check the server logs.";
            return View("Index", vm);
        }
    }

    [HttpPost("update")]
    public async Task<IActionResult> UpdateListing(string storeId, [Bind(Prefix = "Settings")] BtcMapStoreSettings model)
    {
        if (!ModelState.IsValid)
        {
            TempData["StatusMessage"] = "Error: Invalid settings.";
            return RedirectToAction(nameof(Index), new { storeId });
        }

        var listing = await _btcMapService.GetListingForStore(storeId);
        if (listing == null)
        {
            TempData["StatusMessage"] = "Error: No listing found for this store.";
            return RedirectToAction(nameof(Index), new { storeId });
        }

        try
        {
            var acceptsLightning = StoreAcceptsLightning();
            await _btcMapService.UpdateListing(listing, model, acceptsLightning);
            TempData["StatusMessage"] = "Listing updated successfully.";
            return RedirectToAction(nameof(Index), new { storeId });
        }
        catch (PluginBuilderApiException ex)
        {
            _logger.LogError(ex, "API call failed for store {StoreId}", storeId);
            var vm = await BuildViewModel(storeId, model);
            vm.StatusMessage = $"Error: {ex.Message}";
            return View("Index", vm);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update BTC Map listing for store {StoreId}", storeId);
            var vm = await BuildViewModel(storeId, model);
            vm.StatusMessage = "Error: Failed to update listing. Please try again or check the server logs.";
            return View("Index", vm);
        }
    }

    [HttpPost("unlist")]
    public async Task<IActionResult> Unlist(string storeId)
    {
        try
        {
            await _btcMapService.UnlistStore(storeId);
            TempData["StatusMessage"] = "Your business has been removed from BTC Map.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to unlist store {StoreId} from BTC Map", storeId);
            TempData["StatusMessage"] = "Error: Failed to remove listing. Please try again or check the server logs.";
        }

        return RedirectToAction(nameof(Index), new { storeId });
    }

    [HttpPost("directory/submit")]
    public async Task<IActionResult> DirectorySubmit(string storeId,
        [Bind(Prefix = "Settings")] BtcMapStoreSettings model)
    {
        var listing = await _btcMapService.GetListingForStore(storeId);
        if (listing == null || listing.Status != ListingStatus.Active)
        {
            TempData["StatusMessage"] = "Error: No active listing found.";
            return RedirectToAction(nameof(Index), new { storeId });
        }

        try
        {
            var response = await _btcMapService.SubmitToDirectoryOnly(listing, model);
            if (response.Directory?.Skipped?.StartsWith("duplicate-url:") == true)
                TempData["StatusMessage"] = "This URL is already listed in the BTCPay Server Directory.";
            else if (response.Directory?.Skipped != null)
                TempData["StatusMessage"] = $"Directory submission skipped: {response.Directory.Skipped}";
            else if (response.Directory?.PrUrl != null)
                TempData["StatusMessage"] = "Submitted to the BTCPay Server Directory! A PR has been opened for review.";
            else
                TempData["StatusMessage"] = "Directory submission completed.";
            return RedirectToAction(nameof(Index), new { storeId });
        }
        catch (PluginBuilderApiException ex)
        {
            _logger.LogError(ex, "Directory API call failed for store {StoreId}", storeId);
            var vm = await BuildViewModel(storeId, model);
            vm.StatusMessage = $"Error: {ex.Message}";
            return View("Index", vm);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to submit to directory for store {StoreId}", storeId);
            var vm = await BuildViewModel(storeId, model);
            vm.StatusMessage = "Error: Failed to submit. Please try again.";
            return View("Index", vm);
        }
    }

    [HttpPost("geocode")]
    public async Task<IActionResult> Geocode(string storeId, [Bind(Prefix = "Settings")] BtcMapStoreSettings model)
    {
        if (string.IsNullOrWhiteSpace(model.Street) &&
            string.IsNullOrWhiteSpace(model.City) &&
            string.IsNullOrWhiteSpace(model.Country))
        {
            return Json(new { success = false, message = "Address not found." });
        }

        try
        {
            var result = await _nominatimApiClient.Geocode(model.Street, model.City, model.PostCode, model.Country);
            if (result == null)
                return Json(new { success = false, message = "Address not found." });

            return Json(new { success = true, lat = result.Value.lat, lon = result.Value.lon });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Geocoding failed for store {StoreId}", storeId);
            return Json(new { success = false, message = "Geocoding service unavailable. Please try again." });
        }
    }

    [HttpPost("confirm-verification")]
    public async Task<IActionResult> ConfirmVerification(string storeId)
    {
        var listing = await _btcMapService.GetListingForStore(storeId);
        if (listing == null || listing.Status != ListingStatus.Active)
        {
            TempData["StatusMessage"] = "Error: No active listing found for this store.";
            return RedirectToAction(nameof(Index), new { storeId });
        }

        try
        {
            await _btcMapService.ReverifyListing(listing, StoreAcceptsLightning());
            TempData["StatusMessage"] = "Verification confirmed. Your BTC Map listing has been updated.";
        }
        catch (PluginBuilderApiException ex)
        {
            _logger.LogError(ex, "API call failed for store {StoreId}", storeId);
            TempData["StatusMessage"] = $"Error: {ex.Message}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to confirm verification for store {StoreId}", storeId);
            TempData["StatusMessage"] = "Error: Failed to update verification. Please try again or check the server logs.";
        }

        return RedirectToAction(nameof(Index), new { storeId });
    }
}
