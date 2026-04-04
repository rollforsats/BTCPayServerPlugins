using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.BTCMap.Services;

public class BtcMapReverificationService : IHostedService, IDisposable
{
    private readonly BtcMapService _btcMapService;
    private readonly OsmAuthService _osmAuthService;
    private readonly ILogger<BtcMapReverificationService> _logger;
    private Timer _timer;

    public BtcMapReverificationService(
        BtcMapService btcMapService,
        OsmAuthService osmAuthService,
        ILogger<BtcMapReverificationService> logger)
    {
        _btcMapService = btcMapService;
        _osmAuthService = osmAuthService;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = new Timer(DoWork, null, TimeSpan.FromMinutes(5), TimeSpan.FromHours(24));
        return Task.CompletedTask;
    }

    private async void DoWork(object state)
    {
        try
        {
            var settings = await _osmAuthService.GetSettings();
            if (string.IsNullOrEmpty(settings.OsmAccessToken))
            {
                _logger.LogDebug("OSM not connected, skipping re-verification");
                return;
            }

            var listings = await _btcMapService.GetListingsNeedingReverification();
            if (listings.Count == 0)
            {
                _logger.LogDebug("No listings need re-verification");
                return;
            }

            _logger.LogInformation("Re-verifying {Count} listing(s)", listings.Count);

            foreach (var listing in listings)
            {
                try
                {
                    await _btcMapService.ReverifyListing(listing);
                    _logger.LogInformation("Re-verified listing {ListingId} ({BusinessName})",
                        listing.Id, listing.BusinessName);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to re-verify listing {ListingId} ({BusinessName})",
                        listing.Id, listing.BusinessName);
                }

                // Rate-limit: 2 second delay between OSM operations
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Re-verification cycle failed");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }
}
