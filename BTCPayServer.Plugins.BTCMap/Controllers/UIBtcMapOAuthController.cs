using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Plugins.BTCMap.Services;
using BTCPayServer.Plugins.BTCMap.Services.Osm;
using BTCPayServer.Plugins.BTCMap.Services.Osm.Exceptions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.BTCMap.Controllers;

/// <summary>
/// OSM OAuth callback. Hangs off a per-store route — that exact URL is what the
/// merchant pastes into their OSM app's Redirect URI field.
/// </summary>
[Route("~/plugins/btcmap/stores/{storeId}/oauth")]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyStoreSettings)]
public class UIBtcMapOAuthController : Controller
{
    private readonly IOsmAuthService _authService;
    private readonly IBtcMapStoreOAuthRepository _oauthRepo;
    private readonly ILogger<UIBtcMapOAuthController> _logger;

    public UIBtcMapOAuthController(
        IOsmAuthService authService,
        IBtcMapStoreOAuthRepository oauthRepo,
        ILogger<UIBtcMapOAuthController> logger)
    {
        _authService = authService;
        _oauthRepo = oauthRepo;
        _logger = logger;
    }

    [HttpGet("callback")]
    public async Task<IActionResult> Callback(string storeId, string code, string state, string error, string error_description)
    {
        var oauth = await _oauthRepo.GetForStoreAsync(storeId);
        if (oauth == null)
        {
            TempData["StatusMessage"] = "Error: OAuth state not found for this store.";
            return RedirectToStorePage(storeId);
        }

        // OSM redirected back with an OAuth error (?error=...). Surface it.
        if (!string.IsNullOrEmpty(error))
        {
            await _oauthRepo.ClearPendingStateAsync(storeId);
            TempData["OsmErrorKind"] = OsmConnectionErrorKindFromError(error).ToString();
            TempData["OsmErrorMessage"] = string.IsNullOrEmpty(error_description) ? error : error_description;
            _logger.LogInformation("OSM OAuth callback for store {StoreId}: error={Error} description={Description}",
                storeId, error, error_description);
            return RedirectToStorePage(storeId);
        }

        // Validate state with constant-time compare; reject on mismatch or expiry.
        if (string.IsNullOrEmpty(state) || string.IsNullOrEmpty(oauth.PendingState))
        {
            await _oauthRepo.ClearPendingStateAsync(storeId);
            TempData["OsmErrorKind"] = nameof(Models.OsmConnectionErrorKind.Other);
            TempData["OsmErrorMessage"] = "Missing OAuth state. Please click Connect again.";
            return RedirectToStorePage(storeId);
        }

        var storedBytes = Encoding.UTF8.GetBytes(oauth.PendingState ?? string.Empty);
        var suppliedBytes = Encoding.UTF8.GetBytes(state ?? string.Empty);
        if (storedBytes.Length != suppliedBytes.Length ||
            !CryptographicOperations.FixedTimeEquals(storedBytes, suppliedBytes))
        {
            await _oauthRepo.ClearPendingStateAsync(storeId);
            TempData["OsmErrorKind"] = nameof(Models.OsmConnectionErrorKind.Other);
            TempData["OsmErrorMessage"] = "OAuth state mismatch. Please click Connect again.";
            _logger.LogWarning("OSM OAuth callback state mismatch for store {StoreId}", storeId);
            return RedirectToStorePage(storeId);
        }

        if (!oauth.PendingStateExpiresAt.HasValue || oauth.PendingStateExpiresAt.Value <= DateTimeOffset.UtcNow)
        {
            await _oauthRepo.ClearPendingStateAsync(storeId);
            TempData["OsmConnectionState"] = nameof(Models.OsmConnectionState.PendingExpired);
            return RedirectToStorePage(storeId);
        }

        if (string.IsNullOrEmpty(code))
        {
            await _oauthRepo.ClearPendingStateAsync(storeId);
            TempData["OsmErrorKind"] = nameof(Models.OsmConnectionErrorKind.Other);
            TempData["OsmErrorMessage"] = "OSM did not return an authorization code.";
            return RedirectToStorePage(storeId);
        }

        if (string.IsNullOrEmpty(oauth.OsmClientId) || string.IsNullOrEmpty(oauth.OsmClientSecret))
        {
            await _oauthRepo.ClearPendingStateAsync(storeId);
            TempData["OsmErrorKind"] = nameof(Models.OsmConnectionErrorKind.Other);
            TempData["OsmErrorMessage"] = "OSM client credentials are missing. Save them and try again.";
            return RedirectToStorePage(storeId);
        }

        var redirectUri = BuildCallbackUri(storeId);

        try
        {
            var token = await _authService.ExchangeCodeForTokenAsync(
                oauth.OsmClientId, oauth.OsmClientSecret, code, redirectUri, HttpContext.RequestAborted);
            var displayName = await _authService.GetDisplayNameAsync(token, HttpContext.RequestAborted);
            await _oauthRepo.SaveAccessTokenAsync(storeId, token, displayName);
            TempData["StatusMessage"] = $"Connected to OpenStreetMap as {displayName}.";
        }
        catch (OsmTokenExchangeException ex)
        {
            await _oauthRepo.ClearPendingStateAsync(storeId);
            TempData["OsmErrorKind"] = OsmConnectionErrorKindFromError(ex.ErrorCode).ToString();
            TempData["OsmErrorMessage"] = string.IsNullOrEmpty(ex.ErrorDescription) ? ex.ErrorCode : ex.ErrorDescription;
            _logger.LogInformation("OSM token exchange failed for store {StoreId}: status={Status} error={Error}",
                storeId, ex.StatusCode, ex.ErrorCode);
        }
        catch (OsmAuthException ex)
        {
            await _oauthRepo.ClearPendingStateAsync(storeId);
            TempData["OsmErrorKind"] = nameof(Models.OsmConnectionErrorKind.InvalidClient);
            TempData["OsmErrorMessage"] = ex.Message;
        }

        return RedirectToStorePage(storeId);
    }

    private string BuildCallbackUri(string storeId)
    {
        var root = Request.Scheme + "://" + Request.Host.ToUriComponent() + Request.PathBase;
        return $"{root.TrimEnd('/')}/plugins/btcmap/stores/{storeId}/oauth/callback";
    }

    private IActionResult RedirectToStorePage(string storeId)
        => RedirectToAction("Index", "UIBtcMapStore", new { storeId });

    private static Models.OsmConnectionErrorKind OsmConnectionErrorKindFromError(string error)
        => error switch
        {
            "redirect_uri_mismatch" => Models.OsmConnectionErrorKind.RedirectUriMismatch,
            "invalid_client" => Models.OsmConnectionErrorKind.InvalidClient,
            "unauthorized_client" => Models.OsmConnectionErrorKind.PublicAppNotConfidential,
            "access_denied" => Models.OsmConnectionErrorKind.AccessDenied,
            _ => Models.OsmConnectionErrorKind.Other
        };
}
