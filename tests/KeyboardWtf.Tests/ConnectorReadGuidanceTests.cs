namespace KeyboardWtf.Tests;

using System.Text.Json;
using KeyboardWtf.Models;
using KeyboardWtf.Services;
using KeyboardWtf.Services.Connectors;
using Xunit;

[Collection("Live event integration")]
public sealed class ConnectorReadGuidanceTests
{
    private const string Original = "Find and read the BioCyclic winner email and explain the prize";
    private const string Guidance = "Only the 2025 competition email";
    private const string OldAnswer = "The 2026 competition awarded Top 10 Best Performers.";

    [Theory]
    [InlineData("Show only header details like subject, from, and date; do not read or summarize email bodies.", true)]
    [InlineData("Read all bodies and send a new message.", true)]
    [InlineData("headers only, no bodies.", false)]
    public async Task Observed_header_restriction_survives_model_paraphrase_and_later_answer_boundary(string proposed, bool includeRevision)
    {
        await using var f = new Fixture(holdSearch: true, holdModel: 0);
        await f.Resolve();
        var execution = f.ExecuteRead();
        await f.SearchStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        const string human = "Only headers, do not read bodies.";
        f.Commands.ObserveSubmittedJarvisText(human, f.Session.Token);
        var guide = await f.Guide(proposed, includeRevision);
        Assert.True(guide.GetValueOrDefault("ok") is true, JsonSerializer.Serialize(guide));
        Assert.Equal(1, guide["goal_revision"]);
        // v42 acknowledged the update in a different Live reply before the
        // original native result arrived. The restriction must outlive that
        // reply's routing text and must never adopt a model-supplied widening.
        f.Commands.CompleteJarvisUserTurn(true);
        f.ReleaseSearch();
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(result.GetValueOrDefault("metadata_only") is true, JsonSerializer.Serialize(result));
        Assert.DoesNotContain(OldAnswer, JsonSerializer.Serialize(result));
        Assert.Equal(0, f.Bodies);
        Assert.Equal(1, f.Searches);
        Assert.Equal(1, f.Resolves);
        Assert.All(f.ProviderTasks, task => Assert.Equal(f.TaskId, task));
        Assert.All(f.ProviderAccounts, account => Assert.Equal(f.AccountId, account));
    }

