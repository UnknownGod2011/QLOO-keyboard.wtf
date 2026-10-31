namespace KeyboardWtf.Tests;

using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using KeyboardWtf.Services;
using Xunit;

// Actual HTTP handler and embedded page, without starting the app, loading a
// profile, registering hotkeys, capturing audio or requesting a cloud service.
public sealed class ConnectorSettingsHttpTests
{
    [Fact]
    public async Task Real_settings_page_contains_the_native_connector_section_and_request_guard()
    {
        await using var server = new Fixture();
        using var response = await server.Send("GET", "/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("id=\"settings-connectors-panel\"", html);
        Assert.Contains("/connectors/connectors-settings.js", html);
        Assert.Contains("X-Keyboard-Wtf-Settings-Action", html);
        Assert.Contains("target.pathname.startsWith('/api/connectors')", html);
        Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy"));
        Assert.Equal("no-store", response.Headers.CacheControl.ToString());
    }

    [Theory]
    [InlineData("connectors-panel.js", "text/javascript")]
    [InlineData("connectors-settings.js", "text/javascript")]
    [InlineData("connectors-panel.css", "text/css")]
    [InlineData("icons/gmail.svg", "image/svg+xml")]
    public async Task Actual_embedded_connector_assets_are_served_with_safe_mime_and_no_sniffing(string asset, string mime)
    {
        await using var server = new Fixture();
        using var response = await server.Send("GET", "/connectors/" + asset);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(mime, response.Content.Headers.ContentType.MediaType);
        Assert.Contains("nosniff", response.Headers.GetValues("X-Content-Type-Options"));
        Assert.Equal("no-store", response.Headers.CacheControl.ToString());
        Assert.NotEmpty(await response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("GET", "/api/connectors", "missing")]
    [InlineData("GET", "/api/connectors", "wrong")]
    [InlineData("GET", "/api/connectors", "foreign-referer")]
    [InlineData("GET", "/api/connectors", "cross-site")]
    [InlineData("POST", "/api/connectors/connect", "missing")]
    [InlineData("POST", "/api/connectors/connect", "foreign-origin")]
    [InlineData("DELETE", "/api/connectors/accounts/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "cross-site")]
    public async Task Connector_routes_reject_untrusted_settings_requests_before_account_access(string method, string path, string variant)
    {
        await using var server = new Fixture();
        using var response = await server.Send(method, path, variant);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("Account services", await response.Content.ReadAsStringAsync());
        Assert.Contains("DENY", response.Headers.GetValues("X-Frame-Options"));
    }

    [Fact]
    public async Task Authorized_route_reports_unavailable_account_service_without_connected_state_or_provider_payload()
    {
        await using var server = new Fixture();
        using var response = await server.Send("GET", "/api/connectors", "authorized");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Account services are unavailable.", document.RootElement.GetProperty("error").GetString());
        Assert.False(document.RootElement.TryGetProperty("accounts", out _));
        Assert.Equal("no-store", response.Headers.CacheControl.ToString());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly WebSettingsService _service;
        private readonly HttpListener _listener = new();
        private readonly HttpClient _client;
        private static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

        internal Fixture()
        {
            _service = (WebSettingsService)Activator.CreateInstance(typeof(WebSettingsService), nonPublic: true);
            var html = typeof(WebSettingsService).GetMethod("LoadSettingsHtml", PrivateInstance).Invoke(_service, null);
            typeof(WebSettingsService).GetField("_settingsHtml", PrivateInstance).SetValue(_service, html);
            using var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            typeof(WebSettingsService).GetProperty(nameof(WebSettingsService.Port)).SetValue(_service, port);
            var address = new Uri($"http://127.0.0.1:{port}/");
            _listener.Prefixes.Add(address.AbsoluteUri);
            _listener.Start();
            _client = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = address };
        }

        internal async Task<HttpResponseMessage> Send(string method, string path, string variant = "missing")
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var handled = Handle(timeout.Token);
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            if (method is "POST" or "DELETE") request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            if (variant != "missing")
                request.Headers.Add("X-Keyboard-Wtf-Settings-Action", variant == "wrong" ? "invalid"
                    : (string)typeof(WebSettingsService).GetField("_settingsActionToken", PrivateInstance).GetValue(_service));
            request.Headers.Referrer = variant == "foreign-referer" ? new Uri("https://untrusted.example/") : _client.BaseAddress;
            request.Headers.Add("Origin", variant == "foreign-origin" ? "https://untrusted.example" : _client.BaseAddress.GetLeftPart(UriPartial.Authority));
            request.Headers.Add("Sec-Fetch-Site", variant == "cross-site" ? "cross-site" : "same-origin");
            var response = await _client.SendAsync(request, timeout.Token);
            await handled.WaitAsync(timeout.Token);
            return response;
        }

        private async Task Handle(CancellationToken cancellation)
        {
            var context = await _listener.GetContextAsync().WaitAsync(cancellation);
            await ((Task)typeof(WebSettingsService).GetMethod("HandleRequest", PrivateInstance)
                .Invoke(_service, new object[] { context })).WaitAsync(cancellation);
        }

        public ValueTask DisposeAsync()
        {
            _client.Dispose(); _listener.Stop(); _listener.Close(); _service.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
