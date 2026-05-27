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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.BTCMap.Services;

public class PluginBuilderApiException : Exception
{
    public int StatusCode { get; }
    public string CorrelationId { get; }
    public PluginBuilderApiException(int statusCode, string message, string correlationId = null) : base(message)
    {
        StatusCode = statusCode;
        CorrelationId = correlationId;
    }
}

public class PluginBuilderApiClient : IPluginBuilderApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IServiceProvider _services;
    private readonly ILogger<PluginBuilderApiClient> _logger;

    public PluginBuilderApiClient(
        IServiceProvider services,
        ILogger<PluginBuilderApiClient> logger)
    {
        _services = services;
        _logger = logger;
    }

    public async Task<BtcMapSubmitResponse> SubmitAsync(BtcMapSubmitRequest request)
    {
        using var scope = _services.CreateScope();
        var httpClient = scope.ServiceProvider
            .GetRequiredService<BTCPayServer.Plugins.PluginBuilderClient>().HttpClient;

        var json = JsonSerializer.Serialize(request, JsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await httpClient.PostAsync("/apis/btcmaps/v1/submit", content);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reach plugin-builder API");
            throw new PluginBuilderApiException(0, "Could not reach the BTC Map service. Please try again later.");
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync();
            var statusCode = (int)response.StatusCode;

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new PluginBuilderApiException(429,
                    "Rate limit reached (3 submissions per 24 hours). Please try again later.");

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

            if (statusCode >= 500 && statusCode < 600)
            {
                var outcome = TryParseOutcome(body);
                _logger.LogError(
                    "BTC Map API outcome failure {Status} code={Code} correlationId={CorrelationId} body={Body}",
                    statusCode, outcome.error, outcome.correlationId, body);
                var message = MapOutcomeToMessage(outcome.error, statusCode);
                throw new PluginBuilderApiException(statusCode, message, outcome.correlationId);
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("BTC Map API unexpected status {Status}: {Body}",
                    statusCode, body);
                throw new PluginBuilderApiException(statusCode,
                    "An unexpected error occurred. Please try again later.");
            }

            return JsonSerializer.Deserialize<BtcMapSubmitResponse>(body, JsonOptions)
                   ?? new BtcMapSubmitResponse();
        }
    }

    private static (string error, string correlationId) TryParseOutcome(string body)
    {
        try
        {
            var outcome = JsonSerializer.Deserialize<OutcomeErrorResponse>(body, JsonOptions);
            return (outcome?.Error, outcome?.CorrelationId);
        }
        catch
        {
            return (null, null);
        }
    }

    private static string MapOutcomeToMessage(string code, int statusCode) => code switch
    {
        "btcmap-not-configured" => "BTC Map is not yet available on this BTCPay instance. Please contact the administrator.",
        "btcmap-upstream-timeout" => "The BTC Map service timed out. Please try again in a few minutes.",
        "btcmap-upstream-failed" => "BTC Map encountered an upstream error. Please try again later.",
        "directory-not-configured" => "BTCPay Directory submissions are temporarily unavailable. Please try again later.",
        "directory-upstream-timeout" => "BTCPay Directory submissions timed out. Please try again.",
        "directory-upstream-failed" => "BTCPay Directory encountered an upstream error. Please try again later.",
        _ => statusCode switch
        {
            503 => "The BTC Map service is temporarily unavailable. Please try again later.",
            504 => "The BTC Map service timed out. Please try again in a few minutes.",
            502 => "The BTC Map service encountered an upstream error. Please try again later.",
            _ => "An unexpected error occurred. Please try again later."
        }
    };

    private class ValidationErrorResponse
    {
        public List<ValidationErrorItem> Errors { get; set; }
    }

    private class ValidationErrorItem
    {
        public string Path { get; set; }
        public string Message { get; set; }
    }

    private class OutcomeErrorResponse
    {
        public string Error { get; set; }
        public string CorrelationId { get; set; }
    }
}
