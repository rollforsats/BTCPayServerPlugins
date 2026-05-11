using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.BTCMap.Controllers;
using BTCPayServer.Plugins.BTCMap.Services;
using BTCPayServer.Plugins.BTCMap.Services.Osm;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BTCPayServer.Plugins.BTCMap.Tests;

public class UIBtcMapStoreControllerTests
{
    private const string TestStoreId = "store-1";

    [Fact]
    public async Task DisconnectOsm_RevokeThrows_StillClearsLocalState()
    {
        // Pins the revoke best-effort contract: Disconnect must complete locally
        // regardless of the OSM round-trip. Stubbing RevokeAsync to throw simulates
        // a future refactor that violates the "swallowed internally" guarantee in
        // OsmAuthService.RevokeAsync — the controller-side test guards independently.
        var repo = new StubOAuthRepo
        {
            ToReturn = new BtcMapStoreOAuthDecrypted
            {
                OsmClientId = "client-id",
                OsmClientSecret = "secret",
                OsmAccessToken = "tok"
            }
        };
        var auth = new StubAuthService { RevokeThrows = new InvalidOperationException("simulated leak") };
        var controller = MakeController(repo, auth);

        var result = await controller.DisconnectOsm(TestStoreId);

        Assert.True(repo.ClearOAuthCalled,
            "ClearOAuthAsync must run even when RevokeAsync throws — Disconnect is local-source-of-truth.");
        AssertRedirectsToIndex(result);
    }

    [Theory]
    [InlineData("", "https://host.test/plugins/btcmap/stores/store-1/oauth/callback")]
    [InlineData("/", "https://host.test/plugins/btcmap/stores/store-1/oauth/callback")]
    [InlineData("/btcpay", "https://host.test/btcpay/plugins/btcmap/stores/store-1/oauth/callback")]
    [InlineData("/btcpay/", "https://host.test/btcpay/plugins/btcmap/stores/store-1/oauth/callback")]
    public void BuildOsmCallbackUri_NormalizesPathBaseVariants(string pathBase, string expected)
    {
        // OSM validates redirect_uri byte-for-byte against the URI the merchant
        // registered in their OSM app form. Any drift here vs. the value the UI
        // surfaces to the merchant produces a redirect_uri_mismatch with no useful
        // diagnostic. Lock the canonical form for every realistic PathBase shape.
        var actual = UIBtcMapStoreController.BuildOsmCallbackUri(
            scheme: "https", host: "host.test", pathBase: pathBase, storeId: "store-1");
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task ConnectOsm_RepeatedInvocations_RegenerateDistinctStates()
    {
        // Pins the CSRF-replay guard: every Connect press writes a fresh random state.
        // A future refactor that adds "skip if pending state already exists" would
        // silently reintroduce a replay window — this test catches that.
        var repo = new StubOAuthRepo
        {
            ToReturn = new BtcMapStoreOAuthDecrypted
            {
                OsmClientId = "client-id",
                OsmClientSecret = "secret"
            }
        };
        var controller = MakeController(repo, new StubAuthService());

        await controller.ConnectOsm(TestStoreId);
        var first = repo.LastPendingState;
        await controller.ConnectOsm(TestStoreId);
        var second = repo.LastPendingState;

        Assert.False(string.IsNullOrEmpty(first));
        Assert.False(string.IsNullOrEmpty(second));
        Assert.NotEqual(first, second);
        Assert.True(repo.SetPendingStateCallCount >= 2);
    }

    private static UIBtcMapStoreController MakeController(StubOAuthRepo repo, StubAuthService auth)
    {
        var http = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("host.test");
        var controller = new UIBtcMapStoreController(
            btcMapService: null,
            nominatimApiClient: null,
            directoryListingChecker: null,
            oauthRepo: repo,
            osmAuthService: auth,
            networkProvider: null,
            logger: new NullLogger<UIBtcMapStoreController>())
        {
            Url = new StubUrlHelper(),
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, new InMemoryTempDataProvider())
        };
        return controller;
    }

    private class StubUrlHelper : Microsoft.AspNetCore.Mvc.IUrlHelper
    {
        public ActionContext ActionContext { get; } = new();
        public string Action(Microsoft.AspNetCore.Mvc.Routing.UrlActionContext actionContext)
            => $"/{actionContext.Controller}/{actionContext.Action}";
        public string Content(string contentPath) => contentPath;
        public bool IsLocalUrl(string url) => true;
        public string Link(string routeName, object values) => "http://stub";
        public string RouteUrl(Microsoft.AspNetCore.Mvc.Routing.UrlRouteContext routeContext) => "http://stub";
    }

    private static void AssertRedirectsToIndex(IActionResult result)
    {
        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Index", redirect.ActionName);
    }

    private class StubOAuthRepo : IBtcMapStoreOAuthRepository
    {
        public BtcMapStoreOAuthDecrypted ToReturn { get; set; }
        public bool ClearOAuthCalled { get; private set; }
        public string LastPendingState { get; private set; }
        public int SetPendingStateCallCount { get; private set; }

        public Task<BtcMapStoreOAuthDecrypted> GetForStoreAsync(string storeId) => Task.FromResult(ToReturn);
        public Task UpsertAsync(string storeId, BtcMapStoreOAuthDecrypted state) => Task.CompletedTask;
        public Task SaveClientCredentialsAsync(string storeId, string clientId, string clientSecret) => Task.CompletedTask;
        public Task SaveAccessTokenAsync(string storeId, string accessToken, string username) => Task.CompletedTask;

        public Task ClearOAuthAsync(string storeId)
        {
            ClearOAuthCalled = true;
            return Task.CompletedTask;
        }

        public Task ClearTokenOnlyAsync(string storeId) => Task.CompletedTask;

        public Task SetPendingStateAsync(string storeId, string state, DateTimeOffset expiresAt)
        {
            LastPendingState = state;
            SetPendingStateCallCount++;
            return Task.CompletedTask;
        }

        public Task ClearPendingStateAsync(string storeId) => Task.CompletedTask;
    }

    private class StubAuthService : IOsmAuthService
    {
        public Exception RevokeThrows { get; set; }

        public string GetAuthorizationUrl(string clientId, string redirectUri, string state) => "http://stub/authorize";

        public Task<string> ExchangeCodeForTokenAsync(string clientId, string clientSecret, string code,
            string redirectUri, CancellationToken ct)
            => Task.FromResult("tok");

        public Task<string> GetDisplayNameAsync(string accessToken, CancellationToken ct) => Task.FromResult("alice");

        public Task RevokeAsync(string clientId, string clientSecret, string accessToken, CancellationToken ct)
        {
            if (RevokeThrows != null) throw RevokeThrows;
            return Task.CompletedTask;
        }
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
