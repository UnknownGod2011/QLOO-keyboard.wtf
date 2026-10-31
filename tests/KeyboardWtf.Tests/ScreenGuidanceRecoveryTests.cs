using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.Json;
using KeyboardWtf.Models;
using KeyboardWtf.Services;
using Xunit;

namespace KeyboardWtf.Tests;

[Collection("Live event integration")]
public sealed class ScreenGuidanceRecoveryTests
{
    [Fact]
    public void Default_analysis_has_no_local_twenty_second_deadline()
    {
        Assert.Equal(Timeout.InfiniteTimeSpan, GeminiScreenGuidanceService.AnalysisTimeout);
    }

    [Fact]
    public async Task Timeout_leaves_terminal_feedback_instead_of_stale_analysis_ui()
    {
        await Assert.ThrowsAsync<TimeoutException>(() => GeminiScreenGuidanceService.CompleteAnalysisAsync(
            token => Task.Delay(Timeout.InfiniteTimeSpan, token).ContinueWith(_ => "", token),
            CancellationToken.None, TimeSpan.FromMilliseconds(30)));
        Assert.Equal(VoiceUiPhase.Error, KeyboardWtfState.UiPhase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Screen_completion_cannot_replace_newer_conversation_ui(bool fail)
    {
        var provider = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = GeminiScreenGuidanceService.CompleteAnalysisAsync(_ => provider.Task, CancellationToken.None);
        KeyboardWtfState.SetUi(VoiceUiPhase.Listening, "Jarvis", "New conversation turn");
        if (fail) provider.SetException(new IOException("Provider unavailable"));
        else provider.SetResult("Screen details");
        if (fail) await Assert.ThrowsAsync<IOException>(() => pending);
        else Assert.Equal("Screen details", await pending);
        Assert.Equal(VoiceUiPhase.Listening, KeyboardWtfState.UiPhase);
        Assert.Equal("New conversation turn", KeyboardWtfState.UiDetail);
    }

    [Fact]
    public async Task Slow_screen_provider_releases_existing_tool_queue_without_late_result()
    {
        var provider = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextRan = false;
        CancellationToken providerToken = default;
        var published = new ConcurrentQueue<string>();
        using var queue = new LiveToolCallDispatcher(CancellationToken.None,
            async (name, _, token) =>
            {
                if (name == "inspect_screen")
                    await GeminiScreenGuidanceService.CompleteAnalysisAsync(t =>
                    {
                        providerToken = t;
                        started.TrySetResult();
                        return provider.Task; // Deliberately ignores cancellation.
                    }, token, TimeSpan.FromMilliseconds(100));
                else nextRan = true;
                return new Dictionary<string, object> { ["ok"] = true };
            },
            (build, _) => { published.Enqueue(JsonSerializer.Serialize(build())); return Task.CompletedTask; });
        var guard = new ToolAttemptGuard();
        queue.Enqueue(Call("screen", "inspect_screen"), guard);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        queue.Enqueue(Call("next", "next_action"), guard);
        await queue.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(nextRan);
        Assert.True(providerToken.IsCancellationRequested);
        Assert.Equal(0, queue.PendingCount);
        Assert.Equal(2, published.Count);
        var screenResponse = JsonDocument.Parse(published.First()).RootElement
            .GetProperty("toolResponse").GetProperty("functionResponses")[0].GetProperty("response");
        Assert.False(screenResponse.GetProperty("ok").GetBoolean());
        provider.SetResult("Late screen details must not be published");
        await Task.Yield();
        Assert.Equal(2, published.Count);
        Assert.DoesNotContain(published, p => p.Contains("Late screen details"));
    }

    [Fact]
    public async Task User_cancellation_is_not_misreported_as_provider_timeout()
    {
        using var caller = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = GeminiScreenGuidanceService.CompleteAnalysisAsync(async token =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return "unreachable";
        }, caller.Token, TimeSpan.FromSeconds(5));
        await started.Task;
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task Successful_analysis_preserves_the_complete_answer()
    {
        var answer = new string('a', 5000);
        Assert.Equal(answer, await GeminiScreenGuidanceService.CompleteAnalysisAsync(
            _ => Task.FromResult(answer), CancellationToken.None));
        Assert.Equal(2048, GeminiScreenGuidanceService.OutputTokenLimit);
    }

    [Fact]
    public async Task Transient_dns_failure_retries_the_same_analysis_once()
    {
        var attempts = 0;
        var answer = await GeminiScreenGuidanceService.RetryNameResolutionOnceAsync(
            _ => ++attempts == 1
                ? Task.FromException<string>(new HttpRequestException(
                    "DNS unavailable", new SocketException((int)SocketError.HostNotFound)))
                : Task.FromResult("Complete screen analysis"),
            CancellationToken.None, TimeSpan.Zero);
        Assert.Equal("Complete screen analysis", answer);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task Non_dns_or_repeated_failure_is_not_retried_indefinitely()
    {
        var permanentAttempts = 0;
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            GeminiScreenGuidanceService.RetryNameResolutionOnceAsync<string>(
                _ =>
                {
                    permanentAttempts++;
                    return Task.FromException<string>(new HttpRequestException("HTTP error"));
                }, CancellationToken.None, TimeSpan.Zero));
        Assert.Equal(1, permanentAttempts);

        var dnsAttempts = 0;
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            GeminiScreenGuidanceService.RetryNameResolutionOnceAsync<string>(
                _ =>
                {
                    dnsAttempts++;
                    return Task.FromException<string>(new HttpRequestException(
                        "DNS unavailable", new SocketException((int)SocketError.HostNotFound)));
                }, CancellationToken.None, TimeSpan.Zero));
        Assert.Equal(2, dnsAttempts);
    }

    private static JsonElement Call(string id, string name) => JsonSerializer.SerializeToElement(new
    {
        functionCalls = new[] { new { id, name, args = new { } } },
    });
}
