using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Plugins.BTCMap.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace BTCPayServer.Plugins.BTCMap;

public class PluginMigrationRunner : IHostedService
{
    private readonly BtcMapDbContextFactory _dbContextFactory;
    private readonly ISettingsRepository _settingsRepository;
    private static readonly TaskCompletionSource<bool> _ready = new();

    public static Task WaitForMigration => _ready.Task;

    public PluginMigrationRunner(
        ISettingsRepository settingsRepository,
        BtcMapDbContextFactory dbContextFactory)
    {
        _settingsRepository = settingsRepository;
        _dbContextFactory = dbContextFactory;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var settings = await _settingsRepository.GetSettingAsync<BtcMapMigrationHistory>() ??
                           new BtcMapMigrationHistory();
            await using var ctx = _dbContextFactory.CreateContext();
            await ctx.Database.MigrateAsync(cancellationToken);

            if (!settings.InitialMigrationComplete)
            {
                settings.InitialMigrationComplete = true;
                await _settingsRepository.UpdateSetting(settings);
            }

            _ready.TrySetResult(true);
        }
        catch
        {
            _ready.TrySetResult(false);
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private class BtcMapMigrationHistory
    {
        public bool InitialMigrationComplete { get; set; }
    }
}
