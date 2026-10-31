using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Google.Cloud.Firestore;
using KeyboardWtf.Staging.Api.Configuration;
using KeyboardWtf.Staging.Api.Contracts;
using KeyboardWtf.Staging.Api.Domain;
using KeyboardWtf.Staging.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace KeyboardWtf.Staging.Api.Tests;

[Collection("Local accounting emulator")]
public sealed class TerminalProviderRejectionTests
{
    private const long ReservationCreditMicros = 100_000;

    [LocalFirestoreTheory]
    [Trait("Category", "FirestoreEmulator")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rejection_evidence_write_failure_never_blindly_releases_and_ack_loss_remains_recoverable(bool committed)
    {
        var (db, store, caller) = await AccountingEmulatorTests.CreateAccountAsync();
        const string requestId = "rejection-write-failure";
        var (wrapped, proxy) = WrapStore(store);
        proxy.FailBeforeRejectionEvidence = !committed;
        proxy.FailAfterRejectionEvidence = committed;
        var vertex = new SequenceVertex([_ => Task.FromException<VertexGenerateResult>(new VertexRequestRejectedException(503))]);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateService(wrapped, vertex, 1, db.ProjectId)
            .GenerateAsync(caller, Request(requestId), CancellationToken.None));
        Assert.Contains("recovery is pending", error.Message);
        Assert.Equal(0, proxy.ReleaseCalls);
        var account = db.Collection("accounts").Document(caller.UserId);
        var reservation = account.Collection("reservations").Document(requestId);
        var held = await reservation.GetSnapshotAsync();
        Assert.Equal(committed, held.ContainsField("providerHttpStatus"));
        Assert.True(held.GetValue<bool>("concurrencyReleased"));
        Assert.Equal(0, (await account.GetSnapshotAsync()).GetValue<long>("activeRequests"));
        await reservation.UpdateAsync("expiresAt", Timestamp.FromDateTime(DateTime.UtcNow.AddMinutes(-1)));
        var maintenance = CreateMaintenance(db, store);
        await ReconcileAsync(maintenance, await reservation.GetSnapshotAsync(), "synthetic-rejection-write-first");
        Assert.Equal(committed ? 1_000_000 : 900_000, (await account.GetSnapshotAsync()).GetValue<long>("availableCreditMicros"));
        Assert.Equal(committed ? "released" : "reconciliation_pending", (await reservation.GetSnapshotAsync()).GetValue<string>("status"));
        await ReserveNextAsync(store, caller);
        await ReconcileAsync(maintenance, await reservation.GetSnapshotAsync(), "synthetic-rejection-write-second");
        Assert.Equal(1, (await account.GetSnapshotAsync()).GetValue<long>("activeRequests"));
        Assert.Equal(committed ? 1 : 0, (await account.Collection("ledger")
            .WhereEqualTo("type", LedgerTypes.ReservationRelease).GetSnapshotAsync()).Documents.Count);
        Assert.Empty((await account.Collection("usage").GetSnapshotAsync()).Documents);
    }

