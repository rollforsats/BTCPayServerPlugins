using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Plugins.BTCMap.Data;
using BTCPayServer.Plugins.BTCMap.Models;
using BTCPayServer.Plugins.BTCMap.Services;
using BTCPayServer.Plugins.BTCMap.Services.Osm;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using Xunit;

namespace BTCPayServer.Plugins.BTCMap.Tests;

public class BtcMapServiceTests
{
    [Fact]
    public async Task GetListingForStore_DelegatesToRepository()
    {
        var expected = new BtcMapListing { Id = "abc", StoreId = "store-1", Status = ListingStatus.Active };
        var repo = new StubListingRepository { ToReturn = expected };
        var service = new BtcMapService(
            dbContextFactory: null,
            listingRepository: repo,
            apiClient: null,
            overpassApiClient: null,
            osmApiClient: null,
            oauthRepo: null,
            logger: new NullLogger<BtcMapService>());

        var result = await service.GetListingForStore("store-1");

        Assert.Same(expected, result);
        Assert.Equal("store-1", repo.RequestedStoreId);
    }

    [Fact]
    public async Task SubmitListing_NewNode_OsmSucceeds_DirectoryThrows_PersistsListingAndRethrows()
    {
        // Two contracts at once:
        //   1. OSM identifiers MUST persist before re-throw — a retry must target the
        //      existing node, not create a duplicate at the same coordinates.
        //   2. PluginBuilderApiException MUST propagate so the controller's existing
        //      catch block renders the error to the merchant. Swallowing here was
        //      the 2026-05-11 silent-failure bug.
        const string storeId = "store-blocker-create";
        var factory = TestDbFactory.Create();
        var osm = new StubOsmApiClient
        {
            OnCreate = (sid, _, _) => Task.FromResult(new OsmCreateResult { NodeId = 9001, ChangesetId = 1, Version = 1 })
        };
        var api = new StubPluginBuilderApiClient
        {
            OnSubmit = _ => throw new PluginBuilderApiException(502, "directory upstream 502")
        };
        var oauthRepo = new ConnectedOAuthRepo(storeId);
        var service = new BtcMapService(factory, new StubListingRepository(),
            api, new StubOverpassApiClient(), osm, oauthRepo, new NullLogger<BtcMapService>());

        var settings = NewSettingsWithDirectory();
        var ex = await Assert.ThrowsAsync<PluginBuilderApiException>(
            () => service.SubmitListing(storeId, settings,
                acceptsLightning: true, submitToDirectory: true, osmType: null, osmId: null));
        Assert.Equal(502, ex.StatusCode);

        await using var verify = factory.CreateContext();
        var persisted = await verify.Listings.SingleAsync(l => l.StoreId == storeId);
        Assert.Equal(9001, persisted.OsmElementId);
        Assert.Equal("node", persisted.OsmElementType);
        Assert.Equal(ListingStatus.Active, persisted.Status);
        Assert.Null(persisted.DirectorySubmittedAt);
        Assert.Null(persisted.DirectoryPrUrl);
    }

    [Fact]
    public async Task SubmitListing_LinkExisting_OsmSucceeds_DirectoryThrows_PersistsListingAndRethrows()
    {
        // Sibling for the link-existing arm of DispatchOsmSubmitAsync. Same contract:
        // OSM identifiers persisted before re-throw, and the exception propagates.
        const string storeId = "store-blocker-link";
        var factory = TestDbFactory.Create();
        var osm = new StubOsmApiClient
        {
            OnUpdate = (_, nodeId, _, m, _) => Task.FromResult(new OsmUpdateResult
            {
                NewVersion = 7,
                ResolvedName = m.Name
            })
        };
        var api = new StubPluginBuilderApiClient
        {
            OnSubmit = _ => throw new PluginBuilderApiException(502, "directory upstream 502")
        };
        var oauthRepo = new ConnectedOAuthRepo(storeId);
        var service = new BtcMapService(factory, new StubListingRepository(),
            api, new StubOverpassApiClient(), osm, oauthRepo, new NullLogger<BtcMapService>());

        var settings = NewSettingsWithDirectory();
        await Assert.ThrowsAsync<PluginBuilderApiException>(
            () => service.SubmitListing(storeId, settings,
                acceptsLightning: false, submitToDirectory: true, osmType: "node", osmId: 5000));

        await using var verify = factory.CreateContext();
        var persisted = await verify.Listings.SingleAsync(l => l.StoreId == storeId);
        Assert.Equal(5000, persisted.OsmElementId);
        Assert.Equal("node", persisted.OsmElementType);
        Assert.Equal(7, persisted.OsmElementVersion);
        Assert.Equal(ListingStatus.Active, persisted.Status);
        Assert.Null(persisted.DirectorySubmittedAt);
        Assert.Null(persisted.DirectoryPrUrl);
    }

