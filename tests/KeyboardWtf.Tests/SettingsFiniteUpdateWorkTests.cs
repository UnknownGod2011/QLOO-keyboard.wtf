using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using KeyboardWtf.Services;
using Xunit;

namespace KeyboardWtf.Tests;

// Finite Settings HTTP ownership only. Real handlers run with null data/service
// owners, an uninitialized app and one injected in-memory gate. No singleton,
// Initialize/Start, profile, provider, microphone, native host or installer is
// used. Even included routes may publish child/native work before their HTTP
// task finishes; child ownership, excluded resources and full updater acceptance
// remain OPEN. In particular this does not prove native key-dialog teardown.
public sealed class SettingsFiniteUpdateWorkTests
{
    [Theory]
    [InlineData("/api/settings")]
    [InlineData("/api/settings/quick-action")]
    [InlineData("/api/settings/large-saved-setup/dismiss")]
    [InlineData("/api/hotkeys/reset")]
    [InlineData("/api/personal-ai/action")]
    [InlineData("/api/paid/sign-in")]
    [InlineData("/api/paid/retry-sign-in")]
    [InlineData("/api/paid/refresh")]
    [InlineData("/api/paid/revoke-device")]
    [InlineData("/api/paid/cancel-subscription")]
    [InlineData("/api/paid/manage")]
    [InlineData("/api/paid/billing")]
    [InlineData("/api/intent-memory/delete")]
    [InlineData("/api/intent-memory/remember")]
    [InlineData("/api/intent-memory/clear")]
    [InlineData("/api/jarvis-history/clear")]
    [InlineData("/api/learned-mappings/delete")]
    [InlineData("/api/learned-mappings/clear")]
    [InlineData("/api/jarvis-workflow/delete")]
    [InlineData("/api/download-models")]
    [InlineData("/api/tts/install")]
    [InlineData("/api/test-claude")]
    [InlineData("/api/test-ai")]
    [InlineData("/api/camera-test")]
    [InlineData("/api/screen-guidance-test")]
    [InlineData("/api/browser-helper/connect")]
    [InlineData("/api/browser-helper/reconnect")]
    [InlineData("/api/open-voice-notes")]
    [InlineData("/api/pick-voice-notes-folder")]
    [InlineData("/api/camera-open")]
    public void Exact_finite_post_route_requires_admission(string path) =>
        Assert.True(RequiresWork("POST", path));

    [Theory]
    [InlineData("/api/personal-ai/state")]
    [InlineData("/api/preview-filename")]
    [InlineData("/api/browser-helper/test")]
    [InlineData("/api/browser-automation/consent")]
    [InlineData("/api/browser-automation/control")]
    [InlineData("/api/paid/sign-out")]
    [InlineData("/api/paid/sign-out-all")]
    [InlineData("/api/paid/switch-account")]
    [InlineData("/api/paid/cancel-sign-in")]
    [InlineData("/api/paid/unknown-fixture")]
    [InlineData("/api/paid/refresh/")]
    [InlineData("/API/paid/refresh")]
    [InlineData("/api/mic-test")]
    [InlineData("/api/mic-test-start")]
    [InlineData("/api/mic-test-stop")]
    [InlineData("/api/tts/test")]
    [InlineData("/api/jarvis/stop")]
    [InlineData("/api/jarvis/text-turn")]
    [InlineData("/api/private-audit/recording")]
    [InlineData("/api/updates/check")]
    [InlineData("/api/updates/install")]
    [InlineData("/api/app/restart")]
    [InlineData("/api/app/uninstall")]
    [InlineData("/api/exit")]
    [InlineData("/api/saved-state/events")]
    [InlineData("/api/unknown-fixture")]
    [InlineData("/api/settings/")]
    [InlineData("/API/settings")]
    [InlineData("/api/personal-ai/action-other")]
    public void Passive_control_unknown_and_not_yet_owned_post_routes_are_explicitly_excluded(string path) =>
        Assert.False(RequiresWork("POST", path));