    [LocalFirestoreFact]
    [Trait("Category", "FirestoreEmulator")]
    public async Task Rejection_evidence_rejects_invalid_status_missing_dispatch_and_conflicting_completion()
    {
        var (_, store, caller) = await AccountingEmulatorTests.CreateAccountAsync();
        await ReserveNextAsync(store, caller, "conflict");
        foreach (var invalid in new[] { 0, 200, 299, 600, 1011 })
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.RecordTerminalProviderRejectionAsync(
                caller.UserId, "conflict", invalid, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RecordTerminalProviderRejectionAsync(
            caller.UserId, "conflict", 503, CancellationToken.None));
        await store.MarkReservationProviderStartedAsync(caller.UserId, "conflict", CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReleaseReservationAsync(
            caller.UserId, "conflict", "provider_rejected", CancellationToken.None));
        await store.RecordTerminalProviderRejectionAsync(caller.UserId, "conflict", 503, CancellationToken.None);
        await store.RecordTerminalProviderRejectionAsync(caller.UserId, "conflict", 503, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RecordTerminalProviderRejectionAsync(
            caller.UserId, "conflict", 429, CancellationToken.None));
        var usage = new ProviderUsage(10, 5, 0, 0, 0, 0, 15, "synthetic-request");
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RecordProviderCompletionAsync(
            caller.UserId, "conflict", usage, 10, 123, "completed", CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CheckpointReservationUsageAsync(
            caller.UserId, "conflict", usage, 10, 123, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SettleAsync(
            caller.UserId, "conflict", usage, 10, 123, "completed", CancellationToken.None));
        await store.ReleaseReservationAsync(caller.UserId, "conflict", "provider_rejected", CancellationToken.None);
        await store.RecordTerminalProviderRejectionAsync(caller.UserId, "conflict", 503, CancellationToken.None);
        await ReserveNextAsync(store, caller, "completed-conflict");
        await store.MarkReservationProviderStartedAsync(caller.UserId, "completed-conflict", CancellationToken.None);
        await store.RecordProviderCompletionAsync(caller.UserId, "completed-conflict", usage, 10, 123, "completed", CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RecordTerminalProviderRejectionAsync(
            caller.UserId, "completed-conflict", 503, CancellationToken.None));

        var (_, checkpointStore, checkpointCaller) = await AccountingEmulatorTests.CreateAccountAsync();
        await ReserveNextAsync(checkpointStore, checkpointCaller, "checkpoint-first");
        await checkpointStore.MarkReservationProviderStartedAsync(checkpointCaller.UserId, "checkpoint-first", CancellationToken.None);
        await checkpointStore.CheckpointReservationUsageAsync(checkpointCaller.UserId, "checkpoint-first", usage,
            10, 123, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => checkpointStore.RecordTerminalProviderRejectionAsync(
            checkpointCaller.UserId, "checkpoint-first", 503, CancellationToken.None));
    }

    [LocalFirestoreTheory]
    [Trait("Category", "FirestoreEmulator")]
    [InlineData(429, false)]
    [InlineData(503, false)]
    [InlineData(429, true)]
    [InlineData(503, true)]
    public async Task Terminal_rejection_is_durable_before_release_and_maintenance_releases_only_once(
        int terminalStatus, bool useFallback)
    {
        // The shared fixture enforces 127.0.0.1, EmulatorOnly, and a synthetic
        // project. Every accounting operation below uses the real Firestore store.
        var (db, store, caller) = await AccountingEmulatorTests.CreateAccountAsync();
        const string requestId = "terminal-rejection";
        var account = db.Collection("accounts").Document(caller.UserId);
        var reservation = account.Collection("reservations").Document(requestId);
        var steps = new List<Func<CancellationToken, Task<VertexGenerateResult>>>();
        if (useFallback)
        {
            steps.Add(_ => Task.FromException<VertexGenerateResult>(new VertexRequestRejectedException(503)));
            steps.Add(async _ =>
            {
                AssertNoRejectionEvidence(await reservation.GetSnapshotAsync());
                throw new VertexRequestRejectedException(terminalStatus);
            });
        }
        else
        {
            steps.Add(_ => Task.FromException<VertexGenerateResult>(new VertexRequestRejectedException(terminalStatus)));
        }
        var vertex = new SequenceVertex(steps);
        var (wrappedStore, proxy) = WrapStore(store, crashBeforeRelease: true);
        var service = CreateService(wrappedStore, vertex, steps.Count, db.ProjectId);

        var failure = await Record.ExceptionAsync(() => service.GenerateAsync(
            caller, Request(requestId), CancellationToken.None));

        Assert.NotNull(failure);
        Assert.Equal(steps.Count, vertex.Calls);
        Assert.Equal(1, proxy.ReleaseCalls);
        var durable = await reservation.GetSnapshotAsync();
        Assert.True(durable.ContainsField("providerStartedAt"));
        Assert.True(durable.ContainsField("providerHttpStatus"));
        Assert.Equal(terminalStatus, durable.GetValue<long>("providerHttpStatus"));
        Assert.False(durable.ContainsField("providerCompletionRecorded")
            && durable.GetValue<bool>("providerCompletionRecorded"));
        var held = await account.GetSnapshotAsync();
        Assert.Equal(900_000, held.GetValue<long>("availableCreditMicros"));
        Assert.Equal(ReservationCreditMicros, held.GetValue<long>("reservedCreditMicros"));
        Assert.Empty((await account.Collection("ledger")
            .WhereEqualTo("type", LedgerTypes.ReservationRelease).GetSnapshotAsync()).Documents);

        var maintenance = CreateMaintenance(db, store);
        await ReconcileAsync(maintenance, durable, "synthetic-terminal-first");
        var recovered = await account.GetSnapshotAsync();
        Assert.Equal(1_000_000, recovered.GetValue<long>("availableCreditMicros"));
        Assert.Equal(0, recovered.GetValue<long>("reservedCreditMicros"));
        Assert.Equal(0, recovered.GetValue<long>("activeRequests"));
        var released = await reservation.GetSnapshotAsync();
        Assert.Equal("released", released.GetValue<string>("status"));
        Assert.Equal(ReservationCreditMicros, released.GetValue<long>("releasedCreditMicros"));
        Assert.True(released.GetValue<bool>("concurrencyReleased"));
        Assert.Equal(terminalStatus, released.GetValue<long>("providerHttpStatus"));

        await ReserveNextAsync(store, caller);
        // A stale maintenance snapshot must also be harmless after another
        // request acquires the account's sole concurrency slot.
        await ReconcileAsync(maintenance, durable, "synthetic-terminal-retry");
        var afterRetry = await account.GetSnapshotAsync();
        Assert.Equal(900_000, afterRetry.GetValue<long>("availableCreditMicros"));
        Assert.Equal(ReservationCreditMicros, afterRetry.GetValue<long>("reservedCreditMicros"));
        Assert.Equal(1, afterRetry.GetValue<long>("activeRequests"));
        var releases = await account.Collection("ledger")
            .WhereEqualTo("type", LedgerTypes.ReservationRelease).GetSnapshotAsync();
        Assert.Single(releases.Documents);
        Assert.Equal(ReservationCreditMicros, releases.Documents[0].GetValue<long>("availableDeltaMicros"));
        Assert.Empty((await account.Collection("usage").GetSnapshotAsync()).Documents);
        Assert.Empty((await account.Collection("ledger")
            .WhereEqualTo("type", LedgerTypes.UsageSettlement).GetSnapshotAsync()).Documents);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReserveNextAsync(store, caller, "third"));
    }

