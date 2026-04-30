using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.BTCMap.Services;

public class DirectoryListingChecker
{
    private const string MerchantsJsonUrl =
        "btcpayserver/directory.btcpayserver.org/master/src/data/merchants.json";
    private const string MerchantsCacheKey = "DirectoryListingChecker:MerchantsJson";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<DirectoryListingChecker> _logger;

    public DirectoryListingChecker(
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        ILogger<DirectoryListingChecker> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _logger = logger;
    }

    public async Task<DirectoryEntry> FindByUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        try
        {
            var entries = await _cache.GetOrCreateAsync(MerchantsCacheKey, async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = CacheDuration;

                var client = _httpClientFactory.CreateClient("DirectoryRawApi");
                using var response = await client.GetAsync(MerchantsJsonUrl);
                // Don't pin a failure to the full 5min cache window, but do hold it
                // briefly so a single page load doesn't fan out into N upstream calls.
                // Non-positive AbsoluteExpirationRelativeToNow throws on .NET 8.
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("merchants.json fetch failed with status {Status}", (int)response.StatusCode);
                    entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(1);
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync();
                var deserialized = JsonSerializer.Deserialize<List<DirectoryEntry>>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (deserialized == null)
                {
                    _logger.LogWarning("merchants.json deserialized to null");
                    entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(1);
                }
                return deserialized;
            });

            if (entries == null)
                return null;

            var normalized = NormalizeUrl(url);
            return entries.FirstOrDefault(e =>
                !string.IsNullOrEmpty(e.Url) && NormalizeUrl(e.Url) == normalized);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check directory for existing listing");
            return null;
        }
    }

    private static string NormalizeUrl(string url)
    {
        url = url.Trim().TrimEnd('/').ToLowerInvariant();
        if (url.StartsWith("https://"))
            url = url[8..];
        else if (url.StartsWith("http://"))
            url = url[7..];
        if (url.StartsWith("www."))
            url = url[4..];
        return url;
    }
}

public class DirectoryEntry
{
    public string Name { get; set; }
    public string Url { get; set; }
    public string Type { get; set; }
}