    [Theory]
    [InlineData("GET", "/api/settings")]
    [InlineData("GET", "/api/saved-state/events")]
    [InlineData("GET", "/api/browser-automation/status")]
    [InlineData("GET", "/api/mic-level")]
    [InlineData("GET", "/api/updates/check")]
    [InlineData("PUT", "/api/settings")]
    [InlineData("PATCH", "/api/settings")]
    [InlineData("DELETE", "/api/settings")]
    [InlineData("HEAD", "/api/settings")]
    [InlineData("post", "/api/settings")]
    [InlineData(null, "/api/settings")]
    [InlineData("POST", null)]
    public void Classifier_matches_only_exact_existing_post_dispatch(string method, string path) =>
        Assert.False(RequiresWork(method, path));

    [Theory]
    [InlineData("/api/paid/sign-in")]
    [InlineData("/api/paid/retry-sign-in")]
    [InlineData("/api/paid/refresh")]
    [InlineData("/api/paid/revoke-device")]
    [InlineData("/api/paid/cancel-subscription")]
    [InlineData("/api/paid/manage")]
    [InlineData("/api/paid/billing")]
    public async Task Finite_paid_route_refuses_before_null_account_handler_when_frozen_or_committed(string path)
    {
        // Require the actual classifier BEFORE isolated loopback resources.
        // Null account prevents any auth, browser, provider or payment action.
        Assert.True(RequiresWork("POST", path));
        foreach (var commit in new[] { false, true })
        {
            await using var fixture = Fixture.Create();
            using var frozen = fixture.Freeze();
            if (commit) frozen.Commit();
            var response = await fixture.SendAsync("POST", path, authorized: true);
            Assert.Equal(HttpStatusCode.Conflict, response.Status);
            using var json = JsonDocument.Parse(response.Body);
            Assert.Equal("update_installing", json.RootElement.GetProperty("code").GetString());
            AssertFraming(response);
        }
    }

