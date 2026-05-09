using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.BTCMap.Controllers;
using BTCPayServer.Plugins.BTCMap.Services;
using BTCPayServer.Plugins.BTCMap.Services.Osm;
using BTCPayServer.Plugins.BTCMap.Services.Osm.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace BTCPayServer.Plugins.BTCMap.Tests;

public class UIBtcMapOAuthControllerTests
{
    private const string TestStoreId = "store-1";

    [Fact]
    public async Task Callback_NoOAuthRow_RedirectsWithError()
    {
        var (controller, repo, auth) = MakeController(oauthRow: null);

        var result = await controller.Callback(TestStoreId, "code", "state", null, null);

        AssertRedirectsToStorePage(result);
        Assert.Equal("Error: OAuth state not found for this store.", controller.TempData["StatusMessage"]);
        Assert.False(auth.ExchangeCalled);
    }

    [Fact]
    public async Task Callback_OAuthErrorParam_SurfacesAndClearsPending()
    {
        var oauth = ValidPending();
        var (controller, repo, auth) = MakeController(oauth);

        var result = await controller.Callback(TestStoreId, null, null, "access_denied", "User denied");

        AssertRedirectsToStorePage(result);
        Assert.Equal(nameof(Models.OsmConnectionErrorKind.AccessDenied), controller.TempData["OsmErrorKind"]);
        Assert.True(repo.PendingCleared);
        Assert.False(auth.ExchangeCalled);
    }

    [Fact]
    public async Task Callback_StateMismatch_RejectsAndClearsPending()
    {
        var oauth = ValidPending();
        var (controller, repo, auth) = MakeController(oauth);

        var result = await controller.Callback(TestStoreId, "code", "wrong-state", null, null);

        AssertRedirectsToStorePage(result);
        Assert.Equal(nameof(Models.OsmConnectionErrorKind.Other), controller.TempData["OsmErrorKind"]);
        Assert.True(repo.PendingCleared);
        Assert.False(auth.ExchangeCalled);
    }

    [Fact]
    public async Task Callback_ExpiredPendingState_FlagsExpired()
    {
        var oauth = ValidPending(expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        var (controller, repo, auth) = MakeController(oauth);

        var result = await controller.Callback(TestStoreId, "code", oauth.PendingState, null, null);

        AssertRedirectsToStorePage(result);
        Assert.Equal(nameof(Models.OsmConnectionState.PendingExpired), controller.TempData["OsmConnectionState"]);
        Assert.True(repo.PendingCleared);
        Assert.False(auth.ExchangeCalled);
    }

    [Fact]
    public async Task Callback_HappyPath_ExchangesAndSavesToken()
    {
        var oauth = ValidPending();
        var (controller, repo, auth) = MakeController(oauth);
        auth.ExchangeReturnsToken = "access-token-123";
        auth.DisplayNameReturns = "alice";

        var result = await controller.Callback(TestStoreId, "auth-code", oauth.PendingState, null, null);

        AssertRedirectsToStorePage(result);
        Assert.True(auth.ExchangeCalled);
        Assert.Equal("access-token-123", repo.SavedToken);
        Assert.Equal("alice", repo.SavedUsername);
        Assert.Equal("Connected to OpenStreetMap as alice.", controller.TempData["StatusMessage"]);
    }

    [Fact]
    public async Task Callback_TokenExchangeError_SurfacesErrorKind()
    {
        var oauth = ValidPending();
        var (controller, repo, auth) = MakeController(oauth);
        auth.ExchangeThrows = new OsmTokenExchangeException(401, "invalid_client",
            "Bad client credentials", "{\"error\":\"invalid_client\"}");

        var result = await controller.Callback(TestStoreId, "auth-code", oauth.PendingState, null, null);

        AssertRedirectsToStorePage(result);
        Assert.Equal(nameof(Models.OsmConnectionErrorKind.InvalidClient), controller.TempData["OsmErrorKind"]);
        Assert.Null(repo.SavedToken);
        Assert.True(repo.PendingCleared);
    }

    private static BtcMapStoreOAuthDecrypted ValidPending(DateTimeOffset? expiresAt = null) => new()
    {
        OsmClientId = "client-id",
        OsmClientSecret = "client-secret",
        PendingState = "expected-state-123",
        PendingStateExpiresAt = expiresAt ?? DateTimeOffset.UtcNow.AddMinutes(5)
    };

    private static (UIBtcMapOAuthController controller, StubOAuthRepo repo, StubAuthService auth) MakeController(
        BtcMapStoreOAuthDecrypted oauthRow)
    {
        var repo = new StubOAuthRepo { ToReturn = oauthRow };
        var auth = new StubAuthService();
        var http = MakeHttpContext();
        var controller = new UIBtcMapOAuthController(auth, repo, new NullLogger<UIBtcMapOAuthController>())
        {
            Url = new StubUrlHelper(),
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, new InMemoryTempDataProvider())
        };
        return (controller, repo, auth);
    }

