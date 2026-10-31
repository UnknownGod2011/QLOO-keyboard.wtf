using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KeyboardWtf.Staging.Api.Connectors;
using Microsoft.Extensions.Options;
using Xunit;

namespace KeyboardWtf.Staging.Api.Tests;

public sealed class ConnectorFormPrivacyTests
{
    [Fact]
    public async Task Blank_private_form_requests_unpublished_at_the_initial_create_dispatch()
    {
        var calls = 0;
        using var http = new HttpClient(new ProxyHandler(proxy =>
        {
            calls++;
            Assert.Equal("POST", proxy.GetProperty("method").GetString());
            Assert.Equal("https://forms.googleapis.com/v1/forms?unpublished=true", proxy.GetProperty("endpoint").GetString());
            var body = proxy.GetProperty("body");
            Assert.Single(body.EnumerateObject());
            Assert.Equal("Private title", body.GetProperty("info").GetProperty("title").GetString());
            return new { formId = "exactForm" };
        }));
        var adapter = new ComposioConnectorProvider(http, Options.Create(new ConnectorOptions { Enabled = true, ApiKey = "unit-test-only-key" }));
        var plan = ConnectorOperations.Plan("forms_create", Args(new { title = "Private title" }), ConnectorIds.New());
        await adapter.RequestAsync("ca_test123", plan.Endpoint, plan.Method, plan.Body, default);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("private", true, 0)]
    [InlineData("published", true, 1)]
    [InlineData("still_published", false, 1)]
    [InlineData("wrong_id", false, 0)]
    [InlineData("missing_state", false, 0)]
    [InlineData("malformed", false, 0)]
    [InlineData("wrong_title", false, 0)]
    [InlineData("not_blank", false, 0)]
    public async Task Create_verifies_the_exact_acknowledged_form_is_unpublished(string scenario, bool verified, int expectedPosts)
    {
        var posts = 0; var reads = 0; var authorityChecks = 0;
        using var http = new HttpClient(new ProxyHandler(proxy =>
        {
            var endpoint = proxy.GetProperty("endpoint").GetString();
            if (proxy.GetProperty("method").GetString() == "POST")
            {
                posts++;
                Assert.Equal("https://forms.googleapis.com/v1/forms/exactForm:setPublishSettings", endpoint);
                var publication = proxy.GetProperty("body").GetProperty("publishSettings").GetProperty("publishState");
                Assert.False(publication.GetProperty("isPublished").GetBoolean());
                Assert.False(publication.GetProperty("isAcceptingResponses").GetBoolean());
                return new { };
            }
            reads++;
            Assert.Equal("https://forms.googleapis.com/v1/forms/exactForm", endpoint);
            var form = new Dictionary<string, object> { ["formId"] = scenario == "wrong_id" ? "unrelated" : "exactForm",
                ["info"] = new { title = scenario == "wrong_title" ? "unrelated title" : "test" } };
            if (scenario == "not_blank") form["items"] = new[] { new { title = "Existing question" } };
            if (scenario != "missing_state")
                form["publishSettings"] = new { publishState = scenario == "malformed" ? (object)new { isPublished = "false" }
                    : scenario is "published" or "still_published" && (reads == 1 || scenario == "still_published")
                        ? new { isPublished = true, isAcceptingResponses = true } : new { } };
            return form;
        }));
        var adapter = new ComposioConnectorProvider(http, Options.Create(new ConnectorOptions { Enabled = true, ApiKey = "unit-test-only-key" }));
        var plan = ConnectorOperations.Plan("forms_create", Args(new { title = "test" }), ConnectorIds.New());
        var result = await ConnectorOperations.VerifyWriteAsync(adapter, "ca_test123", "forms_create", ConnectorIds.New(), plan,
            JsonSerializer.SerializeToElement(new { formId = "exactForm" }), default, _ => { authorityChecks++; return Task.CompletedTask; });
        Assert.Equal(verified, result.HasValue); Assert.Equal(expectedPosts, posts); Assert.Equal(expectedPosts, authorityChecks);
        Assert.Equal(expectedPosts + 1, reads);
    }

    [Fact]
    public async Task Lost_create_ack_does_not_create_or_guess_a_resource()
    {
        var calls = 0;
        using var http = new HttpClient(new ProxyHandler(_ => { calls++; return new { }; }));
        var adapter = new ComposioConnectorProvider(http, Options.Create(new ConnectorOptions { Enabled = true, ApiKey = "unit-test-only-key" }));
        var result = await ConnectorOperations.VerifyWriteAsync(adapter, "ca_test123", "forms_create", ConnectorIds.New(),
            ConnectorOperations.Plan("forms_create", Args(new { title = "test" }), ConnectorIds.New()), null, default);
        Assert.Null(result); Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ended_authority_or_lost_unpublish_ack_never_repeats_a_write(bool lostAck)
    {
        var posts = 0;
        using var http = new HttpClient(new ProxyHandler(proxy =>
        {
            if (proxy.GetProperty("method").GetString() == "POST") { posts++; throw new HttpRequestException("simulated lost acknowledgment"); }
            return new { formId = "exactForm", info = new { title = "test" }, publishSettings = new { publishState = new { isPublished = true, isAcceptingResponses = true } } };
        }));
        var adapter = new ComposioConnectorProvider(http, Options.Create(new ConnectorOptions { Enabled = true, ApiKey = "unit-test-only-key" }));
        var run = () => ConnectorOperations.VerifyWriteAsync(adapter, "ca_test123", "forms_create", ConnectorIds.New(),
            ConnectorOperations.Plan("forms_create", Args(new { title = "test" }), ConnectorIds.New()), JsonSerializer.SerializeToElement(new { formId = "exactForm" }),
            default, _ => lostAck ? Task.CompletedTask : throw new OperationCanceledException());
        if (lostAck) { var error = await Assert.ThrowsAsync<ConnectorProviderException>(run); Assert.True(error.OutcomeUnknown); }
        else await Assert.ThrowsAsync<OperationCanceledException>(run);
        Assert.Equal(lostAck ? 1 : 0, posts);
    }

    private static Dictionary<string, JsonElement> Args(object value) => JsonSerializer.SerializeToElement(value).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    private sealed class ProxyHandler(Func<JsonElement, object> handle) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var proxy = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(ct));
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { status = 200, data = handle(proxy) }) };
        }
    }
}
