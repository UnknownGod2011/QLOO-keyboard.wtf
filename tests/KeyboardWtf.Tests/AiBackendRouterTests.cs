using KeyboardWtf.Services.Ai.Backends;
using Xunit;

namespace KeyboardWtf.Tests;

public sealed class AiBackendRouterTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task Personal_primary_secondary_and_reconnect_never_enter_managed(int capabilityValue)
    {
        var capability = (AiCapability)capabilityValue;
        var fake = new Fixture(AiBackendPolicy.PersonalOnly);
        using var session = fake.Router.BeginOperation();
        await fake.Run(session, AiCapability.Live);
        fake.Config.Value = new(2, AiBackendPolicy.Managed);
        using var child = session.CreateChild();
        await fake.Run(child, capability);
        await fake.Run(session, AiCapability.Live);
        Assert.Equal(0, fake.ManagedDispatches);
        Assert.Equal(3, fake.PersonalDispatches);
        Assert.Equal(1, fake.Pool.Acquisitions);
        Assert.Equal(session.LogicalOperationId, child.LogicalOperationId);
        Assert.Equal(1, child.Configuration.Revision);
        Assert.Equal(AiBackendOwner.Personal, child.Owner);
    }

    [Fact]
    public async Task Deferred_personal_scope_allows_local_work_but_missing_key_never_dispatches_managed()
    {
        var fake = new Fixture(AiBackendPolicy.PersonalOnly);
        fake.Pool.Fail = true;
        using var operation = fake.Router.BeginOperation(acquireCredential: false);
        Assert.Equal(0, fake.Pool.Acquisitions);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fake.Run(operation));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fake.Run(operation));
        Assert.Equal(0, fake.ManagedDispatches);
        Assert.Equal(0, fake.PersonalDispatches);
    }

    [Fact]
    public async Task Managed_dispatch_remains_managed_and_does_not_acquire_personal_key()
    {
        var fake = new Fixture(AiBackendPolicy.Managed);
        using var task = fake.Router.BeginOperation();
        Assert.Equal("managed", await fake.Run(task));
        fake.Config.Value = new(2, AiBackendPolicy.PersonalOnly);
        Assert.Equal("managed", await fake.Run(task));
        Assert.Equal(2, fake.ManagedDispatches);
        Assert.Equal(0, fake.Pool.Acquisitions);
        using var next = fake.Router.BeginOperation();
        Assert.Equal("personal", await fake.Run(next));
    }

    [Fact]
    public async Task Policy_change_affects_next_live_session_but_not_the_existing_live_owner()
    {
        var fake = new Fixture(AiBackendPolicy.PersonalOnly);
        using var first = fake.Router.BeginOperation();
        Assert.Equal("personal", await fake.Run(first, AiCapability.Live));

        fake.Config.Value = new(2, AiBackendPolicy.Managed);
        Assert.Equal("personal", await fake.Run(first, AiCapability.Live)); // Reconnect keeps its owner.
        using var next = fake.Router.BeginOperation();
        Assert.Equal("managed", await fake.Run(next, AiCapability.Live));
        Assert.NotEqual(first.LogicalOperationId, next.LogicalOperationId);
        Assert.Equal(AiBackendOwner.Personal, first.Owner);
        Assert.Equal(AiBackendOwner.Managed, next.Owner);
        Assert.Equal(1, fake.ManagedDispatches);
        Assert.Equal(2, fake.PersonalDispatches);
        Assert.Equal(1, fake.Pool.Acquisitions);
    }

    [Fact]
    public async Task Only_initial_typed_allowance_refusal_allows_fallback()
    {
        var fake = new Fixture(AiBackendPolicy.ManagedThenPersonal)
        { ManagedError = new ManagedAllowanceRefusedException() };
        using var task = fake.Router.BeginOperation();
        Assert.Equal("personal", await fake.Run(task));
        using var child = task.CreateChild();
        Assert.Equal("personal", await fake.Run(child));
        Assert.Equal(1, fake.ManagedDispatches); // authoritative refusal, no reservation committed
        Assert.Equal(2, fake.PersonalDispatches);
    }

    [Theory]
    [InlineData("network")]
    [InlineData("timeout")]
    [InlineData("auth")]
    [InlineData("quota")]
    [InlineData("global safety")]
    [InlineData("concurrency")]
    [InlineData("The account has insufficient credit")]
    public async Task Untyped_failure_never_falls_back_even_if_later_retry_reports_exhaustion(string message)
    {
        var fake = new Fixture(AiBackendPolicy.ManagedThenPersonal)
        { ManagedError = new InvalidOperationException(message) };
        using var task = fake.Router.BeginOperation();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fake.Run(task));
        fake.ManagedError = new ManagedAllowanceRefusedException();
        await Assert.ThrowsAsync<ManagedAllowanceRefusedException>(() => fake.Run(task));
        Assert.Equal(AiBackendOwner.Managed, task.Owner);
        Assert.Equal(0, fake.PersonalDispatches);
        Assert.Equal(0, fake.Pool.Acquisitions);
    }

    [Fact]
    public async Task No_mid_live_or_progressed_task_fallback()
    {
        var fake = new Fixture(AiBackendPolicy.ManagedThenPersonal);
        using var task = fake.Router.BeginOperation();
        await fake.Run(task, AiCapability.Live);
        fake.ManagedError = new ManagedAllowanceRefusedException();
        using var secondary = task.CreateChild();
        await Assert.ThrowsAsync<ManagedAllowanceRefusedException>(() => fake.Run(secondary));
        Assert.Equal(0, fake.PersonalDispatches);
    }

    [Fact]
    public void Missing_personal_key_or_corrupt_configuration_never_becomes_managed()
    {
        var fake = new Fixture(AiBackendPolicy.PersonalOnly);
        fake.Pool.Fail = true;
        Assert.Throws<InvalidOperationException>(() => fake.Router.BeginOperation());
        fake.Config.Error = new InvalidDataException("Configuration unavailable");
        Assert.Throws<InvalidDataException>(() => fake.Router.BeginOperation());
        Assert.Equal(0, fake.ManagedDispatches);
    }

    [Fact]
    public async Task Disposed_parent_cancels_children_and_cannot_publish_new_requests()
    {
        var fake = new Fixture(AiBackendPolicy.PersonalOnly);
        var parent = fake.Router.BeginOperation();
        using var child = parent.CreateChild();
        parent.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fake.Run(child));
        Assert.Equal(0, fake.PersonalDispatches);
        Assert.Equal(0, fake.Pool.Releases);
        child.Dispose();
        Assert.Equal(1, fake.Pool.Releases);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => fake.Run(parent));
    }

    [Fact]
    public async Task Key_lease_outlives_cancelled_but_still_unwinding_transport()
    {
        var fake = new Fixture(AiBackendPolicy.PersonalOnly);
        var operation = fake.Router.BeginOperation();
        var entered = new TaskCompletionSource();
        var finish = new TaskCompletionSource();
        var running = fake.Router.ExecuteAsync(operation, AiCapability.Text,
            _ => throw new Exception("Must not call managed"), async (_, _, token) =>
            {
                entered.SetResult();
                await finish.Task;
                token.ThrowIfCancellationRequested();
                return "unused";
            });
        await entered.Task;
        operation.Dispose();
        Assert.Equal(0, fake.Pool.Releases);
        finish.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.Equal(1, fake.Pool.Releases);
    }

    [Fact]
    public async Task Late_result_from_transport_ignoring_cancellation_is_not_returned()
    {
        var fake = new Fixture(AiBackendPolicy.PersonalOnly);
        using var operation = fake.Router.BeginOperation();
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fake.Router.ExecuteAsync(
            operation, AiCapability.Text, _ => Task.FromResult("must not call"), (_, _, _) =>
            {
                cancellation.Cancel();
                return Task.FromResult("obsolete");
            }, cancellation.Token));
        Assert.Equal(0, fake.ManagedDispatches);
    }

    [Fact]
    public async Task Managed_only_refusal_never_acquires_personal_credential()
    {
        var fake = new Fixture(AiBackendPolicy.Managed)
        { ManagedError = new ManagedAllowanceRefusedException() };
        using var task = fake.Router.BeginOperation();
        await Assert.ThrowsAsync<ManagedAllowanceRefusedException>(() => fake.Run(task));
        Assert.Equal(0, fake.Pool.Acquisitions);
    }

    [Fact]
    public async Task Concurrent_initial_requests_wait_for_one_backend_choice_not_parallel_fallback()
    {
        var fake = new Fixture(AiBackendPolicy.ManagedThenPersonal);
        using var operation = fake.Router.BeginOperation();
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var managed = 0;
        var personal = 0;
        Task<string> Run() => fake.Router.ExecuteAsync(operation, AiCapability.Text, async _ =>
        {
            Interlocked.Increment(ref managed);
            entered.TrySetResult();
            await release.Task;
            throw new ManagedAllowanceRefusedException();
        }, (_, _, _) => { Interlocked.Increment(ref personal); return Task.FromResult("ok"); });
        var first = Run();
        await entered.Task;
        var second = Run();
        Assert.Equal(1, managed);
        release.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(1, managed);
        Assert.Equal(2, personal);
        Assert.Equal(1, fake.Pool.Acquisitions);
    }

    private sealed class Fixture(AiBackendPolicy policy)
    {
        internal Config Config { get; } = new(policy);
        internal Pool Pool { get; } = new();
        internal AiBackendRouter Router => new(Config, Pool);
        internal Exception ManagedError;
        internal int ManagedDispatches;
        internal int PersonalDispatches;
        internal Task<string> Run(AiBackendOperation operation, AiCapability capability = AiCapability.Text) =>
            Router.ExecuteAsync(operation, capability, _ =>
            {
                ManagedDispatches++;
                if (ManagedError is not null) throw ManagedError;
                return Task.FromResult("managed");
            }, (lease, model, _) =>
            {
                Assert.NotEqual(Guid.Empty, lease.SlotId);
                Assert.StartsWith("gemini-", model);
                PersonalDispatches++;
                return Task.FromResult("personal");
            });
    }
    private sealed class Config(AiBackendPolicy policy) : IAiBackendConfigurationSource
    {
        internal AiBackendConfiguration Value = new(1, policy);
        internal Exception Error;
        public AiBackendConfiguration ReadConfiguration() => Error is null ? Value : throw Error;
    }
    private sealed class Pool : IPersonalCredentialPool
    {
        internal int Acquisitions, Releases;
        internal bool Fail;
        public IPersonalCredentialLease Acquire(AiBackendConfiguration expectedConfiguration)
        {
            Acquisitions++;
            if (Fail) throw new InvalidOperationException("No eligible key");
            return new Lease(this);
        }
        private sealed class Lease(Pool pool) : IPersonalCredentialLease
        {
            public Guid SlotId { get; } = Guid.NewGuid();
            public long CredentialRevision => 1;
            public void Dispose() => pool.Releases++;
        }
    }
}
