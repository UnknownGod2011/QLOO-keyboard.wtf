using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using KeyboardWtf.Services;
using KeyboardWtf.Services.Ai;
using KeyboardWtf.Services.Ai.Backends;
using KeyboardWtf.Services.PaidStaging;
using Xunit;

namespace KeyboardWtf.Tests;

public sealed class PersonalBackendIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "personal-integration-" + Guid.NewGuid());
    private readonly PersonalApiVault _vault;
    private readonly PaidAccountService _account;
    private readonly AiBackendGateway _gateway;
    private readonly List<string> _managedPaths = new();
    private readonly List<AiDispatchEvidence> _evidence = new();
    private readonly List<string> _personalRequests = new();
    private int _liveConnections;
    private int _metadataRequests;
    private int _refusalStatus;
    private string _refusalContract;
    private long _credits;
    private bool _managedSuccess;

    public PersonalBackendIntegrationTests()
    {
        _vault = new(Path.Combine(_directory, "keys.bin"));
        var view = _vault.Add(0, "fake", "synthetic-integration-credential-0001", PersonalKeyValidation.AuthenticationValidated);
        _vault.SetPolicy(view.Revision, AiBackendPolicy.PersonalOnly);
        var store = new PaidSessionStore(Path.Combine(_directory, "session.dat"));
        store.Save(new PaidSessionTokens("fake-access", DateTimeOffset.UtcNow.AddMinutes(30), "fake-refresh",
            DateTimeOffset.UtcNow.AddDays(1), "fake-session", "fake-device", "fake-installation"));
        _account = new(new(true, false, new("https://managed.example.test/"), new("https://account.example.test/")),
            store, new Handler(request =>
            {
                _managedPaths.Add(request.RequestUri.AbsolutePath);
                if (request.RequestUri.AbsolutePath == "/v1/ai/generate" && _managedSuccess)
                    return Reply("{\"requestId\":\"managed-test\",\"text\":\"ok\",\"chargedCreditMicros\":10,\"remainingCreditMicros\":990,\"verified\":true}");
                if (request.RequestUri.AbsolutePath == "/v1/ai/generate" && _refusalStatus != 0)
                {
                    var refusal = new HttpResponseMessage((HttpStatusCode)_refusalStatus)
                        { Content = new StringContent("{\"detail\":\"No usable credits are available.\"}") };
                    if (_refusalContract is not null) refusal.Headers.Add("X-Keyboard-Wtf-AI-Refusal", _refusalContract);
                    return refusal;
                }
                Assert.Equal("/v1/account", request.RequestUri.AbsolutePath); // Any managed inference/reservation is forbidden.
                return Reply(JsonSerializer.Serialize(new { userId = "fake", status = "active", availableCreditMicros = _credits,
                    reservedCreditMicros = 0, cycleUsageCreditMicros = 37, devices = Array.Empty<object>(), recentUsage = Array.Empty<object>(), recentPurchases = Array.Empty<object>() }));
            }));
        _gateway = new(_vault, _account.AuthorizePersonalAsync,
            new(_vault, new Handler(request =>
            {
                Assert.Equal("generativelanguage.googleapis.com", request.RequestUri.Host);
                Assert.Empty(request.RequestUri.Query);
                Assert.False(request.Headers.Contains("Authorization"));
                Assert.Single(request.Headers.GetValues("x-goog-api-key"));
                _personalRequests.Add(request.RequestUri.AbsolutePath);
                return Reply("{\"candidates\":[{\"finishReason\":\"STOP\",\"content\":{\"parts\":[{\"text\":\"{\\\"ok\\\":true}\"}]}}]}");
            })),
            new(_vault, (_, endpoint, _, _) =>
            {
                Assert.Equal("generativelanguage.googleapis.com", endpoint.Host);
                Assert.Empty(endpoint.Query);
                _liveConnections++;
                return Task.CompletedTask; // No sockets or paid provider traffic.
            }), _evidence.Add, new(new Handler(request =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("generativelanguage.googleapis.com", request.RequestUri.Host);
                _metadataRequests++;
                return Reply(JsonSerializer.Serialize(new { name = "models/" + PersonalGeminiModels.Text,
                    supportedGenerationMethods = new[] { "generateContent" } }));
            })));
        _account.AttachAiBackend(_gateway);
    }

    [Fact]
    public async Task Damaged_configuration_fails_at_operation_boundary_without_managed_fallback()
    {
        await _account.InitializeAsync();
        File.WriteAllText(Path.Combine(_directory, "keys.bin"), "synthetic corruption");
        Assert.False(_account.RequiresManagedCredits);
        Assert.Throws<PersonalApiConfigurationException>(() => _account.BeginAiOperation());
        await Assert.ThrowsAsync<PersonalApiConfigurationException>(() =>
            _account.GenerateAsync("test", null, 16, null, null, "desktop-reformat", default));
        Assert.Empty(_evidence);
        Assert.All(_managedPaths, path => Assert.Equal("/v1/account", path));
    }

    [Fact]
    public async Task Personal_settings_health_uses_metadata_without_generating_or_touching_managed_usage()
    {
        await _account.InitializeAsync();
        var before = _account.Account;
        var result = await new PaidCloudAiProvider(_account).TestApiKeyAsync();
        Assert.True(result.success);
        Assert.Contains("have not been tested", result.message);
        Assert.Equal(1, _metadataRequests);
        Assert.Empty(_personalRequests);
        Assert.Empty(_evidence);
        Assert.Same(before, _account.Account);
        Assert.All(_managedPaths, path => Assert.Equal("/v1/account", path));
    }

    [Fact]
    public async Task Owned_selected_translation_cannot_resolve_to_managed_or_change_accounting()
    {
        await _account.InitializeAsync();
        var before = _account.Account;
        using (var session = _account.BeginAiOperation())
        using (var entered = _account.EnterAiOperation(session))
        {
            Assert.Equal(AiBackendOwner.Personal, session.Owner);
            var response = await _account.TranslateSelectionAsync(new string('x', 32808), "French", default);
            Assert.StartsWith("personal_", response.RequestId);
            Assert.Equal(0, response.ChargedCreditMicros);
            Assert.Same(before, _account.Account);
            Assert.Equal(AiBackendOwner.Personal, Assert.Single(_evidence).Owner);
        }
        var view = _vault.GetView();
        _vault.SetPolicy(view.Revision, AiBackendPolicy.Managed);
        using (var session = _account.BeginAiOperation())
        using (var entered = _account.EnterAiOperation(session))
            // A root operation has no bound owner until dispatch. Translation
            // must inherit Live, not choose a fresh owner based on Settings.
            await Assert.ThrowsAsync<InvalidOperationException>(() => _account.TranslateSelectionAsync("test", "French", default));
        Assert.Single(_personalRequests);
        Assert.All(_managedPaths, path => Assert.Equal("/v1/account", path));
    }

    [Fact]
    public async Task Managed_gateway_keeps_existing_accounting_and_never_touches_personal_provider()
    {
        _credits = 1000;
        _managedSuccess = true;
        var view = _vault.GetView();
        _vault.SetPolicy(view.Revision, AiBackendPolicy.Managed);
        await _account.InitializeAsync();
        await _account.GenerateAsync("test", null, 16, null, null, "desktop-reformat", default);
        Assert.Equal(990, _account.Account.AvailableCreditMicros);
        Assert.Equal(47, _account.Account.CycleUsageCreditMicros);
        Assert.Empty(_personalRequests);
        Assert.Equal(AiBackendOwner.Managed, Assert.Single(_evidence).Owner);
    }

    [Fact]
    public async Task Policy_changes_apply_to_next_operation_without_moving_an_active_personal_session()
    {
        _credits = 1000;
        _managedSuccess = true;
        await _account.InitializeAsync();
        var before = _account.Account;
        using (var session = _account.BeginAiOperation())
        using (var entered = _account.EnterAiOperation(session))
        {
            var view = _vault.GetView();
            _vault.SetPolicy(view.Revision, AiBackendPolicy.Managed);
            var stillPersonal = await _account.GenerateAsync("fixture", null, 16, null, null,
                "desktop-reformat", default);
            Assert.StartsWith("personal_", stillPersonal.RequestId);
            Assert.Equal(0, stillPersonal.ChargedCreditMicros);
            Assert.Equal(AiBackendOwner.Personal, session.Owner);
            Assert.Same(before, _account.Account);
            Assert.DoesNotContain("/v1/ai/generate", _managedPaths);
        }

        var managed = await _account.GenerateAsync("fixture", null, 16, null, null,
            "desktop-reformat", default);
        Assert.Equal("managed-test", managed.RequestId);
        Assert.Equal(10, managed.ChargedCreditMicros);
        Assert.Equal(990, _account.Account.AvailableCreditMicros);
        Assert.Equal(47, _account.Account.CycleUsageCreditMicros);
        Assert.Equal(1, _managedPaths.Count(path => path == "/v1/ai/generate"));
        var afterManaged = _account.Account;

        var managedView = _vault.GetView();
        _vault.SetPolicy(managedView.Revision, AiBackendPolicy.PersonalOnly);
        var nextPersonal = await _account.GenerateAsync("fixture", null, 16, null, null,
            "desktop-reformat", default);
        Assert.StartsWith("personal_", nextPersonal.RequestId);
        Assert.Equal(0, nextPersonal.ChargedCreditMicros);
        Assert.Same(afterManaged, _account.Account);
        Assert.Equal(1, _managedPaths.Count(path => path == "/v1/ai/generate"));
        Assert.Equal(2, _personalRequests.Count);
        Assert.Equal(new[] { AiBackendOwner.Personal, AiBackendOwner.Managed, AiBackendOwner.Personal },
            _evidence.Select(item => item.Owner));
    }

    [Theory]
    [InlineData(402, "allowance-pre-dispatch-v1", true)]
    [InlineData(402, null, false)]
    [InlineData(402, "allowance-pre-dispatch-v0", false)]
    [InlineData(429, "allowance-pre-dispatch-v1", false)]
    [InlineData(409, null, false)]
    [InlineData(503, "allowance-pre-dispatch-v1", false)]
    public async Task Automatic_fallback_uses_only_typed_initial_server_refusal_not_cached_zero_balance(int status, string contract, bool fallsBack)
    {
        _refusalStatus = status;
        _refusalContract = contract;
        var view = _vault.GetView();
        _vault.SetPolicy(view.Revision, AiBackendPolicy.ManagedThenPersonal);
        await _account.InitializeAsync();
        var before = _account.Account;
        Task<PaidGenerateResponse> Run() => _account.GenerateAsync("test", null, 16, null, null, "desktop-reformat", default);
        if (fallsBack) Assert.StartsWith("personal_", (await Run()).RequestId);
        else await Assert.ThrowsAnyAsync<Exception>(Run);
        Assert.Equal(1, _managedPaths.Count(path => path == "/v1/ai/generate"));
        Assert.Equal(fallsBack ? 1 : 0, _personalRequests.Count);
        Assert.Same(before, _account.Account);
        Assert.Equal(fallsBack
                ? new[] { AiBackendOwner.Managed, AiBackendOwner.Personal }
                : new[] { AiBackendOwner.Managed },
            _evidence.Select(item => item.Owner));
    }

    [Fact]
    public async Task Personal_live_and_every_secondary_route_leave_zero_balance_and_usage_unchanged()
    {
        await _account.InitializeAsync();
        var before = _account.Account;
        Assert.False(_account.RequiresManagedCredits);
        using var session = _account.BeginAiOperation();
        using (var scope = _account.EnterAiOperation(session))
        {
            using var socket = await _account.ConnectLiveAsync();
            // A later Settings policy change cannot escape the frozen session owner.
            var view = _vault.GetView();
            _vault.SetPolicy(view.Revision, AiBackendPolicy.Managed);
            foreach (var kind in new[] { "browser-plan", "browser-vision-recovery", "screen-guidance", "desktop-structured", "desktop-reformat" })
            {
                var result = await Task.Run(() => _account.GenerateAsync("fixture", "instruction", 64,
                    new { type = "object" }, kind.Contains("vision") || kind.Contains("screen")
                        ? new[] { new PaidInlineImage("image/png", "AQID") } : null, kind, default));
                Assert.True(result.Verified);
                Assert.Equal(0, result.ChargedCreditMicros);
            }
            await new PaidCloudAiProvider(_account).ReformatAudioAsync(new byte[44], "fixture", "auto");
            using var reconnect = await _account.ConnectLiveAsync();
            Assert.False(_account.RequiresManagedCredits);
        }
        Assert.Null(_gateway.Current);
        Assert.True(_account.RequiresManagedCredits); // Next operation observes Managed.
        Assert.Same(before, _account.Account);
        Assert.Equal(37, _account.Account.CycleUsageCreditMicros);
        Assert.Equal(0, _account.Account.AvailableCreditMicros);
        Assert.All(_managedPaths, path => Assert.Equal("/v1/account", path));
        Assert.Equal(6, _personalRequests.Count);
        Assert.Equal(2, _liveConnections);
        Assert.Equal(8, _evidence.Count);
        Assert.All(_evidence, item =>
        {
            Assert.Equal(session.LogicalOperationId, item.OperationId);
            Assert.Equal(AiBackendOwner.Personal, item.Owner);
            Assert.Equal("generativelanguage.googleapis.com", item.EndpointHost);
        });
    }

    [Fact]
    public async Task Actual_tool_dispatcher_preserves_backend_owner_after_async_handoff()
    {
        await _account.InitializeAsync();
        using var session = _account.BeginAiOperation();
        using var entered = _account.EnterAiOperation(session);
        using var dispatcher = new LiveToolCallDispatcher(default, async (_, _, token) =>
        {
            await _account.GenerateAsync("fixture", null, 32, null, null, "screen-guidance", token);
            return new Dictionary<string, object> { ["ok"] = true };
        }, (_, _) => Task.CompletedTask);
        using var message = JsonDocument.Parse("{\"functionCalls\":[{\"id\":\"test-1\",\"name\":\"inspect_screen\",\"args\":{}}]}");
        var view = _vault.GetView();
        _vault.SetPolicy(view.Revision, AiBackendPolicy.Managed);
        dispatcher.Enqueue(message.RootElement, new ToolAttemptGuard());
        await dispatcher.Completion;
        Assert.Single(_personalRequests);
        Assert.Equal(session.LogicalOperationId, Assert.Single(_evidence).OperationId);
    }

    [Fact]
    public async Task Disposed_logical_session_cannot_make_late_personal_or_managed_calls()
    {
        await _account.InitializeAsync();
        var session = _account.BeginAiOperation();
        using var entered = _account.EnterAiOperation(session);
        session.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            _account.GenerateAsync("late", null, 16, null, null, "desktop-reformat", default));
        Assert.Empty(_personalRequests);
        Assert.All(_managedPaths, path => Assert.Equal("/v1/account", path));
    }

    [Fact]
    public void Personal_live_model_is_the_voice_replay_verified_low_latency_route()
    {
        Assert.Equal("gemini-3.1-flash-live-preview", PersonalGeminiModels.For(AiCapability.Live));
        // This is a Personal-only mapping. Managed still gets its effective
        // model from the server-owned catalog, never from this constant.
        Assert.Equal("gemini-3.5-flash-lite", PersonalGeminiModels.For(AiCapability.Text));
        Assert.Equal("gemini-3.5-flash", PersonalGeminiModels.For(AiCapability.Images));
    }

    [Fact]
    public void Personal_setup_preserves_tools_audio_settings_and_resumption_without_mutating_original()
    {
        var original = JsonNode.Parse("""
        {"setup":{"model":"models/old","generationConfig":{"responseModalities":["AUDIO"],"speechConfig":{"voiceConfig":{"prebuiltVoiceConfig":{"voiceName":"Puck"}}}},"tools":[{"functionDeclarations":[{"name":"open_app","parameters":{"type":"object"}}]}],"inputAudioTranscription":{},"outputAudioTranscription":{},"realtimeInputConfig":{"automaticActivityDetection":{"disabled":false,"prefixPaddingMs":80,"silenceDurationMs":550}},"contextWindowCompression":{"slidingWindow":{}},"sessionResumption":{"handle":"same-owner-handle"},"systemInstruction":{"parts":[{"text":"unchanged desktop instruction"}]}}}
        """);
        var snapshot = original.ToJsonString();
        var result = PersonalGeminiLiveTransport.PrepareSetup(original);
        Assert.Equal(snapshot, original.ToJsonString());
        Assert.Equal("models/" + PersonalGeminiModels.Live, result["setup"]["model"].GetValue<string>());
        foreach (var field in new[] { "tools", "generationConfig", "inputAudioTranscription", "outputAudioTranscription", "realtimeInputConfig", "contextWindowCompression", "sessionResumption" })
            Assert.True(JsonNode.DeepEquals(original["setup"][field], result["setup"][field]));
        Assert.Equal(PersonalGeminiLiveTransport.SafetyInstruction + "\n\nDesktop context:\nunchanged desktop instruction",
            result["setup"]["systemInstruction"]["parts"][0]["text"].GetValue<string>());
    }

    private static HttpResponseMessage Reply(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(send(request));
    }
    public void Dispose()
    {
        _gateway.Dispose(); _account.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
