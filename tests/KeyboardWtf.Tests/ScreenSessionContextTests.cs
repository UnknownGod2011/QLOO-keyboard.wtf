using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using KeyboardWtf.Services;
using Xunit;

namespace KeyboardWtf.Tests;

// Injected bytes and analysis only: no native capture, settings, keys, sockets, or providers.
[Collection("Live event integration")]
public sealed class ScreenSessionContextTests
{
    [Fact]
    public async Task Fresh_analysis_captures_once_and_returns_the_full_guidance_and_context()
    {
        using var run = new ScreenReplay();
        var guidance = new string('g', 5000);
        run.Analyze = (_, _, _) => Task.FromResult(guidance);

        var result = await run.Fresh("Read the approved screen");

        Assert.Equal(guidance, result.Guidance);
        Assert.False(result.Reused);
        Assert.True(Guid.TryParseExact(result.ContextId, "N", out _));
        Assert.NotEqual(default, result.CapturedUtc);
        Assert.Equal(TimeSpan.Zero, result.CapturedUtc.Offset);
        Assert.Equal(1, run.Captures);
        Assert.Equal(1, run.Analyses);
        Assert.Equal(ScreenReplay.Image(1), Assert.Single(run.Requests).Image);
    }

    [Fact]
    public async Task Follow_up_uses_the_same_complete_image_and_identity_with_the_new_question()
    {
        using var run = new ScreenReplay();
        var first = await run.Fresh("Summarize the labels");

        var followUp = await run.Reuse("What was the third label?", first.ContextId);

        Assert.True(followUp.Reused);
        Assert.Equal(first.ContextId, followUp.ContextId);
        Assert.Equal(first.CapturedUtc, followUp.CapturedUtc);
        Assert.Equal("Guidance: What was the third label?", followUp.Guidance);
        Assert.Equal(1, run.Captures);
        Assert.Equal(2, run.Analyses);
        var requests = run.Requests.ToArray();
        Assert.Equal("What was the third label?", requests[1].Question);
        Assert.Equal(requests[0].Image, requests[1].Image);
        Assert.Equal(ScreenReplay.Image(1), requests[1].Image);
    }

    [Fact]
    public async Task Explicit_fresh_analysis_replaces_the_previous_reusable_context()
    {
        using var run = new ScreenReplay();
        var first = await run.Fresh("First screen");
        var second = await run.Fresh("Look at the current screen");

        Assert.False(second.Reused);
        Assert.NotEqual(first.ContextId, second.ContextId);
        Assert.Equal(2, run.Captures);
        Assert.Equal(2, run.Analyses);
        await Assert.ThrowsAsync<InvalidOperationException>(() => run.Reuse("Old details", first.ContextId));
        Assert.Equal(2, run.Captures);
        Assert.Equal(2, run.Analyses);

        var reused = await run.Reuse("Current details", second.ContextId);
        Assert.Equal(second.ContextId, reused.ContextId);
        Assert.Equal(ScreenReplay.Image(2), run.Requests.Last().Image);
        Assert.Equal(2, run.Captures);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unknown_context_id_never_falls_back_to_capture_or_provider(bool seedContext)
    {
        using var run = new ScreenReplay();
        if (seedContext) await run.Fresh("Existing screen");
        var captures = run.Captures;
        var analyses = run.Analyses;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            run.Reuse("Use the missing image", "missing-context-id"));

        Assert.Equal(captures, run.Captures);
        Assert.Equal(analyses, run.Analyses);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public async Task Present_empty_context_id_does_not_silently_capture(string contextId)
    {
        using var run = new ScreenReplay();

        await Assert.ThrowsAsync<InvalidOperationException>(() => run.Reuse("Prior screen", contextId));

        Assert.Equal(0, run.Captures);
        Assert.Equal(0, run.Analyses);
    }

    [Fact]
    public async Task Context_id_is_exact_and_is_not_trimmed_into_a_different_request()
    {
        using var run = new ScreenReplay();
        var first = await run.Fresh("Approved image");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            run.Reuse("Prior screen", first.ContextId + " "));

        Assert.Equal(1, run.Captures);
        Assert.Equal(1, run.Analyses);
    }

