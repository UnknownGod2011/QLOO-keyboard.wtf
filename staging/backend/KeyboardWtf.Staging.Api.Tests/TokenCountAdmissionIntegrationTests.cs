using KeyboardWtf.Staging.Api.Domain;
using KeyboardWtf.Staging.Api.Services;
using Xunit;

namespace KeyboardWtf.Staging.Api.Tests;

public sealed partial class SecurityAndUsageTests
{
    [Fact]
    public async Task Token_count_admission_ordinary_selection_adds_zero_admission_or_count_calls()
    {
        var admission = new RecordingTokenCountAdmission { RejectAt = 1 };
        var fixture = CreateExactTokenFixture(admission: admission);
        var result = await fixture.Service.GenerateAsync(Caller,
            ExactTokenRequest("admission-ordinary", new string('x', 50_000)), CancellationToken.None);
        Assert.True(result.Verified);
        Assert.Empty(admission.Calls);
        Assert.Empty(fixture.Vertex.Counted);
        Assert.Equal(1, fixture.Store.ReserveCalls);
    }

    [Fact]
    public async Task Token_count_admission_is_before_each_actual_candidate_count_and_any_paid_work()
    {
        var admission = new RecordingTokenCountAdmission();
        var fixture = CreateExactTokenFixture(admission: admission);
        var request = ExactTokenRequest("admission-order");
        admission.Before = (_, _) =>
        {
            Assert.Equal(fixture.Vertex.Counted.Count + 1, admission.Calls.Count);
            AssertExactTokenNoPaidWork(fixture.Store, fixture.Vertex);
            return Task.CompletedTask;
        };
        fixture.Vertex.BeforeCount = () => Assert.Equal(fixture.Vertex.Counted.Count, admission.Calls.Count);

        var result = await fixture.Service.GenerateAsync(Caller, request, CancellationToken.None);

        Assert.True(result.Verified);
        Assert.Equal(2, admission.Calls.Count);
        Assert.All(admission.Calls, call =>
        {
            Assert.Equal(Caller, call.Caller);
            Assert.Equal(request.RequestId, call.RequestId);
            Assert.True(call.Token.CanBeCanceled);
        });
        Assert.Equal(1, fixture.Store.ReserveCalls);
        Assert.Equal(1, fixture.Vertex.GenerateCalls);
        Assert.Equal(1, fixture.Store.SettleCalls);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Token_count_admission_denial_stops_before_the_denied_count_without_reservation(int deniedAt)
    {
        var admission = new RecordingTokenCountAdmission { RejectAt = deniedAt };
        var fixture = CreateExactTokenFixture(admission: admission);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.GenerateAsync(Caller,
            ExactTokenRequest("admission-denied-" + deniedAt), CancellationToken.None));

        Assert.Equal(deniedAt, admission.Calls.Count);
        Assert.Equal(deniedAt - 1, fixture.Vertex.Counted.Count);
        AssertExactTokenNoPaidWork(fixture.Store, fixture.Vertex);
    }

    [Fact]
    public async Task Token_count_admission_missing_dependency_fails_closed_only_on_exceptional_counting()
    {
        var fixture = CreateExactTokenFixture(omitAdmission: true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.GenerateAsync(Caller,
            ExactTokenRequest("admission-missing"), CancellationToken.None));
        Assert.Empty(fixture.Vertex.Counted);
        AssertExactTokenNoPaidWork(fixture.Store, fixture.Vertex);

        var ordinary = CreateExactTokenFixture(omitAdmission: true);
        Assert.True((await ordinary.Service.GenerateAsync(Caller,
            ExactTokenRequest("missing-ordinary", new string('x', 50_000)), CancellationToken.None)).Verified);
        Assert.Empty(ordinary.Vertex.Counted);
    }

    [Fact]
    public async Task Token_count_admission_commit_acknowledgement_loss_is_not_retried_or_refunded()
    {
        var admission = new RecordingTokenCountAdmission
        {
            Before = (_, _) => throw new IOException("Synthetic admission acknowledgement lost after commit."),
        };
        var fixture = CreateExactTokenFixture(admission: admission);

        await Assert.ThrowsAsync<IOException>(() => fixture.Service.GenerateAsync(Caller,
            ExactTokenRequest("admission-ack-loss"), CancellationToken.None));

        Assert.Single(admission.Calls);
        Assert.Empty(fixture.Vertex.Counted);
        AssertExactTokenNoPaidWork(fixture.Store, fixture.Vertex);
    }

    [Fact]
    public async Task Token_count_admission_caller_cancellation_after_admission_prevents_provider_dispatch()
    {
        using var cancellation = new CancellationTokenSource();
        var admission = new RecordingTokenCountAdmission
        {
            Before = (_, _) => { cancellation.Cancel(); return Task.CompletedTask; },
        };
        var fixture = CreateExactTokenFixture(admission: admission);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.GenerateAsync(Caller,
            ExactTokenRequest("admission-cancel"), cancellation.Token));

        Assert.Single(admission.Calls);
        Assert.Empty(fixture.Vertex.Counted);
        AssertExactTokenNoPaidWork(fixture.Store, fixture.Vertex);
    }

    [Fact]
    public async Task Token_count_admission_existing_count_deadline_also_bounds_a_stalled_admission()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = CancellationToken.None;
        var admission = new RecordingTokenCountAdmission
        {
            Before = async (_, token) =>
            {
                observed = token;
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            },
        };
        var fixture = CreateExactTokenFixture(timeoutSeconds: 2, admission: admission);
        var operation = fixture.Service.GenerateAsync(Caller, ExactTokenRequest("admission-deadline"), CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(observed.IsCancellationRequested);
        Assert.Single(admission.Calls);
        Assert.Empty(fixture.Vertex.Counted);
        AssertExactTokenNoPaidWork(fixture.Store, fixture.Vertex);
    }

    [Fact]
    public async Task Token_count_admission_provider_failure_does_not_re_admit_retry_reserve_or_refund()
    {
        var admission = new RecordingTokenCountAdmission();
        var fixture = CreateExactTokenFixture(admission: admission);
        fixture.Vertex.Failures[fixture.Primary.Model] = new VertexRequestRejectedException(429);

        await Assert.ThrowsAsync<VertexRequestRejectedException>(() => fixture.Service.GenerateAsync(Caller,
            ExactTokenRequest("admission-provider-failure"), CancellationToken.None));

        Assert.Single(admission.Calls);
        Assert.Single(fixture.Vertex.Counted);
        AssertExactTokenNoPaidWork(fixture.Store, fixture.Vertex);
    }

    private sealed class RecordingTokenCountAdmission : ITokenCountAdmission
    {
        internal List<(CallerIdentity Caller, string RequestId, CancellationToken Token)> Calls { get; } = [];
        internal int RejectAt { get; init; }
        internal Func<CallerIdentity, CancellationToken, Task>? Before { get; set; }
        public async Task AdmitAsync(CallerIdentity caller, string requestId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add((caller, requestId, cancellationToken));
            if (Calls.Count == RejectAt) throw new InvalidOperationException("Synthetic preflight admission refused.");
            if (Before is not null) await Before(caller, cancellationToken);
        }
    }
}
