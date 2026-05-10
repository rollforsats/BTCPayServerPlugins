using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.BTCMap.Services;
using BTCPayServer.Plugins.BTCMap.Services.Osm;
using BTCPayServer.Plugins.BTCMap.Services.Osm.Exceptions;
using Xunit;

namespace BTCPayServer.Plugins.BTCMap.Tests;

public class OsmApiClientTests
{
    private const string TestStoreId = "store-test";
    private const string TestToken = "test-access-token";

    [Fact]
    public async Task CreateNode_OpensChangeset_PutsNode_ClosesChangeset()
    {
        var http = new StubHttp();
        http.PutResponses["node/create"] = "9876";
        var changesets = new StubChangesetManager(opensWithId: 1234);

        var client = MakeClient(http, changesets, withToken: TestToken);

        var result = await client.CreateNodeAsync(TestStoreId,
            new BtcMapMerchant { Name = "Bitcoin Cafe", Latitude = 32.6838298, Longitude = -117.1839771 },
            CancellationToken.None);

        Assert.Equal(1234, result.ChangesetId);
        Assert.Equal(9876, result.NodeId);
        Assert.Equal(1, result.Version);
        Assert.True(changesets.Opened);
        Assert.True(changesets.Closed);
        Assert.Contains("node/create", http.PutCalls);
    }

    [Fact]
    public async Task UpdateNode_VersionConflict_RetriesWithSameChangeset()
    {
        var http = new StubHttp();
        http.GetResponses["node/100"] = NodeXml(100, 5, ("currency:XBT", "yes"));
        // First PUT 409, second PUT succeeds with newVersion=6.
        http.PutSequence["node/100"] = new Queue<Func<string>>(new Func<string>[]
        {
            () => throw new OsmConflictException("PUT node/100", "version conflict"),
            () => "6"
        });
        var changesets = new StubChangesetManager(opensWithId: 999);

        var client = MakeClient(http, changesets, withToken: TestToken);

        var result = await client.UpdateNodeAsync(TestStoreId, 100, "node",
            new BtcMapMerchant { Name = "Bitcoin Cafe" }, CancellationToken.None);

        Assert.Equal(6, result.NewVersion);
        Assert.Equal("Bitcoin Cafe", result.ResolvedName);
        Assert.Equal(1, changesets.OpenCallCount);
        Assert.True(changesets.Closed);
        // Two PUT attempts within the single changeset.
        Assert.Equal(2, http.PutCalls.FindAll(p => p == "node/100").Count);
    }

    [Fact]
    public async Task UpdateNode_VersionConflictPersists_PropagatesAfterMaxAttempts()
    {
        var http = new StubHttp();
        http.GetResponses["node/100"] = NodeXml(100, 5, ("currency:XBT", "yes"));
        http.PutSequence["node/100"] = new Queue<Func<string>>(new Func<string>[]
        {
            () => throw new OsmConflictException("PUT node/100", "v conflict"),
            () => throw new OsmConflictException("PUT node/100", "v conflict"),
            () => throw new OsmConflictException("PUT node/100", "v conflict")
        });
        var changesets = new StubChangesetManager(opensWithId: 1);

        var client = MakeClient(http, changesets, withToken: TestToken);

        await Assert.ThrowsAsync<OsmConflictException>(() =>
            client.UpdateNodeAsync(TestStoreId, 100, "node",
                new BtcMapMerchant { Name = "Bitcoin Cafe" }, CancellationToken.None));

        Assert.True(changesets.Closed);
    }

    [Fact]
    public async Task UpdateNode_PropagatesAuthExceptionImmediately()
    {
        var http = new StubHttp();
        http.GetResponses["node/100"] = NodeXml(100, 5, ("currency:XBT", "yes"));
        http.PutSequence["node/100"] = new Queue<Func<string>>(new Func<string>[]
        {
            () => throw new OsmAuthException("PUT node/100", "unauthorized")
        });
        var changesets = new StubChangesetManager(opensWithId: 1);

        var client = MakeClient(http, changesets, withToken: TestToken);

        await Assert.ThrowsAsync<OsmAuthException>(() =>
            client.UpdateNodeAsync(TestStoreId, 100, "node",
                new BtcMapMerchant { Name = "Bitcoin Cafe" }, CancellationToken.None));
    }

    [Fact]
    public async Task UnlistNode_NoBitcoinTags_ShortCircuitsWithoutChangeset()
    {
        var http = new StubHttp();
        http.GetResponses["node/100"] = NodeXml(100, 5, ("name", "Bitcoin Cafe"));
        var changesets = new StubChangesetManager(opensWithId: 999);

        var client = MakeClient(http, changesets, withToken: TestToken);

        var result = await client.UnlistNodeAsync(TestStoreId, 100, "node", "Bitcoin Cafe", CancellationToken.None);

        Assert.True(result.AlreadyUnlisted);
        Assert.False(changesets.Opened);
        Assert.False(changesets.Closed);
    }

