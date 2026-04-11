using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Plugins.BTCMap.Models;
using BTCPayServer.Plugins.BTCMap.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.BTCMap.Controllers;

[Route("~/plugins/btcmap/stores/{storeId}")]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyStoreSettings)]
public class UIBtcMapStoreController : Controller
{
    private readonly BtcMapService _btcMapService;
    private readonly OsmAuthService _osmAuthService;
    private readonly NominatimApiClient _nominatimApiClient;
    private readonly IAuthorizationService _authorizationService;

    public UIBtcMapStoreController(
        BtcMapService btcMapService,
        OsmAuthService osmAuthService,
        NominatimApiClient nominatimApiClient,
        IAuthorizationService authorizationService)
    {
        _btcMapService = btcMapService;
        _osmAuthService = osmAuthService;
        _nominatimApiClient = nominatimApiClient;
        _authorizationService = authorizationService;
    }

    private async Task<BtcMapListingViewModel> BuildViewModel(string storeId, BtcMapStoreSettings settings = null)
    {
        var osmSettings = await _osmAuthService.GetSettings();
        var listing = await _btcMapService.GetListingForStore(storeId);
        var isAdmin = (await _authorizationService.AuthorizeAsync(User, Policies.CanModifyServerSettings)).Succeeded;
        var storeData = HttpContext.GetStoreData();

        var vm = new BtcMapListingViewModel
        {
            OsmConnected = !string.IsNullOrEmpty(osmSettings.OsmAccessToken),
            IsMainnet = _osmAuthService.IsMainnet,
            IsAdmin = isAdmin,
            OsmDisplayName = osmSettings.OsmDisplayName,
            OsmClientId = isAdmin ? osmSettings.OsmClientId : null,
            OsmClientSecretSet = !string.IsNullOrEmpty(osmSettings.OsmClientSecret),
            ExistingListing = listing,
            StatusMessage = TempData["StatusMessage"]?.ToString()
        };

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
                AcceptsOnchain = listing.AcceptsOnchain,
                AcceptsLightning = listing.AcceptsLightning
            };
        }
        else if (storeData != null)
        {
            // Auto-populate from BTCPay store data for new listings (no settings posted yet,
            // no prior listing). BTCPay's StoreData only exposes StoreName/StoreWebsite — no
            // address fields — so the merchant still has to enter address/coordinates manually
            // (or click "Lookup Coordinates").
            var enabledIds = storeData.GetEnabledPaymentIds().Select(p => p.ToString()).ToArray();
            vm.Settings = new BtcMapStoreSettings
            {
                BusinessName = storeData.StoreName,
                AcceptsOnchain = enabledIds.Any(id => id.EndsWith("-CHAIN", StringComparison.OrdinalIgnoreCase)),
                AcceptsLightning = enabledIds.Any(id => id.EndsWith("-LN", StringComparison.OrdinalIgnoreCase))
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

            // Merge Bitcoin-tagged duplicates (first) with name/address nearby matches, dedupe by OSM type+id
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
                IsMainnet = _osmAuthService.IsMainnet,
                OsmConnected = true
            });
        }
        catch (Exception ex)
        {
            var vm = await BuildViewModel(storeId, model);
            vm.StatusMessage = $"Error: Search failed — {ex.Message}";
            return View("Index", vm);
        }
    }

    [HttpPost("create")]
    public async Task<IActionResult> CreateListing(string storeId, [Bind(Prefix = "Settings")] BtcMapStoreSettings model)
    {
        if (!ModelState.IsValid)
        {
            TempData["StatusMessage"] = "Error: Please fill in all required fields.";
            return RedirectToAction(nameof(Index), new { storeId });
        }

        try
        {
            await _btcMapService.CreateNewListing(storeId, model);
            TempData["StatusMessage"] = "Your business has been listed on BTC Map! It may take up to 10 minutes to appear.";
        }
        catch (Exception ex)
        {
            TempData["StatusMessage"] = $"Error: Failed to create listing — {ex.Message}";
        }

        return RedirectToAction(nameof(Index), new { storeId });
    }

    [HttpPost("link")]
    public async Task<IActionResult> LinkExisting(string storeId, BtcMapStoreSettings model, string osmType, long osmId)
    {
        if (!ModelState.IsValid)
        {
            TempData["StatusMessage"] = "Error: Please fill in all required fields.";
            return RedirectToAction(nameof(Index), new { storeId });
        }

        if (osmType is not ("node" or "way"))
        {
            TempData["StatusMessage"] = "Error: Invalid OSM element type.";
            return RedirectToAction(nameof(Index), new { storeId });
        }

        try
        {
            await _btcMapService.LinkToExistingElement(storeId, model, osmType, osmId);
            TempData["StatusMessage"] = "Bitcoin acceptance tags added to the existing location! It may take up to 10 minutes to appear on BTC Map.";
        }
        catch (Exception ex)
        {
            TempData["StatusMessage"] = $"Error: Failed to update OSM element — {ex.Message}";
        }

        return RedirectToAction(nameof(Index), new { storeId });
    }

    [HttpPost("update")]
    public async Task<IActionResult> UpdateListing(string storeId, [Bind(Prefix = "Settings")] BtcMapStoreSettings model)
    {
        var listing = await _btcMapService.GetListingForStore(storeId);
        if (listing == null)
        {
            TempData["StatusMessage"] = "Error: No listing found for this store.";
            return RedirectToAction(nameof(Index), new { storeId });
        }

        try
        {
            await _btcMapService.UpdateListing(listing, model);
            TempData["StatusMessage"] = "Listing updated successfully.";
        }
        catch (Exception ex)
        {
            TempData["StatusMessage"] = $"Error: Failed to update listing — {ex.Message}";
        }

        return RedirectToAction(nameof(Index), new { storeId });
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
            TempData["StatusMessage"] = $"Error: Failed to remove listing — {ex.Message}";
        }

        return RedirectToAction(nameof(Index), new { storeId });
    }

    [HttpPost("geocode")]
    public async Task<IActionResult> Geocode(string storeId, [Bind(Prefix = "Settings")] BtcMapStoreSettings model)
    {
        var result = await _nominatimApiClient.Geocode(model.Street, model.City, model.PostCode, model.Country);
        if (result == null)
            return Json(new { success = false, message = "Address not found." });

        return Json(new { success = true, lat = result.Value.lat, lon = result.Value.lon });
    }
}
