using System;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Plugins.BTCMap.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.BTCMap.Services;

public class BtcMapStoreOAuthRepository : IBtcMapStoreOAuthRepository
{
    public const string ProtectorPurpose = "BTCMapPlugin.OAuth";

    private readonly BtcMapDbContextFactory _dbContextFactory;
    private readonly IDataProtector _protector;

    public BtcMapStoreOAuthRepository(
        BtcMapDbContextFactory dbContextFactory,
        IDataProtectionProvider dataProtectionProvider)
    {
        _dbContextFactory = dbContextFactory;
        _protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);
    }

    public async Task<BtcMapStoreOAuthDecrypted> GetForStoreAsync(string storeId)
    {
        await using var ctx = _dbContextFactory.CreateContext();
        var row = await ctx.StoreOAuth.AsNoTracking().FirstOrDefaultAsync(r => r.StoreId == storeId);
        if (row == null) return null;
        return Decrypt(row);
    }

    public async Task UpsertAsync(string storeId, BtcMapStoreOAuthDecrypted state)
    {
        await using var ctx = _dbContextFactory.CreateContext();
        var row = await ctx.StoreOAuth.FirstOrDefaultAsync(r => r.StoreId == storeId);
        if (row == null)
        {
            row = new BtcMapStoreOAuth { Id = Guid.NewGuid().ToString(), StoreId = storeId };
            ctx.StoreOAuth.Add(row);
        }
        ApplyState(row, state);
        await ctx.SaveChangesAsync();
    }

    public async Task SaveClientCredentialsAsync(string storeId, string clientId, string clientSecret)
    {
        await using var ctx = _dbContextFactory.CreateContext();
        var row = await GetOrCreateAsync(ctx, storeId);
        row.OsmClientId = clientId;
        row.OsmClientSecretEncrypted = string.IsNullOrEmpty(clientSecret) ? null : _protector.Protect(clientSecret);
        await ctx.SaveChangesAsync();
    }

    public async Task SaveAccessTokenAsync(string storeId, string accessToken, string username)
    {
        await using var ctx = _dbContextFactory.CreateContext();
        var row = await GetOrCreateAsync(ctx, storeId);
        row.OsmAccessTokenEncrypted = string.IsNullOrEmpty(accessToken) ? null : _protector.Protect(accessToken);
        row.OsmUsername = username;
        row.OsmConnectedAt = DateTimeOffset.UtcNow;
        row.OsmDisconnectedAt = null;
        row.PendingState = null;
        row.PendingStateExpiresAt = null;
        await ctx.SaveChangesAsync();
    }

    public async Task ClearOAuthAsync(string storeId)
    {
        await using var ctx = _dbContextFactory.CreateContext();
        var row = await ctx.StoreOAuth.FirstOrDefaultAsync(r => r.StoreId == storeId);
        if (row == null) return;
        row.OsmClientId = null;
        row.OsmClientSecretEncrypted = null;
        row.OsmAccessTokenEncrypted = null;
        row.OsmUsername = null;
        row.PendingState = null;
        row.PendingStateExpiresAt = null;
        row.OsmConnectedAt = null;
        row.OsmDisconnectedAt = DateTimeOffset.UtcNow;
        await ctx.SaveChangesAsync();
    }

    public async Task ClearTokenOnlyAsync(string storeId)
    {
        await using var ctx = _dbContextFactory.CreateContext();
        var row = await ctx.StoreOAuth.FirstOrDefaultAsync(r => r.StoreId == storeId);
        if (row == null) return;
        row.OsmAccessTokenEncrypted = null;
        row.OsmUsername = null;
        await ctx.SaveChangesAsync();
    }

    public async Task SetPendingStateAsync(string storeId, string state, DateTimeOffset expiresAt)
    {
        await using var ctx = _dbContextFactory.CreateContext();
        var row = await GetOrCreateAsync(ctx, storeId);
        row.PendingState = state;
        row.PendingStateExpiresAt = expiresAt;
        await ctx.SaveChangesAsync();
    }

    public async Task ClearPendingStateAsync(string storeId)
    {
        await using var ctx = _dbContextFactory.CreateContext();
        var row = await ctx.StoreOAuth.FirstOrDefaultAsync(r => r.StoreId == storeId);
        if (row == null) return;
        row.PendingState = null;
        row.PendingStateExpiresAt = null;
        await ctx.SaveChangesAsync();
    }

    private static async Task<BtcMapStoreOAuth> GetOrCreateAsync(BtcMapDbContext ctx, string storeId)
    {
        var row = await ctx.StoreOAuth.FirstOrDefaultAsync(r => r.StoreId == storeId);
        if (row == null)
        {
            row = new BtcMapStoreOAuth { Id = Guid.NewGuid().ToString(), StoreId = storeId };
            ctx.StoreOAuth.Add(row);
        }
        return row;
    }

    private void ApplyState(BtcMapStoreOAuth row, BtcMapStoreOAuthDecrypted state)
    {
        row.OsmClientId = state.OsmClientId;
        row.OsmClientSecretEncrypted = string.IsNullOrEmpty(state.OsmClientSecret)
            ? null : _protector.Protect(state.OsmClientSecret);
        row.OsmAccessTokenEncrypted = string.IsNullOrEmpty(state.OsmAccessToken)
            ? null : _protector.Protect(state.OsmAccessToken);
        row.OsmUsername = state.OsmUsername;
        row.PendingState = state.PendingState;
        row.PendingStateExpiresAt = state.PendingStateExpiresAt;
        row.OsmConnectedAt = state.OsmConnectedAt;
        row.OsmDisconnectedAt = state.OsmDisconnectedAt;
    }

    private BtcMapStoreOAuthDecrypted Decrypt(BtcMapStoreOAuth row) => new()
    {
        OsmClientId = row.OsmClientId,
        OsmClientSecret = string.IsNullOrEmpty(row.OsmClientSecretEncrypted)
            ? null : _protector.Unprotect(row.OsmClientSecretEncrypted),
        OsmAccessToken = string.IsNullOrEmpty(row.OsmAccessTokenEncrypted)
            ? null : _protector.Unprotect(row.OsmAccessTokenEncrypted),
        OsmUsername = row.OsmUsername,
        PendingState = row.PendingState,
        PendingStateExpiresAt = row.PendingStateExpiresAt,
        OsmConnectedAt = row.OsmConnectedAt,
        OsmDisconnectedAt = row.OsmDisconnectedAt
    };
}