    [Fact]
    public async Task SubmitListing_DirectoryThrows_SaveAlsoThrows_OriginalExceptionWins()
    {
        // Pins the round-3 fix: when the directory leg throws AND the catch-block
        // SaveChangesAsync also fails (DB blip, concurrency, EF validation), the
        // original PluginBuilderApiException MUST still propagate so the controller's
        // typed catch renders the error. Without the inner try/catch, the EF
        // exception would replace the in-flight one on the call stack.
        const string storeId = "store-blocker-save-fails";
        var factory = TestDbFactory.Create(onSave: () => throw new DbUpdateException("simulated EF failure"));
        var osm = new StubOsmApiClient
        {
            OnCreate = (sid, _, _) => Task.FromResult(new OsmCreateResult { NodeId = 9002, ChangesetId = 1, Version = 1 })
        };
        var api = new StubPluginBuilderApiClient
        {
            OnSubmit = _ => throw new PluginBuilderApiException(502, "directory upstream 502")
        };
        var oauthRepo = new ConnectedOAuthRepo(storeId);
        var service = new BtcMapService(factory, new StubListingRepository(),
            api, new StubOverpassApiClient(), osm, oauthRepo, new NullLogger<BtcMapService>());

        var settings = NewSettingsWithDirectory();
        var ex = await Assert.ThrowsAsync<PluginBuilderApiException>(
            () => service.SubmitListing(storeId, settings,
                acceptsLightning: true, submitToDirectory: true, osmType: null, osmId: null));
        Assert.Equal(502, ex.StatusCode);
        Assert.Equal("directory upstream 502", ex.Message);
    }

    private static BtcMapStoreSettings NewSettingsWithDirectory() => new()
    {
        BusinessName = "Bitcoin Cafe",
        Category = "cafe",
        Latitude = 32.6838298,
        Longitude = -117.1839771,
        Url = "https://example.test",
        DirectoryDescription = "A cafe that takes bitcoin",
        DirectoryType = "merchants",
        DirectorySubType = "food-drink",
        SubmitToDirectory = true
    };

    private class StubListingRepository : IListingRepository
    {
        public BtcMapListing ToReturn { get; set; }
        public string RequestedStoreId { get; private set; }

        public Task<BtcMapListing> GetListingForStoreAsync(string storeId)
        {
            RequestedStoreId = storeId;
            return Task.FromResult(ToReturn);
        }
    }

    private class ConnectedOAuthRepo : IBtcMapStoreOAuthRepository
    {
        private readonly string _connectedStoreId;
        public ConnectedOAuthRepo(string connectedStoreId) { _connectedStoreId = connectedStoreId; }

        public Task<BtcMapStoreOAuthDecrypted> GetForStoreAsync(string storeId)
            => Task.FromResult(storeId == _connectedStoreId
                ? new BtcMapStoreOAuthDecrypted { OsmAccessToken = "tok-test", OsmUsername = "tester" }
                : null);

        public Task UpsertAsync(string storeId, BtcMapStoreOAuthDecrypted state) => Task.CompletedTask;
        public Task SaveClientCredentialsAsync(string storeId, string clientId, string clientSecret) => Task.CompletedTask;
        public Task SaveAccessTokenAsync(string storeId, string accessToken, string username) => Task.CompletedTask;
        public Task ClearOAuthAsync(string storeId) => Task.CompletedTask;
        public Task ClearTokenOnlyAsync(string storeId) => Task.CompletedTask;
        public Task SetPendingStateAsync(string storeId, string state, DateTimeOffset expiresAt) => Task.CompletedTask;
        public Task ClearPendingStateAsync(string storeId) => Task.CompletedTask;
    }

