using System.Net;
using System.Text.Json;
using KeyboardWtf.Staging.Api.Connectors;
using KeyboardWtf.Staging.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace KeyboardWtf.Staging.Api.Tests;

public sealed partial class ConnectorPlatformTests
{
    [Fact]
    public async Task Read_recovery_retries_only_the_initial_GET_under_one_existing_task_and_operation()
    {
        var f = Fixture(); await Connect(f);
        var task = await f.Tasks.ResolveAsync(f.Caller, new("gmail", "Count matching email"), default);
        var endpoints = new List<string>();
        f.Provider.RequestOverride = (endpoint, ct) =>
        {
            endpoints.Add(endpoint);
            return endpoints.Count == 1 ? Task.FromException<JsonElement>(TransientRead())
                : Task.FromResult(EmptyMailPage());
        };

        var reply = await f.Tasks.ExecuteAsync(f.Caller,
            new(task.TaskId, "mail_count", Args(new { query = "topic" })), default);

        Assert.True(reply.Verified); Assert.Equal("completed", reply.State);
        Assert.Equal(task.TaskId, reply.TaskId); Assert.Equal(2, endpoints.Count);
        Assert.Equal(endpoints[0], endpoints[1]); Assert.Equal(0, f.Provider.Writes);
        Assert.Equal("ready", (await f.Tasks.StatusAsync(f.Caller, task.TaskId, default)).State);
        var operation = Assert.Single(await f.Store.OperationsAsync(f.Caller.UserId, task.TaskId, default));
        Assert.Equal(reply.OperationId, operation.Id); Assert.Equal(2, operation.ProviderRequestCount);
        Assert.Equal("completed", operation.State); Assert.Null(operation.ErrorCode); Assert.Null(operation.ResourceId);
    }

    [Fact]
    public async Task Read_recovery_second_transient_failure_settles_without_a_third_attempt()
    {
        var f = Fixture(); await Connect(f);
        var task = await f.Tasks.ResolveAsync(f.Caller, new("gmail", "Count matching email"), default);
        var calls = 0;
        f.Provider.RequestOverride = (endpoint, ct) =>
        {
            calls++; return Task.FromException<JsonElement>(TransientRead());
        };

        var reply = await f.Tasks.ExecuteAsync(f.Caller,
            new(task.TaskId, "mail_count", Args(new { query = "topic" })), default);

        Assert.Equal(2, calls); Assert.False(reply.Verified); Assert.Equal("failed", reply.State); Assert.Null(reply.Result);
        var operation = Assert.Single(await f.Store.OperationsAsync(f.Caller.UserId, task.TaskId, default));
        Assert.Equal(2, operation.ProviderRequestCount); Assert.Equal("unavailable", operation.ErrorCode);
        Assert.Equal("failed", (await f.Tasks.StatusAsync(f.Caller, task.TaskId, default)).State);
    }

    [Theory]
    [InlineData("unavailable", false)]
    [InlineData("rate_limited", false)]
    [InlineData("scope_required", false)]
    [InlineData("reconnect_required", false)]
    [InlineData("outcome_unknown", true)]
    public async Task Read_recovery_never_infers_retry_permission_from_a_customer_error_code(string code, bool unknown)
    {
        var f = Fixture(); await Connect(f);
        var task = await f.Tasks.ResolveAsync(f.Caller, new("gmail", "Count matching email"), default);
        var calls = 0;
        f.Provider.RequestOverride = (endpoint, ct) =>
        {
            calls++; return Task.FromException<JsonElement>(new ConnectorProviderException(code, unknown));
        };

        var reply = await f.Tasks.ExecuteAsync(f.Caller,
            new(task.TaskId, "mail_count", Args(new { query = "topic" })), default);

        Assert.Equal(1, calls); Assert.False(reply.Verified); Assert.Null(reply.Result);
        Assert.Equal(1, Assert.Single(await f.Store.OperationsAsync(f.Caller.UserId, task.TaskId, default)).ProviderRequestCount);
    }

    [Fact]
    public async Task Read_recovery_never_replays_an_unknown_outcome_even_if_the_exception_has_a_retry_flag()
    {
        var f = Fixture(); await Connect(f);
        var task = await f.Tasks.ResolveAsync(f.Caller, new("gmail", "Count matching email"), default);
        var calls = 0;
        f.Provider.RequestOverride = (endpoint, ct) =>
        {
            calls++; return Task.FromException<JsonElement>(new ConnectorProviderException("outcome_unknown", true, true));
        };
        var reply = await f.Tasks.ExecuteAsync(f.Caller,
            new(task.TaskId, "mail_count", Args(new { query = "topic" })), default);
        Assert.Equal(1, calls); Assert.Equal("outcome_unknown", reply.State); Assert.False(reply.Verified);
    }

