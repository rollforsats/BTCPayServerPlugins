using System;
using System.Security.Cryptography;
using System.Threading.Tasks;
using BTCPayServer.Plugins.BTCMap.Controllers;
using BTCPayServer.Plugins.BTCMap.Data;
using BTCPayServer.Plugins.BTCMap.Models;
using BTCPayServer.Plugins.BTCMap.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BTCPayServer.Plugins.BTCMap.Tests;

public class CredentialsResetTests
{
    [Fact]
    public async Task Repository_CryptographicException_OnUnprotect_ClearsRowAndFlagsReset()
    {
        // Pins the data-protection-key-rotated path: GetForStoreAsync must return a
        // DTO with CredentialsReset=true, null-out the unrecoverable encrypted fields,
        // and persist the cleared state so subsequent reads see the converged shape.
        const string storeId = "store-reset";
        var factory = TestDbFactory.Create();
        var protector = new ThrowingProtector();
        var provider = new SingleProtectorProvider(protector);
        var repo = new BtcMapStoreOAuthRepository(factory, provider, new NullLogger<BtcMapStoreOAuthRepository>());

        // Seed a row directly so we have something to "fail to decrypt".
        await using (var seedCtx = factory.CreateContext())
        {
            seedCtx.StoreOAuth.Add(new BtcMapStoreOAuth
            {
                Id = Guid.NewGuid().ToString(),
                StoreId = storeId,
                OsmClientId = "client-id",
                OsmClientSecretEncrypted = "ciphertext-secret",
                OsmAccessTokenEncrypted = "ciphertext-token",
                OsmUsername = "tester"
            });
            await seedCtx.SaveChangesAsync();
        }

        var first = await repo.GetForStoreAsync(storeId);

        Assert.NotNull(first);
        Assert.True(first.CredentialsReset);
        Assert.Null(first.OsmClientSecret);
        Assert.Null(first.OsmAccessToken);
        Assert.Equal("client-id", first.OsmClientId);

        // Persistent: a subsequent read sees the cleared encrypted columns, not the
        // original ciphertext that would re-trigger Unprotect again.
        await using var verify = factory.CreateContext();
        var row = await verify.StoreOAuth.SingleAsync(r => r.StoreId == storeId);
        Assert.Null(row.OsmClientSecretEncrypted);
        Assert.Null(row.OsmAccessTokenEncrypted);
        Assert.Null(row.OsmUsername);
    }

    [Fact]
    public void Controller_ApplyOsmStateToViewModel_OnCredentialsReset_RendersConnectionError()
    {
        // Pins the Step-2 fix: CredentialsReset must flip OsmState into ConnectionError
        // so the Razor partial renders the banner copy, not the initial-setup form.
        var vm = new BtcMapListingViewModel();
        var oauth = new BtcMapStoreOAuthDecrypted
        {
            OsmClientId = "client-id",
            CredentialsReset = true
            // OsmClientSecret + OsmAccessToken intentionally null: the repo just cleared
            // them after the CryptographicException.
        };

        UIBtcMapStoreController.ApplyOsmStateToViewModel(vm, oauth,
            tempPendingState: null, tempErrorKind: null, tempErrorMessage: null);

        Assert.Equal(OsmConnectionState.ConnectionError, vm.OsmState);
        Assert.Equal(OsmConnectionErrorKind.Other, vm.OsmErrorKind);
        Assert.Contains("credentials were reset", vm.OsmErrorMessage);
    }

    [Fact]
    public void Controller_ApplyOsmStateToViewModel_HappyPath_RendersConnected()
    {
        var vm = new BtcMapListingViewModel();
        var oauth = new BtcMapStoreOAuthDecrypted
        {
            OsmClientId = "client-id",
            OsmClientSecret = "secret",
            OsmAccessToken = "tok",
            OsmUsername = "alice"
        };

        UIBtcMapStoreController.ApplyOsmStateToViewModel(vm, oauth,
            tempPendingState: null, tempErrorKind: null, tempErrorMessage: null);

        Assert.Equal(OsmConnectionState.Connected, vm.OsmState);
        Assert.Equal(OsmConnectionErrorKind.None, vm.OsmErrorKind);
        Assert.Equal("alice", vm.OsmUsername);
    }

    private sealed class ThrowingProtector : IDataProtector
    {
        public IDataProtector CreateProtector(string purpose) => this;
        public byte[] Protect(byte[] plaintext) => plaintext;
        public byte[] Unprotect(byte[] protectedData)
            => throw new CryptographicException("simulated key rotation");
    }

    private sealed class SingleProtectorProvider : IDataProtectionProvider
    {
        private readonly IDataProtector _protector;
        public SingleProtectorProvider(IDataProtector protector) { _protector = protector; }
        public IDataProtector CreateProtector(string purpose) => _protector;
    }
}
