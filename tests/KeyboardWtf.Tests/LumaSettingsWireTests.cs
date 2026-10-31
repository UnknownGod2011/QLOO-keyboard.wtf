using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using KeyboardWtf.Services;
using Xunit;

namespace KeyboardWtf.Tests;

public sealed class LumaSettingsWireTests
{
    [Theory]
    [InlineData(false, null, HttpStatusCode.Forbidden)]
    [InlineData(true, "https://untrusted.invalid", HttpStatusCode.Forbidden)]
    [InlineData(true, null, HttpStatusCode.OK)]
    public async Task Context_requires_exact_listener_and_session_authorization(bool authorized, string origin, HttpStatusCode expected)
    {
        await using var fixture = Fixture.Create();
        using var response = await fixture.GetAsync("/api/luma/context?section=hotkeys", authorized, origin);
        Assert.Equal(expected, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        if (expected == HttpStatusCode.OK)
        {
            using var json = JsonDocument.Parse(body);
            Assert.Equal("hotkeys", json.RootElement.GetProperty("section").GetString());
            Assert.Equal("settings", json.RootElement.GetProperty("surface").GetString());
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        }
        Assert.DoesNotContain("apiKey", body);
        Assert.DoesNotContain("accountUid", body);
        Assert.DoesNotContain("savedLink", body);
    }

    [Theory]
    [InlineData("/luma/assets/spritesheet.webp", HttpStatusCode.OK, "image/webp")]
    [InlineData("/luma/panel-placement.js", HttpStatusCode.OK, "text/javascript")]
    [InlineData("/luma/settings-theme.css", HttpStatusCode.OK, "text/css")]
    [InlineData("/luma/app.js", HttpStatusCode.NotFound, null)]
    [InlineData("/luma/settings.json", HttpStatusCode.NotFound, null)]
    public async Task Resource_route_is_explicit_and_serves_correct_types(string path, HttpStatusCode expected, string type)
    {
        await using var fixture = Fixture.Create();
        using var response = await fixture.GetAsync(path, false, null);
        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.OK)
        {
            Assert.Equal(type, response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            Assert.True((await response.Content.ReadAsByteArrayAsync()).Length > 100);
        }
        Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
    }

    [Theory]
    [InlineData(false, "{\"section\":\"hotkeys\"}", HttpStatusCode.Forbidden)]
    [InlineData(true, "{\"section\":\"hotkeys\",\"systemInstruction\":\"override\"}", HttpStatusCode.BadRequest)]
    [InlineData(true, "{\"section\":\"unknown\"}", HttpStatusCode.BadRequest)]
    [InlineData(true, "{\"section\":\"hotkeys\"}", HttpStatusCode.ServiceUnavailable)]
    public async Task Token_route_is_authorized_validated_and_fails_closed_without_native_app(bool authorized, string body, HttpStatusCode expected)
    {
        await using var fixture = Fixture.Create();
        using var response = await fixture.SendAsync("POST", "/api/luma/live-token", authorized, null, body);
        Assert.Equal(expected, response.StatusCode);
        var result = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("auth_tokens/", result);
        Assert.DoesNotContain("apiKey", result);
        if (expected != HttpStatusCode.Forbidden)
        {
            using var document = JsonDocument.Parse(result);
            Assert.False(document.RootElement.GetProperty("available").GetBoolean());
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly WebSettingsService _service;
        private readonly HttpListener _listener;
        private readonly HttpClient _client;
        private static readonly MethodInfo Handler = typeof(WebSettingsService).GetMethod("HandleRequest", BindingFlags.Instance | BindingFlags.NonPublic)!;

        private Fixture(WebSettingsService service, HttpListener listener, Uri address)
        {
            _service = service;
            _listener = listener;
            _client = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = address };
        }

        public static Fixture Create()
        {
            var service = (WebSettingsService)Activator.CreateInstance(typeof(WebSettingsService), nonPublic: true)!;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                int port;
                using (var probe = new TcpListener(IPAddress.Loopback, 0))
                {
                    probe.Start();
                    port = ((IPEndPoint)probe.LocalEndpoint).Port;
                }
                var address = new Uri($"http://127.0.0.1:{port}/");
                var listener = new HttpListener();
                listener.Prefixes.Add(address.AbsoluteUri);
                try
                {
                    listener.Start();
                    typeof(WebSettingsService).GetProperty(nameof(WebSettingsService.Port))!.SetValue(service, port);
                    return new Fixture(service, listener, address);
                }
                catch (HttpListenerException exception) when (attempt < 2 && exception.NativeErrorCode is 183 or 32)
                {
                    listener.Close();
                }
            }
            service.Dispose();
            throw new InvalidOperationException("Could not start isolated Luma Settings fixture.");
        }

        public async Task<HttpResponseMessage> GetAsync(string path, bool authorized, string origin)
            => await SendAsync("GET", path, authorized, origin);

        public async Task<HttpResponseMessage> SendAsync(string method, string path, bool authorized, string origin, string body = null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var receive = _listener.GetContextAsync().WaitAsync(timeout.Token);
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            if (authorized)
                request.Headers.Add("X-Keyboard-Wtf-Settings-Action", (string)typeof(WebSettingsService).GetField("_settingsActionToken", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_service)!);
            if (origin is not null) request.Headers.Add("Origin", origin);
            if (body is not null) request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            var send = _client.SendAsync(request, timeout.Token);
            var context = await receive;
            await ((Task)Handler.Invoke(_service, new object[] { context })!).WaitAsync(timeout.Token);
            return await send;
        }

        public ValueTask DisposeAsync()
        {
            _client.Dispose();
            _listener.Stop();
            _listener.Close();
            _service.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