    [LocalFirestoreTheory]
    [Trait("Category", "FirestoreEmulator")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unknown_timeout_or_client_cancellation_has_no_terminal_rejection_evidence(bool cancelClient)
    {
        var (db, store, caller) = await AccountingEmulatorTests.CreateAccountAsync();
        const string requestId = "unknown-provider-result";
        using var requestCancellation = new CancellationTokenSource();
        var vertex = new SequenceVertex([
            token =>
            {
                if (!cancelClient)
                    return Task.FromException<VertexGenerateResult>(new TimeoutException("Synthetic provider timeout."));
                requestCancellation.Cancel();
                return Task.FromCanceled<VertexGenerateResult>(token);
            },
        ]);
        var (wrappedStore, proxy) = WrapStore(store);
        var service = CreateService(wrappedStore, vertex, 1, db.ProjectId);

        if (cancelClient)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GenerateAsync(
                caller, Request(requestId), requestCancellation.Token));
        else
            await Assert.ThrowsAsync<TimeoutException>(() => service.GenerateAsync(
                caller, Request(requestId), requestCancellation.Token));

        Assert.Equal(1, vertex.Calls);
        Assert.Equal(0, proxy.ReleaseCalls);
        await AssertAmbiguousResultHeldAsync(db, store, caller, requestId);
    }

