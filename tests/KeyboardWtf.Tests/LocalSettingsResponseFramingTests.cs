using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using KeyboardWtf.Services;
using KeyboardWtf.Models;
using KeyboardWtf.Services.Updates;
using Xunit;

namespace KeyboardWtf.Tests;

[Collection("Legacy secret persistence")]
public sealed class LocalSettingsResponseFramingTests
{
    [Fact]
    public async Task Popup_toggle_uses_authenticated_single_setting_http_and_truthfully_reports_disk_failure()
    {
        var root = Path.Combine(Path.GetTempPath(), "keyboard-popup-settings-wire-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "settings.json");
        var settings = new SettingsService(path);
        settings.Current.AssistantName = "Preserved assistant";
        settings.Current.Language = "preserved-language";
        settings.Save();
        try
        {
            await using var fixture = LoopbackSettingsFixture.Create(false, settings);
            var original = File.ReadAllBytes(path);
            var denied = await fixture.SendAsync("POST", "/api/settings/quick-action", "{\"showQuickActionButton\":false}");
            Assert.Equal(HttpStatusCode.Forbidden, denied.Status);
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.True(settings.Current.ShowQuickActionButton);
            using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var failed = await fixture.SendAsync("POST", "/api/settings/quick-action", "{\"showQuickActionButton\":false}", authorized: true);
                Assert.Equal(HttpStatusCode.InternalServerError, failed.Status);
                using var failure = JsonDocument.Parse(failed.Body);
                Assert.Equal("settings_storage_failed", failure.RootElement.GetProperty("code").GetString());
                Assert.True(settings.Current.ShowQuickActionButton);
                Assert.Equal(original, File.ReadAllBytes(path));
                Assert.False(File.Exists(path + ".tmp"));
            }
            foreach (var value in new[] { false, true })
            {
                var body = JsonSerializer.Serialize(new { showQuickActionButton = value });
                var saved = await fixture.SendAsync("POST", "/api/settings/quick-action", body, authorized: true);
                Assert.Equal(HttpStatusCode.OK, saved.Status);
                using var response = JsonDocument.Parse(saved.Body);
                Assert.True(response.RootElement.GetProperty("ok").GetBoolean());
                Assert.Equal(value, response.RootElement.GetProperty("showQuickActionButton").GetBoolean());
                var current = await fixture.SendAsync("GET", "/api/settings");
                using var snapshot = JsonDocument.Parse(current.Body);
                Assert.Equal(value, snapshot.RootElement.GetProperty("showQuickActionButton").GetBoolean());
                Assert.Equal("Preserved assistant", settings.Current.AssistantName);
                Assert.Equal("preserved-language", settings.Current.Language);
            }
        }
        finally { File.Delete(path + ".tmp"); File.Delete(path); Directory.Delete(root); }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"showQuickActionButton\":\"false\"}")]
    [InlineData("{\"showQuickActionButton\":false,\"assistantName\":\"Stale assistant\"}")]
    [InlineData("{\"showQuickActionButton\":false,\"showQuickActionButton\":true}")]
    [InlineData("[]")]
    [InlineData("{")]
    public async Task Popup_toggle_rejects_invalid_or_stale_multi_setting_payloads(string body)
    {
        var settings = new SettingsService(Path.Combine(Path.GetTempPath(), "unused-popup-setting-" + Guid.NewGuid().ToString("N"), "settings.json"));
        await using var fixture = LoopbackSettingsFixture.Create(false, settings);
        var response = await fixture.SendAsync("POST", "/api/settings/quick-action", body, authorized: true);
        Assert.Equal(HttpStatusCode.BadRequest, response.Status);
        Assert.True(settings.Current.ShowQuickActionButton);
        Assert.False(File.Exists(settings.SettingsPath));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Authorized_settings_save_failure_is_truthful_and_does_not_commit_or_activate_secrets(bool incomingSecret)
    {
        var testDirectory = Path.Combine(Path.GetTempPath(), "keyboard-wtf-settings-wire-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDirectory);
        var path = Path.Combine(testDirectory, "settings.json");
        const string original = "{\"AiProvider\":\"Claude\",\"GeminiApiKey\":\"legacy-fixture-key\"}";
        File.WriteAllText(path, original);
        var oldProvider = KeyboardWtfState.SelectedAiProvider;
        var oldKey = KeyboardWtfState.GeminiApiKey;
        var oldUseAi = KeyboardWtfState.UseAi;
        try
        {
            var settings = new SettingsService(path, _ => throw new InvalidOperationException("synthetic-secret-must-not-be-exposed"));
            settings.Current.AiProvider = AiProvider.Claude;
            settings.Current.GeminiApiKey = "legacy-fixture-key";
            KeyboardWtfState.SelectedAiProvider = AiProvider.Claude;
            KeyboardWtfState.GeminiApiKey = "legacy-runtime-key";
            await using var fixture = LoopbackSettingsFixture.Create(false, settings);
            var body = incomingSecret
                ? "{\"geminiApiKey\":\"new-fixture-key\",\"aiProvider\":\"Gemini\"}"
                : "{\"useAi\":false}";
            var response = await fixture.SendAsync("POST", "/api/settings", body, authorized: true);
            Assert.Equal(HttpStatusCode.InternalServerError, response.Status);
            using var json = JsonDocument.Parse(response.Body);
            Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("settings_storage_failed", json.RootElement.GetProperty("code").GetString());
            Assert.DoesNotContain("fixture-key", response.Body);
            Assert.DoesNotContain("synthetic-secret", response.Body);
            Assert.Equal(original, File.ReadAllText(path));
            Assert.False(File.Exists(path + ".tmp"));
            Assert.Equal("legacy-fixture-key", settings.Current.GeminiApiKey);
            Assert.Equal("legacy-runtime-key", KeyboardWtfState.GeminiApiKey);
            Assert.Equal(AiProvider.Claude, settings.Current.AiProvider);
            Assert.Equal(AiProvider.Claude, KeyboardWtfState.SelectedAiProvider);
            AssertSingleHeader(response, "Content-Security-Policy", "frame-ancestors 'none'");
        }
        finally
        {
            KeyboardWtfState.SelectedAiProvider = oldProvider;
            KeyboardWtfState.GeminiApiKey = oldKey;
            KeyboardWtfState.UseAi = oldUseAi;
            File.Delete(path + ".tmp");
            File.Delete(path);
            Directory.Delete(testDirectory);
        }
    }

    [Theory]
    [InlineData("GET", "/", false, HttpStatusCode.OK)]
    [InlineData("GET", "/api/mic-level", false, HttpStatusCode.OK)]
    [InlineData("POST", "/api/settings", false, HttpStatusCode.Forbidden)]
    [InlineData("GET", "/missing-framing-fixture", false, HttpStatusCode.NotFound)]
    [InlineData("GET", "/", true, HttpStatusCode.Forbidden)]
    public async Task Real_settings_responses_preserve_their_result_and_disallow_framing(
        string method,
        string path,
        bool mismatchServicePort,
        HttpStatusCode expectedStatus)
    {
        await using var fixture = LoopbackSettingsFixture.Create(mismatchServicePort);

        // Repeated requests must not accumulate duplicate framing header values.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var response = await fixture.SendAsync(method, path);
            Assert.Equal(expectedStatus, response.Status);
            AssertOriginalResponse(response, method, path, mismatchServicePort);
            AssertSingleHeader(response, "Content-Security-Policy", "frame-ancestors 'none'");
            AssertSingleHeader(response, "X-Frame-Options", "DENY");
        }
    }

    [Theory]
    [InlineData("unauthorized")]
    [InlineData("missing")]
    [InlineData("wrong")]
    [InlineData("expired")]
    [InlineData("replaced")]
    [InlineData("reused")]
    [InlineData("wrong-channel")]
    public async Task Actual_update_route_rejects_unapproved_artifacts_before_download(string variant)
    {
        var root = Path.Combine(Path.GetTempPath(), "kwt-update-consent-wire-" + Guid.NewGuid().ToString("N"));
        var settings = new SettingsService(Path.Combine(root, "unused-settings.json"));
        var handler = new CountingUpdateHandler();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var consent = new VerifiedUpdateConsent(() => now);
        var update = SyntheticUpdate();
        var token = consent.Issue(update);
        if (variant == "wrong") token = (token[0] == '0' ? "1" : "0") + token[1..];
        if (variant == "expired") now = now.AddMinutes(30);
        if (variant == "replaced") consent.Issue(update with { Sha256 = new string('b', 64) });
        if (variant == "reused") consent.Take(token, "stable");
        if (variant == "wrong-channel") settings.Current.UpdateChannel = "beta";

        await using var fixture = LoopbackSettingsFixture.Create(false, settings, service =>
        {
            ReplaceUpdateService(service, handler, root);
            typeof(WebSettingsService).GetField("_updateConsent", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(service, consent);
        });
        var response = await fixture.SendAsync("POST", "/api/updates/install", authorized: variant != "unauthorized",
            extraHeaders: variant == "missing" ? null : new Dictionary<string, string>
            {
                ["X-Keyboard-Wtf-Update-Consent"] = token,
            });

        Assert.Equal(variant == "unauthorized" ? HttpStatusCode.Forbidden : HttpStatusCode.Conflict, response.Status);
        using var json = JsonDocument.Parse(response.Body);
        Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Empty(handler.Requests);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task Actual_update_route_defers_unready_installation_without_consuming_consent_or_downloading()
    {
        var root = Path.Combine(Path.GetTempPath(), "kwt-update-consent-wire-" + Guid.NewGuid().ToString("N"));
        try
        {
            var handler = new CountingUpdateHandler();
            var consent = new VerifiedUpdateConsent();
            var update = SyntheticUpdate();
            var token = consent.Issue(update);
            await using var fixture = LoopbackSettingsFixture.Create(false, configure: service =>
            {
                ReplaceUpdateService(service, handler, root);
                typeof(WebSettingsService).GetField("_updateConsent", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(service, consent);
            });
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var response = await fixture.SendAsync("POST", "/api/updates/install", authorized: true,
                    extraHeaders: new Dictionary<string, string> { ["X-Keyboard-Wtf-Update-Consent"] = token });
                Assert.Equal(HttpStatusCode.Conflict, response.Status);
                using var json = JsonDocument.Parse(response.Body);
                Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
                Assert.False(json.RootElement.GetProperty("started").GetBoolean());
                Assert.Equal("update_install_not_ready", json.RootElement.GetProperty("code").GetString());
                Assert.Empty(handler.Requests);
                Assert.False(Directory.Exists(root));
            }
            Assert.Same(update, consent.Take(token, "stable"));
        }
        finally { RemoveUpdateFixture(root); }
    }

    [Fact]
    public async Task Downloader_uses_only_the_approved_tuple_and_refuses_publication_on_size_mismatch()
    {
        var root = Path.Combine(Path.GetTempPath(), "kwt-update-consent-wire-" + Guid.NewGuid().ToString("N"));
        try
        {
            var handler = new CountingUpdateHandler();
            var update = SyntheticUpdate();
            using var downloader = new SecureUpdateService(handler, publicUpdateEligibility: () => true, downloadRoot: root);
            var error = await Record.ExceptionAsync(() => downloader.DownloadAsync(update, CancellationToken.None));
            Assert.IsType<InvalidDataException>(error);
            Assert.Equal(update.PackageUri, Assert.Single(handler.Requests));
            // The intentionally one-byte body cannot satisfy the signed 1,024
            // bytes: neither executable publication nor launch can be reached.
            Assert.Empty(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Assert.StartsWith("kwt-update-consent-wire-", Path.GetFileName(root));
                Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(root));
                Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories),
                    path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0);
                Directory.Delete(root, true);
            }
        }
    }

    private static void RemoveUpdateFixture(string root)
    {
        if (!Directory.Exists(root)) return;
        Assert.StartsWith("kwt-update-consent-wire-", Path.GetFileName(root));
        Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(root));
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories),
            path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0);
        Directory.Delete(root, true);
    }

    private static VerifiedUpdate SyntheticUpdate() => new(
        new Version(9, 0, 0), new Version(1, 4, 17), "stable", DateTimeOffset.UtcNow.AddDays(-1),
        new Uri("https://github.com/UnknownGod2011/keyboard.wtf/releases/download/v9.0.0/approved-fixture.exe"),
        new string('a', 64), 1024, "Synthetic verified tuple", "synthetic-fixture-key");

    private static void ReplaceUpdateService(WebSettingsService service, HttpMessageHandler handler, string root)
    {
        var field = typeof(WebSettingsService).GetField("_secureUpdates", BindingFlags.Instance | BindingFlags.NonPublic)!;
        ((SecureUpdateService)field.GetValue(service)!).Dispose();
        field.SetValue(service, new SecureUpdateService(handler, publicUpdateEligibility: () => true, downloadRoot: root));
    }

    private sealed class CountingUpdateHandler : HttpMessageHandler
    {
        internal List<Uri> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[1]) });
        }
    }

    private static void AssertOriginalResponse(
        WireResponse response,
        string method,
        string path,
        bool mismatchServicePort)
    {
        if (mismatchServicePort || response.Status == HttpStatusCode.NotFound)
        {
            Assert.Empty(response.Body);
            return;
        }

        if (path == "/")
        {
            Assert.Equal("text/html", response.ContentType);
            Assert.Contains("<h1>Isolated Settings fixture</h1>", response.Body);
            Assert.Contains("onclick=\"window.fixtureClicked=true\"", response.Body);
            Assert.Contains("X-Keyboard-Wtf-Settings-Action", response.Body);
            Assert.Contains("settings_session_expired", response.Body);
            AssertSingleHeader(response, "Cache-Control", "no-store");
            return;
        }

        Assert.Equal("application/json", response.ContentType);
        using var document = JsonDocument.Parse(response.Body);
        var root = document.RootElement;
        Assert.False(root.GetProperty("ok").GetBoolean());
        if (method == "POST")
        {
            Assert.Equal("settings_session_expired", root.GetProperty("code").GetString());
            Assert.Equal(
                "This settings action is not authorized for the current app session.",
                root.GetProperty("error").GetString());
        }
        else
        {
            Assert.Equal(0, root.GetProperty("peak").GetInt32());
            Assert.Equal(-96.0, root.GetProperty("peakDb").GetDouble());
            Assert.False(root.GetProperty("detected").GetBoolean());
        }
    }

    private static void AssertSingleHeader(WireResponse response, string name, string expected)
    {
        Assert.True(response.Headers.TryGetValue(name, out var values), $"Missing HTTP header: {name}");
        Assert.Equal(expected, Assert.Single(values));
    }

    private sealed record WireResponse(
        HttpStatusCode Status,
        string ContentType,
        string Body,
        IReadOnlyDictionary<string, string[]> Headers);

    // This fixture does not start the application, use the singleton/Start/Initialize,
    // load a profile, inspect the registry, or dispatch any provider request. It uses
    // the real request handler with an independently owned loopback listener.
    private sealed class LoopbackSettingsFixture : IAsyncDisposable
    {
        private static readonly MethodInfo HandleRequest = typeof(WebSettingsService)
            .GetMethod("HandleRequest", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private readonly WebSettingsService _service;
        private readonly HttpListener _listener;
        private readonly HttpClient _client;

        private LoopbackSettingsFixture(
            WebSettingsService service,
            HttpListener listener,
            Uri address)
        {
            _service = service;
            _listener = listener;
            _client = new HttpClient(new HttpClientHandler { UseProxy = false })
            {
                BaseAddress = address,
                Timeout = Timeout.InfiniteTimeSpan,
            };
        }

        public static LoopbackSettingsFixture Create(bool mismatchServicePort, SettingsService settings = null,
            Action<WebSettingsService> configure = null)
        {
            var service = (WebSettingsService)Activator.CreateInstance(
                typeof(WebSettingsService), nonPublic: true)!;
            try
            {
                typeof(WebSettingsService).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(service, settings);
                configure?.Invoke(service);
                typeof(WebSettingsService).GetField(
                    "_settingsHtml", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(
                    service,
                    "<!doctype html><html><head><title>Fixture</title></head><body>" +
                    "<h1>Isolated Settings fixture</h1>" +
                    "<button onclick=\"window.fixtureClicked=true\">Fixture button</button>" +
                    "</body></html>");

                // Port 0 chooses an isolated port. The release/claim race between TCP
                // allocation and HTTP.sys binding is retried only when already in use.
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
                        var servicePort = mismatchServicePort
                            ? (port == 65535 ? port - 1 : port + 1)
                            : port;
                        typeof(WebSettingsService).GetProperty(nameof(WebSettingsService.Port))!
                            .SetValue(service, servicePort);
                        return new LoopbackSettingsFixture(service, listener, address);
                    }
                    catch (HttpListenerException exception) when (
                        attempt < 2 && exception.NativeErrorCode is 183 or 32)
                    {
                        listener.Close();
                    }
                    catch
                    {
                        listener.Close();
                        throw;
                    }
                }
                throw new InvalidOperationException("Could not claim an isolated loopback Settings port.");
            }
            catch
            {
                service.Dispose();
                throw;
            }
        }

        public async Task<WireResponse> SendAsync(string method, string path, string body = "{}", bool authorized = false,
            IReadOnlyDictionary<string, string> extraHeaders = null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var handled = HandleNextRequestAsync(timeout.Token);
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            if (authorized)
                request.Headers.Add("X-Keyboard-Wtf-Settings-Action", (string)typeof(WebSettingsService)
                    .GetField("_settingsActionToken", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_service));
            if (extraHeaders is not null)
                foreach (var header in extraHeaders)
                    request.Headers.Add(header.Key, header.Value);
            if (method == "POST")
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            try
            {
                using var response = await _client.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                var responseBody = await response.Content.ReadAsStringAsync(timeout.Token);
                await handled.WaitAsync(timeout.Token);
                return new WireResponse(
                    response.StatusCode,
                    response.Content.Headers.ContentType?.MediaType,
                    responseBody,
                    response.Headers.ToDictionary(
                        header => header.Key,
                        header => header.Value.ToArray(),
                        StringComparer.OrdinalIgnoreCase));
            }
            catch
            {
                // Unblock GetContextAsync as well as cancelling the client request.
                timeout.Cancel();
                _listener.Stop();
                try { await handled.WaitAsync(TimeSpan.FromSeconds(1)); }
                catch { }
                throw;
            }
        }

        private async Task HandleNextRequestAsync(CancellationToken cancellationToken)
        {
            var context = await _listener.GetContextAsync().WaitAsync(cancellationToken);
            var handler = (Task)HandleRequest.Invoke(_service, new object[] { context })!;
            await handler.WaitAsync(cancellationToken);
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
