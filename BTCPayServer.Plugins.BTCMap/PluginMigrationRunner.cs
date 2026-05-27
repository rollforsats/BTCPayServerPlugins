using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Plugins.BTCMap.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.BTCMap;

public class PluginMigrationRunner : IHostedService
{
    private readonly BtcMapDbContextFactory _dbContextFactory;
    private readonly ISettingsRepository _settingsRepository;
    private readonly IPluginBuilderApiClient _apiClient;
    private readonly BtcMapCapabilityState _capabilityState;
    private readonly ILogger<PluginMigrationRunner> _logger;
    private static readonly TaskCompletionSource<bool> _ready = new();

    public static Task WaitForMigration => _ready.Task;

    public PluginMigrationRunner(
        ISettingsRepository settingsRepository,
        BtcMapDbContextFactory dbContextFactory,
        IPluginBuilderApiClient apiClient,
        BtcMapCapabilityState capabilityState,
        ILogger<PluginMigrationRunner> logger)
    {
        _settingsRepository = settingsRepository;
        _dbContextFactory = dbContextFactory;
        _apiClient = apiClient;
        _capabilityState = capabilityState;
        _logger = logger;
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

            var reachable = await _apiClient.PingAsync();
            _capabilityState.Record(reachable);
            if (!reachable)
                _logger.LogWarning("BTC Map capability probe failed at startup; submissions will short-circuit until restart.");
            else
                _logger.LogInformation("BTC Map capability probe succeeded.");

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
