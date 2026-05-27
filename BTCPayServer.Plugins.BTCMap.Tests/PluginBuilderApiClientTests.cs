#nullable enable
using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.BTCMap.Models;
using BTCPayServer.Plugins.BTCMap.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BTCPayServer.Plugins.BTCMap.Tests;

public class PluginBuilderApiClientTests
{
    [Fact]
    public async Task TooManyRequests_emits_3_per_24h_text()
    {
        var ex = await SubmitAndExpectAsync(HttpStatusCode.TooManyRequests, "");
        Assert.Equal(429, ex.StatusCode);
        Assert.Contains("3 submissions per 24 hours", ex.Message);
    }

    [Fact]
    public async Task BadRequest_with_validation_envelope_concatenates_messages()
    {
        var body = """{"errors":[{"path":"Lat","message":"required"},{"path":"Lon","message":"required"}]}""";
        var ex = await SubmitAndExpectAsync(HttpStatusCode.BadRequest, body);
        Assert.Equal(400, ex.StatusCode);
        Assert.Contains("Lat: required", ex.Message);
        Assert.Contains("Lon: required", ex.Message);
    }

    [Fact]
    public async Task BadRequest_with_non_envelope_body_falls_back_to_generic_message()
    {
        var ex = await SubmitAndExpectAsync(HttpStatusCode.BadRequest, "not json");
        Assert.Equal(400, ex.StatusCode);
        Assert.Contains("rejected", ex.Message);
    }

    [Theory]
    [InlineData(503, "btcmap-not-configured", "BTC Map is not yet available")]
    [InlineData(504, "btcmap-upstream-timeout", "BTC Map service timed out")]
    [InlineData(502, "btcmap-upstream-failed", "BTC Map encountered an upstream error")]
    [InlineData(503, "directory-not-configured", "BTCPay Directory submissions are temporarily unavailable")]
    [InlineData(504, "directory-upstream-timeout", "BTCPay Directory submissions timed out")]
    [InlineData(502, "directory-upstream-failed", "BTCPay Directory encountered an upstream error")]
    public async Task Outcome_envelope_maps_each_code_to_user_facing_message(int status, string code, string substring)
    {
        var body = $$"""{"error":"{{code}}","correlationId":"abc123"}""";
        var ex = await SubmitAndExpectAsync((HttpStatusCode)status, body);
        Assert.Equal(status, ex.StatusCode);
        Assert.Contains(substring, ex.Message);
        Assert.Equal("abc123", ex.CorrelationId);
    }

    [Fact]
    public async Task Outcome_envelope_unknown_code_falls_back_to_status_based_message()
    {
        var body = """{"error":"some-future-code","correlationId":"xyz"}""";
        var ex = await SubmitAndExpectAsync((HttpStatusCode)503, body);
        Assert.Equal(503, ex.StatusCode);
        Assert.Contains("temporarily unavailable", ex.Message);
        Assert.Equal("xyz", ex.CorrelationId);
    }

    [Fact]
    public async Task Successful_submit_deserializes_response()
    {
        var body = """{"btcMap":{"id":42,"origin":"btcpayserver","externalId":"host:store"}}""";
        var client = BuildClient(HttpStatusCode.OK, body);
        var response = await client.SubmitAsync(new BtcMapSubmitRequest());
        Assert.NotNull(response.BtcMap);
        Assert.Equal(42, response.BtcMap.Id);
        Assert.Equal("btcpayserver", response.BtcMap.Origin);
        Assert.Equal("host:store", response.BtcMap.ExternalId);
    }

    private static async Task<PluginBuilderApiException> SubmitAndExpectAsync(HttpStatusCode status, string body)
    {
        var client = BuildClient(status, body);
        return await Assert.ThrowsAsync<PluginBuilderApiException>(
            () => client.SubmitAsync(new BtcMapSubmitRequest()));
    }

    private static PluginBuilderApiClient BuildClient(HttpStatusCode status, string body)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body ?? "", Encoding.UTF8, "application/json")
        });
        return BuildClient(handler);
    }

    private static PluginBuilderApiClient BuildClient(HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://plugin-builder.test/") };
        var pluginBuilderClient = new BTCPayServer.Plugins.PluginBuilderClient(httpClient);

        var services = new ServiceCollection();
        services.AddSingleton(pluginBuilderClient);
        var provider = services.BuildServiceProvider();

        return new PluginBuilderApiClient(provider, new NullLogger<PluginBuilderApiClient>());
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _impl;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> impl) { _impl = impl; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_impl(request));
    }
}
