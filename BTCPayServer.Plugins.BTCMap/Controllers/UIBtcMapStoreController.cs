using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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

namespace BTCPayServer.Plugins.BTCMap.Controllers;

[Route("~/plugins/btcmap/stores/{storeId}")]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyStoreSettings)]
public class UIBtcMapStoreController : Controller
{
    private readonly BtcMapService _btcMapService;
    private readonly OsmAuthService _osmAuthService;
    private readonly NominatimApiClient _nominatimApiClient;
    private readonly DirectoryService _directoryService;
    private readonly IAuthorizationService _authorizationService;
    private readonly ILogger<UIBtcMapStoreController> _logger;

    public UIBtcMapStoreController(
        BtcMapService btcMapService,
        OsmAuthService osmAuthService,
        NominatimApiClient nominatimApiClient,
        DirectoryService directoryService,
        IAuthorizationService authorizationService,
        ILogger<UIBtcMapStoreController> logger)
    {
        _btcMapService = btcMapService;
        _osmAuthService = osmAuthService;
        _nominatimApiClient = nominatimApiClient;
        _directoryService = directoryService;
        _authorizationService = authorizationService;
        _logger = logger;
    }

    private async Task<BtcMapListingViewModel> BuildViewModel(string storeId, BtcMapStoreSettings settings = null)
    {
        await PluginMigrationRunner.WaitForMigration;
        var osmSettings = await _osmAuthService.GetSettings();
        var listing = await _btcMapService.GetListingForStore(storeId);
        var storeData = HttpContext.GetStoreData();

        var isServerAdmin = (await _authorizationService.AuthorizeAsync(
            User, Policies.CanModifyServerSettings)).Succeeded;

        var vm = new BtcMapListingViewModel
        {
            OsmConnected = !string.IsNullOrEmpty(osmSettings.OsmAccessToken),
            OsmClientIdConfigured = _osmAuthService.IsClientIdConfigured,
            IsServerAdmin = isServerAdmin,
            IsMainnet = _osmAuthService.IsMainnet,
            OsmDisplayName = osmSettings.OsmDisplayName,
            ExistingListing = listing,
            StatusMessage = TempData["StatusMessage"]?.ToString(),
            DirectorySubmittedAt = listing?.DirectorySubmittedAt
        };

        if (listing != null)
        {
            var parts = new[] { listing.Street, listing.City, listing.PostCode, listing.Country }
                .Where(p => !string.IsNullOrWhiteSpace(p));
            vm.Address = string.Join(", ", parts);

            if (listing.Status == ListingStatus.Active)
            {
                var verifiedAt = listing.LastVerifiedAt;

                // Dev-only override: BTCMAP_VERIFICATION_AGE shifts the display calculation
                // so verification banners can be tested without waiting 11 months.
                var ageOverride = Environment.GetEnvironmentVariable("BTCMAP_VERIFICATION_AGE");
                if (!string.IsNullOrEmpty(ageOverride) && !_osmAuthService.IsMainnet)
                {
                    verifiedAt = ageOverride switch
                    {
                        "expiring" => DateTimeOffset.UtcNow.AddDays(-320), // ~10.5 months ago
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
                AcceptsOnchain = listing.AcceptsOnchain,
                AcceptsLightning = listing.AcceptsLightning,
                DirectoryUrl = storeData?.StoreWebsite
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
                AcceptsOnchain = enabledIds.Any(id => string.Equals(id, "BTC-CHAIN", StringComparison.OrdinalIgnoreCase)),
                AcceptsLightning = enabledIds.Any(id => string.Equals(id, "BTC-LN", StringComparison.OrdinalIgnoreCase)),
                DirectoryUrl = storeData.StoreWebsite
            };
        }

        // Check if store is already listed in the directory. Prefer the URL the merchant
        // actually submitted (DirectorySubmittedUrl), fall back to the BTCPay store website
        // for listings created before DirectorySubmittedUrl was introduced.
        if (listing != null)
        {
            var checkUrl = !string.IsNullOrEmpty(listing.DirectorySubmittedUrl)
                ? listing.DirectorySubmittedUrl
                : storeData?.StoreWebsite;
            if (!string.IsNullOrEmpty(checkUrl))
            {
                var existingEntry = await _directoryService.CheckExistingListing(checkUrl);
                if (existingEntry != null)
                {
                    vm.DirectoryExistingUrl = existingEntry.Url;
                    vm.DirectoryExistingName = existingEntry.Name;
                    vm.DirectoryExistingType = existingEntry.Type;
                }
            }
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
            _logger.LogError(ex, "BTC Map search failed for store {StoreId}", storeId);
            var vm = await BuildViewModel(storeId, model);
            vm.StatusMessage = "Error: Search failed. Please try again or check the server logs.";
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
            _logger.LogError(ex, "Failed to create BTC Map listing for store {StoreId}", storeId);
            TempData["StatusMessage"] = "Error: Failed to create listing. Please try again or check the server logs.";
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

        if (osmId <= 0)
        {
            TempData["StatusMessage"] = "Error: Invalid OSM element ID.";
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
            _logger.LogError(ex, "Failed to link OSM element for store {StoreId}", storeId);
            TempData["StatusMessage"] = "Error: Failed to update OSM element. Please try again or check the server logs.";
        }

        return RedirectToAction(nameof(Index), new { storeId });
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
            await _btcMapService.UpdateListing(listing, model);
            TempData["StatusMessage"] = "Listing updated successfully.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update BTC Map listing for store {StoreId}", storeId);
            TempData["StatusMessage"] = "Error: Failed to update listing. Please try again or check the server logs.";
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
            _logger.LogError(ex, "Failed to unlist store {StoreId} from BTC Map", storeId);
            TempData["StatusMessage"] = "Error: Failed to remove listing. Please try again or check the server logs.";
        }

        return RedirectToAction(nameof(Index), new { storeId });
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

    [HttpPost("directory/submit")]
    public async Task<IActionResult> DirectorySubmit(string storeId, [Bind(Prefix = "Settings")] BtcMapStoreSettings model)
    {
        if (string.IsNullOrWhiteSpace(model.DirectoryUrl) ||
            string.IsNullOrWhiteSpace(model.DirectoryDescription) ||
            string.IsNullOrWhiteSpace(model.DirectoryType))
        {
            return Json(new { success = false, message = "URL, description, and type are required." });
        }

        // Type allow-list — the BTC Map plugin only accepts directory submissions for the
        // two physical-location-bearing types. Other types (apps, hosted-btcpay) are valid
        // upstream but out of scope for this plugin's mental model.
        if (model.DirectoryType != "merchants" && model.DirectoryType != "non-profits")
        {
            return Json(new
            {
                success = false,
                message = "Only Merchants and Non-Profits can be submitted from the BTC Map plugin."
            });
        }

        if (model.DirectoryType == "merchants" && string.IsNullOrWhiteSpace(model.DirectorySubType))
            return Json(new { success = false, message = "Subcategory is required for merchants." });

        // Re-check merchants.json against the URL the user actually typed (not the BTCPay
        // store website). The page-load duplicate alert in BuildViewModel only checks
        // storeData.StoreWebsite, but the user can type any URL into the form, so we need
        // a second check here against the actual submission URL.
        var existing = await _directoryService.CheckExistingListing(model.DirectoryUrl);
        if (existing != null)
        {
            var existingName = string.IsNullOrEmpty(existing.Name) ? "an existing entry" : existing.Name;
            return Json(new
            {
                success = false,
                message = $"This URL is already listed in the BTCPay Server Directory as \"{existingName}\" ({existing.Url}). No need to submit again."
            });
        }

        var listing = await _btcMapService.GetListingForStore(storeId);
        if (listing == null)
        {
            return Json(new
            {
                success = false,
                message = "Create the BTC Map listing before submitting to the BTCPay Server Directory."
            });
        }

        var name = listing.BusinessName;
        var country = listing.Country;

        var issueUrl = _directoryService.BuildGitHubIssueUrl(
            name,
            model.DirectoryUrl,
            model.DirectoryTwitter,
            model.DirectoryType,
            model.DirectorySubType,
            country,
            model.DirectoryDescription);

        // Records submission before the GitHub issue popup opens. If the popup
        // is blocked or abandoned, the UI still shows "submitted" — the merchant
        // can use "Submit Again" (DirectoryReset) to retry.
        await _directoryService.RecordSubmission(storeId, model.DirectoryUrl);

        return Json(new { success = true, issueUrl });
    }

    [HttpPost("directory/reset")]
    public async Task<IActionResult> DirectoryReset(string storeId)
    {
        await _directoryService.ClearSubmission(storeId);
        TempData["StatusMessage"] = "Directory submission reset. You can submit again.";
        return RedirectToAction(nameof(Index), new { storeId });
    }

    // Server-admin-only. The Connect/Disconnect actions manage a server-wide OSM
    // token, but we expose them from the store page so the admin doesn't need to
    // hop to a separate server-settings page.
    [HttpPost("connect-osm")]
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyServerSettings)]
    public async Task<IActionResult> ConnectOsm(string storeId)
    {
        if (!_osmAuthService.IsClientIdConfigured)
        {
            TempData["StatusMessage"] = $"Error: OSM integration is not configured. Set the {OsmAuthService.ClientIdEnvVar} environment variable.";
            return RedirectToAction(nameof(Index), new { storeId });
        }

        var codeVerifier = OsmAuthService.GenerateCodeVerifier();
        var codeChallenge = OsmAuthService.GenerateCodeChallenge(codeVerifier);

        // Local callback URL on this BTCPay instance — used directly in dev, or
        // encoded as the state on mainnet so the bounce page knows where to
        // redirect the auth code after OSM returns it.
        var localCallbackUrl = Url.Action("Callback", "UIBtcMapOAuth", null, Request.Scheme);
        var redirectUri = _osmAuthService.GetRedirectUri(localCallbackUrl);

        var nonce = OsmAuthService.GenerateStateNonce();

        // state = base64(origin + "|" + nonce). The bounce page splits on the
        // last "|" to extract the origin for routing and the nonce for forwarding
        // to the BTCPay callback. On dev (direct redirect), OSM sends state back
        // as-is and the callback decodes it to extract the nonce.
        var origin = Request.GetAbsoluteRoot();
        var state = Convert.ToBase64String(Encoding.UTF8.GetBytes(origin + "|" + nonce));
        var osmSettings = await _osmAuthService.GetSettings();

        // Prune stale flows (abandoned tabs, etc.)
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-15);
        foreach (var key in osmSettings.PendingFlows
                     .Where(kv => kv.Value.CreatedAt < cutoff)
                     .Select(kv => kv.Key).ToList())
            osmSettings.PendingFlows.Remove(key);

