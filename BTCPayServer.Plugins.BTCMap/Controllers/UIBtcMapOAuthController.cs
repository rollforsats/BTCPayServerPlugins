using System;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Plugins.BTCMap.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.BTCMap.Controllers;

// Fixed callback route for the OSM OAuth flow. Not store-scoped because OSM's
// registered redirect URI must be a fixed exact-match string. The originating
// store is retrieved from PendingStoreId in server settings, which was written
// by UIBtcMapStoreController.ConnectOsm before the authorize redirect.
[Route("~/plugins/btcmap/oauth")]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyServerSettings)]
public class UIBtcMapOAuthController : Controller
{
    private readonly OsmAuthService _osmAuthService;
    private readonly ILogger<UIBtcMapOAuthController> _logger;

    public UIBtcMapOAuthController(OsmAuthService osmAuthService, ILogger<UIBtcMapOAuthController> logger)
    {
        _osmAuthService = osmAuthService;
        _logger = logger;
    }

    [HttpGet("callback")]
    public async Task<IActionResult> Callback(string code)
    {
        var settings = await _osmAuthService.GetSettings();

        if (string.IsNullOrEmpty(settings.PendingCodeVerifier) ||
            string.IsNullOrEmpty(settings.PendingRedirectUri))
        {
            TempData["StatusMessage"] = "Error: No pending OSM connection. Please try connecting again.";
            return RedirectToStoreOrFallback(settings.PendingStoreId);
        }

        if (string.IsNullOrEmpty(code))
        {
            settings.PendingCodeVerifier = null;
            settings.PendingRedirectUri = null;
            settings.PendingStoreId = null;
            await _osmAuthService.SaveSettings(settings);
            TempData["StatusMessage"] = "Error: OpenStreetMap did not return an authorization code.";
            return RedirectToStoreOrFallback(settings.PendingStoreId);
        }

        var storeId = settings.PendingStoreId;

        try
        {
            var accessToken = await _osmAuthService.ExchangeCodeForToken(
                code, settings.PendingRedirectUri, settings.PendingCodeVerifier);
            var displayName = await _osmAuthService.GetDisplayName(accessToken);

            settings.OsmAccessToken = accessToken;
            settings.OsmDisplayName = displayName;
            settings.PendingCodeVerifier = null;
            settings.PendingRedirectUri = null;
            settings.PendingStoreId = null;
            await _osmAuthService.SaveSettings(settings);

            TempData["StatusMessage"] = $"Successfully connected as {displayName}.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OSM OAuth token exchange failed");

            // Clear pending state on failure so the user can retry cleanly.
            settings.PendingCodeVerifier = null;
            settings.PendingRedirectUri = null;
            settings.PendingStoreId = null;
            await _osmAuthService.SaveSettings(settings);

            TempData["StatusMessage"] = "Error: OAuth authentication failed. Please try again or check the server logs.";
        }

        return RedirectToStoreOrFallback(storeId);
    }

    private IActionResult RedirectToStoreOrFallback(string storeId)
    {
        if (!string.IsNullOrEmpty(storeId))
            return RedirectToAction("Index", "UIBtcMapStore", new { storeId });

        // No pending store — unusual, but redirect to the BTCPay home so the
        // admin isn't stranded on a blank page.
        return Redirect("~/");
    }
}