    private class StubOsmApiClient : IOsmApiClient
    {
        public Func<string, BtcMapMerchant, CancellationToken, Task<OsmCreateResult>> OnCreate { get; set; }
        public Func<string, long, string, BtcMapMerchant, CancellationToken, Task<OsmUpdateResult>> OnUpdate { get; set; }

        public Task<OsmCreateResult> CreateNodeAsync(string storeId, BtcMapMerchant merchant, CancellationToken ct)
            => OnCreate != null ? OnCreate(storeId, merchant, ct) : throw new InvalidOperationException("OnCreate not stubbed");

        public Task<OsmUpdateResult> UpdateNodeAsync(string storeId, long nodeId, string nodeType, BtcMapMerchant merchant, CancellationToken ct)
            => OnUpdate != null ? OnUpdate(storeId, nodeId, nodeType, merchant, ct) : throw new InvalidOperationException("OnUpdate not stubbed");

        public Task<int> ReverifyNodeAsync(string storeId, long nodeId, string nodeType, BtcMapMerchant merchant, CancellationToken ct)
            => throw new InvalidOperationException("ReverifyNodeAsync not used in these tests");

        public Task<OsmUnlistResult> UnlistNodeAsync(string storeId, long nodeId, string nodeType, string merchantName, CancellationToken ct)
            => throw new InvalidOperationException("UnlistNodeAsync not used in these tests");
    }

    private class StubPluginBuilderApiClient : IPluginBuilderApiClient
    {
        public Func<BtcMapSubmitRequest, Task<BtcMapSubmitResponse>> OnSubmit { get; set; }

        public Task<BtcMapSubmitResponse> SubmitAsync(BtcMapSubmitRequest request)
            => OnSubmit != null ? OnSubmit(request) : Task.FromResult(new BtcMapSubmitResponse());
    }

    private class StubOverpassApiClient : IOverpassApiClient
    {
        public Task<List<OverpassElement>> SearchNearby(double lat, double lon, int radius, string name)
            => Task.FromResult(new List<OverpassElement>());

        public Task<List<OverpassElement>> SearchByAddress(double lat, double lon, int radius, string street, string city)
            => Task.FromResult(new List<OverpassElement>());

        public Task<List<OverpassElement>> SearchByCoordinates(double lat, double lon, int radius)
            => Task.FromResult(new List<OverpassElement>());

        public Task<List<OverpassElement>> CheckExistingBitcoinTags(double lat, double lon)
            => Task.FromResult(new List<OverpassElement>());
    }
}

/// <summary>
/// In-memory BtcMapDbContextFactory for unit tests. Each Create() returns a factory
/// scoped to a unique database name so parallel tests don't share state. The optional
/// onSave hook fires before each SaveChangesAsync — pass a throwing func to simulate
/// EF failures (e.g., concurrency conflicts, DbUpdateException).
/// </summary>
internal static class TestDbFactory
{
    public static BtcMapDbContextFactory Create(Func<Task> onSave = null)
        => new InMemoryFactory(Guid.NewGuid().ToString("N"), onSave);

    private sealed class InMemoryFactory : BtcMapDbContextFactory
    {
        private readonly string _dbName;
        private readonly Func<Task> _onSave;

        public InMemoryFactory(string dbName, Func<Task> onSave)
            : base(Options.Create(new DatabaseOptions { ConnectionString = "Host=ignored;Database=ignored" }))
        {
            _dbName = dbName;
            _onSave = onSave;
        }

        public override BtcMapDbContext CreateContext(Action<NpgsqlDbContextOptionsBuilder> npgsqlOptionsAction = null)
        {
            var builder = new DbContextOptionsBuilder<BtcMapDbContext>();
            builder.UseInMemoryDatabase(_dbName);
            return _onSave == null
                ? new BtcMapDbContext(builder.Options)
                : new HookedDbContext(builder.Options, _onSave);
        }
    }

    private sealed class HookedDbContext : BtcMapDbContext
    {
        private readonly Func<Task> _onSave;

        public HookedDbContext(DbContextOptions<BtcMapDbContext> options, Func<Task> onSave)
            : base(options)
        {
            _onSave = onSave;
        }

        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            await _onSave();
            return await base.SaveChangesAsync(cancellationToken);
        }
    }
}
