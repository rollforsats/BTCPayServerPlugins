using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Plugins.BTCMap.Data;
using BTCPayServer.Plugins.BTCMap.Models;
using BTCPayServer.Plugins.BTCMap.Services;
using Microsoft.AspNetCore.Http;
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
        var service = BuildService(repo: repo);

        var result = await service.GetListingForStore("store-1");

        Assert.Same(expected, result);
        Assert.Equal("store-1", repo.RequestedStoreId);
    }

    [Theory]
    [InlineData("amenity=cafe", "cafe")]
    [InlineData("shop=jewelry", "jewelry")]
    [InlineData("tourism=hotel", "hotel")]
    [InlineData("craft=brewery", "brewery")]
    [InlineData("office=lawyer", "lawyer")]
    [InlineData("shop=yes", "shop")]
    [InlineData("office=yes", "office")]
    [InlineData("AMENITY=CAFE", "cafe")]
    [InlineData("cafe", "cafe")]
    [InlineData(null, null)]
    [InlineData("", null)]
    public void NormalizeCategory_StripsKey_And_HandlesYesFallback(string input, string expected)
    {
        Assert.Equal(expected, BtcMapService.NormalizeCategory(input));
    }

    [Fact]
    public async Task SubmitListing_HappyPath_PersistsBtcMapIdentifiers()
    {
        const string storeId = "store-1";
        var factory = TestDbFactory.Create();
        var api = new StubPluginBuilderApiClient
        {
            OnSubmit = req => Task.FromResult(new BtcMapSubmitResponse
            {
                BtcMap = new BtcMapBtcMapResult { Id = 42, Origin = "btcpayserver", ExternalId = req.ExternalId }
            })
        };
        var service = BuildService(factory: factory, api: api, host: "btcpay.example.com");

        var listing = await service.SubmitListing(storeId, NewSettings(),
            acceptsLightning: true, acceptsOnchain: false, submitToDirectory: false);

        Assert.Equal(ListingStatus.Active, listing.Status);
        Assert.Equal(42, listing.BtcMapSubmissionId);
        Assert.Equal("btcpay.example.com:store-1", listing.BtcMapExternalId);
        Assert.NotNull(listing.BtcMapSubmittedAt);
        Assert.Null(listing.BtcMapLastEditedAt);

        await using var verify = factory.CreateContext();
        var persisted = await verify.Listings.SingleAsync(l => l.StoreId == storeId);
        Assert.Equal(42, persisted.BtcMapSubmissionId);
        Assert.Equal("btcpay.example.com:store-1", persisted.BtcMapExternalId);
    }

    [Fact]
    public async Task SubmitListing_ApiThrows_PersistsErrorStatus_AndRethrows()
    {
        const string storeId = "store-1";
        var factory = TestDbFactory.Create();
        var api = new StubPluginBuilderApiClient
        {
            OnSubmit = _ => throw new PluginBuilderApiException(503, "btcmap-not-configured")
        };
        var service = BuildService(factory: factory, api: api);

        var ex = await Assert.ThrowsAsync<PluginBuilderApiException>(
            () => service.SubmitListing(storeId, NewSettings(),
                acceptsLightning: true, acceptsOnchain: false, submitToDirectory: false));
        Assert.Equal(503, ex.StatusCode);

        await using var verify = factory.CreateContext();
        var persisted = await verify.Listings.SingleAsync(l => l.StoreId == storeId);
        Assert.Equal(ListingStatus.Error, persisted.Status);
    }

    [Fact]
    public async Task SubmitListing_ResubmitWithSameStoreId_UpdatesExistingRow_AndBumpsLastEditedAt()
    {
        const string storeId = "store-1";
        var factory = TestDbFactory.Create();
        var api = new StubPluginBuilderApiClient
        {
            OnSubmit = req => Task.FromResult(new BtcMapSubmitResponse
            {
                BtcMap = new BtcMapBtcMapResult { Id = 42, Origin = "btcpayserver", ExternalId = req.ExternalId }
            })
        };
        var service = BuildService(factory: factory, api: api, host: "btcpay.example.com");

        var first = await service.SubmitListing(storeId, NewSettings(),
            acceptsLightning: false, acceptsOnchain: false, submitToDirectory: false);
        Assert.NotNull(first.BtcMapSubmittedAt);

        await Task.Delay(10);

        var settings = NewSettings();
        settings.BusinessName = "Updated Cafe";
        var second = await service.SubmitListing(storeId, settings,
            acceptsLightning: true, acceptsOnchain: true, submitToDirectory: false);

        Assert.Equal("Updated Cafe", second.BusinessName);
        Assert.True(second.AcceptsLightning);
        Assert.True(second.AcceptsOnchain);
        Assert.Equal(first.BtcMapSubmittedAt, second.BtcMapSubmittedAt);
        Assert.NotNull(second.BtcMapLastEditedAt);

        await using var verify = factory.CreateContext();
        var allRows = await verify.Listings.Where(l => l.StoreId == storeId).ToListAsync();
        Assert.Single(allRows);
    }

    [Fact]
    public async Task SubmitListing_HostLowercased_AndPortStripped()
    {
        const string storeId = "store-1";
        var factory = TestDbFactory.Create();
        var api = new StubPluginBuilderApiClient
        {
            OnSubmit = req => Task.FromResult(new BtcMapSubmitResponse
            {
                BtcMap = new BtcMapBtcMapResult { Id = 1, ExternalId = req.ExternalId }
            })
        };
        var service = BuildService(factory: factory, api: api, host: "BTCPay.Example.COM:443");

        var listing = await service.SubmitListing(storeId, NewSettings(),
            acceptsLightning: false, acceptsOnchain: false, submitToDirectory: false);
        Assert.Equal("btcpay.example.com:store-1", listing.BtcMapExternalId);
    }

    [Fact]
    public async Task SubmitListing_GlobalCountry_OmittedFromBtcMapRequest()
    {
        const string storeId = "store-1";
        var factory = TestDbFactory.Create();
        BtcMapSubmitRequest captured = null;
        var api = new StubPluginBuilderApiClient
        {
            OnSubmit = req => { captured = req; return Task.FromResult(new BtcMapSubmitResponse()); }
        };
        var service = BuildService(factory: factory, api: api);

        var settings = NewSettings();
        settings.Country = "GLOBAL";
        await service.SubmitListing(storeId, settings,
            acceptsLightning: false, acceptsOnchain: false, submitToDirectory: false);

        Assert.NotNull(captured);
        Assert.Null(captured.Country);
    }

    private static BtcMapStoreSettings NewSettings() => new()
    {
        BusinessName = "Bitcoin Cafe",
        Category = "amenity=cafe",
        Latitude = 32.6838298,
        Longitude = -117.1839771,
        Url = "https://example.test",
        SubmitToDirectory = false
    };

    private static BtcMapService BuildService(
        BtcMapDbContextFactory factory = null,
        IListingRepository repo = null,
        IPluginBuilderApiClient api = null,
        string host = "btcpay.example.com")
    {
        var http = new DefaultHttpContext();
        http.Request.Host = new HostString(host);
        var accessor = new HttpContextAccessor { HttpContext = http };

        return new BtcMapService(
            dbContextFactory: factory ?? TestDbFactory.Create(),
            listingRepository: repo ?? new StubListingRepository(),
            apiClient: api ?? new StubPluginBuilderApiClient(),
            overpassApiClient: new StubOverpassApiClient(),
            httpContextAccessor: accessor,
            logger: new NullLogger<BtcMapService>());
    }

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

    private class StubPluginBuilderApiClient : IPluginBuilderApiClient
    {
        public Func<BtcMapSubmitRequest, Task<BtcMapSubmitResponse>> OnSubmit { get; set; }

        public Task<BtcMapSubmitResponse> SubmitAsync(BtcMapSubmitRequest request)
            => OnSubmit != null ? OnSubmit(request) : Task.FromResult(new BtcMapSubmitResponse());

        public Task<bool> PingAsync() => Task.FromResult(true);
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

internal static class TestDbFactory
{
    public static BtcMapDbContextFactory Create()
        => new InMemoryFactory(Guid.NewGuid().ToString("N"));

    private sealed class InMemoryFactory : BtcMapDbContextFactory
    {
        private readonly string _dbName;

        public InMemoryFactory(string dbName)
            : base(Options.Create(new DatabaseOptions { ConnectionString = "Host=ignored;Database=ignored" }))
        {
            _dbName = dbName;
        }

        public override BtcMapDbContext CreateContext(Action<NpgsqlDbContextOptionsBuilder> npgsqlOptionsAction = null)
        {
            var builder = new DbContextOptionsBuilder<BtcMapDbContext>();
            builder.UseInMemoryDatabase(_dbName);
            return new BtcMapDbContext(builder.Options);
        }
    }
}
