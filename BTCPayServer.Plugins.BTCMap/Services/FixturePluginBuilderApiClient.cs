using System.Threading.Tasks;
using BTCPayServer.Plugins.BTCMap.Models;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.BTCMap.Services;

public class FixturePluginBuilderApiClient : IPluginBuilderApiClient
{
    private readonly string _scenarioName;
    private readonly PluginBuilderApiScenario _scenario;
    private readonly ILogger<FixturePluginBuilderApiClient> _logger;

    public FixturePluginBuilderApiClient(string scenarioName, ILogger<FixturePluginBuilderApiClient> logger)
    {
        _scenarioName = scenarioName;
        _scenario = PluginBuilderApiFixtureScenarios.Get(scenarioName);
        _logger = logger;
    }

    public Task<BtcMapSubmitResponse> SubmitAsync(BtcMapSubmitRequest request)
    {
        if (_scenario.SubmitFailure != null)
        {
            var ex = _scenario.SubmitFailure(request);
            _logger.LogWarning("[FIXTURE:{Scenario}] Submit → {Status} {Message}",
                _scenarioName, ex.StatusCode, ex.Message);
            throw ex;
        }
        var response = _scenario.SubmitSuccess(request);
        _logger.LogInformation("[FIXTURE:{Scenario}] Submit → success", _scenarioName);
        return Task.FromResult(response);
    }

    public Task<bool> PingAsync()
    {
        _logger.LogInformation("[FIXTURE:{Scenario}] Ping → {Reachable}",
            _scenarioName, _scenario.PingSucceeds);
        return Task.FromResult(_scenario.PingSucceeds);
    }
}
