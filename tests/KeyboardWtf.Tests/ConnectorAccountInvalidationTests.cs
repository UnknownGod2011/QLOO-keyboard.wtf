namespace KeyboardWtf.Tests;

using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using KeyboardWtf.Services.PaidStaging;
using Xunit;

public sealed class ConnectorAccountInvalidationTests
{
    [Theory]
    [InlineData("DELETE", "v1/connectors/accounts/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "{\"state\":\"disconnected\"}", true)]
    [InlineData("POST", "v1/connectors/accounts/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/default", "{\"state\":\"saved\"}", false)]
    [InlineData("GET", "v1/connectors/", "{\"accounts\":[{\"accountId\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"state\":\"reconnect_required\"}]}", true)]
    [InlineData("GET", "v1/connectors/", "{\"accounts\":[{\"accountId\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"state\":\"connected\"}]}", false)]
    [InlineData("POST", "v1/connectors/resolve", "{\"choices\":[{\"accountId\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"state\":\"scope_required\"}]}", false)]
    public async Task Authenticated_observed_account_state_invalidates_without_extra_HTTP(string method, string path, string response, bool invalidated)
    {
        using var handler = new Handler(response);
        using var account = Create(handler);
        var events = new List<(string Owner, string Account)>();
        account.ConnectorAccountInvalidated += (owner, id) => events.Add((owner, id));
        await account.ConnectorRequestAsync(new HttpMethod(method), path);
        Assert.Equal(1, handler.Requests);
        if (invalidated) Assert.Equal(("synthetic-session", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), Assert.Single(events));
        else Assert.Empty(events);
    }

    [Fact]
    public async Task Failed_disconnect_does_not_invalidate_existing_context()
    {
        using var handler = new Handler("{\"title\":\"Temporary failure\"}", HttpStatusCode.ServiceUnavailable);
        using var account = Create(handler);
        var invalidated = false;
        account.ConnectorAccountInvalidated += (_, _) => invalidated = true;
        await Assert.ThrowsAsync<PaidApiException>(() => account.ConnectorRequestAsync(HttpMethod.Delete,
            "v1/connectors/accounts/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        Assert.False(invalidated); Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public void Receipt_from_old_owner_cannot_invalidate_current_account()
    {
        using var handler = new Handler("{}");
        using var account = Create(handler);
        var invalidated = false;
        account.ConnectorAccountInvalidated += (_, _) => invalidated = true;
        account.ObserveConnectorAccountState("old-session", HttpMethod.Delete,
            "v1/connectors/accounts/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", JsonSerializer.SerializeToElement(new { state = "disconnected" }));
        Assert.False(invalidated); Assert.Equal(0, handler.Requests);
    }

    private static PaidAccountService Create(HttpMessageHandler handler)
    {
        var service = new PaidAccountService(new PaidStagingConfiguration(true, false,
            new Uri("https://connector.example.test/"), new Uri("https://settings.example.test/")),
            new PaidSessionStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "unused.dat")), handler,
            _ => throw new InvalidOperationException("No browser is allowed in this transport fixture."));
        typeof(PaidAccountService).GetField("_tokens", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(service,
            new PaidSessionTokens("synthetic-access", DateTimeOffset.UtcNow.AddHours(2), "synthetic-refresh", DateTimeOffset.UtcNow.AddDays(1),
                "synthetic-session", "synthetic-device", "synthetic-installation"));
        return service;
    }

    private sealed class Handler(string response, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal("connector.example.test", request.RequestUri.Host);
            Requests++;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(response, Encoding.UTF8, "application/json") });
        }
    }
}
