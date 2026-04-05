using System;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Plugins.BTCMap.Models;
using BTCPayServer.Plugins.BTCMap.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.BTCMap.Controllers;

[Route("~/plugins/btcmap/server")]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyServerSettings)]
public class UIBtcMapServerController : Controller
{
    private readonly OsmAuthService _osmAuthService;

    public UIBtcMapServerController(OsmAuthService osmAuthService)
    {
        _osmAuthService = osmAuthService;
    }

    [HttpPost("save")]
    public async Task<IActionResult> SaveSettings(string osmClientId, string osmClientSecret, string returnStoreId)
    {
        var settings = await _osmAuthService.GetSettings();
        settings.OsmClientId = osmClientId?.Trim();
        if (!string.IsNullOrEmpty(osmClientSecret?.Trim()))
            settings.OsmClientSecret = osmClientSecret.Trim();
        await _osmAuthService.SaveSettings(settings);

        TempData["StatusMessage"] = "OSM settings saved.";
        return RedirectToStore(returnStoreId);
    }

    [HttpGet("connect")]
    public async Task<IActionResult> ConnectOsm(string returnStoreId)
    {
        var settings = await _osmAuthService.GetSettings();
        if (string.IsNullOrEmpty(settings.OsmClientId) || string.IsNullOrEmpty(settings.OsmClientSecret))
        {
            TempData["StatusMessage"] = "Error: Please save OAuth Client ID and Secret first.";
            return RedirectToStore(returnStoreId);
        }

        var state = Guid.NewGuid().ToString("N");
        TempData["OAuthState"] = state;
        TempData["OAuthReturnStoreId"] = returnStoreId;

        var redirectUri = Url.Action(nameof(OAuthCallback), "UIBtcMapServer", null, Request.Scheme);
        var authUrl = _osmAuthService.GetAuthorizationUrl(settings, redirectUri, state);
        return Redirect(authUrl);
    }

    [HttpGet("oauth/callback")]
    public async Task<IActionResult> OAuthCallback(string code, string state)
    {
        var expectedState = TempData["OAuthState"]?.ToString();
        var returnStoreId = TempData["OAuthReturnStoreId"]?.ToString();

        if (string.IsNullOrEmpty(expectedState) || expectedState != state)
        {
            TempData["StatusMessage"] = "Error: Invalid OAuth state. Please try again.";
            return RedirectToStore(returnStoreId);
        }

        try
        {
            var settings = await _osmAuthService.GetSettings();
            var redirectUri = Url.Action(nameof(OAuthCallback), "UIBtcMapServer", null, Request.Scheme);
            var accessToken = await _osmAuthService.ExchangeCodeForToken(settings, code, redirectUri);
            var displayName = await _osmAuthService.GetDisplayName(settings, accessToken);

            settings.OsmAccessToken = accessToken;
            settings.OsmDisplayName = displayName;
            await _osmAuthService.SaveSettings(settings);

            TempData["StatusMessage"] = $"Successfully connected as {displayName}.";
        }
        catch (Exception ex)
        {
            TempData["StatusMessage"] = $"Error: OAuth failed — {ex.Message}";
        }

        return RedirectToStore(returnStoreId);
    }

    [HttpPost("disconnect")]
    public async Task<IActionResult> DisconnectOsm(string returnStoreId)
    {
        var settings = await _osmAuthService.GetSettings();
        settings.OsmAccessToken = null;
        settings.OsmDisplayName = null;
        await _osmAuthService.SaveSettings(settings);

        TempData["StatusMessage"] = "OSM account disconnected.";
        return RedirectToStore(returnStoreId);
    }

    private IActionResult RedirectToStore(string storeId)
    {
        if (!string.IsNullOrEmpty(storeId))
            return RedirectToAction("Index", "UIBtcMapStore", new { storeId });
        return RedirectToAction("ListStores", "UIStores");
    }
}