        osmSettings.PendingFlows[nonce] = new PendingOAuthFlow
        {
            CodeVerifier = codeVerifier,
            RedirectUri = redirectUri,
            StoreId = storeId,
            CreatedAt = DateTimeOffset.UtcNow
        };
        await _osmAuthService.SaveSettings(osmSettings);

        var authUrl = _osmAuthService.GetAuthorizationUrl(redirectUri, state, codeChallenge);
        return Redirect(authUrl);
    }

    [HttpPost("disconnect-osm")]
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyServerSettings)]
    public async Task<IActionResult> DisconnectOsm(string storeId)
    {
        var osmSettings = await _osmAuthService.GetSettings();
        osmSettings.OsmAccessToken = null;
        osmSettings.OsmDisplayName = null;
        osmSettings.PendingFlows.Clear();
        await _osmAuthService.SaveSettings(osmSettings);

        TempData["StatusMessage"] = "OSM account disconnected.";
        return RedirectToAction(nameof(Index), new { storeId });
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
            await _btcMapService.ReverifyListing(listing);
            TempData["StatusMessage"] = "Verification confirmed. Your BTC Map listing has been updated.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to confirm verification for store {StoreId}", storeId);
            TempData["StatusMessage"] = "Error: Failed to update verification. Please try again or check the server logs.";
        }

        return RedirectToAction(nameof(Index), new { storeId });
    }

}
