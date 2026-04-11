using System.Collections.Generic;
using System.Threading.Tasks;
using BTCPayServer.Plugins.BTCMap.Models;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.BTCMap.Services;

/// <summary>
/// Dev-only replacement for <see cref="OverpassApiClient"/> that returns hardcoded
/// scenario data instead of calling the real Overpass API. Only registered when the
/// <c>BTCMAP_OVERPASS_SCENARIO</c> env var is set AND the plugin is running in the
/// Development environment on non-mainnet — see the triple-gate in Plugin.cs.
/// </summary>
public class FixtureOverpassApiClient : IOverpassApiClient
{
    private readonly string _scenarioName;
    private readonly OverpassScenario _scenario;
    private readonly ILogger<FixtureOverpassApiClient> _logger;

    public FixtureOverpassApiClient(string scenarioName, ILogger<FixtureOverpassApiClient> logger)
    {
        _scenarioName = scenarioName;
        _scenario = OverpassFixtureScenarios.Get(scenarioName);
        _logger = logger;
    }

    public Task<List<OverpassElement>> SearchNearby(double lat, double lon, int radiusMeters, string name)
    {
        _logger.LogInformation(
            "[FIXTURE:{Scenario}] SearchNearby(name='{Name}') → {Count} elements",
            _scenarioName, name, _scenario.NameSearch.Count);
        return Task.FromResult(_scenario.NameSearch);
    }

    public Task<List<OverpassElement>> SearchByAddress(double lat, double lon, int radiusMeters, string street, string city)
    {
        _logger.LogInformation(
            "[FIXTURE:{Scenario}] SearchByAddress(street='{Street}', city='{City}') → {Count} elements",
            _scenarioName, street, city, _scenario.AddressSearch.Count);
        return Task.FromResult(_scenario.AddressSearch);
    }

    public Task<List<OverpassElement>> SearchByCoordinates(double lat, double lon, int radiusMeters)
    {
        _logger.LogInformation(
            "[FIXTURE:{Scenario}] SearchByCoordinates() → {Count} elements",
            _scenarioName, _scenario.CoordinatesSearch.Count);
        return Task.FromResult(_scenario.CoordinatesSearch);
    }

    public Task<List<OverpassElement>> CheckExistingBitcoinTags(double lat, double lon)
    {
        _logger.LogInformation(
            "[FIXTURE:{Scenario}] CheckExistingBitcoinTags() → {Count} elements",
            _scenarioName, _scenario.Duplicates.Count);
        return Task.FromResult(_scenario.Duplicates);
    }
}
