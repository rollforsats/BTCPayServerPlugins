using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using BTCPayServer.Plugins.BTCMap.Models;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.BTCMap.Services;

public class PluginBuilderApiException : Exception
{
    public int StatusCode { get; }
    public PluginBuilderApiException(int statusCode, string message) : base(message)
    {
        StatusCode = statusCode;
    }
}

public class PluginBuilderApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;
    private readonly ILogger<PluginBuilderApiClient> _logger;

    public PluginBuilderApiClient(
        BTCPayServer.Plugins.PluginBuilderClient pluginBuilderClient,
        ILogger<PluginBuilderApiClient> logger)
    {
        _httpClient = pluginBuilderClient.HttpClient;
        _logger = logger;
    }

    public async Task<BtcMapSubmitResponse> SubmitAsync(BtcMapSubmitRequest request)
    {
        var json = JsonSerializer.Serialize(request, JsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.PostAsync("/apis/btcmaps/v1/submit", content);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reach plugin-builder API");
            throw new PluginBuilderApiException(0, "Could not reach the BTC Map service. Please try again later.");
        }

        var body = await response.Content.ReadAsStringAsync();

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new PluginBuilderApiException(429,
                "Rate limit reached (5 submissions per 24 hours). Please try again later.");

        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            _logger.LogWarning("BTC Map API validation error: {Body}", body);
            string message;
            try
            {
                var err = JsonSerializer.Deserialize<ValidationErrorResponse>(body, JsonOptions);
                message = err?.Errors?.Count > 0
                    ? string.Join("; ", err.Errors.Select(e =>
                        string.IsNullOrEmpty(e.Path) ? e.Message : $"{e.Path}: {e.Message}"))
                    : "The submission was rejected. Please check your inputs and try again.";
            }
            catch
            {
                message = "The submission was rejected. Please check your inputs and try again.";
            }
            throw new PluginBuilderApiException(400, message);
        }

        if (response.StatusCode == HttpStatusCode.BadGateway)
        {
            _logger.LogError("BTC Map API upstream failure: {Body}", body);
            throw new PluginBuilderApiException(502,
                "The BTC Map service encountered an upstream error. Please try again later.");
        }

        // 409 "already-unlisted" is treated as success — the merchant is unlisted either way
        if (response.StatusCode == HttpStatusCode.Conflict)
            return JsonSerializer.Deserialize<BtcMapSubmitResponse>(body, JsonOptions)
                   ?? new BtcMapSubmitResponse();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("BTC Map API unexpected status {Status}: {Body}",
                (int)response.StatusCode, body);
            throw new PluginBuilderApiException((int)response.StatusCode,
                "An unexpected error occurred. Please try again later.");
        }

        return JsonSerializer.Deserialize<BtcMapSubmitResponse>(body, JsonOptions)
               ?? new BtcMapSubmitResponse();
    }

    private class ValidationErrorResponse
    {
        public List<ValidationErrorItem> Errors { get; set; }
    }

    private class ValidationErrorItem
    {
        public string Path { get; set; }
        public string Message { get; set; }
    }
}