    [LocalFirestoreFact]
    [Trait("Category", "FirestoreEmulator")]
    public async Task Fallback_success_has_completion_evidence_without_terminal_rejection_evidence()
    {
        var (db, store, caller) = await AccountingEmulatorTests.CreateAccountAsync();
        const string requestId = "fallback-success";
        var account = db.Collection("accounts").Document(caller.UserId);
        var reservation = account.Collection("reservations").Document(requestId);
        var vertex = new SequenceVertex([
            _ => Task.FromException<VertexGenerateResult>(new VertexRequestRejectedException(503)),
            async _ =>
            {
                AssertNoRejectionEvidence(await reservation.GetSnapshotAsync());
                return new VertexGenerateResult("synthetic response",
                    new ProviderUsage(10, 5, 0, 0, 0, 0, 15, "synthetic-request"));
            },
        ]);
        var (wrappedStore, proxy) = WrapStore(store);
        var service = CreateService(wrappedStore, vertex, 2, db.ProjectId);

        var response = await service.GenerateAsync(caller, Request(requestId), CancellationToken.None);

        Assert.Equal(2, vertex.Calls);
        Assert.Equal(0, proxy.ReleaseCalls);
        Assert.Equal("synthetic-model-1", response.Model);
        Assert.Equal(10, response.ChargedCreditMicros);
        var completed = await reservation.GetSnapshotAsync();
        AssertNoRejectionEvidence(completed);
        Assert.True(completed.GetValue<bool>("providerCompletionRecorded"));
        Assert.Equal("settled", completed.GetValue<string>("status"));
        Assert.Equal("synthetic-model-1", completed.GetValue<string>("model"));
        Assert.Equal(999_990, (await account.GetSnapshotAsync()).GetValue<long>("availableCreditMicros"));
        Assert.Equal(0, (await account.GetSnapshotAsync()).GetValue<long>("reservedCreditMicros"));
        Assert.Single((await account.Collection("usage").GetSnapshotAsync()).Documents);
        Assert.Single((await account.Collection("ledger")
            .WhereEqualTo("type", LedgerTypes.UsageSettlement).GetSnapshotAsync()).Documents);
        Assert.Empty((await account.Collection("ledger")
            .WhereEqualTo("type", LedgerTypes.ReservationRelease).GetSnapshotAsync()).Documents);
    }

    [LocalFirestoreFact]
    [Trait("Category", "FirestoreEmulator")]
    public async Task Fallback_timeout_does_not_reuse_the_first_candidates_rejection_as_terminal_proof()
    {
        var (db, store, caller) = await AccountingEmulatorTests.CreateAccountAsync();
        const string requestId = "fallback-timeout";
        var reservation = db.Collection("accounts").Document(caller.UserId)
            .Collection("reservations").Document(requestId);
        var vertex = new SequenceVertex([
            _ => Task.FromException<VertexGenerateResult>(new VertexRequestRejectedException(503)),
            async _ =>
            {
                AssertNoRejectionEvidence(await reservation.GetSnapshotAsync());
                throw new TimeoutException("Synthetic fallback provider timeout.");
            },
        ]);
        var (wrappedStore, proxy) = WrapStore(store);
        var service = CreateService(wrappedStore, vertex, 2, db.ProjectId);

        await Assert.ThrowsAsync<TimeoutException>(() => service.GenerateAsync(
            caller, Request(requestId), CancellationToken.None));

        Assert.Equal(2, vertex.Calls);
        Assert.Equal(0, proxy.ReleaseCalls);
        await AssertAmbiguousResultHeldAsync(db, store, caller, requestId);
    }

