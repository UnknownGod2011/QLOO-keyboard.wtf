using System.Net;
using System.Text.Json;
using KeyboardWtf.Staging.Api.Connectors;
using Microsoft.Extensions.Options;
using Xunit;

namespace KeyboardWtf.Staging.Api.Tests;

public sealed partial class ConnectorPlatformTests
{
    [Theory]
    [InlineData(408, true)]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(502, true)]
    [InlineData(503, true)]
    [InlineData(504, true)]
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(403, false)]
    [InlineData(404, false)]
    [InlineData(409, false)]
    [InlineData(501, false)]
    public async Task Adapter_marks_only_specific_transient_reads_not_writes(int status, bool transient)
    {
        foreach (var method in new[] { "GET", "POST" })
        foreach (var proxyStatus in new[] { false, true })
        {
            var handler = new Handler(_ => new HttpResponseMessage(proxyStatus ? HttpStatusCode.OK : (HttpStatusCode)status)
                { Content = new StringContent(proxyStatus ? JsonSerializer.Serialize(new { status, data = new { } }) : "not saved") });
            using var http = new HttpClient(handler);
            var adapter = new ComposioConnectorProvider(http, Options.Create(Settings()));
            var error = await Assert.ThrowsAsync<ConnectorProviderException>(() => adapter.RequestAsync("ca_test123",
                "https://api.github.com/user", method, null, default));
            Assert.Equal(method == "GET" && transient, error.RetryableRead);
            Assert.Equal(method == "POST" && status >= 500, error.OutcomeUnknown);
            Assert.Equal(1, handler.Calls); // hints are not adapter-owned replay
        }
    }

    [Theory]
    [InlineData("{bad json")]
    [InlineData("{}")]
    [InlineData("{\"status\":200}")]
    [InlineData("{\"status\":200,\"data\":{\"ok\":false,\"error\":\"unexpected_error\"}}")]
    [InlineData("{\"status\":200,\"data\":{\"ok\":false,\"error\":\"missing_scope\"}}")]
    public async Task Adapter_malformed_or_unknown_envelopes_are_not_recoverable(string response)
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response) });
        using var http = new HttpClient(handler);
        var adapter = new ComposioConnectorProvider(http, Options.Create(Settings()));
        var error = await Assert.ThrowsAsync<ConnectorProviderException>(() => adapter.RequestAsync("ca_test123",
            "https://api.github.com/user", "GET", null, default));
        Assert.False(error.RetryableRead); Assert.Null(error.RetryAfter); Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Adapter_oversized_read_is_not_recoverable()
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(new string('x', 2 * 1024 * 1024 + 1)) });
        using var http = new HttpClient(handler);
        var adapter = new ComposioConnectorProvider(http, Options.Create(Settings()));
        var error = await Assert.ThrowsAsync<ConnectorProviderException>(() => adapter.RequestAsync("ca_test123",
            "https://api.github.com/user", "GET", null, default));
        Assert.False(error.RetryableRead); Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("GET", true)]
    [InlineData("POST", false)]
    public async Task Adapter_transport_failure_is_recoverable_only_for_read(string method, bool retryable)
    {
        var handler = new Handler(_ => throw new HttpRequestException("unsafe remote diagnostics"));
        using var http = new HttpClient(handler);
        var adapter = new ComposioConnectorProvider(http, Options.Create(Settings()));
        var error = await Assert.ThrowsAsync<ConnectorProviderException>(() => adapter.RequestAsync("ca_test123",
            "https://api.github.com/user", method, null, default));
        Assert.Equal(retryable, error.RetryableRead); Assert.Equal(!retryable, error.OutcomeUnknown);
        Assert.DoesNotContain("unsafe", error.ToString()); Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("GET", true)]
    [InlineData("POST", false)]
    public async Task Adapter_internal_body_timeout_is_a_read_hint_not_replay(string method, bool retryable)
    {
        using var body = new StalledBody();
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) });
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(50) };
        var adapter = new ComposioConnectorProvider(http, Options.Create(Settings()));
        var error = await Assert.ThrowsAsync<ConnectorProviderException>(() => adapter.RequestAsync("ca_test123",
            "https://api.github.com/user", method, null, default).WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(retryable, error.RetryableRead); Assert.Equal(!retryable, error.OutcomeUnknown);
        Assert.True(body.ReadCancelled); Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Adapter_caller_cancelled_read_propagates_cancellation_without_replay_hint()
    {
        using var body = new StalledBody();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) });
        using var http = new HttpClient(handler);
        var adapter = new ComposioConnectorProvider(http, Options.Create(Settings()));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.RequestAsync("ca_test123",
            "https://api.github.com/user", "GET", null, cancellation.Token).WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("1", 1)]
    [InlineData("60", 60)]
    [InlineData("not-a-duration", -1)]
    public async Task Adapter_retains_only_typed_wait_not_raw_headers(string header, int seconds)
    {
        foreach (var proxyStatus in new[] { false, true })
        {
            var handler = new Handler(_ =>
            {
                var response = new HttpResponseMessage(proxyStatus ? HttpStatusCode.OK : HttpStatusCode.TooManyRequests)
                    { Content = new StringContent(proxyStatus
                        ? JsonSerializer.Serialize(new { status = 429, headers = new Dictionary<string, string> { ["retry-after"] = header }, data = new { } }) : "ignored") };
                if (!proxyStatus) response.Headers.TryAddWithoutValidation("Retry-After", header);
                return response;
            });
            using var http = new HttpClient(handler);
            var adapter = new ComposioConnectorProvider(http, Options.Create(Settings()));
            var error = await Assert.ThrowsAsync<ConnectorProviderException>(() => adapter.RequestAsync("ca_test123",
                "https://api.github.com/user", "GET", null, default));
            Assert.Equal(seconds < 0 ? TimeSpan.MaxValue : TimeSpan.FromSeconds(seconds), error.RetryAfter);
            Assert.True(error.RetryableRead); Assert.DoesNotContain(header, error.Message);
            Assert.Equal(1, handler.Calls);
        }
    }

    [Theory]
    [InlineData("{\"Retry-After\":\"0\",\"retry-after\":\"60\"}")]
    [InlineData("{\"Retry-After\":[\"0\",\"60\"]}")]
    [InlineData("{\"Retry-After\":null}")]
    [InlineData("{\"Retry-After\":-1}")]
    [InlineData("[]")]
    public async Task Adapter_ambiguous_proxy_wait_never_permits_early_retry(string headers)
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"status\":429,\"headers\":" + headers + ",\"data\":{}}") });
        using var http = new HttpClient(handler);
        var adapter = new ComposioConnectorProvider(http, Options.Create(Settings()));
        var error = await Assert.ThrowsAsync<ConnectorProviderException>(() => adapter.RequestAsync("ca_test123",
            "https://api.github.com/user", "GET", null, default));
        Assert.Equal(TimeSpan.MaxValue, error.RetryAfter); Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Adapter_multiple_outer_waits_never_permit_early_retry()
    {
        var handler = new Handler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.TryAddWithoutValidation("Retry-After", new[] { "0", "60" });
            return response;
        });
        using var http = new HttpClient(handler);
        var adapter = new ComposioConnectorProvider(http, Options.Create(Settings()));
        var error = await Assert.ThrowsAsync<ConnectorProviderException>(() => adapter.RequestAsync("ca_test123",
            "https://api.github.com/user", "GET", null, default));
        Assert.Equal(TimeSpan.MaxValue, error.RetryAfter); Assert.Equal(1, handler.Calls);
    }
}
