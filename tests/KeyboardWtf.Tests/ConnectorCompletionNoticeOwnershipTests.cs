namespace KeyboardWtf.Tests;

using System.Text.Json;
using KeyboardWtf.Models;
using KeyboardWtf.Services;
using KeyboardWtf.Services.Ai.Backends;
using KeyboardWtf.Services.Connectors;
using Xunit;

// Exercise the actual delayed announcement object, including its final send
// predicate. A signalled "notice happened" fixture cannot catch this race.
[Collection("Live event integration")]
public sealed class ConnectorCompletionNoticeOwnershipTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Collected_result_invalidates_queued_completion_notice(bool reasoningUnavailable)
    {
        await using var f = new Fixture(reasoningUnavailable);
        var started = await f.Start();
        Assert.True(started.GetValueOrDefault("started") is true);
        f.ReleaseWork();
        var announcement = await f.QueuedNotice.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(announcement.CanPublish);

        // The model may collect through a tool call while the trusted notice
        // waits for the previous turn or outbound send lock.
        var collected = await f.Control("result");
        Assert.True(collected.GetValueOrDefault("ok") is true);
        Assert.False(announcement.CanPublish);
        Assert.Equal(1, f.NativeCalls);
    }

    [Fact]
    public async Task Native_partial_answer_after_503_invalidates_queued_completion_notice()
    {
        await using var f = new Fixture(reasoningUnavailable: true);
        var started = await f.Start();
        Assert.True(started.GetValueOrDefault("started") is true);
        f.ReleaseWork();
        var announcement = await f.QueuedNotice.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True((bool)(await f.Control("result"))["ok"]);
        f.Controller.CompleteSettledUserTurn(f.Identity);
        f.Controller.ObserveCompletedReply("I found the conversation. Would you like more information?", f.Identity);

        // Native evidence is retained although synthesis has terminally failed.
        Assert.True((bool)(await f.Control("recall"))["ok"]);
        Assert.False(announcement.CanPublish);
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly string Id = Guid.NewGuid().ToString("N");
        internal readonly string Identity = "original-human-turn";
        private readonly CancellationTokenSource _session = new();
        private readonly TaskCompletionSource<bool> _work = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _send = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<JarvisLifecycleAnnouncement> QueuedNotice = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly JarvisConnectorController Controller;
        internal int NativeCalls;

        internal Fixture(bool reasoningUnavailable)
        {
            Controller = new(Request, () => "owner", () => _session.Token, () => "Find the relevant email conversation.",
                async (announcement, token) =>
                {
                    QueuedNotice.TrySetResult(announcement);
                    await _send.Task.WaitAsync(token);
                    return announcement.CanPublish;
                }, userRequestIdentity: () => Identity,
                reasonRead: reasoningUnavailable ? async (_, _, _, _, _, _, token) =>
                {
                    await _work.Task.WaitAsync(token);
                    throw new PersonalAiException(PersonalAiFailure.Provider) { HttpStatusCode = 503 };
                } : null);
        }

        internal async Task<Dictionary<string, object>> Start()
        {
            await Controller.ExecuteAsync("connector_resolve", JsonSerializer.SerializeToElement(new { connector_id = "gmail" }), false, default);
            return await Controller.ExecuteAsync("connector_execute", JsonSerializer.SerializeToElement(new
            {
                task_id = Id, capability_id = "mail_search", arguments_json = "{\"query\":\"invoice\"}"
            }), false, default);
        }

        internal void ReleaseWork() => _work.TrySetResult(true);
        internal Task<Dictionary<string, object>> Control(string action) => Controller.ExecuteAsync("connector_control",
            JsonSerializer.SerializeToElement(new { task_id = Id, action }), false, default);

        private async Task<JsonElement> Request(HttpMethod method, string path, object body, CancellationToken token)
        {
            if (path == "v1/connectors/resolve")
                return JsonSerializer.SerializeToElement(new
                {
                    taskId = Id, state = "ready", connectorId = "gmail", accountId = "aabbccddaabbccddaabbccddaabbccdd",
                    expiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
                    choices = new[] { new { accountId = "aabbccddaabbccddaabbccddaabbccdd", label = "reader@example.test" } },
                    capabilities = new[] { new { id = "mail_search", writes = false, activity = "Searching Gmail…",
                        arguments = new Dictionary<string, string> { ["query"] = "search terms", ["matchMode"] = "exact or topic" } } }
                });
            if (path == "v1/connectors/execute")
            {
                Interlocked.Increment(ref NativeCalls);
                await _work.Task.WaitAsync(token);
                return JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
                    result = new { truncated = false, data = new { messages = new[]
                        { new { id = "aabbccdd11", threadId = "aabbccdd22", subject = "Fixture conversation" } } } } });
            }
            return JsonSerializer.SerializeToElement(new { state = "completed" });
        }

        public async ValueTask DisposeAsync()
        {
            _session.Cancel();
            _work.TrySetResult(true); _send.TrySetResult(true);
            await Controller.ResetAsync();
            _session.Dispose();
        }
    }
}