    private static async Task AssertAmbiguousResultHeldAsync(
        FirestoreDb db, FirestoreAccountStore store, CallerIdentity caller, string requestId)
    {
        var account = db.Collection("accounts").Document(caller.UserId);
        var reservation = account.Collection("reservations").Document(requestId);
        var unresolved = await reservation.GetSnapshotAsync();
        AssertNoRejectionEvidence(unresolved);
        Assert.False(unresolved.ContainsField("providerCompletionRecorded")
            && unresolved.GetValue<bool>("providerCompletionRecorded"));
        Assert.Equal("reconciliation_pending", unresolved.GetValue<string>("status"));
        Assert.True(unresolved.GetValue<bool>("concurrencyReleased"));
        await reservation.UpdateAsync("expiresAt", Timestamp.FromDateTime(DateTime.UtcNow.AddMinutes(-1)));
        var maintenance = CreateMaintenance(db, store);
        await ReconcileAsync(maintenance, await reservation.GetSnapshotAsync(), "synthetic-unknown-first");
        var held = await account.GetSnapshotAsync();
        Assert.Equal(900_000, held.GetValue<long>("availableCreditMicros"));
        Assert.Equal(ReservationCreditMicros, held.GetValue<long>("reservedCreditMicros"));
        Assert.Equal(0, held.GetValue<long>("activeRequests"));

        await ReserveNextAsync(store, caller);
        await ReconcileAsync(maintenance, await reservation.GetSnapshotAsync(), "synthetic-unknown-retry");
        var afterRetry = await account.GetSnapshotAsync();
        Assert.Equal(800_000, afterRetry.GetValue<long>("availableCreditMicros"));
        Assert.Equal(ReservationCreditMicros * 2, afterRetry.GetValue<long>("reservedCreditMicros"));
        Assert.Equal(1, afterRetry.GetValue<long>("activeRequests"));
        AssertNoRejectionEvidence(await reservation.GetSnapshotAsync());
        Assert.Empty((await account.Collection("usage").GetSnapshotAsync()).Documents);
        Assert.Empty((await account.Collection("ledger")
            .WhereEqualTo("type", LedgerTypes.ReservationRelease).GetSnapshotAsync()).Documents);
        Assert.Empty((await account.Collection("ledger")
            .WhereEqualTo("type", LedgerTypes.UsageSettlement).GetSnapshotAsync()).Documents);
    }

    private static void AssertNoRejectionEvidence(DocumentSnapshot reservation) =>
        Assert.False(reservation.ContainsField("providerHttpStatus"));

    private static GenerateRequest Request(string requestId) => new(requestId, "synthetic prompt", null, 128);

    private static Task<ReservationResult> ReserveNextAsync(
        FirestoreAccountStore store, CallerIdentity caller, string requestId = "next") =>
        store.ReserveAsync(caller, requestId, "synthetic-model", "synthetic-pricing", ReservationCreditMicros,
            500_000, 100, 1, 1_000_000_000, CancellationToken.None);

    private static UsageReconciliationService CreateMaintenance(FirestoreDb db, IAccountStore store) =>
        new(db, store, new SystemClock(), Options.Create(new PaidSystemOptions { ProjectId = db.ProjectId }),
            NullLogger<UsageReconciliationService>.Instance);