    [Fact]
    public async Task Stateless_settings_analysis_does_not_seed_or_replace_session_context()
    {
        using var run = new ScreenReplay();
        Assert.Equal("Guidance: Settings first", await run.Service.InspectAsync("Settings first", CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => run.Reuse("Settings recall", "missing-context-id"));
        Assert.Equal(1, run.Captures);
        Assert.Equal(1, run.Analyses);

        var session = await run.Fresh("Jarvis image");
        await run.Service.InspectAsync("Settings later", CancellationToken.None);
        var recalled = await run.Reuse("Jarvis follow-up", session.ContextId);

        Assert.True(recalled.Reused);
        Assert.Equal(session.ContextId, recalled.ContextId);
        Assert.Equal(session.CapturedUtc, recalled.CapturedUtc);
        Assert.Equal(ScreenReplay.Image(2), run.Requests.Last().Image);
        Assert.Equal(3, run.Captures);
        Assert.Equal(4, run.Analyses);
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("empty")]
    [InlineData("whitespace")]
    public async Task Failed_first_analysis_does_not_create_reusable_context(string failure)
    {
        using var run = new ScreenReplay();
        run.Analyze = (_, _, _) => FailedAnalysis(failure);

        await AssertAnalysisFailure(failure, () => run.Fresh("Failed image"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => run.Reuse("Recall failure", "missing-context-id"));

        Assert.Equal(1, run.Captures);
        Assert.Equal(1, run.Analyses);
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("empty")]
    [InlineData("whitespace")]
    public async Task Failed_new_analysis_keeps_the_previous_success_without_an_automatic_fallback(string failure)
    {
        using var run = new ScreenReplay();
        var previous = await run.Fresh("Successful old image");
        run.Analyze = (question, _, _) => question == "Failed new image"
            ? FailedAnalysis(failure) : Task.FromResult("Explicit old-image answer");

        await AssertAnalysisFailure(failure, () => run.Fresh("Failed new image"));
        Assert.Equal(2, run.Captures);
        Assert.Equal(2, run.Analyses);
        Assert.Equal(ScreenReplay.Image(2), run.Requests.Last().Image);

        var recalled = await run.Reuse("Explicitly reuse the old image", previous.ContextId);
        Assert.Equal("Explicit old-image answer", recalled.Guidance);
        Assert.Equal(previous.ContextId, recalled.ContextId);
        Assert.Equal(previous.CapturedUtc, recalled.CapturedUtc);
        Assert.Equal(ScreenReplay.Image(1), run.Requests.Last().Image);
        Assert.Equal(2, run.Captures);
        Assert.Equal(3, run.Analyses);
    }

    [Fact]
    public async Task Failed_reuse_preserves_the_same_previously_approved_image()
    {
        using var run = new ScreenReplay();
        var previous = await run.Fresh("Initial image");
        run.Analyze = (question, _, _) => question == "Failed follow-up"
            ? Task.FromException<string>(new IOException("Offline provider failure")) : Task.FromResult("Recovered details");

        await Assert.ThrowsAsync<IOException>(() => run.Reuse("Failed follow-up", previous.ContextId));
        var recovered = await run.Reuse("Another follow-up", previous.ContextId);

        Assert.Equal(previous.ContextId, recovered.ContextId);
        Assert.Equal(previous.CapturedUtc, recovered.CapturedUtc);
        Assert.Equal(ScreenReplay.Image(1), run.Requests.Last().Image);
        Assert.Equal(1, run.Captures);
        Assert.Equal(3, run.Analyses);
    }

    [Fact]
    public async Task Already_cancelled_request_does_not_capture_or_analyze()
    {
        using var run = new ScreenReplay();
        using var caller = new CancellationTokenSource();
        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            run.Service.InspectSessionAsync("Cancelled", null, caller.Token));

        Assert.Equal(0, run.Captures);
        Assert.Equal(0, run.Analyses);
    }

    [Fact]
    public async Task Caller_cancellation_during_analysis_does_not_cache_a_successful_ignored_result()
    {
        using var run = new ScreenReplay();
        using var caller = new CancellationTokenSource();
        run.Analyze = (_, _, _) =>
        {
            caller.Cancel();
            return Task.FromResult("Ignored cancelled answer");
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            run.Service.InspectSessionAsync("Cancelled image", null, caller.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => run.Reuse("Recall cancelled image", "missing-context-id"));

        Assert.Equal(1, run.Captures);
        Assert.Equal(1, run.Analyses);
    }

    [Fact]
    public async Task Clear_evicts_the_previous_context_and_allows_a_new_explicit_capture()
    {
        using var run = new ScreenReplay();
        var old = await run.Fresh("Old session image");

        run.Service.ClearSessionContext();
        await Assert.ThrowsAsync<InvalidOperationException>(() => run.Reuse("Old session recall", old.ContextId));
        Assert.Equal(1, run.Captures);
        Assert.Equal(1, run.Analyses);

        var fresh = await run.Fresh("New session image");
        Assert.NotEqual(old.ContextId, fresh.ContextId);
        Assert.Equal(2, run.Captures);
        Assert.Equal(2, run.Analyses);
    }

    [Fact]
    public async Task Disposal_evicts_context_and_rejects_every_new_request_before_capture()
    {
        using var run = new ScreenReplay();
        var old = await run.Fresh("Before dispose");

        run.Service.Dispose();
        run.Service.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => run.Reuse("After dispose", old.ContextId));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => run.Fresh("Fresh after dispose"));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => run.Service.InspectAsync("Settings after dispose", CancellationToken.None));