    [Theory]
    [InlineData("/api/paid/sign-in")]
    [InlineData("/api/paid/retry-sign-in")]
    [InlineData("/api/paid/refresh")]
    [InlineData("/api/paid/revoke-device")]
    [InlineData("/api/paid/cancel-subscription")]
    [InlineData("/api/paid/manage")]
    [InlineData("/api/paid/billing")]
    public async Task Finite_paid_handler_error_response_unwinds_and_null_app_legacy_is_unchanged(string path)
    {
        Assert.True(RequiresWork("POST", path));
        foreach (var withApp in new[] { true, false })
        {
            await using var fixture = Fixture.Create(withApp: withApp);
            using var frozen = withApp ? null : fixture.Freeze();
            var response = await fixture.SendAsync("POST", path, authorized: true);
            Assert.Equal(HttpStatusCode.BadRequest, response.Status);
            using var json = JsonDocument.Parse(response.Body);
            Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("Paid account is not enabled for this build.", json.RootElement.GetProperty("error").GetString());
            Assert.DoesNotContain("update_installing", response.Body);
            AssertFraming(response);
            if (withApp) fixture.AssertIdle();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Authorized_finite_handler_is_rejected_before_its_effect_when_frozen_or_committed(bool commit)
    {
        await using var fixture = Fixture.Create();
        using var frozen = fixture.Freeze();
        if (commit) frozen.Commit();

        var response = await fixture.SendAsync("POST", "/api/intent-memory/clear", authorized: true);
        Assert.Equal(HttpStatusCode.Conflict, response.Status);
        using var json = JsonDocument.Parse(response.Body);
        Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("update_installing", json.RootElement.GetProperty("code").GetString());
        Assert.Equal("An update is preparing installation. Please wait.", json.RootElement.GetProperty("error").GetString());
        AssertFraming(response);
    }

    [Theory]
    [InlineData("token")]
    [InlineData("origin")]
    [InlineData("content-type")]
    public async Task Existing_authorization_and_body_validation_run_before_frozen_admission(string failure)
    {
        await using var fixture = Fixture.Create();
        using var frozen = fixture.Freeze();
        var headers = failure == "origin"
            ? new Dictionary<string, string> { ["Origin"] = "https://untrusted-fixture.invalid" }
            : null;
        var response = await fixture.SendAsync("POST", "/api/intent-memory/clear",
            authorized: failure != "token", extraHeaders: headers,
            contentType: failure == "content-type" ? "text/plain" : "application/json");

        Assert.Equal(failure == "content-type" ? HttpStatusCode.UnsupportedMediaType : HttpStatusCode.Forbidden,
            response.Status);
        Assert.DoesNotContain("update_installing", response.Body);
        using var json = JsonDocument.Parse(response.Body);
        Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
        if (failure == "token")
            Assert.Equal("settings_session_expired", json.RootElement.GetProperty("code").GetString());
        AssertFraming(response);
    }

    [Fact]
    public async Task Locality_validation_still_precedes_frozen_admission()
    {
        await using var fixture = Fixture.Create(mismatchPort: true);
        using var frozen = fixture.Freeze();
        var response = await fixture.SendAsync("POST", "/api/intent-memory/clear", authorized: true);
        Assert.Equal(HttpStatusCode.Forbidden, response.Status);
        Assert.Empty(response.Body);
        AssertFraming(response);
    }

    [Theory]
    [InlineData("POST", "/api/unknown-fixture", HttpStatusCode.NotFound)]
    [InlineData("POST", "/api/browser-helper/test", HttpStatusCode.OK)]
    [InlineData("POST", "/api/jarvis/stop", HttpStatusCode.OK)]
    [InlineData("POST", "/api/mic-test-stop", HttpStatusCode.OK)]
    [InlineData("GET", "/api/mic-level", HttpStatusCode.OK)]
    public async Task Passive_unknown_and_safe_stop_dispatch_remain_reachable_while_frozen(
        string method, string path, HttpStatusCode status)
    {
        await using var fixture = Fixture.Create();
        using var frozen = fixture.Freeze();
        var response = await fixture.SendAsync(method, path, authorized: true);
        Assert.Equal(status, response.Status);
        Assert.DoesNotContain("update_installing", response.Body);
        AssertFraming(response);
    }

    [Fact]
    public async Task Null_app_legacy_fixture_keeps_existing_dispatch_without_using_the_synthetic_gate()
    {
        await using var fixture = Fixture.Create(withApp: false);
        using var frozen = fixture.Freeze();
        var response = await fixture.SendAsync("POST", "/api/intent-memory/clear", authorized: true);
        Assert.Equal(HttpStatusCode.OK, response.Status);
        using var json = JsonDocument.Parse(response.Body);
        Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
    }

    [Theory]
    [InlineData("/api/intent-memory/delete", false)]
    [InlineData("/api/intent-memory/delete", true)]
    [InlineData("/api/learned-mappings/delete", false)]
    public async Task Finite_request_owns_real_incomplete_body_until_handler_and_response_unwind(
        string path, bool malformed)
    {
        await using var fixture = Fixture.Create();
        using var body = new HeldBody("{\"id\":", malformed ? "}" : "\"synthetic-fixture-id\"}");
        var started = Signal();
        var request = fixture.SendContentAsync(path, body, authorized: true, handlerEntered: started);
        try
        {
            await body.PrefixSent.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(request.IsCompleted);
            fixture.AssertBusy();
        }
        finally
        {
            body.Release();
            // Even a failing ownership assertion must let the real handler
            // unwind before fixture disposal. Do not mask that assertion; the
            // normal path below still asserts any request failure or timeout.
            try { await request.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch { }
        }

        var response = await request.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.OK, response.Status);
        using var json = JsonDocument.Parse(response.Body);
        var expectedOk = !malformed && path == "/api/intent-memory/delete";
        Assert.Equal(expectedOk, json.RootElement.GetProperty("ok").GetBoolean());
        fixture.AssertIdle();
    }

    [Fact]
    public async Task Outer_handler_failure_releases_the_admitted_request()
    {
        await using var fixture = Fixture.Create();
        // Null Settings owner intentionally throws before any data mutation.
        var response = await fixture.SendAsync("POST", "/api/settings/large-saved-setup/dismiss", authorized: true);
        Assert.Equal(HttpStatusCode.InternalServerError, response.Status);
        fixture.AssertIdle();
    }

    private static bool RequiresWork(string method, string path)
    {
        var classifier = typeof(WebSettingsService).GetMethod("RequiresFiniteSettingsUpdateWork",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.True(classifier is not null, "Finite Settings update admission classifier has not been implemented.");
        return (bool)Invoke(null, classifier, new object[] { method, path });
    }

    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void AssertFraming(WireResponse response)
    {
        Assert.Equal("frame-ancestors 'none'", Assert.Single(response.Headers["Content-Security-Policy"]));
        Assert.Equal("DENY", Assert.Single(response.Headers["X-Frame-Options"]));
    }

    private sealed record WireResponse(HttpStatusCode Status, string Body, IReadOnlyDictionary<string, string[]> Headers);

    private sealed class HeldBody : HttpContent
    {
        private readonly byte[] _prefix;
        private readonly byte[] _suffix;
        private readonly TaskCompletionSource<bool> _release = Signal();
        internal TaskCompletionSource<bool> PrefixSent { get; } = Signal();
        internal HeldBody(string prefix, string suffix)
        {
            _prefix = Encoding.UTF8.GetBytes(prefix);
            _suffix = Encoding.UTF8.GetBytes(suffix);
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        }
        protected override bool TryComputeLength(out long length)
        {
            length = _prefix.Length + _suffix.Length;
            return true;
        }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext context)
        {
            await stream.WriteAsync(_prefix);
            await stream.FlushAsync();
            PrefixSent.TrySetResult(true);
            await _release.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await stream.WriteAsync(_suffix);
        }
        internal void Release() => _release.TrySetResult(true);
        protected override void Dispose(bool disposing)
        {
            if (disposing) Release();
            base.Dispose(disposing);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private static readonly MethodInfo Handler = typeof(WebSettingsService).GetMethod("HandleRequest",
            BindingFlags.Instance | BindingFlags.NonPublic);
        private readonly WebSettingsService _service;
        private readonly HttpListener _listener;
        private readonly HttpClient _client;
        private readonly object _gate;
        private Fixture(WebSettingsService service, HttpListener listener, Uri address, object gate)
        {
            _service = service;
            _listener = listener;
            _gate = gate;
            _client = new HttpClient(new HttpClientHandler { UseProxy = false })
            {
                BaseAddress = address,
                Timeout = Timeout.InfiniteTimeSpan,
            };
        }

        internal static Fixture Create(bool withApp = true, bool mismatchPort = false)
        {
            var service = (WebSettingsService)Activator.CreateInstance(typeof(WebSettingsService), nonPublic: true);
            Assert.NotNull(service);
            var property = typeof(KeyboardWtfApp).GetProperty("UpdateWorkAdmission", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(property);
            var gate = Activator.CreateInstance(property.PropertyType, nonPublic: true);
            Assert.NotNull(gate);
            if (withApp)
            {
                var app = (KeyboardWtfApp)RuntimeHelpers.GetUninitializedObject(typeof(KeyboardWtfApp));
                RequiredField(typeof(KeyboardWtfApp), "<UpdateWorkAdmission>k__BackingField").SetValue(app, gate);
                RequiredField(typeof(WebSettingsService), "_app").SetValue(service, app);
            }
            try
            {
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
                        typeof(WebSettingsService).GetProperty(nameof(WebSettingsService.Port)).SetValue(service,
                            mismatchPort ? (port == 65535 ? port - 1 : port + 1) : port);
                        return new Fixture(service, listener, address, gate);
                    }
                    catch (HttpListenerException error) when (attempt < 2 && error.NativeErrorCode is 183 or 32)
                    {
                        listener.Close();
                    }
                    catch
                    {
                        listener.Close();
                        throw;
                    }
                }
                throw new InvalidOperationException("Could not bind an isolated loopback fixture.");
            }
            catch
            {
                service.Dispose();
                throw;
            }
        }

        internal Frozen Freeze()
        {
            Assert.True(TryFreeze(out var frozen));
            return frozen;
        }

        internal void AssertBusy()
        {
            var idle = TryFreeze(out var premature);
            premature?.Dispose();
            Assert.False(idle, "The real incomplete HTTP request was not held as finite work.");
        }

        internal void AssertIdle()
        {
            using var frozen = Freeze();
        }

        private bool TryFreeze(out Frozen frozen)
        {
            var arguments = new object[] { null };
            var method = _gate.GetType().GetMethod("TryFreeze", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);
            var accepted = (bool)Invoke(_gate, method, arguments);
            frozen = accepted ? new Frozen(arguments[0]) : null;
            return accepted;
        }

        internal Task<WireResponse> SendAsync(string method, string path, string body = "{}", bool authorized = false,
            IReadOnlyDictionary<string, string> extraHeaders = null, string contentType = "application/json") =>
            SendCoreAsync(method, path, method == "POST" ? new StringContent(body, Encoding.UTF8, contentType) : null,
                authorized, extraHeaders, null);

        internal Task<WireResponse> SendContentAsync(string path, HttpContent body, bool authorized,
            TaskCompletionSource<bool> handlerEntered) =>
            SendCoreAsync("POST", path, body, authorized, null, handlerEntered);

        private async Task<WireResponse> SendCoreAsync(string method, string path, HttpContent body, bool authorized,
            IReadOnlyDictionary<string, string> extraHeaders, TaskCompletionSource<bool> handlerEntered)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var handled = HandleNextAsync(timeout.Token, handlerEntered);
            using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = body };
            if (authorized)
                request.Headers.Add("X-Keyboard-Wtf-Settings-Action",
                    (string)RequiredField(typeof(WebSettingsService), "_settingsActionToken").GetValue(_service));
            if (extraHeaders is not null)
                foreach (var header in extraHeaders) request.Headers.Add(header.Key, header.Value);
            try
            {
                using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                var text = await response.Content.ReadAsStringAsync(timeout.Token);
                await handled.WaitAsync(timeout.Token);
                return new WireResponse(response.StatusCode, text, response.Headers.ToDictionary(
                    header => header.Key, header => header.Value.ToArray(), StringComparer.OrdinalIgnoreCase));
            }
            catch
            {
                timeout.Cancel();
                _listener.Stop();
                try { await handled.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
                throw;
            }
        }

        private async Task HandleNextAsync(CancellationToken token, TaskCompletionSource<bool> entered)
        {
            var context = await _listener.GetContextAsync().WaitAsync(token);
            Assert.NotNull(Handler);
            var handler = Assert.IsAssignableFrom<Task>(Invoke(_service, Handler, new object[] { context }));
            // Invoke returns only once the real handler has reached its first
            // asynchronous wait. Held-body tests keep the remaining bytes gated.
            entered?.TrySetResult(true);
            await handler.WaitAsync(token);
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

    private sealed class Frozen : IDisposable
    {
        private readonly object _value;
        private readonly IDisposable _dispose;
        internal Frozen(object value)
        {
            Assert.NotNull(value);
            _value = value;
            _dispose = Assert.IsAssignableFrom<IDisposable>(value);
        }
        internal void Commit()
        {
            var method = _value.GetType().GetMethod("Commit", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);
            Invoke(_value, method, Array.Empty<object>());
        }
        public void Dispose() => _dispose.Dispose();
    }

    private static FieldInfo RequiredField(Type type, string name)
    {
        var field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.True(field is not null, $"Missing fixture field {type.Name}.{name}.");
        return field;
    }

    private static object Invoke(object target, MethodInfo method, object[] arguments)
    {
        try { return method.Invoke(target, arguments); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }
}
