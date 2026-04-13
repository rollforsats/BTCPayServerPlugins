using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.BTCMap.Services;

public class DirectoryService
{
    private readonly BtcMapDbContextFactory _dbContextFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DirectoryService> _logger;

    private const string MerchantsJsonUrl =
        "btcpayserver/directory.btcpayserver.org/master/src/data/merchants.json";

    private const string GitHubIssueBaseUrl =
        "https://github.com/btcpayserver/directory.btcpayserver.org/issues/new";

    public DirectoryService(
        BtcMapDbContextFactory dbContextFactory,
        IHttpClientFactory httpClientFactory,
        ILogger<DirectoryService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public string BuildGitHubIssueUrl(string name, string url, string twitter,
        string type, string subType, string country, string description)
    {
        var lines = new List<string>
        {
            "New submission:",
            "",
            $"Name: {name.Trim()}",
            $"Url: {url.Trim()}"
        };

        if (!string.IsNullOrWhiteSpace(twitter))
            lines.Add($"Twitter: {twitter.Trim()}");

        lines.Add($"Type: {type}");

        if (type == "merchants" && !string.IsNullOrWhiteSpace(subType))
            lines.Add($"SubType: {subType}");

        if (type == "hosted-btcpay" && !string.IsNullOrWhiteSpace(country))
            lines.Add($"Country: {country}");

        lines.Add($"Description: {description.Trim()}");

        var issueTitle = $"New entry submission - {name.Trim()}";
        var issueBody = string.Join("\n", lines);

        var query = $"title={Uri.EscapeDataString(issueTitle)}&body={Uri.EscapeDataString(issueBody)}";
        return $"{GitHubIssueBaseUrl}?{query}";
    }

    public async Task<DirectoryEntry> CheckExistingListing(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        try
        {
            var client = _httpClientFactory.CreateClient("DirectoryApi");
            var response = await client.GetAsync(MerchantsJsonUrl);
            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync();
            var entries = JsonSerializer.Deserialize<List<DirectoryEntry>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (entries == null)
                return null;

            var normalizedUrl = NormalizeUrl(url);
            return entries.FirstOrDefault(e =>
                !string.IsNullOrEmpty(e.Url) && NormalizeUrl(e.Url) == normalizedUrl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check directory for existing listing");
            return null;
        }
    }

    public async Task<bool> RecordSubmission(string storeId, string submittedUrl)
    {
        await using var ctx = _dbContextFactory.CreateContext();
        var listing = await ctx.Listings.FirstOrDefaultAsync(l => l.StoreId == storeId);
        if (listing == null)
            return false;

        listing.DirectorySubmittedAt = DateTimeOffset.UtcNow;
        listing.DirectorySubmittedUrl = submittedUrl;
        await ctx.SaveChangesAsync();
        return true;
    }

    public async Task ClearSubmission(string storeId)
    {
        await using var ctx = _dbContextFactory.CreateContext();
        var listing = await ctx.Listings.FirstOrDefaultAsync(l => l.StoreId == storeId);
        if (listing != null)
        {
            listing.DirectorySubmittedAt = null;
            listing.DirectorySubmittedUrl = null;
            await ctx.SaveChangesAsync();
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
    public string Description { get; set; }
    public string Type { get; set; }
    public string SubType { get; set; }
    public string Country { get; set; }
    public string Twitter { get; set; }
}
