using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.BTCMap.Models;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.BTCMap.Services;

public class OverpassUnavailableException : Exception
{
    public OverpassUnavailableException()
        : base("The OpenStreetMap search service (Overpass API) is currently unavailable. Please try again later.") { }
}

public class OverpassApiClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OverpassApiClient> _logger;
    private readonly SemaphoreSlim _rateLimiter = new(1, 1);
    private DateTimeOffset _lastRequest = DateTimeOffset.MinValue;

    public OverpassApiClient(IHttpClientFactory httpClientFactory, ILogger<OverpassApiClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<List<OverpassElement>> SearchNearby(double lat, double lon, int radiusMeters, string name)
    {
        var escapedName = Regex.Escape(name).Replace("\"", "\\\"");
        var latStr = lat.ToString(CultureInfo.InvariantCulture);
        var lonStr = lon.ToString(CultureInfo.InvariantCulture);

        var query = $"""
            [out:json][timeout:25];
            nwr["name"~"{escapedName}",i](around:{radiusMeters},{latStr},{lonStr});
            out body center;
            """;

        return await ExecuteQuery(query);
    }

    public async Task<List<OverpassElement>> CheckExistingBitcoinTags(double lat, double lon)
    {
        var latStr = lat.ToString(CultureInfo.InvariantCulture);
        var lonStr = lon.ToString(CultureInfo.InvariantCulture);

        var query = $"""
            [out:json][timeout:25];
            nwr["currency:XBT"="yes"](around:50,{latStr},{lonStr});
            out body center;
            """;

        return await ExecuteQuery(query);
    }

    private async Task<List<OverpassElement>> ExecuteQuery(string query)
    {
        await _rateLimiter.WaitAsync();
        try
        {
            var elapsed = DateTimeOffset.UtcNow - _lastRequest;
            if (elapsed < TimeSpan.FromSeconds(1))
                await Task.Delay(TimeSpan.FromSeconds(1) - elapsed);

            var client = _httpClientFactory.CreateClient("OverpassApi");
            var content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("data", query) });

            var response = await client.PostAsync("api/interpreter", content);

            if (response.StatusCode == System.Net.HttpStatusCode.GatewayTimeout ||
                response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
            {
                _logger.LogWarning("Overpass API unavailable: {Status}", response.StatusCode);
                throw new OverpassUnavailableException();
            }

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<OverpassResponse>(json);

            _lastRequest = DateTimeOffset.UtcNow;
            return result?.Elements ?? new List<OverpassElement>();
        }
        catch (OverpassUnavailableException)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("Overpass API request timed out");
            throw new OverpassUnavailableException();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Overpass API query failed");
            throw;
        }
        finally
        {
            _rateLimiter.Release();
        }
    }
}
