using System;
using System.Linq;
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
    public async Task<IActionResult> Callback(
        string code,
        string error,
        [FromQuery(Name = "error_description")] string errorDescription)
    {
        var settings = await _osmAuthService.GetSettings();

        // Find the most recent pending flow. The bounce page doesn't forward
        // the state param, so we can't look up by nonce — but there's typically
        // only one pending flow (concurrent flows are rare).
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-15);
        var staleKeys = settings.PendingFlows
            .Where(kv => kv.Value.CreatedAt < cutoff)
            .Select(kv => kv.Key).ToList();
        foreach (var key in staleKeys)
            settings.PendingFlows.Remove(key);

        var entry = settings.PendingFlows
            .OrderByDescending(kv => kv.Value.CreatedAt)
            .FirstOrDefault();

        if (entry.Value == null)
        {
            TempData["StatusMessage"] = "Error: No pending OSM connection. Please try connecting again.";
            return RedirectToStoreOrFallback(null);
        }

        var nonce = entry.Key;
        var flow = entry.Value;
        var storeId = flow.StoreId;

        // Consume this flow entry — it cannot be reused regardless of outcome.
        settings.PendingFlows.Remove(nonce);

        if (!string.IsNullOrEmpty(error))
        {
            await _osmAuthService.SaveSettings(settings);
            var message = !string.IsNullOrEmpty(errorDescription)
                ? $"Error: OpenStreetMap authorization failed: {errorDescription}"
                : $"Error: OpenStreetMap authorization failed ({error}).";
            TempData["StatusMessage"] = message;
            return RedirectToStoreOrFallback(storeId);
        }

        if (string.IsNullOrEmpty(code))
        {
            await _osmAuthService.SaveSettings(settings);
            TempData["StatusMessage"] = "Error: OpenStreetMap did not return an authorization code.";
            return RedirectToStoreOrFallback(storeId);
        }

        try
        {
            var accessToken = await _osmAuthService.ExchangeCodeForToken(
                code, flow.RedirectUri, flow.CodeVerifier);
            var displayName = await _osmAuthService.GetDisplayName(accessToken);

            settings.OsmAccessToken = accessToken;
            settings.OsmDisplayName = displayName;
            await _osmAuthService.SaveSettings(settings);

            TempData["StatusMessage"] = $"Successfully connected as {displayName}.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OSM OAuth token exchange failed");
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