    [Theory]
    [InlineData("Only headers. Check today instead.")]
    [InlineData("Only headers. Use exact query: subject:\"Unaccepted C\"")]
    public async Task Missing_revision_can_reduce_content_permission_but_cannot_replace_search_scope(string human)
    {
        await using var f = new Fixture(holdSearch: true, holdModel: 0);
        await f.Resolve(); var execution = f.ExecuteRead();
        await f.SearchStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        f.Commands.ObserveSubmittedJarvisText(human, f.Session.Token);
        var guide = await f.Guide(human, includeRevision: false);
        Assert.True(guide.GetValueOrDefault("ok") is true);
        Assert.True(guide.GetValueOrDefault("content_only") is true);
        f.ReleaseSearch(); var result = await execution.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(result.GetValueOrDefault("metadata_only") is true);
        Assert.Equal(0, f.Bodies); Assert.Equal(1, f.Searches);
        Assert.All(f.Prompts, prompt => Assert.DoesNotContain(human, prompt, StringComparison.Ordinal));
        Assert.DoesNotContain("Unaccepted C", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restrictive_guide_cannot_replace_an_explicit_stale_revision()
    {
        await using var f = new Fixture(holdSearch: true, holdModel: 0);
        await f.Resolve(); var execution = f.ExecuteRead();
        await f.SearchStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        const string human = "Only headers, do not read bodies.";
        f.Commands.ObserveSubmittedJarvisText(human, f.Session.Token);
        var guide = await f.Guide(human, expectedRevision: 99);
        Assert.False(guide.GetValueOrDefault("ok") is true);
        Assert.Equal("guidance_stale", guide["state"]);
        Assert.Equal(0, guide["goal_revision"]);
        Assert.False(f.ReadToken.IsCancellationRequested);
        f.ReleaseSearch(); await execution.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Relevant_human_guidance_during_native_search_reaches_same_reader_without_replacing_task()
    {
        await using var f = new Fixture(holdSearch: true, holdModel: 1);
        await f.Resolve();
        var execution = f.ExecuteRead();
        await f.SearchStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var activity = KeyboardWtfState.LongTaskDisplayState(TimeSpan.FromSeconds(20));
        f.Commands.ObserveSubmittedJarvisText(Guidance, f.Session.Token);
        var guide = await f.Guide(Guidance);
        Assert.Equal(1, f.Resolves);
        Assert.Equal(1, f.Searches);
        Assert.False(f.ReadToken.IsCancellationRequested);
        Assert.Equal(activity, KeyboardWtfState.LongTaskDisplayState(TimeSpan.FromSeconds(20)));
        f.ReleaseSearch();
        var prompt = await f.HeldPrompt.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, f.Resolves);
        Assert.All(f.ProviderTasks, id => Assert.Equal(f.TaskId, id));
        Assert.All(f.ProviderAccounts, id => Assert.Equal(f.AccountId, id));
        // Intended causal RED on G: this unique exact human phrase does not
        // occur in the harmless backend receipts or frozen original request.
        Assert.Contains(Guidance, prompt, StringComparison.Ordinal);
        Assert.Contains("BioCyclic", prompt, StringComparison.Ordinal);
        Assert.True(guide.GetValueOrDefault("ok") is true);
        f.ReleaseModel();
        await execution.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Old_valid_answer_cannot_publish_over_new_relevant_human_guidance()
    {
        await using var f = new Fixture(holdSearch: false, holdModel: 2);
        await f.Resolve();
        var execution = f.ExecuteRead();
        await f.HeldPrompt.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, f.Searches);
        Assert.Equal(1, f.Bodies);
        Assert.Equal(2, f.ModelCalls);
        Assert.True(KeyboardWtfState.HasLongTaskUiOwner);
        f.Commands.ObserveSubmittedJarvisText(Guidance, f.Session.Token);
        var guide = await f.Guide(Guidance);
        Assert.Equal(1, f.Resolves);
        Assert.False(f.ReadToken.IsCancellationRequested);
        f.ReleaseModel(); // Valid supported r2 answer; not malformed JSON.
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(3));
        var connector = Assert.IsType<JsonElement>(result["connector"]);
        var answer = connector.GetProperty("result").GetProperty("data").GetProperty("answer").GetString();
        // Intended causal RED on G: the original r2 answer publishes unchanged.
        Assert.NotEqual(OldAnswer, answer);
        Assert.True(guide.GetValueOrDefault("ok") is true);
        Assert.Contains(Guidance, f.Prompts.Last(), StringComparison.Ordinal);
        Assert.Equal(3, f.ModelCalls); // Discarded inference still consumes budget.
        Assert.Equal(1, f.Resolves);
        Assert.Equal(1, f.Searches);
        Assert.Equal(1, f.Bodies); // Existing complete content was not replayed.
        Assert.All(f.ProviderTasks, id => Assert.Equal(f.TaskId, id));
        Assert.All(f.ProviderAccounts, id => Assert.Equal(f.AccountId, id));
    }

    [Fact]
    public async Task Unrelated_observed_speech_does_not_replace_or_cancel_the_current_reader()
    {
        await using var f = new Fixture(holdSearch: false, holdModel: 2);
        await f.Resolve();
        var execution = f.ExecuteRead();
        var prompt = await f.HeldPrompt.Task.WaitAsync(TimeSpan.FromSeconds(3));
        f.Commands.ObserveSubmittedJarvisText("Open Spotify", f.Session.Token);
        Assert.False(f.ReadToken.IsCancellationRequested);
        Assert.True(KeyboardWtfState.HasLongTaskUiOwner);
        Assert.Equal(1, f.Resolves);
        f.ReleaseModel();
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(3));
        var connector = Assert.IsType<JsonElement>(result["connector"]);
        Assert.Equal(OldAnswer, connector.GetProperty("result").GetProperty("data").GetProperty("answer").GetString());
        Assert.Contains(Original, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Open Spotify", f.Prompts.Last(), StringComparison.Ordinal);
        Assert.Equal(2, f.ModelCalls);
        Assert.Equal(1, f.Resolves);
        Assert.Equal(1, f.Searches);
        Assert.Equal(1, f.Bodies);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "keyboard-wtf-tests", Guid.NewGuid().ToString("N"));
        private readonly bool _holdSearch;
        private readonly int _holdModel;
        private readonly TaskCompletionSource<bool> _searchRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _modelRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly JarvisAutomationService _automation;
        private readonly JarvisConnectorController _controller;
        private readonly Dictionary<string, string> _serverTaskAccounts = new(StringComparer.Ordinal);
        internal CancellationTokenSource Session { get; } = new();
        internal CommandRegistry Commands { get; }
        internal string TaskId { get; } = Guid.NewGuid().ToString("N");
        internal string AccountId { get; } = Guid.NewGuid().ToString("N");
        internal string LogicalLiveSessionId { get; } = Guid.NewGuid().ToString("N");
        internal TaskCompletionSource<bool> SearchStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<string> HeldPrompt { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal List<string> Prompts { get; } = [];
        internal List<string> ProviderTasks { get; } = [];
        internal List<string> ProviderAccounts { get; } = [];
        internal CancellationToken ReadToken;
        internal int Resolves, Searches, Bodies, ModelCalls;
        internal Fixture(bool holdSearch, int holdModel)
        {
            _holdSearch = holdSearch; _holdModel = holdModel;
            var settings = new SettingsService(Path.Combine(_directory, "settings.json"));
            settings.Current.JarvisPermissionMode = JarvisPermissionMode.AutoExecute;
            var notifications = new NotificationService(null);
            _automation = new(notifications, settings, new(Path.Combine(_directory, "history.json")),
                new(Path.Combine(_directory, "mappings.json")), () => throw new InvalidOperationException("Fixture refuses Settings opening"));
            Commands = new(null, null, null, settings, notifications, automation: _automation);
            _controller = new(Request, () => "fixture-owner", () => Session.Token, () => Commands.ConnectorUserRequest,
                (_, _) => Task.FromResult(true), userRequestIdentity: () => Commands.ConnectorUserRequestIdentity,
                logicalLiveSessionId: () => LogicalLiveSessionId, reasonRead: Model);
            _automation.Connectors = _controller;
            KeyboardWtfState.SetUi(VoiceUiPhase.Listening, "Jarvis", "Fixture listening");
            Commands.ObserveSubmittedJarvisText(Original, Session.Token);
        }
        internal Task<Dictionary<string, object>> Resolve() => Commands.ExecuteJarvisToolAsync("connector_resolve",
            JsonSerializer.SerializeToElement(new { connector_id = "gmail" }), Session.Token);
        internal Task<Dictionary<string, object>> ExecuteRead() => Commands.ExecuteJarvisToolAsync("connector_execute",
            JsonSerializer.SerializeToElement(new { task_id = TaskId, capability_id = "mail_search", arguments_json = "{\"query\":\"biocyclic winner\"}" }), Session.Token);
        internal Task<Dictionary<string, object>> Guide(string text, bool includeRevision = true, int expectedRevision = 0)
        {
            var args = new Dictionary<string, object> { ["task_id"] = TaskId, ["action"] = "guide", ["instruction"] = text,
                ["request_identity"] = Commands.ConnectorUserRequestIdentity };
            if (includeRevision) args["expected_goal_revision"] = expectedRevision;
            return Commands.ExecuteJarvisToolAsync("connector_control", JsonSerializer.SerializeToElement(args), Session.Token);
        }
        internal void ReleaseSearch() => _searchRelease.TrySetResult(true);
        internal void ReleaseModel() => _modelRelease.TrySetResult(true);
        private async Task<string> Model(string task, string logical, string instruction, string prompt, object schema, int limit, CancellationToken token)
        {
            Assert.Equal(TaskId, task);
            Assert.Equal(LogicalLiveSessionId, logical);
            Assert.Equal(2400, limit);
            Prompts.Add(prompt);
            var call = ++ModelCalls;
            if (call == _holdModel) { HeldPrompt.TrySetResult(prompt); await _modelRelease.Task.WaitAsync(token); }
            return call == 1
                ? JsonSerializer.Serialize(new { action = "execute", capability = "mail_read", arguments_json = "{\"id\":\"message-1\"}", answer = "", references = Array.Empty<string>(), partial = false })
                : JsonSerializer.Serialize(new { action = "answer", capability = "", arguments_json = "{}",
                    answer = call == 2 ? OldAnswer : "The observed email concerns 2026; no 2025 competition winner was verified.",
                    references = new[] { "r2" }, partial = call > 2 });
        }
        private async Task<JsonElement> Request(HttpMethod method, string path, object body, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (path == "v1/connectors/resolve")
            {
                ++Resolves;
                _serverTaskAccounts.Add(TaskId, AccountId);
                return JsonSerializer.SerializeToElement(new { taskId = TaskId, connectorId = "gmail", accountId = AccountId,
                    state = "ready", expiresAt = DateTimeOffset.UtcNow.AddMinutes(10), choices = new[] { new { accountId = AccountId, label = "Fixture" } },
                    capabilities = new[] {
                        new { id = "mail_search", writes = false, activity = "Searching Gmail…", description = "Native search",
                            arguments = new Dictionary<string, string> { ["query"] = "search terms", ["matchMode"] = "exact or topic" } },
                        new { id = "mail_read", writes = false, activity = "Reading Gmail…", description = "Observed message content",
                            arguments = new Dictionary<string, string> { ["id"] = "observed message ID" } },
                        // Real Gmail metadata exposes write capabilities too;
                        // their existence alone must not reject a read-only
                        // guidance operation or grant a write to this request.
                        new { id = "mail_send", writes = true, activity = "Sending email…", description = "Existing permission-owned write",
                            arguments = new Dictionary<string, string> { ["to"] = "recipient", ["subject"] = "subject", ["body"] = "body" } } } });
            }
            if (path == "v1/connectors/execute")
            {
                ReadToken = token;
                var payload = JsonSerializer.SerializeToElement(body);
                var executedTask = payload.GetProperty("taskId").GetString()!;
                Assert.True(_serverTaskAccounts.TryGetValue(executedTask, out var selectedAccount));
                Assert.Equal(LogicalLiveSessionId, payload.GetProperty("logicalLiveSessionId").GetString());
                ProviderTasks.Add(executedTask);
                ProviderAccounts.Add(selectedAccount!); // Fake server's resolved binding; execute DTO has no account field.
                var capability = payload.GetProperty("capabilityId").GetString();
                if (capability == "mail_search")
                {
                    ++Searches; SearchStarted.TrySetResult(true);
                    if (_holdSearch) await _searchRelease.Task.WaitAsync(token);
                    return JsonSerializer.SerializeToElement(new { taskId = TaskId, operationId = Guid.NewGuid().ToString("N"), state = "completed", verified = true,
                        result = new { truncated = false, data = new { queryUsed = "biocyclic winner", pageItemCount = 1, hasMore = false,
                            messages = new[] { new { id = "message-1", threadId = "thread-1" } } } } });
                }
                Assert.Equal("mail_read", capability); ++Bodies;
                Assert.Equal("message-1", payload.GetProperty("arguments").GetProperty("id").GetString());
                return JsonSerializer.SerializeToElement(new { taskId = TaskId, operationId = Guid.NewGuid().ToString("N"), state = "completed", verified = true,
                    result = new { truncated = false, data = new { id = "message-1", threadId = "thread-1", contentTruncated = false,
                        text = "The 2026 BioCyclic competition prize is Top 10 Best Performers.",
                        headers = new[] { new { name = "Subject", value = "BioCyclic 2026 winner" } } } } });
            }
            Assert.True(path.EndsWith("/cancel", StringComparison.Ordinal) || path.EndsWith("/complete", StringComparison.Ordinal));
            return JsonSerializer.SerializeToElement(new { state = path.EndsWith("/complete", StringComparison.Ordinal) ? "completed" : "cancelled" });
        }
        public async ValueTask DisposeAsync()
        {
            Session.Cancel();
            await _controller.ResetAsync();
            _automation.Dispose(); Session.Dispose();
            var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "keyboard-wtf-tests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(_directory).StartsWith(parent, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Fixture cleanup containment failed");
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
    }
}
