using System;
using System.Linq;
using System.Text;
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
// store is resolved by looking up the nonce-keyed PendingFlows entry (written
// by UIBtcMapStoreController.ConnectOsm before the authorize redirect).
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
        string state,
        [FromQuery(Name = "state_nonce")] string stateNonce,
        string error,
        [FromQuery(Name = "error_description")] string errorDescription)
    {
        var settings = await _osmAuthService.GetSettings();

        // Prune stale flows (>15 min).
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-15);
        var staleKeys = settings.PendingFlows
            .Where(kv => kv.Value.CreatedAt < cutoff)
            .Select(kv => kv.Key).ToList();
        foreach (var key in staleKeys)
            settings.PendingFlows.Remove(key);

        // Extract the nonce. On mainnet the bounce page forwards it as
        // state_nonce; on dev OSM sends the full state param directly.
        var nonce = stateNonce;
        if (string.IsNullOrEmpty(nonce) && !string.IsNullOrEmpty(state))
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(state));
                var pipeIdx = decoded.LastIndexOf('|');
                if (pipeIdx >= 0)
                    nonce = decoded[(pipeIdx + 1)..];
            }
            catch (FormatException)
            {
                // Malformed base64 — fall through to nonce validation below.
            }
        }

        // Validate the nonce against pending flows.
        if (string.IsNullOrEmpty(nonce) || !settings.PendingFlows.TryGetValue(nonce, out var flow))
        {
            TempData["StatusMessage"] = "Error: Invalid or expired OAuth state. Please try connecting again.";
            return RedirectToStoreOrFallback(null);
        }

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