    [Fact]
    public async Task UnlistNode_RemovesBitcoinTags_PreservesOthers()
    {
        var http = new StubHttp();
        http.GetResponses["node/100"] = NodeXml(100, 5,
            ("currency:XBT", "yes"),
            ("payment:lightning", "yes"),
            ("name", "Bitcoin Cafe"),
            ("amenity", "cafe"));
        http.PutResponses["node/100"] = "6";
        var changesets = new StubChangesetManager(opensWithId: 1234);

        var client = MakeClient(http, changesets, withToken: TestToken);

        var result = await client.UnlistNodeAsync(TestStoreId, 100, "node", "Bitcoin Cafe", CancellationToken.None);

        Assert.False(result.AlreadyUnlisted);
        Assert.Equal(6, result.NewVersion);
        Assert.Contains("currency:XBT", result.RemovedTags);
        Assert.Contains("payment:lightning", result.RemovedTags);
        Assert.True(changesets.Opened);
        Assert.True(changesets.Closed);

        var lastPutBody = http.LastPutBody["node/100"];
        Assert.DoesNotContain("currency:XBT", lastPutBody);
        Assert.DoesNotContain("payment:lightning", lastPutBody);
        Assert.Contains("\"name\"", lastPutBody.Replace("'", "\""));
        Assert.Contains("\"amenity\"", lastPutBody.Replace("'", "\""));
    }

    [Fact]
    public async Task NoToken_ThrowsOsmNotConnected()
    {
        var http = new StubHttp();
        var changesets = new StubChangesetManager(opensWithId: 1);
        var client = MakeClient(http, changesets, withToken: null);

        await Assert.ThrowsAsync<OsmNotConnectedException>(() =>
            client.CreateNodeAsync(TestStoreId,
                new BtcMapMerchant { Name = "Bitcoin Cafe", Latitude = 32.6838298, Longitude = -117.1839771 }, CancellationToken.None));
    }

    private static OsmApiClient MakeClient(StubHttp http, StubChangesetManager changesets, string withToken)
    {
        var oauthRepo = new StubOAuthRepo
        {
            ToReturn = withToken == null
                ? null
                : new BtcMapStoreOAuthDecrypted { OsmAccessToken = withToken }
        };
        return new OsmApiClient(http, changesets,
            new OsmTagBuilder(() => new DateTime(2026, 5, 8, 12, 0, 0, DateTimeKind.Utc)),
            oauthRepo, new NullLogger<OsmApiClient>());
    }

    private static string NodeXml(long id, int version, params (string k, string v)[] tags)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("<osm><node id=\"").Append(id).Append("\" version=\"").Append(version).Append("\">");
        foreach (var (k, v) in tags)
            sb.Append("<tag k=\"").Append(k).Append("\" v=\"").Append(v).Append("\" />");
        sb.Append("</node></osm>");
        return sb.ToString();
    }

    private class StubHttp : IOsmHttpClient
    {
        public Dictionary<string, string> GetResponses { get; } = new();
        public Dictionary<string, string> PutResponses { get; } = new();
        public Dictionary<string, Queue<Func<string>>> PutSequence { get; } = new();
        public Dictionary<string, string> LastPutBody { get; } = new();
        public List<string> PutCalls { get; } = new();

        public Task<string> GetStringAsync(string accessToken, string path, CancellationToken ct)
        {
            if (!GetResponses.TryGetValue(path, out var body))
                throw new InvalidOperationException($"No GET stub for {path}");
            return Task.FromResult(body);
        }

        public Task<string> PutXmlAsync(string accessToken, string path, string xmlBody, CancellationToken ct)
        {
            PutCalls.Add(path);
            LastPutBody[path] = xmlBody;
            if (PutSequence.TryGetValue(path, out var queue) && queue.Count > 0)
            {
                var fn = queue.Dequeue();
                return Task.FromResult(fn());
            }
            if (PutResponses.TryGetValue(path, out var body))
                return Task.FromResult(body);
            throw new InvalidOperationException($"No PUT stub for {path}");
        }
    }

    private class StubChangesetManager : IOsmChangesetManager
    {
        private readonly long _opensWithId;
        public StubChangesetManager(long opensWithId) { _opensWithId = opensWithId; }

        public bool Opened { get; private set; }
        public bool Closed { get; private set; }
        public int OpenCallCount { get; private set; }

        public Task<long> OpenAsync(string accessToken, string commentTemplate, string merchantName, CancellationToken ct)
        {
            Opened = true;
            OpenCallCount++;
            return Task.FromResult(_opensWithId);
        }

        public Task CloseAsync(string accessToken, long changesetId, CancellationToken ct)
        {
            Closed = true;
            return Task.CompletedTask;
        }
    }

    private class StubOAuthRepo : IBtcMapStoreOAuthRepository
    {
        public BtcMapStoreOAuthDecrypted ToReturn { get; set; }

        public Task<BtcMapStoreOAuthDecrypted> GetForStoreAsync(string storeId) => Task.FromResult(ToReturn);
        public Task UpsertAsync(string storeId, BtcMapStoreOAuthDecrypted state) => Task.CompletedTask;
        public Task SaveClientCredentialsAsync(string storeId, string clientId, string clientSecret) => Task.CompletedTask;
        public Task SaveAccessTokenAsync(string storeId, string accessToken, string username) => Task.CompletedTask;
        public Task ClearOAuthAsync(string storeId) => Task.CompletedTask;
        public Task ClearTokenOnlyAsync(string storeId) => Task.CompletedTask;
        public Task SetPendingStateAsync(string storeId, string state, DateTimeOffset expiresAt) => Task.CompletedTask;
        public Task ClearPendingStateAsync(string storeId) => Task.CompletedTask;
    }
}
