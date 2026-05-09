using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.BTCMap.Services;

public class NominatimApiClient : INominatimApiClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<NominatimApiClient> _logger;
    private readonly SemaphoreSlim _rateLimiter = new(1, 1);
    private DateTimeOffset _lastRequest = DateTimeOffset.MinValue;

    public NominatimApiClient(IHttpClientFactory httpClientFactory, ILogger<NominatimApiClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<(double lat, double lon)?> Geocode(string street, string city, string postcode, string country)
    {
        await _rateLimiter.WaitAsync();
        try
        {
            var elapsed = DateTimeOffset.UtcNow - _lastRequest;
            if (elapsed < TimeSpan.FromSeconds(1))
                await Task.Delay(TimeSpan.FromSeconds(1) - elapsed);

            var client = _httpClientFactory.CreateClient("NominatimApi");
            var parts = new[] { street, city, postcode, country };
            var q = string.Join(", ", Array.FindAll(parts, p => !string.IsNullOrWhiteSpace(p)));

            var response = await client.GetAsync($"search?q={Uri.EscapeDataString(q)}&format=json&limit=1");
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var results = doc.RootElement;

            if (results.GetArrayLength() == 0)
                return null;

            var first = results[0];
            var lat = double.Parse(first.GetProperty("lat").GetString()!,
                System.Globalization.CultureInfo.InvariantCulture);
            var lon = double.Parse(first.GetProperty("lon").GetString()!,
                System.Globalization.CultureInfo.InvariantCulture);

            return (lat, lon);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Nominatim geocode failed");
            return null;
        }
        finally
        {
            _lastRequest = DateTimeOffset.UtcNow;
            _rateLimiter.Release();
        }
    }
}
