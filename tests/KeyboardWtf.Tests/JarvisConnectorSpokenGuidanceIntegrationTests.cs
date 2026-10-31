using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.Json;
using KeyboardWtf.Models;
using KeyboardWtf.Services;
using KeyboardWtf.Services.Ai.Backends;
using KeyboardWtf.Services.Connectors;
using Xunit;

namespace KeyboardWtf.Tests;

// Replay the real receive/dispatcher/command/controller path. Only the remote
// backend, reasoning response and socket publication are replaced; no Live
// session, microphone, credentials or provider requests are started.
[Collection("Live event integration")]
public sealed class JarvisConnectorSpokenGuidanceIntegrationTests
{
    private const string Original = "Find and read the BioCyclic winner email and explain the prize";
    private const string Added = "Only the 2025 competition email";
    private const string OldAnswer = "The 2026 competition awarded Top 10 Best Performers.";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Spoken_same_identity_guidance_survives_old_answer_boundaries_until_current_result_is_collected(bool personal)
    {
        await using var replay = new Replay(personal, guidance: true);
        await replay.Resolve(); var origin = replay.Commands.ConnectorUserRequestIdentity;
        var execution = replay.Tool("read", "connector_execute", replay.ExecuteArguments);
        await replay.ReasoningHeld.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var started = await execution; Assert.True(started.GetProperty("started").GetBoolean());
        var operation = started.GetProperty("operation_id").GetString();
        replay.TurnComplete(); await replay.Drain(); AssertThinking();
        await replay.Input(Added); Assert.Equal(origin, replay.Commands.ConnectorUserRequestIdentity);
        var observed = replay.Commands.ConnectorUserRequest;
        Assert.Contains(Added, observed, StringComparison.Ordinal);
        Assert.Equal(observed, replay.Commands.ConnectorUserRequestSnapshot.Text);
        Assert.Equal(origin, replay.Commands.ConnectorUserRequestSnapshot.Identity);
        var guide = await replay.Tool("guide", "connector_control", new { task_id = replay.TaskId, action = "guide",
            instruction = observed, expected_goal_revision = 0 }, prematureAnswer: "An old pre-receipt answer.");
        Assert.True(guide.GetProperty("ok").GetBoolean()); Assert.Equal(1, guide.GetProperty("goal_revision").GetInt32());
        Assert.DoesNotContain("An old pre-receipt answer.", replay.AssistantTranscript, StringComparison.Ordinal);
        await replay.Answer("An older answer while the reader is still running.");
        AssertThinking(); Assert.Empty(replay.Completions); Assert.False(replay.WorkToken.IsCancellationRequested);
        Assert.Equal(origin, replay.Commands.ConnectorUserRequestIdentity);
        replay.ReleaseReasoning(); await replay.Notice.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(Added, await replay.LatestReasoningPrompt.Task.WaitAsync(TimeSpan.FromSeconds(10)), StringComparison.Ordinal);
        await replay.Answer("An uncollected earlier answer after background reading finished.");
        AssertThinking(); Assert.Empty(replay.Completions); Assert.False(replay.WorkToken.IsCancellationRequested);
        var current = await replay.Collect(operation);
        Assert.NotEqual(OldAnswer, current.GetProperty("connector").GetProperty("result").GetProperty("data").GetProperty("answer").GetString());
        replay.TurnComplete(); await replay.Drain(); AssertThinking(); Assert.Empty(replay.Completions);
        await replay.Answer("The observed email concerns 2026; the requested 2025 winner was not verified.");
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner); AssertListening();
        Assert.Equal(replay.TaskId, Assert.Single(replay.Completions));
        Assert.Equal(3, replay.ReasoningCalls); Assert.Equal(2, replay.BackendExecutions); Assert.Equal(1, replay.Resolves);
        Assert.True(replay.WorkToken.IsCancellationRequested);
        // This uses only protocol ordering the source can observe. A raw output
        // frame arriving after a collected result carries no old-answer ID;
        // this fixture does not claim the source can semantically identify it.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Changed_spoken_ASR_text_under_same_identity_updates_guidance_but_exact_replay_does_not(bool personal)
    {
        await using var replay = new Replay(personal, guidance: true);
        await replay.Resolve(); var origin = replay.Commands.ConnectorUserRequestIdentity;
        var execution = replay.Tool("read", "connector_execute", replay.ExecuteArguments);
        await replay.ReasoningHeld.Task.WaitAsync(TimeSpan.FromSeconds(10)); var started = await execution;
        Assert.True(started.GetProperty("started").GetBoolean()); var operation = started.GetProperty("operation_id").GetString();
        replay.TurnComplete(); await replay.Drain(); await replay.Input(Added);
        var firstText = replay.Commands.ConnectorUserRequest;
        var first = await replay.Tool("guide-first", "connector_control", new { task_id = replay.TaskId,
            action = "guide", instruction = firstText, expected_goal_revision = 0 });
        Assert.True(first.GetProperty("ok").GetBoolean()); Assert.Equal(1, first.GetProperty("goal_revision").GetInt32());
        await replay.Input("Also only senders from Devpost");
        var laterText = replay.Commands.ConnectorUserRequest;
        Assert.NotEqual(firstText, laterText); Assert.Equal(origin, replay.Commands.ConnectorUserRequestIdentity);
        Assert.Equal(laterText, replay.Commands.ConnectorUserRequestSnapshot.Text);
        var changed = await replay.Tool("guide-changed-asr", "connector_control", new { task_id = replay.TaskId,
            action = "guide", instruction = laterText, expected_goal_revision = 1 });
        Assert.True(changed.GetProperty("ok").GetBoolean()); Assert.Equal(2, changed.GetProperty("goal_revision").GetInt32());
        var repeated = await replay.Tool("guide-exact-replay", "connector_control", new { task_id = replay.TaskId,
            action = "guide", instruction = laterText, expected_goal_revision = 2 });
        Assert.True(repeated.GetProperty("ok").GetBoolean()); Assert.True(repeated.GetProperty("already_applied").GetBoolean());
        Assert.Equal(2, repeated.GetProperty("goal_revision").GetInt32()); Assert.Equal(2, replay.ReasoningCalls);
        Assert.Equal(origin, replay.Commands.ConnectorUserRequestIdentity); AssertThinking();
        replay.ReleaseReasoning(); await replay.Notice.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var prompt = await replay.LatestReasoningPrompt.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(firstText, prompt, StringComparison.Ordinal); Assert.Contains(laterText, prompt, StringComparison.Ordinal);
        await replay.Collect(operation); await replay.Answer("The current combined read constraints were applied; the 2025 result was not verified.");
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner); Assert.Equal(replay.TaskId, Assert.Single(replay.Completions));
        Assert.Equal(3, replay.ReasoningCalls); Assert.Equal(2, replay.BackendExecutions); Assert.Equal(1, replay.Resolves);
    }
    private static void AssertThinking()
    {
        Assert.True(KeyboardWtfState.HasLongTaskUiOwner);
        Assert.Equal(("Thinking…", "gmail"), KeyboardWtfState.LongTaskDisplayState(TimeSpan.FromSeconds(20)));
    }

    private static void AssertListening()
    {
        Assert.Equal(VoiceUiPhase.Listening, KeyboardWtfState.UiPhase);
        Assert.Equal("Listening for your next request...", KeyboardWtfState.UiDetail);
    }

    private sealed class Replay : IAsyncDisposable
    {
        private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "keyboard-wtf-tests", Guid.NewGuid().ToString("N"));
        private readonly GeminiLiveConversationService _live;
        private readonly JarvisAutomationService _automation;
        private readonly JarvisConnectorController _controller;
        private readonly CancellationTokenSource _session = new();
        private readonly object _diagnostics;
        private readonly LiveToolCallDispatcher _dispatcher;
        private readonly bool _personal;
        private readonly bool _holdBackend;
        private readonly bool _guidance;
        private readonly string _logicalLiveSessionId = Guid.NewGuid().ToString("N");
        private readonly TaskCompletionSource<bool> _reasonRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<JsonElement> _backend = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentDictionary<string, JsonElement> _receipts = new();
        private readonly MethodInfo _handle = typeof(GeminiLiveConversationService).GetMethod("HandleServerMessage", PrivateInstance)!;
        private readonly MethodInfo _execute = typeof(GeminiLiveConversationService).GetMethod("ExecuteToolAsync", PrivateInstance)!;
        internal string TaskId { get; } = Guid.NewGuid().ToString("N");
        internal CommandRegistry Commands { get; }
        internal CancellationToken SessionToken => _session.Token;
        internal CancellationToken WorkToken { get; private set; }
        internal TaskCompletionSource<bool> WorkStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Notice { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ConcurrentQueue<string> Completions { get; } = new();
        internal int BackendExecutions;
        internal int ReasoningCalls;
        internal int Resolves;
        internal TaskCompletionSource<bool> ReasoningHeld { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<string> LatestReasoningPrompt { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal object ExecuteArguments => new { task_id = TaskId, capability_id = "mail_search", arguments_json = "{\"query\":\"biocyclic winner\"}" };
        internal string AssistantTranscript => ((StringBuilder)typeof(GeminiLiveConversationService)
            .GetField("_currentAssistantTranscript", PrivateInstance)!.GetValue(_live)!).ToString();

        internal Replay(bool personal, bool holdBackend = false, bool quota = false, bool guidance = false)
        {
            _personal = personal;
            _holdBackend = holdBackend;
            _guidance = guidance;
            var settings = new SettingsService(Path.Combine(_directory, "settings.json"));
            settings.Current.JarvisPermissionMode = JarvisPermissionMode.AutoExecute;
            var notifications = new NotificationService(null);
            _live = new(notifications, settings, null);
            _automation = new(notifications, settings, new(Path.Combine(_directory, "history.json")),
                new(Path.Combine(_directory, "mappings.json")), () => { });
            Commands = new(null, null, null, settings, notifications, _live, _automation);
            _controller = new(Request, () => "fixture-account-owner", () => _session.Token,
                () => Commands.ConnectorUserRequest, (announcement, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    if (announcement.CanPublish) Notice.TrySetResult(true);
                    return Task.FromResult(true);
                }, userRequestIdentity: () => Commands.ConnectorUserRequestIdentity,
                logicalLiveSessionId: () => _logicalLiveSessionId,
                reasonRead: guidance ? ReasonRead : quota ? (_, _, _, _, _, _, _) =>
                {
                    Interlocked.Increment(ref ReasoningCalls);
                    throw new PersonalAiException(PersonalAiFailure.Quota);
                } : null, userRequestSnapshot: () => Commands.ConnectorUserRequestSnapshot);
            _automation.Connectors = _controller;
            _live.SetToolExecutor(Commands.ExecuteJarvisToolAsync);
            _live.SetUserTranscriptObserver(Commands.ObserveJarvisTranscript,
                Commands.ObserveImmediateJarvisControl, Commands.CompleteJarvisUserTurn);
            typeof(GeminiLiveConversationService).GetField("_sessionCts", PrivateInstance)!.SetValue(_live, _session);
            var diagnosticsType = typeof(GeminiLiveConversationService).GetNestedType("ProviderSocketDiagnostics", BindingFlags.NonPublic)!;
            _diagnostics = Activator.CreateInstance(diagnosticsType, "connector-answer-fixture", 1, DateTimeOffset.UtcNow, 0, false)!;
            diagnosticsType.GetProperty("UsesPersonalToolProtocol")!.SetValue(_diagnostics, personal);
            _dispatcher = new(_session.Token, (name, args, token) =>
                (Task<Dictionary<string, object>>)_execute.Invoke(_live, [name, args, token])!,
                (build, onSent, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    var response = build();
                    if (response is not null)
                        foreach (var receipt in JsonSerializer.SerializeToElement(response).GetProperty("toolResponse").GetProperty("functionResponses").EnumerateArray())
                            _receipts[receipt.GetProperty("id").GetString()!] = receipt.GetProperty("response").Clone();
                    onSent();
                    return Task.CompletedTask;
                });
            diagnosticsType.GetProperty("ToolCalls")!.SetValue(_diagnostics, _dispatcher);
        }

        internal async Task Resolve()
        {
            await Input(Original);
            var receipt = await Tool("resolve", "connector_resolve", new { connector_id = "gmail" });
            Assert.Equal(TaskId, receipt.GetProperty("task_id").GetString());
        }

        internal async Task Input(string text)
        {
            Push(JsonSerializer.Serialize(new { serverContent = new { inputTranscription = new { text } } }));
            await Drain();
        }

        internal async Task<JsonElement> Tool(string id, string name, object args, string prematureAnswer = null)
        {
            var frame = new Dictionary<string, object>
            {
                ["toolCall"] = new { functionCalls = new[] { new { id, name, args } } }
            };
            if (!_personal || prematureAnswer is not null)
            {
                var content = new Dictionary<string, object> { ["turnComplete"] = true };
                if (prematureAnswer is not null) content["outputTranscription"] = new { text = prematureAnswer };
                frame["serverContent"] = content;
            }
            Push(JsonSerializer.Serialize(frame));
            await Drain();
            Assert.True(_receipts.TryGetValue(id, out var receipt), "The real dispatcher did not publish this tool's receipt.");
            return receipt;
        }

        internal Task<JsonElement> Collect(string operationId) => Tool(Guid.NewGuid().ToString("N"), "connector_control",
            new { task_id = TaskId, action = "result", operation_id = operationId });
        internal void Output(string text) => Push(JsonSerializer.Serialize(new { serverContent = new { outputTranscription = new { text } } }));
        internal void TurnComplete() => Push("""{"serverContent":{"turnComplete":true}}""");
        internal async Task Answer(string text)
        {
            Output(text);
            TurnComplete();
            await Drain();
        }
        internal Task Drain() => _dispatcher.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        internal void ReleaseBackend() => _backend.TrySetResult(CompletedResult());
        internal void ReleaseReasoning() => _reasonRelease.TrySetResult(true);
        private void Push(string frame) => _handle.Invoke(_live, [frame, new TaskCompletionSource<bool>(), _diagnostics, _session.Token]);

        private async Task<JsonElement> Request(HttpMethod method, string path, object body, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (path == "v1/connectors/resolve")
            {
                Interlocked.Increment(ref Resolves);
                return JsonSerializer.SerializeToElement(new
                {
                    taskId = TaskId, state = "ready", connectorId = "gmail", accountId = "fixture-gmail-account",
                    expiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
                    capabilities = new[] { new { id = "mail_search", writes = false, activity = "Searching Gmail…",
                        arguments = new Dictionary<string, string> { ["query"] = "search terms", ["matchMode"] = "exact or topic" } },
                        new { id = "mail_read", writes = false, activity = "Reading Gmail…",
                            arguments = new Dictionary<string, string> { ["id"] = "observed message ID" } } }
                });
            }
            if (path == "v1/connectors/execute")
            {
                Interlocked.Increment(ref BackendExecutions);
                WorkToken = token;
                WorkStarted.TrySetResult(true);
                if (_guidance)
                {
                    var payload = JsonSerializer.SerializeToElement(body);
                    Assert.Equal(TaskId, payload.GetProperty("taskId").GetString());
                    Assert.Equal(_logicalLiveSessionId, payload.GetProperty("logicalLiveSessionId").GetString());
                    return payload.GetProperty("capabilityId").GetString() == "mail_search"
                        ? JsonSerializer.SerializeToElement(new { state = "completed", verified = true, result = new { truncated = false,
                            data = new { queryUsed = "biocyclic winner", pageItemCount = 1, hasMore = false,
                                messages = new[] { new { id = "message-1", threadId = "thread-1" } } } } })
                        : JsonSerializer.SerializeToElement(new { state = "completed", verified = true, result = new { truncated = false,
                            data = new { id = "message-1", threadId = "thread-1", contentTruncated = false,
                                text = "The 2026 BioCyclic competition prize is Top 10 Best Performers." } } });
                }
                return _holdBackend ? await _backend.Task.WaitAsync(token) : CompletedResult();
            }
            if (path == "v1/connectors/tasks/" + TaskId + "/complete")
            {
                Completions.Enqueue(TaskId);
                return JsonSerializer.SerializeToElement(new { state = "completed" });
            }
            if (path == "v1/connectors/tasks/" + TaskId + "/cancel")
                return JsonSerializer.SerializeToElement(new { state = "cancelled" });
            throw new InvalidOperationException("Unexpected fixture backend route: " + path);
        }

        private async Task<string> ReasonRead(string task, string logical, string instruction, string prompt, object schema, int limit, CancellationToken token)
        {
            Assert.Equal(TaskId, task); Assert.Equal(_logicalLiveSessionId, logical); Assert.Equal(2400, limit);
            var call = Interlocked.Increment(ref ReasoningCalls);
            if (call == 1) return JsonSerializer.Serialize(new { action = "execute", capability = "mail_read", arguments_json = "{\"id\":\"message-1\"}",
                answer = "", references = Array.Empty<string>(), partial = false });
            if (call == 2) { ReasoningHeld.TrySetResult(true); await _reasonRelease.Task.WaitAsync(token); }
            else LatestReasoningPrompt.TrySetResult(prompt);
            return JsonSerializer.Serialize(new { action = "answer", capability = "", arguments_json = "{}",
                answer = call == 2 ? OldAnswer : "The observed email concerns 2026; the requested 2025 winner was not verified.",
                references = new[] { "r2" }, partial = call > 2 });
        }

        private static JsonElement CompletedResult() => JsonSerializer.SerializeToElement(new
        {
            state = "completed", verified = true,
            result = new { data = new { id = "fixture-invoice", text = "An isolated test invoice." } }
        });

        public async ValueTask DisposeAsync()
        {
            _session.Cancel();
            _dispatcher.Stop();
            ReleaseBackend();
            ReleaseReasoning();
            await Drain();
            await _controller.ResetAsync();
            _dispatcher.Dispose();
            _automation.Dispose();
            _live.Dispose();
            _session.Dispose();
            KeyboardWtfState.SetUi(VoiceUiPhase.Idle, "keyboard.wtf", "Ready");
            var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "keyboard-wtf-tests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(_directory).StartsWith(parent, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Fixture cleanup containment failed");
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
    }
}