    private static async Task ReconcileAsync(
        UsageReconciliationService service, DocumentSnapshot reservation, string runId)
    {
        var method = typeof(UsageReconciliationService).GetMethod("ReconcileReservationAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(service, [reservation, runId, CancellationToken.None])!;
    }

    private static AiUsageService CreateService(
        IAccountStore store, IVertexAiClient vertex, int candidateCount, string projectId)
    {
        var catalog = new SyntheticCatalog();
        var candidates = Enumerable.Range(0, candidateCount)
            .Select(index => new ModelCandidateSnapshot($"synthetic-model-{index}", "synthetic-region", catalog.Pricing))
            .ToArray();
        var route = new ModelRouteSnapshot("fast_text", "synthetic-route", "vertex", candidates,
            true, false, 200_000, 8192, 5, 0, 0, "1.3.0", "local terminal rejection regression");
        return new AiUsageService(store, catalog, new FixedRouting(route), vertex, Options.Create(new PaidSystemOptions
        {
            ProjectId = projectId,
            DefaultTaskCreditMicrosCap = 500_000,
            GlobalUsageShardCount = 1,
        }));
    }

    private static (IAccountStore Store, ReleaseCrashProxy Proxy) WrapStore(
        IAccountStore realStore, bool crashBeforeRelease = false)
    {
        var wrapped = DispatchProxy.Create<IAccountStore, ReleaseCrashProxy>();
        var proxy = (ReleaseCrashProxy)wrapped;
        proxy.Inner = realStore;
        proxy.CrashBeforeRelease = crashBeforeRelease;
        return (wrapped, proxy);
    }

    // Dynamic forwarding deliberately avoids implementing a shadow account
    // store. Newly added evidence methods still reach the real transaction.
    public class ReleaseCrashProxy : DispatchProxy
    {
        public IAccountStore Inner { get; set; } = null!;
        public bool CrashBeforeRelease { get; set; }
        public bool FailBeforeRejectionEvidence { get; set; }
        public bool FailAfterRejectionEvidence { get; set; }
        public int ReleaseCalls { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            if (targetMethod.Name == nameof(IAccountStore.RecordTerminalProviderRejectionAsync))
            {
                if (FailBeforeRejectionEvidence) return Task.FromException(new IOException("Synthetic evidence write failure."));
                if (FailAfterRejectionEvidence)
                    return LoseAcknowledgementAsync((Task)targetMethod.Invoke(Inner, args)!);
            }
            if (targetMethod.Name == nameof(IAccountStore.ReleaseReservationAsync))
            {
                ReleaseCalls++;
                if (CrashBeforeRelease)
                    throw new SimulatedReleaseCrashException();
            }
            try
            {
                return targetMethod.Invoke(Inner, args);
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
        }

        private static async Task LoseAcknowledgementAsync(Task committed)
        {
            await committed;
            throw new IOException("Synthetic lost evidence acknowledgement.");
        }
    }

    private sealed class SimulatedReleaseCrashException : Exception { }

    private sealed class FixedRouting(ModelRouteSnapshot route) : IModelRoutingService
    {
        public Task<ModelRouteSnapshot> ResolveAsync(
            string? taskKind, string routingKey, CancellationToken cancellationToken) => Task.FromResult(route);
    }

    private sealed class SyntheticCatalog : ICatalogService
    {
        public PricingSnapshot Pricing { get; } = new("synthetic-pricing", "synthetic-model",
            100_000, 200_000, 25_000, 200_000, 8_500, 10_000, DateTimeOffset.UnixEpoch);

        public Task<CatalogSnapshot> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CatalogSnapshot("synthetic-catalog", [], Pricing, false, null));

        public Task<CatalogProduct> GetProductAsync(
            string sku, CancellationToken cancellationToken, bool allowPaymentCanary = false) =>
            throw new NotSupportedException();

        public long EstimateReservationCreditMicros(
            PricingSnapshot pricing, string prompt, int maxOutputTokens, long taskCapCreditMicros) => ReservationCreditMicros;

        public (long ProviderCostUsdMicros, long ChargeCreditMicros) CalculateActualCost(
            PricingSnapshot pricing, ProviderUsage usage) => (123, 10);

        public (long ProviderCostUsdMicros, long ChargeCreditMicros) CalculateLiveActualCost(
            PricingSnapshot pricing, ProviderUsage usage, int estimateSafetyBasisPoints) => throw new NotSupportedException();
    }

    private sealed class SequenceVertex(IReadOnlyList<Func<CancellationToken, Task<VertexGenerateResult>>> steps)
        : IVertexAiClient
    {
        public int Calls { get; private set; }

        public Task<VertexGenerateResult> GenerateAsync(
            string model, string location, string prompt, string? systemInstruction, int maxOutputTokens,
            JsonElement? responseJsonSchema, IReadOnlyList<InlineImageRequest>? images, InlineAudioRequest? audio,
            CancellationToken cancellationToken)
        {
            var index = Calls++;
            Assert.True(index < steps.Count, "An unexpected provider candidate was dispatched.");
            return steps[index](cancellationToken);
        }
    }
}
