using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Plugins.BTCMap.Data;
using BTCPayServer.Plugins.BTCMap.Models;
using BTCPayServer.Plugins.BTCMap.Services;
using BTCPayServer.Plugins.BTCMap.Services.Osm;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace BTCPayServer.Plugins.BTCMap.Controllers;

[Route("~/plugins/btcmap/stores/{storeId}")]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyStoreSettings)]
public class UIBtcMapStoreController : Controller
{
    private const int PendingStateTtlMinutes = 10;

    private readonly IBtcMapService _btcMapService;
    private readonly INominatimApiClient _nominatimApiClient;
    private readonly IDirectoryListingChecker _directoryListingChecker;
    private readonly IBtcMapStoreOAuthRepository _oauthRepo;
    private readonly IOsmAuthService _osmAuthService;
    private readonly BTCPayNetworkProvider _networkProvider;
    private readonly ILogger<UIBtcMapStoreController> _logger;

    public UIBtcMapStoreController(
        IBtcMapService btcMapService,
        INominatimApiClient nominatimApiClient,
        IDirectoryListingChecker directoryListingChecker,
        IBtcMapStoreOAuthRepository oauthRepo,
        IOsmAuthService osmAuthService,
        BTCPayNetworkProvider networkProvider,
        ILogger<UIBtcMapStoreController> logger)
    {
        _btcMapService = btcMapService;
        _nominatimApiClient = nominatimApiClient;
        _directoryListingChecker = directoryListingChecker;
        _oauthRepo = oauthRepo;
        _osmAuthService = osmAuthService;
        _networkProvider = networkProvider;
        _logger = logger;
    }

    private bool IsMainnet => _networkProvider.NetworkType == ChainName.Mainnet;

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

    private static string BuildSubmissionStatusMessage(BtcMapListing listing, bool submitToDirectory, bool isLink)
    {
        if (submitToDirectory && listing.DirectorySubmittedAt.HasValue)
            return isLink
                ? "Your store has been linked on BTC Map and submitted to the BTCPay Server Directory."
                : "Your business has been listed on BTC Map and submitted to the BTCPay Server Directory.";

        return isLink
            ? "Your store has been linked on BTC Map. It may take up to 10 minutes for changes to appear."
            : "Your business has been listed on BTC Map! It may take up to 10 minutes to appear.";
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
            DirectoryPrUrl = listing?.DirectoryPrUrl,
            RedirectUriToShow = BuildOsmCallbackUri(storeId)
        };

        await PopulateOsmStateAsync(vm, storeId);

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

            if (listing.Status == ListingStatus.Active)
            {
                var verifiedAt = listing.LastVerifiedAt;

                var ageOverride = Environment.GetEnvironmentVariable("BTCMAP_VERIFICATION_AGE");
                if (!string.IsNullOrEmpty(ageOverride) && !IsMainnet)
                {
                    verifiedAt = ageOverride switch
                    {
                        "expiring" => DateTimeOffset.UtcNow.AddMonths(-12).AddDays(15),
                        "expired" => DateTimeOffset.UtcNow.AddMonths(-13),
                        _ => verifiedAt
                    };
                }

                var expiresAt = verifiedAt.AddMonths(12);
                var remaining = expiresAt - DateTimeOffset.UtcNow;
                vm.DaysUntilVerificationExpires = remaining <= TimeSpan.Zero
                    ? 0
                    : (int)Math.Ceiling(remaining.TotalDays);
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
        string osmType, long osmId, bool submitToDirectory = false)
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

        NormalizeUrls(model);

