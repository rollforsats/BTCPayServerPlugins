using System;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using BTCPayServer.Plugins.BTCMap.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.BTCMap;

public class Plugin : BaseBTCPayServerPlugin
{
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    {
        new() { Identifier = nameof(BTCPayServer), Condition = ">=1.12.0" }
    };

    public override void Execute(IServiceCollection services)
    {
        // UI extensions
        services.AddUIExtension("store-integrations-nav", "BtcMapStoreNav");
        services.AddUIExtension("server-nav", "BtcMapServerNav");

        // Database
        services.AddHostedService<PluginMigrationRunner>();
        services.AddSingleton<BtcMapDbContextFactory>();
        services.AddDbContext<BtcMapDbContext>((provider, o) =>
        {
            var factory = provider.GetRequiredService<BtcMapDbContextFactory>();
            factory.ConfigureBuilder(o);
        });

        // Services
        services.AddSingleton<BtcMapService>();
        services.AddSingleton<OsmAuthService>();
        services.AddSingleton<OsmApiClient>();
        services.AddSingleton<OverpassApiClient>();
        services.AddSingleton<NominatimApiClient>();
        services.AddSingleton<DirectoryService>();

        // IOverpassApiClient binding — dev fixture mode when BTCMAP_OVERPASS_SCENARIO is
        // set on non-mainnet Development builds, otherwise the real OverpassApiClient.
        // Gate is checked lazily on first resolution so startup fails loudly if the env
        // var leaks into a Production or mainnet deployment.
        var scenarioName = Environment.GetEnvironmentVariable("BTCMAP_OVERPASS_SCENARIO");
        if (!string.IsNullOrWhiteSpace(scenarioName))
        {
            services.AddSingleton<IOverpassApiClient>(sp =>
            {
                var env = sp.GetRequiredService<IHostEnvironment>();
                var auth = sp.GetRequiredService<OsmAuthService>();

                if (!env.IsDevelopment())
                    throw new InvalidOperationException(
                        $"BTCMAP_OVERPASS_SCENARIO='{scenarioName}' refused: ASPNETCORE_ENVIRONMENT is not Development");
                if (auth.IsMainnet)
                    throw new InvalidOperationException(
                        $"BTCMAP_OVERPASS_SCENARIO='{scenarioName}' refused: running on mainnet");

                var logger = sp.GetRequiredService<ILogger<FixtureOverpassApiClient>>();
                logger.LogWarning(
                    "Overpass fixture mode ACTIVE — scenario '{Scenario}'. All Overpass search calls will return hardcoded data.",
                    scenarioName);
                return new FixtureOverpassApiClient(scenarioName, logger);
            });
        }
        else
        {
            services.AddSingleton<IOverpassApiClient>(sp => sp.GetRequiredService<OverpassApiClient>());
        }

        // Background services
        services.AddHostedService<BtcMapReverificationService>();

        // Named HTTP clients
        services.AddHttpClient("OsmApi", client =>
        {
            client.DefaultRequestHeaders.Add("User-Agent", "BTCPayServer-BtcMap-Plugin/1.0");
            client.Timeout = TimeSpan.FromSeconds(20);
        });
        services.AddHttpClient("OverpassApi", client =>
        {
            client.BaseAddress = new Uri("https://overpass-api.de/");
            client.DefaultRequestHeaders.Add("User-Agent", "BTCPayServer-BtcMap-Plugin/1.0");
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddHttpClient("NominatimApi", client =>
        {
            client.BaseAddress = new Uri("https://nominatim.openstreetmap.org/");
            client.DefaultRequestHeaders.Add("User-Agent", "BTCPayServer-BtcMap-Plugin/1.0");
            client.Timeout = TimeSpan.FromSeconds(20);
        });
        services.AddHttpClient("DirectoryApi", client =>
        {
            client.BaseAddress = new Uri("https://raw.githubusercontent.com/");
            client.DefaultRequestHeaders.Add("User-Agent", "BTCPayServer-BtcMap-Plugin/1.0");
            client.Timeout = TimeSpan.FromSeconds(20);
        });
    }
}
