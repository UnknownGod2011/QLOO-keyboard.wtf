using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using KeyboardWtf.Models;
using KeyboardWtf.Services;
using KeyboardWtf.Services.Ai.Backends;
using KeyboardWtf.Services.PaidStaging;
using Xunit;

namespace KeyboardWtf.Tests;

// Actual dispatcher -> complete capture -> Personal transport boundary,
// without microphone, sockets, foreground input, or Windows clipboard access.
// AlwaysAsk preserves the real copy permission owner even if generation finishes.
[Collection("Live event integration")]
public sealed class SelectedTranslationDispatchBoundaryTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "keyboard-wtf-selected-dispatch-tests", Guid.NewGuid().ToString("N"));
    private readonly TaskCompletionSource<string> _providerEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<string> _providerResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentQueue<string> _managedPaths = new();
    private readonly ConcurrentQueue<string> _personalPayloads = new();
    private readonly List<AiDispatchEvidence> _evidence = new();
    private readonly VoiceUiPhase _initialPhase = KeyboardWtfState.UiPhase;
    private readonly string _initialTitle = KeyboardWtfState.UiTitle;
    private readonly string _initialDetail = KeyboardWtfState.UiDetail;
    private readonly PersonalApiVault _vault;
    private readonly PaidAccountService _account;
    private readonly AiBackendGateway _gateway;
    private readonly SettingsService _settings;
    private readonly JarvisAutomationService _automation;
    private readonly GeminiLiveConversationService _live;
    private readonly CommandRegistry _commands;
    private AiBackendOperation _session;
    private int _captures;
    private int _providerRequests;
    private readonly bool _personal;
    private readonly string _source = "BEGIN|" + new string('x', 32_716) + "|MIDDLE|END!";
    private const string LatestLargeRequest = "I have selected this page. Could you translate the entire selected text to French and copy the result to my clipboard?";

    public SelectedTranslationDispatchBoundaryTests() : this(personal: true) { }

    private SelectedTranslationDispatchBoundaryTests(bool personal, string source = null)
    {
        _personal = personal;
        if (source != null) _source = source;
        _settings = new(Path.Combine(_directory, "settings.json"));
        _settings.Current.JarvisPermissionMode = JarvisPermissionMode.AlwaysAsk;
        _vault = new(Path.Combine(_directory, "keys.bin"));
        var view = _vault.Add(0, "fake", "synthetic-dispatch-credential-0001", PersonalKeyValidation.AuthenticationValidated);
        _vault.SetPolicy(view.Revision, personal ? AiBackendPolicy.PersonalOnly : AiBackendPolicy.Managed);
        var store = new PaidSessionStore(Path.Combine(_directory, "session.dat"));
        store.Save(new PaidSessionTokens("fake-access", DateTimeOffset.UtcNow.AddMinutes(30), "fake-refresh",
            DateTimeOffset.UtcNow.AddDays(1), "fake-session", "fake-device", "fake-installation"));
        _account = new(new(true, false, new("https://managed.example.test/"), new("https://account.example.test/")),
            store, new Handler(async (request, token) =>
            {
                _managedPaths.Enqueue(request.RequestUri.AbsolutePath);
                if (!personal && request.RequestUri.AbsolutePath == "/v1/ai/generate")
                {
                    Interlocked.Increment(ref _providerRequests);
                    _providerEntered.TrySetResult(await request.Content.ReadAsStringAsync(token));
                    return Reply(await _providerResult.Task.WaitAsync(token));
                }
                Assert.Equal("/v1/account", request.RequestUri.AbsolutePath); // Never managed inference/reservation.
                return Reply(JsonSerializer.Serialize(new
                {
                    userId = "fake", status = "active", availableCreditMicros = personal ? 0 : 30_000_000, reservedCreditMicros = 0,
                    cycleUsageCreditMicros = 37, devices = Array.Empty<object>(), recentUsage = Array.Empty<object>(),
                    recentPurchases = Array.Empty<object>(),
                }));
            }));
        _gateway = new(_vault, _account.AuthorizePersonalAsync, new(_vault, new Handler(async (request, token) =>
        {
            Assert.Equal("generativelanguage.googleapis.com", request.RequestUri.Host);
            Interlocked.Increment(ref _providerRequests);
            var payload = await request.Content.ReadAsStringAsync(token);
            _personalPayloads.Enqueue(payload);
            _providerEntered.TrySetResult(payload);
            return Reply(await _providerResult.Task.WaitAsync(token));
        })), new(_vault, (_, _, _, _) => throw new InvalidOperationException("No Live connection belongs to this fixture.")),
            item => _evidence.Add(item));
        _account.AttachAiBackend(_gateway);
        _automation = new(null, _settings, new(Path.Combine(_directory, "history.json")),
            new(Path.Combine(_directory, "mappings.json")), () => { });
        _live = new(null, _settings, null, _account);
        _commands = new(null, null, null, _settings, null, _live, _automation,
            currentSelectionContextId: () => "fixture-foreground-document",
            captureSelectedText: _ =>
            {
                Interlocked.Increment(ref _captures);
                var captured = JarvisAutomationService.SelectionResult(_source, fresh: true);
                captured["capture_id"] = "fixture-complete-capture";
                captured["source_context_id"] = "fixture-foreground-document";
                return Task.FromResult(captured);
            });
    }

    [Theory]
    [InlineData("Analyze the selected text.", true)]
    [InlineData("Can you please analyze my selection?", true)]
    [InlineData("Explain the highlighted text on my screen.", true)]
    [InlineData("Analyze my screen.", false)]
    [InlineData("Analyze the selected text and then inspect my screen.", false)]
    [InlineData("Analyze this business idea.", false)]
    public void Requested_content_source_not_the_analysis_verb_selects_the_boundary(string request, bool selected) =>
        Assert.Equal(selected, CommandRegistry.ShouldReadSelectionInsteadOfScreen("inspect_screen", request));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cancel_or_session_shutdown_clears_ui_and_forbids_a_late_copy_in_either_mode(bool personal)
    {
        var fixture = new SelectedTranslationDispatchBoundaryTests(personal);
        try
        {
            await fixture.InitializeAsync();
            fixture._commands.ObserveJarvisTranscript(LatestLargeRequest, default);
            await fixture.Execute("get_selected_text", new { });
            var operation = Assert.IsType<SelectedTextTranslationOperation>(fixture.OwnedOperation);
            await fixture._providerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(operation.Cancel());
            fixture.CompleteProvider("A late translation must not be copied.");
            await operation.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(operation.IsCancelled);
            Assert.Equal("", KeyboardWtfState.LongTaskDetail);
            Assert.Null(fixture._automation.PendingSnapshot());
            Assert.Null(fixture._automation.LatestVerifiedClipboardCopy);
            Assert.Equal(1, fixture._providerRequests);
        }
        finally { await fixture.DisposeAsync(); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Settings_switch_does_not_change_the_originating_session_owner_before_translation(bool personal)
    {
        var fixture = new SelectedTranslationDispatchBoundaryTests(personal);
        try
        {
            await fixture.InitializeAsync();
            var changed = fixture._vault.GetView();
            fixture._vault.SetPolicy(changed.Revision, personal ? AiBackendPolicy.Managed : AiBackendPolicy.PersonalOnly);
            fixture._commands.ObserveJarvisTranscript(LatestLargeRequest, default);
            await fixture.Execute("get_selected_text", new { });
            Assert.IsType<SelectedTextTranslationOperation>(fixture.OwnedOperation);
            await fixture.AssertCompletePersonalRequest();
            Assert.Equal(personal ? AiBackendOwner.Personal : AiBackendOwner.Managed, Assert.Single(fixture._evidence).Owner);
            Assert.Equal(fixture._session.LogicalOperationId, fixture._evidence[0].OperationId);
            if (personal) Assert.DoesNotContain("/v1/ai/generate", fixture._managedPaths);
        }
        finally { await fixture.DisposeAsync(); }
    }

    public async Task InitializeAsync()
    {
        await _account.InitializeAsync();
        _session = _account.BeginAiOperation();
        if (!_personal)
        {
            using var scope = _account.EnterAiOperation(_session);
            await _gateway.GenerateAsync(new("bind managed session", "", 1), "desktop-health", 30_000_000,
                _ => Task.FromResult(new PaidGenerateResponse("bound", "ready", "fake", 0, 30_000_000, default, true)), default);
            _evidence.Clear(); // Session establishment is not the translation dispatch.
        }
        Assert.Equal(_personal ? AiBackendOwner.Personal : AiBackendOwner.Managed, _session.Owner);
        SetLiveField("_backendOperation", _session);
        SetLiveField("_sessionCts", new CancellationTokenSource());
        Assert.Equal(_personal, _live.HasPersonalSession);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Both_backends_use_the_same_owned_translation_and_permission_lifecycle(bool personal)
    {
        var fixture = new SelectedTranslationDispatchBoundaryTests(personal);
        try
        {
            await fixture.InitializeAsync();
            fixture._commands.ObserveJarvisTranscript(LatestLargeRequest, default);
            var receipt = await fixture.Execute("get_selected_text", new { });
            var operation = Assert.IsType<SelectedTextTranslationOperation>(fixture.OwnedOperation);
            Assert.Equal("", receipt["message"]);
            Assert.Equal("selected_text_translation_lifecycle", receipt["announcement_owner"]);
            var payload = await fixture._providerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var parsed = JsonDocument.Parse(payload);
            Assert.Equal(fixture._source, personal
                ? parsed.RootElement.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString()
                : parsed.RootElement.GetProperty("prompt").GetString());
            Assert.Equal(65_536, personal
                ? parsed.RootElement.GetProperty("generationConfig").GetProperty("maxOutputTokens").GetInt32()
                : parsed.RootElement.GetProperty("maxOutputTokens").GetInt32());
            Assert.Equal(personal ? AiBackendOwner.Personal : AiBackendOwner.Managed, Assert.Single(fixture._evidence).Owner);
            Assert.Equal(fixture._session.LogicalOperationId, fixture._evidence[0].OperationId);
            Assert.Equal("Translating...", KeyboardWtfState.LongTaskDetail);
            var changed = fixture._vault.GetView();
            fixture._vault.SetPolicy(changed.Revision, personal ? AiBackendPolicy.Managed : AiBackendPolicy.PersonalOnly);
            fixture.CompleteProvider("Le document traduit complet.");
            await operation.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(true, operation.Receipt()["confirmation_required"]);
            Assert.Equal("", KeyboardWtfState.LongTaskDetail);
            Assert.Equal(1, fixture._captures);
            Assert.Equal(1, fixture._providerRequests);
            Assert.Null(fixture._automation.LatestVerifiedClipboardCopy);
            Assert.Equal(personal ? 37 : 162, fixture._account.Account.CycleUsageCreditMicros);
            Assert.Equal(personal ? 0 : 29_999_875, fixture._account.Account.AvailableCreditMicros);
        }
        finally { await fixture.DisposeAsync(); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Selected_text_analysis_cannot_execute_a_model_screen_proposal(bool personal)
    {
        var fixture = new SelectedTranslationDispatchBoundaryTests(personal);
        try
        {
            await fixture.InitializeAsync();
            fixture._commands.ObserveJarvisTranscript("Analyze the selected text.", default);
            var captured = await fixture.Execute("inspect_screen", new { request = "Analyze the selected text." });
            Assert.Equal(true, captured["ok"]);
            Assert.Equal(fixture._source, captured["text"]);
            Assert.Equal(1, fixture._captures);
            Assert.False(captured.ContainsKey("screen_context_id"));
            Assert.Null(fixture.OwnedOperation);
            Assert.Equal(0, fixture._providerRequests);
        }
        finally { await fixture.DisposeAsync(); }
    }

    [Fact]
    public async Task Incomplete_managed_result_cannot_copy_and_always_clears_ui()
    {
        var fixture = new SelectedTranslationDispatchBoundaryTests(personal: false);
        try
        {
            await fixture.InitializeAsync();
            fixture._commands.ObserveJarvisTranscript(LatestLargeRequest, default);
            await fixture.Execute("get_selected_text", new { });
            var operation = Assert.IsType<SelectedTextTranslationOperation>(fixture.OwnedOperation);
            await fixture._providerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture._providerResult.TrySetResult(JsonSerializer.Serialize(new
            {
                requestId = "managed-fixture", text = "partial", model = "fake-managed", chargedCreditMicros = 125,
                remainingCreditMicros = 29_999_875, verified = true, usage = new { }, contentComplete = false,
            }));
            await operation.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(false, operation.Receipt()["ok"]);
            Assert.Equal(true, operation.Receipt()["clipboard_unchanged"]);
            Assert.Null(fixture._automation.PendingSnapshot());
            Assert.Null(fixture._automation.LatestVerifiedClipboardCopy);
            Assert.Equal("", KeyboardWtfState.LongTaskDetail);
            Assert.Equal(162, fixture._account.Account.CycleUsageCreditMicros);
            Assert.Equal(1, fixture._providerRequests);
        }
        finally { await fixture.DisposeAsync(); }
    }

    [Fact]
    public async Task Above_former_ceiling_exact_user_request_enters_existing_owned_translation()
    {
        var source = string.Concat(Enumerable.Repeat("\r\nमराठी 日本語 👋\n", 5000));
        var fixture = new SelectedTranslationDispatchBoundaryTests(personal: true, source);
        try
        {
            await fixture.InitializeAsync();
            fixture._commands.ObserveJarvisTranscript(
                "translate the selected text to french and copy it to my clipboard.", default);
            var receipt = await fixture.Execute("get_selected_text", new { });
            var operation = Assert.IsType<SelectedTextTranslationOperation>(fixture.OwnedOperation);
            Assert.Equal(true, receipt["started"]);
            using var firstPayload = JsonDocument.Parse(await fixture._providerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(SelectedTextTranslationOperation.SplitSource(source, 50000)[0],
                firstPayload.RootElement.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString());
            Assert.Equal(1, fixture._captures);
            Assert.Equal(1, fixture._providerRequests);
            fixture.CompleteProvider("Le document complet.");
            await operation.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(source, string.Concat(fixture._personalPayloads.Select(body =>
            {
                using var part = JsonDocument.Parse(body);
                return part.RootElement.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString();
            })));
            Assert.True(fixture._providerRequests > 1);
            Assert.Equal(true, operation.Receipt()["confirmation_required"]);
            Assert.Equal("", KeyboardWtfState.LongTaskDetail);
        }
        finally { await fixture.DisposeAsync(); }
    }

    [Fact]
    public async Task Natural_large_request_starts_real_owned_operation_with_complete_capture()
    {
        const string request = LatestLargeRequest;
        _commands.ObserveJarvisTranscript(request, CancellationToken.None);
        var receipt = await Execute("get_selected_text", new { });
        var operation = Assert.IsType<SelectedTextTranslationOperation>(OwnedOperation);

        Assert.Equal(true, receipt["started"]);
        Assert.Equal(operation.Id, receipt["operation_id"]);
        Assert.Equal("French", operation.Language);
        Assert.Equal(request, operation.Request);
        await AssertCompletePersonalRequest();
        Assert.Equal(1, _captures);
        Assert.Equal("Translating...", KeyboardWtfState.LongTaskDetail);
        Assert.Null(_automation.LatestVerifiedClipboardCopy);
        Assert.Null(_automation.PendingSnapshot());
    }

    [Theory]
    [InlineData("Thanks. Do me a favor: translate the selected text to French and copy it.")]
    [InlineData("Hi Jarvis, could you translate it to French and copy it to my clipboard?")]
    [InlineData("I selected some text. Would you translate it to French, then put the translation on the clipboard?")]
    [InlineData("Translate the selection to French and copy it to the clipboard.")]
    [InlineData("Could you translate this into French and copy it to my clipboard?")]
    [InlineData("Translate this to German, then put the translation on the clipboard.")]
    public async Task Natural_capture_proposal_hands_off_directly_to_existing_executor(string request)
    {
        _commands.ObserveJarvisTranscript(request, CancellationToken.None);
        var receipt = await Execute("get_selected_text", new { });
        var operation = Assert.IsType<SelectedTextTranslationOperation>(OwnedOperation);
        Assert.Equal(true, receipt["started"]);
        Assert.Equal("", receipt["message"]);
        Assert.Equal("selected_text_translation_lifecycle", receipt["announcement_owner"]);
        Assert.Equal(request, operation.Request);
        await AssertCompletePersonalRequest(operation.Language);
        Assert.Equal(1, _captures);
        Assert.Equal(1, _providerRequests);
        Assert.Null(_automation.LatestVerifiedClipboardCopy);
        Assert.Null(_automation.PendingSnapshot());
    }

    [Fact]
    public async Task Final_copy_clause_starts_from_the_same_interim_capture_without_recapture()
    {
        const string interim = "I have selected some text. Could you translate it to French";
        const string complete = interim + " and copy it to my clipboard.";
        _commands.ObserveJarvisTranscript(interim, CancellationToken.None);
        var captured = await Execute("get_selected_text", new { });
        Assert.Equal(_source, captured["text"]);
        Assert.Null(OwnedOperation);
        Assert.Equal(1, _captures);

        _commands.ObserveJarvisTranscript(complete, CancellationToken.None);
        var receipt = await Execute("copy_text", new { text = "Translation in progress; handled by the system." });
        var operation = Assert.IsType<SelectedTextTranslationOperation>(OwnedOperation);
        Assert.Equal(true, receipt["started"]);
        Assert.Equal(complete, operation.Request);
        await AssertCompletePersonalRequest();
        Assert.Equal(1, _captures);
        Assert.Null(_automation.LatestVerifiedClipboardCopy);
        Assert.Null(_automation.PendingSnapshot()); // Narration never reached the copy owner.
    }

    [Theory]
    [InlineData("get_selected_text")]
    [InlineData("copy_text")]
    public async Task Translation_intent_survives_tool_only_provider_boundary(string tool)
    {
        const string request = "translate the selected text to french and copy it to my clipboard.";
        _commands.ObserveJarvisTranscript(request, CancellationToken.None);
        _commands.CompleteJarvisUserTurn(assistantAnswerComplete: false);
        var receipt = await Execute(tool, new { text = "French translation placeholder" });
        var operation = Assert.IsType<SelectedTextTranslationOperation>(OwnedOperation);
        Assert.Equal(request, operation.Request);
        Assert.Equal(true, receipt["started"]);
        await AssertCompletePersonalRequest();
        Assert.Null(_automation.PendingSnapshot());
        Assert.Equal(1, _providerRequests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Translation_authority_does_not_survive_a_real_answer_or_new_request(bool answer)
    {
        _commands.ObserveJarvisTranscript("translate the selected text to french and copy it to my clipboard.", CancellationToken.None);
        _commands.CompleteJarvisUserTurn(assistantAnswerComplete: answer);
        if (!answer) _commands.ObserveJarvisTranscript("Copy hello to the clipboard.", CancellationToken.None);
        await Execute("copy_text", new { text = "hello" });
        Assert.Null(OwnedOperation);
        Assert.Equal(0, _providerRequests);
        Assert.NotNull(_automation.PendingSnapshot());
    }

    [Fact]
    public async Task Same_batch_capture_does_not_block_an_independent_literal_copy()
    {
        _commands.ObserveJarvisTranscript("Read the selection and copy hello to the clipboard.", CancellationToken.None);
        // An unambiguously literal copy keeps its ordinary permission owner.
        _commands.ObserveJarvisTranscript("Copy hello to the clipboard.", CancellationToken.None);
        using var dispatcher = new LiveToolCallDispatcher(CancellationToken.None, _commands.ExecuteJarvisToolAsync,
            (build, token) => { _ = build(); return Task.CompletedTask; });
        dispatcher.Enqueue(JsonSerializer.SerializeToElement(new { functionCalls = new object[] {
            new { id = "capture", name = "get_selected_text", args = new { } },
            new { id = "copy", name = "copy_text", args = new { text = "hello" } }
        } }), new ToolAttemptGuard());
        await dispatcher.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(_automation.PendingSnapshot());
        Assert.False(LiveToolCallDispatcher.HasUnpublishedSelectedContext);
        Assert.Equal(0, _providerRequests);
    }

    [Fact]
    public async Task Unbound_selected_translation_cannot_publish_a_model_placeholder()
    {
        _commands.ObserveJarvisTranscript(
            "translate the selected text to french and copy it to my clipboard.", CancellationToken.None);
        SetLiveField("_backendOperation", null);
        try
        {
            var receipt = await Execute("copy_text", new { text = "French translation placeholder" });
            Assert.Equal(false, receipt["ok"]);
            Assert.True(receipt.GetValueOrDefault("clipboard_unchanged") is true);
            Assert.Null(_automation.PendingSnapshot());
            Assert.Null(_automation.LatestVerifiedClipboardCopy);
            Assert.Equal(0, _providerRequests);
        }
        finally { SetLiveField("_backendOperation", _session); }
    }

    [Fact]
    public async Task Same_batch_capture_cannot_ground_precomputed_transformed_copy()
    {
        _commands.ObserveJarvisTranscript("Rewrite the selected text and copy it to the clipboard.", CancellationToken.None);
        var responses = new List<string>();
        using var dispatcher = new LiveToolCallDispatcher(CancellationToken.None, _commands.ExecuteJarvisToolAsync,
            (build, _) => { responses.Add(JsonSerializer.Serialize(build())); return Task.CompletedTask; });
        dispatcher.Enqueue(JsonSerializer.SerializeToElement(new { functionCalls = new object[] {
            new { id = "capture", name = "get_selected_text", args = new { } },
            new { id = "copy", name = "copy_text", args = new { text = "A result proposed without seeing the source" } }
        } }), new ToolAttemptGuard());
        await dispatcher.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        using var reply = JsonDocument.Parse(Assert.Single(responses));
        var copy = reply.RootElement.GetProperty("toolResponse").GetProperty("functionResponses")[1].GetProperty("response");
        Assert.False(copy.GetProperty("ok").GetBoolean());
        Assert.True(copy.GetProperty("selected_context_result_required").GetBoolean());
        Assert.True(copy.GetProperty("clipboard_unchanged").GetBoolean());
        Assert.Null(_automation.PendingSnapshot());
        Assert.Equal(1, _captures);
        // Once that receipt is published, a newly generated result is eligible.
        await Execute("copy_text", new { text = "A newly generated result based on the source" });
        Assert.NotNull(_automation.PendingSnapshot()); // Existing AlwaysAsk boundary remains.
    }

    [Fact]
    public async Task Same_batch_translation_uses_only_the_owned_complete_result()
    {
        _commands.ObserveJarvisTranscript("Translate this into French and copy it to the clipboard.", CancellationToken.None);
        using var dispatcher = new LiveToolCallDispatcher(CancellationToken.None, _commands.ExecuteJarvisToolAsync,
            (build, token) => { _ = build(); return Task.CompletedTask; });
        dispatcher.Enqueue(JsonSerializer.SerializeToElement(new { functionCalls = new object[] {
            new { id = "capture", name = "get_selected_text", args = new { } },
            new { id = "copy", name = "copy_text", args = new { text = "French translation placeholder" } }
        } }), new ToolAttemptGuard());
        await dispatcher.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        await AssertCompletePersonalRequest();
        Assert.Null(_automation.PendingSnapshot());
        CompleteProvider("Bonjour"); // Short legitimate output must not be rejected by length heuristics.
        await OwnedOperation.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(_automation.PendingSnapshot());
        Assert.Equal(1, _providerRequests);
        Assert.Equal(1, _captures);
    }

    [Theory]
    [InlineData("French or German")]
    [InlineData("Martian")]
    public async Task Unclear_target_cannot_fall_through_to_model_narration_copy(string target)
    {
        _commands.ObserveJarvisTranscript(
            $"I have selected this page. Could you translate it to {target} and copy it to clipboard?", CancellationToken.None);
        var captureProposal = await Execute("get_selected_text", new { });
        Assert.Equal(false, captureProposal["ok"]);
        Assert.Null(OwnedOperation);

        var copyProposal = await Execute("copy_text", new { text = "The translation is running in the background." });
        Assert.Equal(false, copyProposal["ok"]);
        Assert.Null(OwnedOperation);
        Assert.Equal(0, _providerRequests);
        Assert.Null(_automation.PendingSnapshot());
        Assert.Null(_automation.LatestVerifiedClipboardCopy);
        Assert.Equal("", KeyboardWtfState.LongTaskDetail);
    }

    [Fact]
    public async Task Duplicate_model_copy_proposals_remain_owned_and_do_not_dispatch_or_copy_again()
    {
        _commands.ObserveJarvisTranscript(
            "I have selected this page. Please translate it to French and copy it to clipboard.", CancellationToken.None);
        var initial = await Execute("get_selected_text", new { });
        var operation = Assert.IsType<SelectedTextTranslationOperation>(OwnedOperation);
        await AssertCompletePersonalRequest();
        for (var index = 0; index < 5; index++)
        {
            var duplicate = await Execute("copy_text", new { text = "Model-generated placeholder " + index });
            Assert.Equal(initial["operation_id"], duplicate["operation_id"]);
            Assert.Equal(true, duplicate["duplicate_suppressed"]);
            Assert.Same(operation, OwnedOperation);
        }
        Assert.Equal(1, _captures);
        Assert.Equal(1, _providerRequests);
        Assert.Null(_automation.PendingSnapshot());
        Assert.Null(_automation.LatestVerifiedClipboardCopy);
    }

    [Fact]
    public async Task Completed_owned_result_retains_normal_permission_and_clears_task_feedback()
    {
        _commands.ObserveJarvisTranscript(
            "I have selected this page. Please translate it to French and copy it to clipboard.", CancellationToken.None);
        await Execute("get_selected_text", new { });
        var operation = Assert.IsType<SelectedTextTranslationOperation>(OwnedOperation);
        await AssertCompletePersonalRequest();
        CompleteProvider("Le document traduit complet.");
        await operation.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        var receipt = operation.Receipt();
        Assert.Equal(true, receipt["completed"]);
        Assert.Equal(true, receipt["confirmation_required"]);
        Assert.False(receipt.GetValueOrDefault("clipboard_verified") is true);
        Assert.Null(_automation.LatestVerifiedClipboardCopy);
        var pending = JsonSerializer.SerializeToElement(_automation.PendingSnapshot());
        Assert.Equal("copy_text", pending.GetProperty("ToolName").GetString());
        Assert.Equal("", KeyboardWtfState.LongTaskDetail);
        Assert.Equal(37, _account.Account.CycleUsageCreditMicros);
        Assert.Equal(0, _account.Account.AvailableCreditMicros);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Fifty_actual_dispatches_each_create_one_complete_operation_and_suppress_model_copy(bool personal)
    {
        var requests = new[]
        {
            LatestLargeRequest,
            "I have selected some text can you please translate it to French and copy it to my clipboard",
            "Translate the text that I have selected into French. Could you copy the translation to my clipboard?",
            "Thanks. Do me a favor: translate the selected text to French and copy it.",
            "Hi Jarvis, could you translate it to French and copy it to my clipboard?",
            "I selected some text. Would you translate it to French, then put the translation on the clipboard?",
        };
        for (var index = 0; index < 50; index++)
        {
            var fixture = new SelectedTranslationDispatchBoundaryTests(personal);
            try
            {
                await fixture.InitializeAsync();
                var request = requests[index % requests.Length];
                fixture._commands.ObserveJarvisTranscript(request, CancellationToken.None);
                var started = await fixture.Execute("get_selected_text", new { });
                var operation = Assert.IsType<SelectedTextTranslationOperation>(fixture.OwnedOperation);
                Assert.Equal(request, operation.Request);
                Assert.Equal("French", operation.Language);
                Assert.Equal(operation.Id, started["operation_id"]);
                await fixture.AssertCompletePersonalRequest();

                var duplicate = await fixture.Execute("copy_text", new { text = "Generated command/progress metadata, not the document result." });
                Assert.Equal(true, duplicate["duplicate_suppressed"]);
                Assert.Equal(operation.Id, duplicate["operation_id"]);
                Assert.Same(operation, fixture.OwnedOperation);
                Assert.Equal(1, fixture._captures);
                Assert.Equal(1, fixture._providerRequests);
                Assert.Null(fixture._automation.PendingSnapshot());
                Assert.Null(fixture._automation.LatestVerifiedClipboardCopy);
                Assert.Equal(37, fixture._account.Account.CycleUsageCreditMicros);
                Assert.Equal(personal ? 0 : 30_000_000, fixture._account.Account.AvailableCreditMicros);
            }
            finally { await fixture.DisposeAsync(); }
            Assert.Equal("", KeyboardWtfState.LongTaskDetail);
        }
    }

    private Task<Dictionary<string, object>> Execute(string tool, object args) =>
        _commands.ExecuteJarvisToolAsync(tool, JsonSerializer.SerializeToElement(args), CancellationToken.None);

    private SelectedTextTranslationOperation OwnedOperation => (SelectedTextTranslationOperation)
        typeof(CommandRegistry).GetField("_selectedTranslation", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(_commands);

    private void SetLiveField(string name, object value) =>
        typeof(GeminiLiveConversationService).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(_live, value);

    private async Task AssertCompletePersonalRequest(string language = "French")
    {
        using var payload = JsonDocument.Parse(await _providerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(_source, _personal
            ? payload.RootElement.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString()
            : payload.RootElement.GetProperty("prompt").GetString());
        Assert.Equal(65_536, _personal
            ? payload.RootElement.GetProperty("generationConfig").GetProperty("maxOutputTokens").GetInt32()
            : payload.RootElement.GetProperty("maxOutputTokens").GetInt32());
        Assert.Contains("into " + language, _personal
            ? payload.RootElement.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString()
            : payload.RootElement.GetProperty("systemInstruction").GetString());
        Assert.Equal(_personal ? AiBackendOwner.Personal : AiBackendOwner.Managed, Assert.Single(_evidence).Owner);
        if (_personal) Assert.All(_managedPaths, path => Assert.Equal("/v1/account", path));
        else Assert.Equal(1, _managedPaths.Count(path => path == "/v1/ai/generate"));
    }

    private void CompleteProvider(string output) => _providerResult.TrySetResult(_personal ? JsonSerializer.Serialize(new
    {
        candidates = new[] { new { finishReason = "STOP", content = new { parts = new[] { new { text = output } } } } },
    }) : JsonSerializer.Serialize(new
    {
        requestId = "managed-fixture", text = output, model = "fake-managed", chargedCreditMicros = 125,
        remainingCreditMicros = 29_999_875, verified = true, usage = new { }, contentComplete = true,
    }));

    public async Task DisposeAsync()
    {
        var operation = OwnedOperation;
        operation?.Cancel();
        CompleteProvider("A late fixture result must never be copied.");
        if (operation?.Completion != null) await operation.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await _commands.HandleJarvisSessionEndingAsync();
        _live.Dispose();
        _session?.Dispose();
        _automation.Dispose();
        _gateway.Dispose();
        _account.Dispose();
        KeyboardWtfState.SetUi(_initialPhase, _initialTitle, _initialDetail);
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static HttpResponseMessage Reply(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
}