    [Fact]
    public async Task Read_recovery_long_retry_after_does_not_retry_early_or_wait_unboundedly()
    {
        var f = Fixture(); await Connect(f);
        var task = await f.Tasks.ResolveAsync(f.Caller, new("gmail", "Count matching email"), default);
        var calls = 0;
        f.Provider.RequestOverride = (endpoint, ct) =>
        {
            calls++; return Task.FromException<JsonElement>(TransientRead(TimeSpan.FromMinutes(5)));
        };
        var run = f.Tasks.ExecuteAsync(f.Caller,
            new(task.TaskId, "mail_count", Args(new { query = "topic" })), default);
        var reply = await run.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, calls); Assert.Equal("failed", reply.State); Assert.False(reply.Verified);
    }

    [Fact]
    public async Task Read_recovery_remote_cancel_during_wait_prevents_second_dispatch()
    {
        var f = Fixture(); await Connect(f);
        var task = await f.Tasks.ResolveAsync(f.Caller, new("gmail", "Count matching email"), default);
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        f.Provider.RequestOverride = (endpoint, ct) =>
        {
            calls++; first.TrySetResult();
            return Task.FromException<JsonElement>(TransientRead(TimeSpan.FromMilliseconds(250)));
        };
        var run = f.Tasks.ExecuteAsync(f.Caller,
            new(task.TaskId, "mail_count", Args(new { query = "topic" })), default);
        await first.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await f.Tasks.CancelAsync(f.Caller, task.TaskId, default);
        var reply = await run.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, calls); Assert.False(reply.Verified); Assert.Null(reply.Result); Assert.Equal("cancelled", reply.State);
        Assert.Equal("cancelled", (await f.Tasks.StatusAsync(f.Caller, task.TaskId, default)).State);
    }

    [Fact]
    public async Task Read_recovery_caller_cancel_during_wait_prevents_second_dispatch()
    {
        var f = Fixture(); await Connect(f);
        var task = await f.Tasks.ResolveAsync(f.Caller, new("gmail", "Count matching email"), default);
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        f.Provider.RequestOverride = (endpoint, ct) =>
        {
            calls++; cancellation.Cancel();
            return Task.FromException<JsonElement>(TransientRead());
        };
        var run = f.Tasks.ExecuteAsync(f.Caller,
            new(task.TaskId, "mail_count", Args(new { query = "topic" })), cancellation.Token);
        // Production Firestore may propagate the cancelled request during the
        // final lookup; the fixture can instead return the normalized result.
        try
        {
            var reply = await run.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(reply.Verified); Assert.Null(reply.Result); Assert.Equal("cancelled", reply.State);
        }
        catch (OperationCanceledException) { }
        Assert.Equal(1, calls);
        Assert.Equal("cancelled", Assert.Single(await f.Store.OperationsAsync(f.Caller.UserId, task.TaskId, default)).State);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("disconnected")]
    [InlineData("account_changed")]
    [InlineData("user_deleted")]
    [InlineData("revoked")]
    public async Task Read_recovery_rechecks_deadline_account_and_customer_before_dispatch(string change)
    {
        var f = Fixture(); var accountId = await Connect(f);
        var task = await f.Tasks.ResolveAsync(f.Caller, new("gmail", "Count matching email"), default);
        var calls = 0;
        f.Provider.RequestOverride = async (endpoint, ct) =>
        {
            calls++;
            switch (change)
            {
                case "expired":
                    var original = await f.Store.GetTaskAsync(f.Caller.UserId, task.TaskId, default);
                    await f.Store.CreateTaskAsync(f.Caller.UserId, original with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) }, default);
                    break;
                case "disconnected": await f.Store.RemoveAccountAsync(f.Caller.UserId, accountId, default); break;
                case "account_changed":
                    var changed = await f.Store.GetTaskAsync(f.Caller.UserId, task.TaskId, default);
                    await f.Store.CreateTaskAsync(f.Caller.UserId, changed with { AccountId = ConnectorIds.New() }, default);
                    break;
                case "user_deleted": f.Store.Active = false; break;
                case "revoked": f.Provider.Status = "FAILED"; break;
            }
            throw TransientRead();
        };
        var reply = await f.Tasks.ExecuteAsync(f.Caller,
            new(task.TaskId, "mail_count", Args(new { query = "topic" })), default);
        Assert.Equal(1, calls); Assert.False(reply.Verified); Assert.Null(reply.Result);
        Assert.Equal(1, Assert.Single(await f.Store.OperationsAsync(f.Caller.UserId, task.TaskId, default)).ProviderRequestCount);
    }

    [Fact]
    public async Task Read_recovery_checks_task_again_after_live_account_verification()
    {
        var f = Fixture(); await Connect(f);
        var task = await f.Tasks.ResolveAsync(f.Caller, new("gmail", "Count matching email"), default);
        var calls = 0;
        f.Provider.RequestOverride = (endpoint, ct) =>
        {
            calls++;
            f.Provider.BeforeGet = () => f.Tasks.CancelAsync(f.Caller, task.TaskId, default);
            return Task.FromException<JsonElement>(TransientRead());
        };
        var reply = await f.Tasks.ExecuteAsync(f.Caller,
            new(task.TaskId, "mail_count", Args(new { query = "topic" })), default);
        Assert.Equal(1, calls); Assert.False(reply.Verified); Assert.Null(reply.Result); Assert.Equal("cancelled", reply.State);
    }

    [Fact]
    public async Task Read_recovery_same_logical_account_with_changed_provider_binding_is_not_replayed()
    {
        var f = Fixture(); var accountId = await Connect(f);
        var task = await f.Tasks.ResolveAsync(f.Caller, new("gmail", "Count matching email"), default);
        var account = Assert.Single(await f.Store.AccountsAsync(f.Caller.UserId, default));
        var calls = 0;
        f.Provider.RequestOverride = (endpoint, ct) =>
        {
            calls++;
            f.Store.SeedAccount(f.Caller.UserId, account with { ProviderId = "ca_changed123" });
            return Task.FromException<JsonElement>(TransientRead());
        };
        var reply = await f.Tasks.ExecuteAsync(f.Caller,
            new(task.TaskId, "mail_count", Args(new { query = "topic" })), default);
        Assert.Equal(accountId, account.Id); Assert.Equal(1, calls); Assert.False(reply.Verified); Assert.Null(reply.Result);
        Assert.Equal(1, Assert.Single(await f.Store.OperationsAsync(f.Caller.UserId, task.TaskId, default)).ProviderRequestCount);
    }

    [Fact]
    public async Task Read_recovery_does_not_wait_or_replay_beyond_the_original_task_deadline()
    {
        var f = Fixture(); await Connect(f);
        var clock = new RecoveryClock(DateTimeOffset.UtcNow);
        var tasks = new ConnectorTaskService(f.Store, f.Provider, f.Connections,
            new ConnectorRegistry(Options.Create(Settings())), clock, NullLogger<ConnectorTaskService>.Instance);
        var task = await tasks.ResolveAsync(f.Caller, new("gmail", "Count matching email"), default);
        var original = await f.Store.GetTaskAsync(f.Caller.UserId, task.TaskId, default);
        await f.Store.CreateTaskAsync(f.Caller.UserId, original with { ExpiresAt = clock.UtcNow.AddMilliseconds(150) }, default);
        var calls = 0;
        f.Provider.RequestOverride = (endpoint, ct) =>
        {
            calls++; return Task.FromException<JsonElement>(TransientRead(TimeSpan.FromMilliseconds(250)));
        };
        var reply = await tasks.ExecuteAsync(f.Caller,
            new(task.TaskId, "mail_count", Args(new { query = "topic" })), default).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, calls); Assert.False(reply.Verified); Assert.Null(reply.Result);
        Assert.Equal(1, Assert.Single(await f.Store.OperationsAsync(f.Caller.UserId, task.TaskId, default)).ProviderRequestCount);
    }

    [Fact]
    public async Task Read_recovery_late_uncooperative_response_after_remote_cancel_cannot_publish()
    {
        var f = Fixture(); await Connect(f);
        var task = await f.Tasks.ResolveAsync(f.Caller, new("gmail", "Count matching email"), default);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        f.Provider.RequestOverride = async (endpoint, ct) =>
        {
            if (++calls == 1) throw TransientRead();
            second.TrySetResult(); await release.Task;
            return EmptyMailPage(); // Deliberately ignores cancellation.
        };
        var run = f.Tasks.ExecuteAsync(f.Caller,
            new(task.TaskId, "mail_count", Args(new { query = "topic" })), default);
        try
        {
            await second.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await f.Tasks.CancelAsync(f.Caller, task.TaskId, default);
        }
        finally { release.TrySetResult(); }
        var reply = await run.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(2, calls); Assert.Equal("cancelled", reply.State); Assert.False(reply.Verified); Assert.Null(reply.Result);
        Assert.Equal("cancelled", (await f.Tasks.StatusAsync(f.Caller, task.TaskId, default)).State);
        Assert.Single(await f.Store.OperationsAsync(f.Caller.UserId, task.TaskId, default));
    }

    [Fact]
    public async Task Read_recovery_does_not_replay_a_successful_initial_page_when_continuation_fails()
    {
        var f = Fixture(); await Connect(f);
        var task = await f.Tasks.ResolveAsync(f.Caller, new("gmail", "Count matching email"), default);
        var calls = 0;
        f.Provider.RequestOverride = (endpoint, ct) => ++calls == 1
            ? Task.FromResult(JsonSerializer.SerializeToElement(new { messages = new[] { new { id = "m1", threadId = "t1" } }, nextPageToken = "next" }))
            : Task.FromException<JsonElement>(TransientRead());
        var reply = await f.Tasks.ExecuteAsync(f.Caller,
            new(task.TaskId, "mail_count", Args(new { query = "topic" })), default);
        Assert.Equal(2, calls); Assert.True(reply.Verified);
        Assert.True(reply.Result!.Value.GetProperty("truncated").GetBoolean());
        Assert.False(reply.Result.Value.GetProperty("data").GetProperty("complete").GetBoolean());
        Assert.Equal(1, reply.Result.Value.GetProperty("data").GetProperty("matchedMessageCount").GetInt32());
    }

    [Fact]
    public async Task Read_recovery_never_resends_an_ambiguous_write_and_preserves_read_only_reconciliation()
    {
        var f = Fixture(); await Connect(f);
        var task = await f.Tasks.ResolveAsync(f.Caller, new("gmail", "Send an email to test@example.com saying hello"), default);
        var sends = 0; var verifications = 0;
        f.Provider.RequestOverride = (endpoint, ct) =>
        {
            if (endpoint.EndsWith("/messages/send", StringComparison.Ordinal))
            {
                sends++;
                return Task.FromException<JsonElement>(new ConnectorProviderException("outcome_unknown", true, true));
            }
            verifications++; return Task.FromResult(EmptyMailPage());
        };
        var reply = await f.Tasks.ExecuteAsync(f.Caller,
            new(task.TaskId, "mail_send", Args(new { to = "test@example.com", subject = "Test", text = "Hello" }), true), default);
        Assert.Equal(1, sends); Assert.Equal(1, verifications); Assert.False(reply.Verified); Assert.Equal("outcome_unknown", reply.State);
        var operation = Assert.Single(await f.Store.OperationsAsync(f.Caller.UserId, task.TaskId, default));
        Assert.Equal(2, operation.ProviderRequestCount); Assert.Equal("outcome_unknown", operation.State);
    }

    [Theory]
    [InlineData("{\"status\":\"200\",\"data\":{}}")]
    [InlineData("{\"status\":null,\"data\":{}}")]
    [InlineData("{\"status\":[],\"data\":{}}")]
    [InlineData("[]")]
    public async Task Read_recovery_adapter_wrong_JSON_kinds_are_normalized_without_a_retry_grant(string payload)
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload) });
        var adapter = new ComposioConnectorProvider(new HttpClient(handler), Options.Create(Settings()));
        var error = await Assert.ThrowsAsync<ConnectorProviderException>(() =>
            adapter.RequestAsync("ca_test123", "https://api.github.com/user", "GET", null, default));
        Assert.False(error.RetryableRead); Assert.Equal(1, handler.Calls);
    }

    private static ConnectorProviderException TransientRead(TimeSpan? after = null) =>
        new("unavailable", retryableRead: true, retryAfter: after ?? TimeSpan.Zero);
    private static JsonElement EmptyMailPage() => JsonSerializer.SerializeToElement(new { messages = Array.Empty<object>() });
    private sealed class RecoveryClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