        Assert.Equal(1, run.Captures);
        Assert.Equal(1, run.Analyses);
    }

    [Fact]
    public async Task Late_ignored_cancellation_cannot_overwrite_a_newer_success()
    {
        using var run = new ScreenReplay();
        using var caller = new CancellationTokenSource();
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = Signal<string>();
        var exited = Signal<bool>();
        run.Analyze = async (question, _, token) =>
        {
            if (question != "Cancelled slow image") return "Current successful guidance";
            entered.TrySetResult(token);
            try { return await release.Task; }
            finally { exited.TrySetResult(true); }
        };
        var pending = run.Service.InspectSessionAsync("Cancelled slow image", null, caller.Token);
        var providerToken = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        var current = await run.Fresh("New successful image");

        release.SetResult("Late cancelled guidance");
        await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var recalled = await run.Reuse("Recall current image", current.ContextId);

        Assert.True(providerToken.IsCancellationRequested);
        Assert.Equal(current.ContextId, recalled.ContextId);
        Assert.Equal("Current successful guidance", recalled.Guidance);
        Assert.Equal(ScreenReplay.Image(2), run.Requests.Last().Image);
        Assert.Equal(2, run.Captures);
        Assert.Equal(3, run.Analyses);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Late_success_after_session_clear_or_dispose_cannot_resurrect_context(bool dispose)
    {
        using var run = new ScreenReplay();
        var entered = Signal<bool>();
        var release = Signal<string>();
        run.Analyze = (question, _, _) =>
        {
            if (question != "Old pending image") return Task.FromResult("New-session guidance");
            entered.TrySetResult(true);
            return release.Task;
        };
        var pending = run.Fresh("Old pending image");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (dispose) run.Service.Dispose();
        else run.Service.ClearSessionContext();
        var current = dispose ? null : await run.Fresh("New session image");

        release.SetResult("Old late guidance");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));

        if (dispose)
        {
            await Assert.ThrowsAsync<ObjectDisposedException>(() => run.Fresh("Cannot resurrect disposed service"));
            Assert.Equal(1, run.Captures);
            Assert.Equal(1, run.Analyses);
        }
        else
        {
            var recalled = await run.Reuse("Current session details", current.ContextId);
            Assert.Equal(current.ContextId, recalled.ContextId);
            Assert.Equal(ScreenReplay.Image(2), run.Requests.Last().Image);
            Assert.Equal(2, run.Captures);
            Assert.Equal(3, run.Analyses);
        }
    }

    [Fact]
    public async Task Newer_success_wins_when_an_older_analysis_finishes_later()
    {
        using var run = new ScreenReplay();
        var entered = Signal<bool>();
        var release = Signal<string>();
        run.Analyze = (question, _, _) =>
        {
            if (question != "Older slow image") return Task.FromResult("Newer-image guidance");
            entered.TrySetResult(true);
            return release.Task;
        };
        var olderPending = run.Fresh("Older slow image");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var newer = await run.Fresh("Newer fast image");

        release.SetResult("Older late guidance");
        var older = await olderPending.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => run.Reuse("Unavailable old image", older.ContextId));
        var recalled = await run.Reuse("Latest image details", newer.ContextId);

        Assert.Equal(newer.ContextId, recalled.ContextId);
        Assert.Equal(ScreenReplay.Image(2), run.Requests.Last().Image);
        Assert.Equal(2, run.Captures);
        Assert.Equal(3, run.Analyses);
    }

    [Fact]
    public async Task A_newer_failed_capture_does_not_disqualify_an_older_pending_success()
    {
        using var run = new ScreenReplay();
        var entered = Signal<bool>();
        var release = Signal<string>();
        run.Analyze = (question, _, _) =>
        {
            if (question == "Newer failed image") return Task.FromException<string>(new IOException("Offline failure"));
            if (question != "Older pending success") return Task.FromResult("Successful image details");
            entered.TrySetResult(true);
            return release.Task;
        };
        var pending = run.Fresh("Older pending success");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<IOException>(() => run.Fresh("Newer failed image"));

        release.SetResult("Earlier capture succeeded");
        var successful = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        var recalled = await run.Reuse("Keep the latest successful image", successful.ContextId);

        Assert.Equal(successful.ContextId, recalled.ContextId);
        Assert.Equal(successful.CapturedUtc, recalled.CapturedUtc);
        Assert.Equal(ScreenReplay.Image(1), run.Requests.Last().Image);
        Assert.Equal(2, run.Captures);
        Assert.Equal(3, run.Analyses);
    }

    [Fact]
    public async Task Only_the_latest_successful_snapshot_is_reusable_after_thirty_capture_cycles()
    {
        using var run = new ScreenReplay();
        var contexts = new List<GeminiScreenGuidanceService.ScreenAnalysis>();
        for (var index = 0; index < 30; index++) contexts.Add(await run.Fresh("Explicit image " + index));

        foreach (var context in contexts.Take(29))
            await Assert.ThrowsAsync<InvalidOperationException>(() => run.Reuse("Expired image", context.ContextId));
        Assert.Equal(30, run.Captures);
        Assert.Equal(30, run.Analyses);

        var current = contexts.Last();
        var recalled = await run.Reuse("Latest image details", current.ContextId);
        Assert.Equal(current.ContextId, recalled.ContextId);
        Assert.Equal(ScreenReplay.Image(30), run.Requests.Last().Image);
        Assert.Equal(30, run.Captures);
        Assert.Equal(31, run.Analyses);
    }

    [Fact]
    public void Live_screen_tool_requires_only_request_and_exposes_an_optional_exact_context_id()
    {
        var buildTools = typeof(GeminiLiveConversationService).GetMethod("BuildTools", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(buildTools);
        var declaration = JsonSerializer.SerializeToElement(buildTools.Invoke(null, null))[0]
            .GetProperty("functionDeclarations").EnumerateArray()
            .Single(tool => tool.GetProperty("name").GetString() == "inspect_screen");
        var schema = declaration.GetProperty("parameters");
        var properties = schema.GetProperty("properties");

        Assert.Equal(new[] { "request" }, schema.GetProperty("required").EnumerateArray().Select(value => value.GetString()).ToArray());
        Assert.Equal(new[] { "request", "screen_context_id" }, properties.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray());
        Assert.Equal("string", properties.GetProperty("request").GetProperty("type").GetString());
        Assert.Equal("string", properties.GetProperty("screen_context_id").GetProperty("type").GetString());
        Assert.Contains("exact", properties.GetProperty("screen_context_id").GetProperty("description").GetString());
    }

    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Task<string> FailedAnalysis(string failure) => failure switch
    {
        "provider" => Task.FromException<string>(new IOException("Offline provider failure")),
        "empty" => Task.FromResult(""),
        _ => Task.FromResult(" \t\r\n"),
    };

    private static async Task AssertAnalysisFailure(string failure, Func<Task<GeminiScreenGuidanceService.ScreenAnalysis>> analyze)
    {
        if (failure == "provider") await Assert.ThrowsAsync<IOException>(analyze);
        else await Assert.ThrowsAsync<InvalidDataException>(analyze);
    }

    private sealed record AnalysisRequest(string Question, byte[] Image);

    private sealed class ScreenReplay : IDisposable
    {
        private int _captures;
        private int _analyses;
        internal int Captures => Volatile.Read(ref _captures);
        internal int Analyses => Volatile.Read(ref _analyses);
        internal ConcurrentQueue<AnalysisRequest> Requests { get; } = new();
        internal Func<string, byte[], CancellationToken, Task<string>> Analyze { get; set; } =
            (question, _, _) => Task.FromResult("Guidance: " + question);
        internal GeminiScreenGuidanceService Service { get; }

        internal ScreenReplay()
        {
            Service = new GeminiScreenGuidanceService(paidAccount: null,
                capture: _ => Image(Interlocked.Increment(ref _captures)),
                analyze: (question, image, token, _) =>
                {
                    Interlocked.Increment(ref _analyses);
                    Requests.Enqueue(new AnalysisRequest(question, image.ToArray()));
                    return Analyze(question, image, token);
                });
        }

        internal Task<GeminiScreenGuidanceService.ScreenAnalysis> Fresh(string question) =>
            Service.InspectSessionAsync(question, null, CancellationToken.None);

        internal Task<GeminiScreenGuidanceService.ScreenAnalysis> Reuse(string question, string contextId) =>
            Service.InspectSessionAsync(question, contextId, CancellationToken.None);

        internal static byte[] Image(int capture) => Enumerable.Range(0, 512)
            .Select(index => (byte)((index + capture * 17) % 256)).ToArray();

        public void Dispose() => Service.Dispose();
    }
}