        try
        {
            var acceptsLightning = StoreAcceptsLightning();
            var listing = await _btcMapService.SubmitListing(storeId, model, acceptsLightning, submitToDirectory, osmType, osmId);
            TempData["StatusMessage"] = BuildSubmissionStatusMessage(listing, submitToDirectory, isLink: true);
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

        NormalizeUrls(model);

        try
        {
            var acceptsLightning = StoreAcceptsLightning();
            var listing = await _btcMapService.SubmitListing(storeId, model, acceptsLightning, submitToDirectory);
            TempData["StatusMessage"] = BuildSubmissionStatusMessage(listing, submitToDirectory, isLink: false);
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

        NormalizeUrls(model);

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

        NormalizeUrls(model);

        try
        {
            var response = await _btcMapService.SubmitToDirectoryOnly(listing, model);
            var skipped = response.Directory?.Skipped;
            if (skipped?.StartsWith("duplicate-url:") == true)
                TempData["StatusMessage"] = "This URL is already listed in the BTCPay Server Directory.";
            else if (skipped == "duplicate-open-pr")
                TempData["StatusMessage"] = "A pending submission for your store already exists in the BTCPay Server Directory.";
            else if (skipped == "directory-github-token-not-configured")
            {
                _logger.LogWarning("Directory submission skipped (server config): {Reason}", skipped);
                TempData["StatusMessage"] = "The BTCPay Server Directory is temporarily unavailable. Please try again later.";
            }
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

    [HttpPost("oauth/save-credentials")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveOsmCredentials(string storeId, [Bind(Prefix = "OsmCredentials")] OsmCredentialsViewModel model)
    {
        var clientId = (model?.OsmClientId ?? string.Empty).Trim();
        var clientSecret = (model?.OsmClientSecret ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
        {
            TempData["StatusMessage"] = "Error: OSM Client ID and Client Secret are both required.";
            return RedirectToAction(nameof(Index), new { storeId });
        }

        await _oauthRepo.SaveClientCredentialsAsync(storeId, clientId, clientSecret);
        TempData["StatusMessage"] = "OSM credentials saved. Click Connect to authorize.";
        return RedirectToAction(nameof(Index), new { storeId });
    }

    [HttpPost("oauth/clear-credentials")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ClearOsmCredentials(string storeId)
    {
        await _oauthRepo.ClearOAuthAsync(storeId);
        TempData["StatusMessage"] = "OSM credentials cleared.";
        return RedirectToAction(nameof(Index), new { storeId });
    }

    [HttpPost("oauth/connect")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ConnectOsm(string storeId)
    {
        var oauth = await _oauthRepo.GetForStoreAsync(storeId);
        if (oauth == null || string.IsNullOrWhiteSpace(oauth.OsmClientId) || string.IsNullOrWhiteSpace(oauth.OsmClientSecret))
        {
            TempData["StatusMessage"] = "Error: Save OSM credentials before connecting.";
            return RedirectToAction(nameof(Index), new { storeId });
        }

        var stateBytes = RandomNumberGenerator.GetBytes(32);
        var state = WebEncoders.Base64UrlEncode(stateBytes);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(PendingStateTtlMinutes);
        await _oauthRepo.SetPendingStateAsync(storeId, state, expiresAt);

        var redirectUri = BuildOsmCallbackUri(storeId);
        var authorizeUrl = _osmAuthService.GetAuthorizationUrl(oauth.OsmClientId, redirectUri, state);
        return Redirect(authorizeUrl);
    }

    [HttpPost("oauth/disconnect")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DisconnectOsm(string storeId)
    {
        var oauth = await _oauthRepo.GetForStoreAsync(storeId);
        if (oauth != null && !string.IsNullOrWhiteSpace(oauth.OsmAccessToken)
            && !string.IsNullOrWhiteSpace(oauth.OsmClientId) && !string.IsNullOrWhiteSpace(oauth.OsmClientSecret))
        {
            await _osmAuthService.RevokeAsync(oauth.OsmClientId, oauth.OsmClientSecret, oauth.OsmAccessToken,
                HttpContext.RequestAborted);
        }
        await _oauthRepo.ClearOAuthAsync(storeId);
        TempData["StatusMessage"] = "Disconnected from OpenStreetMap.";
        return RedirectToAction(nameof(Index), new { storeId });
    }

    private string BuildOsmCallbackUri(string storeId)
    {
        var root = Request.Scheme + "://" + Request.Host.ToUriComponent() + Request.PathBase;
        return $"{root.TrimEnd('/')}/plugins/btcmap/stores/{storeId}/oauth/callback";
    }

    private async Task PopulateOsmStateAsync(BtcMapListingViewModel vm, string storeId)
    {
        var oauth = await _oauthRepo.GetForStoreAsync(storeId);

        // Surface error/state hints handed off from the OAuth callback via TempData.
        var pendingExpiredFlag = TempData["OsmConnectionState"]?.ToString();
        var errorKindFlag = TempData["OsmErrorKind"]?.ToString();
        var errorMessage = TempData["OsmErrorMessage"]?.ToString();

        if (!string.IsNullOrEmpty(errorKindFlag) && Enum.TryParse<OsmConnectionErrorKind>(errorKindFlag, out var kind))
        {
            vm.OsmErrorKind = kind;
            vm.OsmErrorMessage = errorMessage;
            vm.OsmState = OsmConnectionState.ConnectionError;
        }
        else if (string.Equals(pendingExpiredFlag, nameof(OsmConnectionState.PendingExpired), StringComparison.Ordinal))
        {
            vm.OsmState = OsmConnectionState.PendingExpired;
        }

        if (oauth == null)
        {
            if (vm.OsmState == OsmConnectionState.NotConfigured)
                vm.OsmState = OsmConnectionState.NotConfigured;
            return;
        }

        vm.OsmUsername = oauth.OsmUsername;
        vm.OsmConnectedAt = oauth.OsmConnectedAt;
        vm.OsmClientIdMasked = MaskClientId(oauth.OsmClientId);
        vm.OsmCredentials.OsmClientId = oauth.OsmClientId;
        // Never echo the secret back into the form. Show masked indicator only.

        // Only override OsmState if no error/expired flag was carried over from the callback.
        if (vm.OsmErrorKind == OsmConnectionErrorKind.None && vm.OsmState != OsmConnectionState.PendingExpired)
        {
            if (!string.IsNullOrEmpty(oauth.OsmAccessToken))
                vm.OsmState = OsmConnectionState.Connected;
            else if (!string.IsNullOrEmpty(oauth.OsmClientId) && !string.IsNullOrEmpty(oauth.OsmClientSecret))
                vm.OsmState = OsmConnectionState.ConfiguredNotConnected;
            else
                vm.OsmState = OsmConnectionState.NotConfigured;
        }
    }

    private static string MaskClientId(string clientId)
    {
        if (string.IsNullOrEmpty(clientId)) return null;
        if (clientId.Length <= 8) return new string('•', clientId.Length);
        return clientId[..4] + new string('•', Math.Max(4, clientId.Length - 8)) + clientId[^4..];
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