    private static HttpContext MakeHttpContext()
    {
        var http = new DefaultHttpContext();
        var services = new ServiceCollection().BuildServiceProvider();
        http.RequestServices = services;
        http.Request.Scheme = "http";
        http.Request.Host = new HostString("localhost", 14142);
        return http;
    }

    private class StubUrlHelper : IUrlHelper
    {
        public ActionContext ActionContext { get; } = new();
        public string Action(UrlActionContext actionContext) =>
            $"/{actionContext.Controller}/{actionContext.Action}";
        public string Content(string contentPath) => contentPath;
        public bool IsLocalUrl(string url) => true;
        public string Link(string routeName, object values) => "http://stub";
        public string RouteUrl(UrlRouteContext routeContext) => "http://stub";
    }

    private static void AssertRedirectsToStorePage(IActionResult result)
    {
        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
        Assert.Equal("UIBtcMapStore", redirect.ControllerName);
    }

    private class StubOAuthRepo : IBtcMapStoreOAuthRepository
    {
        public BtcMapStoreOAuthDecrypted ToReturn { get; set; }
        public bool PendingCleared { get; private set; }
        public string SavedToken { get; private set; }
        public string SavedUsername { get; private set; }

        public Task<BtcMapStoreOAuthDecrypted> GetForStoreAsync(string storeId) => Task.FromResult(ToReturn);
        public Task UpsertAsync(string storeId, BtcMapStoreOAuthDecrypted state) => Task.CompletedTask;
        public Task SaveClientCredentialsAsync(string storeId, string clientId, string clientSecret) => Task.CompletedTask;

        public Task SaveAccessTokenAsync(string storeId, string accessToken, string username)
        {
            SavedToken = accessToken;
            SavedUsername = username;
            return Task.CompletedTask;
        }

        public Task ClearOAuthAsync(string storeId) => Task.CompletedTask;
        public Task ClearTokenOnlyAsync(string storeId) => Task.CompletedTask;
        public Task SetPendingStateAsync(string storeId, string state, DateTimeOffset expiresAt) => Task.CompletedTask;

        public Task ClearPendingStateAsync(string storeId)
        {
            PendingCleared = true;
            return Task.CompletedTask;
        }
    }

    private class StubAuthService : IOsmAuthService
    {
        public bool ExchangeCalled { get; private set; }
        public string ExchangeReturnsToken { get; set; }
        public string DisplayNameReturns { get; set; }
        public Exception ExchangeThrows { get; set; }

        public string GetAuthorizationUrl(string clientId, string redirectUri, string state) => "http://stub";

        public Task<string> ExchangeCodeForTokenAsync(string clientId, string clientSecret, string code,
            string redirectUri, CancellationToken ct)
        {
            ExchangeCalled = true;
            if (ExchangeThrows != null) throw ExchangeThrows;
            return Task.FromResult(ExchangeReturnsToken);
        }

        public Task<string> GetDisplayNameAsync(string accessToken, CancellationToken ct)
            => Task.FromResult(DisplayNameReturns);

        public Task RevokeAsync(string clientId, string clientSecret, string accessToken, CancellationToken ct)
            => Task.CompletedTask;
    }

    private class InMemoryTempDataProvider : ITempDataProvider
    {
        private readonly Dictionary<string, object> _store = new();
        public IDictionary<string, object> LoadTempData(HttpContext context) => _store;
        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
            _store.Clear();
            foreach (var kv in values) _store[kv.Key] = kv.Value;
        }
    }
}
