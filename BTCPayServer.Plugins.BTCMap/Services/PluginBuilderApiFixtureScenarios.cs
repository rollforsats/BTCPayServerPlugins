using System;
using System.Collections.Generic;
using BTCPayServer.Plugins.BTCMap.Models;

namespace BTCPayServer.Plugins.BTCMap.Services;

public record PluginBuilderApiScenario(
    Func<BtcMapSubmitRequest, BtcMapSubmitResponse> SubmitSuccess,
    Func<BtcMapSubmitRequest, PluginBuilderApiException> SubmitFailure);

public static class PluginBuilderApiFixtureScenarios
{
    public static readonly IReadOnlyList<string> Names = new[]
    {
        "directory-only-success",
        "directory-duplicate-url",
        "btcmap-only-success",
        "both-lanes-success",
        "btcmap-not-configured",
        "btcmap-upstream-timeout",
        "btcmap-upstream-failed",
        "directory-not-configured",
        "directory-upstream-timeout",
        "directory-upstream-failed",
        "rate-limited",
        "validation-error",
        "transport-error"
    };

    public static PluginBuilderApiScenario Get(string name) => name switch
    {
        "directory-only-success" => DirectoryOnlySuccess(),
        "directory-duplicate-url" => DirectoryDuplicateUrl(),
        "btcmap-only-success" => BtcMapOnlySuccess(),
        "both-lanes-success" => BothLanesSuccess(),
        "btcmap-not-configured" => FixedFailure("btcmap-not-configured", 503,
            "BTC Map is not yet available on this BTCPay instance. Please contact the administrator."),
        "btcmap-upstream-timeout" => FixedFailure("btcmap-upstream-timeout", 504,
            "The BTC Map service timed out. Please try again in a few minutes."),
        "btcmap-upstream-failed" => FixedFailure("btcmap-upstream-failed", 502,
            "BTC Map encountered an upstream error. Please try again later."),
        "directory-not-configured" => FixedFailure("directory-not-configured", 503,
            "BTCPay Directory submissions are temporarily unavailable. Please try again later."),
        "directory-upstream-timeout" => FixedFailure("directory-upstream-timeout", 504,
            "BTCPay Directory submissions timed out. Please try again."),
        "directory-upstream-failed" => FixedFailure("directory-upstream-failed", 502,
            "BTCPay Directory encountered an upstream error. Please try again later."),
        "rate-limited" => FixedFailure(null, 429,
            "Rate limit reached (3 submissions per 24 hours). Please try again later."),
        "validation-error" => FixedFailure(null, 400,
            "Lat: required; Lon: required"),
        "transport-error" => new PluginBuilderApiScenario(
            SubmitSuccess: null,
            SubmitFailure: _ => new PluginBuilderApiException(0,
                "Could not reach the BTC Map service. Please try again later.")),
        _ => throw new ArgumentException(
            $"Unknown BTCMAP_PLUGINBUILDER_SCENARIO '{name}'. Valid values: {string.Join(", ", Names)}")
    };

    private const string FixturePrUrl = "https://plugin-builder.test/fixture/directory/pull/1234";

    private static BtcMapDirectoryResult DirectorySuccess() => new()
    {
        PrUrl = FixturePrUrl,
        PrNumber = 1234,
        Branch = "submission/fixture"
    };

    private static BtcMapBtcMapResult BtcMapSuccess(BtcMapSubmitRequest req) => new()
    {
        Id = 42,
        Origin = "btcpayserver",
        ExternalId = req?.ExternalId ?? "fixture:store"
    };

    private static PluginBuilderApiScenario DirectoryOnlySuccess() => new(
        SubmitSuccess: req => new BtcMapSubmitResponse
        {
            Directory = req.SubmitToDirectory ? DirectorySuccess() : null
        },
        SubmitFailure: null);

    private static PluginBuilderApiScenario DirectoryDuplicateUrl() => new(
        SubmitSuccess: req => new BtcMapSubmitResponse
        {
            Directory = req.SubmitToDirectory
                ? new BtcMapDirectoryResult { Skipped = "duplicate-url:https://example.com" }
                : null
        },
        SubmitFailure: null);

    private static PluginBuilderApiScenario BtcMapOnlySuccess() => new(
        SubmitSuccess: req => new BtcMapSubmitResponse
        {
            BtcMap = req.SubmitToBtcMap ? BtcMapSuccess(req) : null
        },
        SubmitFailure: null);

    private static PluginBuilderApiScenario BothLanesSuccess() => new(
        SubmitSuccess: req => new BtcMapSubmitResponse
        {
            Directory = req.SubmitToDirectory ? DirectorySuccess() : null,
            BtcMap = req.SubmitToBtcMap ? BtcMapSuccess(req) : null
        },
        SubmitFailure: null);

    private static PluginBuilderApiScenario FixedFailure(string code, int status, string message) => new(
        SubmitSuccess: null,
        SubmitFailure: _ => new PluginBuilderApiException(status, message,
            string.IsNullOrEmpty(code) ? null : Guid.NewGuid().ToString("N")));
}
