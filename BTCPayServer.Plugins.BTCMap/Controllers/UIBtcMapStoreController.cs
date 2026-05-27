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
    private readonly IBtcMapService _btcMapService;
    private readonly INominatimApiClient _nominatimApiClient;
    private readonly IDirectoryListingChecker _directoryListingChecker;
    private readonly BtcMapCapabilityState _capabilityState;
    private readonly BTCPayNetworkProvider _networkProvider;
    private readonly ILogger<UIBtcMapStoreController> _logger;

    public UIBtcMapStoreController(
        IBtcMapService btcMapService,
        INominatimApiClient nominatimApiClient,
        IDirectoryListingChecker directoryListingChecker,
        BtcMapCapabilityState capabilityState,
        BTCPayNetworkProvider networkProvider,
        ILogger<UIBtcMapStoreController> logger)
    {
        _btcMapService = btcMapService;
        _nominatimApiClient = nominatimApiClient;
        _directoryListingChecker = directoryListingChecker;
        _capabilityState = capabilityState;
        _networkProvider = networkProvider;
        _logger = logger;
    }

    private bool IsMainnet => _networkProvider.NetworkType == ChainName.Mainnet;

    private const string PluginBuilderUnreachableMessage =
        "Error: BTC Map submissions are temporarily unavailable. Please contact the administrator.";

    // BTC-LN is BTCPay's canonical Lightning paymentId; LNURL/BOLT12 reuse it
    // rather than registering distinct top-level IDs, so this gate stays correct
    // as Lightning protocols evolve.
    private bool StoreAcceptsLightning()
    {
        var storeData = HttpContext.GetStoreData();
        if (storeData == null) return false;
        return storeData.GetEnabledPaymentIds()
            .Select(p => p.ToString())
            .Any(id => string.Equals(id, "BTC-LN", StringComparison.OrdinalIgnoreCase));
    }

    private bool StoreAcceptsOnchain()
    {
        var storeData = HttpContext.GetStoreData();
        if (storeData == null) return false;
        return storeData.GetEnabledPaymentIds()
            .Select(p => p.ToString())
            .Any(id => string.Equals(id, "BTC", StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeUrl(string input, string defaultScheme)
    {
        if (string.IsNullOrWhiteSpace(input)) return input;
        var trimmed = input.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out _)) return trimmed;
        return $"{defaultScheme}://{trimmed}";
    }

    private static void NormalizeUrls(BtcMapStoreSettings model)
    {
        model.Url = NormalizeUrl(model.Url, "https");
        model.DirectoryGithub = NormalizeUrl(model.DirectoryGithub, "https");
        model.DirectoryOnionUrl = NormalizeUrl(model.DirectoryOnionUrl, "http");
    }

    internal static bool PhoneIsWellFormed(string phone)
        => string.IsNullOrWhiteSpace(phone) || phone.TrimStart().StartsWith("+");

    private const string PhoneFormatErrorMessage =
        "Error: Phone must start with a country code (e.g. +44 20 8452 7891), or set the address Country so we can add it automatically.";

    private static string BuildSubmissionStatusMessage(BtcMapListing listing, bool submitToDirectory)
    {
        if (submitToDirectory && listing.DirectorySubmittedAt.HasValue)
            return "Your business has been submitted to BTC Map and the BTCPay Server Directory.";
        return "Your business has been submitted to BTC Map. BTC Map moderators review submissions before publishing them to the map.";
    }

    private async Task<BtcMapListingViewModel> BuildViewModel(string storeId, BtcMapStoreSettings settings = null,
        bool editMode = false)
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
            DirectoryPrUrl = listing?.DirectoryPrUrl,
            EditMode = editMode,
            PluginBuilderReachable = _capabilityState.PluginBuilderReachable
        };

        if (listing != null)
        {
            var streetLine = string.Join(" ",
                new[] { listing.HouseNumber, listing.Street }.Where(s => !string.IsNullOrWhiteSpace(s)));
            var parts = new[] { streetLine, listing.City, listing.PostCode, listing.Country }
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
                HouseNumber = listing.HouseNumber,
                Street = listing.Street,
                City = listing.City,
                PostCode = listing.PostCode,
                Country = listing.Country,
                Phone = listing.Phone,
                Email = listing.Email,
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
            vm.Settings = new BtcMapStoreSettings
            {
                BusinessName = storeData.StoreName,
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

    [HttpGet("edit")]
    public async Task<IActionResult> Edit(string storeId)
    {
        var listing = await _btcMapService.GetListingForStore(storeId);
        if (listing == null)
            return RedirectToAction(nameof(Index), new { storeId });
        return View("Index", await BuildViewModel(storeId, editMode: true));
    }

    [HttpPost("search")]
    public async Task<IActionResult> Search(string storeId, [Bind(Prefix = "Settings")] BtcMapStoreSettings model)
    {
        if (!ModelState.IsValid)
            return View("Index", await BuildViewModel(storeId, model));

        NormalizeUrls(model);

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
                IsMainnet = IsMainnet,
                PluginBuilderReachable = _capabilityState.PluginBuilderReachable
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

    [HttpPost("create-new")]
    public async Task<IActionResult> CreateNew(string storeId, [Bind(Prefix = "Settings")] BtcMapStoreSettings model,
        bool submitToDirectory = false)
    {
        if (!_capabilityState.PluginBuilderReachable)
        {
            TempData["StatusMessage"] = PluginBuilderUnreachableMessage;
            return RedirectToAction(nameof(Index), new { storeId });
        }

        if (!ModelState.IsValid)
        {
            TempData["StatusMessage"] = "Error: Please fill in all required fields.";
            return RedirectToAction(nameof(Index), new { storeId });
        }

        if (!PhoneIsWellFormed(model.Phone))
        {
            var vm = await BuildViewModel(storeId, model);
            vm.StatusMessage = PhoneFormatErrorMessage;
            return View("Index", vm);
        }

        NormalizeUrls(model);

        try
        {
            var listing = await _btcMapService.SubmitListing(
                storeId, model, StoreAcceptsLightning(), StoreAcceptsOnchain(), submitToDirectory);
            TempData["StatusMessage"] = BuildSubmissionStatusMessage(listing, submitToDirectory);
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
        if (!_capabilityState.PluginBuilderReachable)
        {
            TempData["StatusMessage"] = PluginBuilderUnreachableMessage;
            return RedirectToAction(nameof(Index), new { storeId });
        }

        if (!ModelState.IsValid)
        {
            TempData["StatusMessage"] = "Error: Invalid settings.";
            return RedirectToAction(nameof(Index), new { storeId });
        }

        if (!PhoneIsWellFormed(model.Phone))
        {
            var vm = await BuildViewModel(storeId, model, editMode: true);
            vm.StatusMessage = PhoneFormatErrorMessage;
            return View("Index", vm);
        }

        var listing = await _btcMapService.GetListingForStore(storeId);
        if (listing == null)
        {
            TempData["StatusMessage"] = "Error: No listing found for this store.";
            return RedirectToAction(nameof(Index), new { storeId });
        }

        NormalizeUrls(model);

        try
        {
            await _btcMapService.UpdateListing(listing, model, StoreAcceptsLightning(), StoreAcceptsOnchain());
            TempData["StatusMessage"] = "Listing updated successfully.";
            return RedirectToAction(nameof(Index), new { storeId });
        }
        catch (PluginBuilderApiException ex)
        {
            _logger.LogError(ex, "API call failed for store {StoreId}", storeId);
            var vm = await BuildViewModel(storeId, model, editMode: true);
            vm.StatusMessage = $"Error: {ex.Message}";
            return View("Index", vm);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update BTC Map listing for store {StoreId}", storeId);
            var vm = await BuildViewModel(storeId, model, editMode: true);
            vm.StatusMessage = "Error: Failed to update listing. Please try again or check the server logs.";
            return View("Index", vm);
        }
    }

    [HttpPost("directory/submit")]
    public async Task<IActionResult> DirectorySubmit(string storeId,
        [Bind(Prefix = "Settings")] BtcMapStoreSettings model)
    {
        if (!_capabilityState.PluginBuilderReachable)
        {
            TempData["StatusMessage"] = PluginBuilderUnreachableMessage;
            return RedirectToAction(nameof(Index), new { storeId });
        }

        var listing = await _btcMapService.GetListingForStore(storeId);
        if (listing == null || listing.Status != ListingStatus.Active)
        {
            TempData["StatusMessage"] = "Error: No active listing found.";
            return RedirectToAction(nameof(Index), new { storeId });
        }

        NormalizeUrls(model);

        try
        {
            var response = await _btcMapService.SubmitToDirectoryOnly(listing, model);
            var skipped = response.Directory?.Skipped;
            if (skipped?.StartsWith("duplicate-url:") == true)
                TempData["StatusMessage"] = "This URL is already listed in the BTCPay Server Directory.";
            else if (skipped == "duplicate-open-pr")
                TempData["StatusMessage"] = "A pending submission for your store already exists in the BTCPay Server Directory.";
            else if (skipped != null)
            {
                _logger.LogWarning("Directory submission skipped (unknown reason): {Reason}", skipped);
                TempData["StatusMessage"] = "Directory submission could not be completed.";
            }
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
            var streetForGeocode = string.Join(" ",
                new[] { model.HouseNumber, model.Street }.Where(s => !string.IsNullOrWhiteSpace(s)));
            var result = await _nominatimApiClient.Geocode(streetForGeocode, model.City, model.PostCode, model.Country);
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
}
