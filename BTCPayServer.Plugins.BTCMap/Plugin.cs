using System;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using BTCPayServer.Plugins.BTCMap.Services;
using Microsoft.Extensions.DependencyInjection;

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
        services.AddSingleton<IUIExtension>(new UIExtension("BtcMapStoreNav", "store-integrations-nav"));

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

        // Background services
        services.AddHostedService<BtcMapReverificationService>();

        // Named HTTP clients
        services.AddHttpClient("OsmApi", client =>
        {
            client.DefaultRequestHeaders.Add("User-Agent", "BTCPayServer-BtcMap-Plugin/1.0");
        });
        services.AddHttpClient("OverpassApi", client =>
        {
            client.BaseAddress = new Uri("https://overpass-api.de/");
            client.DefaultRequestHeaders.Add("User-Agent", "BTCPayServer-BtcMap-Plugin/1.0");
        });
        services.AddHttpClient("NominatimApi", client =>
        {
            client.BaseAddress = new Uri("https://nominatim.openstreetmap.org/");
            client.DefaultRequestHeaders.Add("User-Agent", "BTCPayServer-BtcMap-Plugin/1.0");
        });
    }
}
