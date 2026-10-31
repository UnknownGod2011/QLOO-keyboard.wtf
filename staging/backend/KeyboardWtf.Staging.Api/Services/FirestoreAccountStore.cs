using Google.Cloud.Firestore;
using KeyboardWtf.Staging.Api.Configuration;
using KeyboardWtf.Staging.Api.Domain;
using KeyboardWtf.Staging.Api.Infrastructure;
using Microsoft.Extensions.Options;
using Grpc.Core;

namespace KeyboardWtf.Staging.Api.Services;

public sealed partial class FirestoreAccountStore : IAccountStore
{
    private const int RecentDeviceLimit = 10;
    private const int RecentUsageLimit = 10;
    private const int RecentPurchaseLimit = 5;
    internal static readonly TimeSpan SettledUsageDetailRetention = TimeSpan.FromDays(30);
    private readonly FirestoreDb _db;
    private readonly IClock _clock;
    private readonly PaidSystemOptions _options;
    public FirestoreAccountStore(FirestoreDb db, IClock clock, IOptions<PaidSystemOptions> options)
    {
        _db = db;
        _clock = clock;
        _options = options.Value;
    }

    public async Task EnsureAccountAsync(CallerIdentity caller, CancellationToken cancellationToken)
    {
        var accountRef = Account(caller.UserId);
        var email = caller.Email.Trim().ToLowerInvariant();
        var displayName = caller.DisplayName ?? "";
        var existing = await accountRef.GetSnapshotAsync(cancellationToken);
        if (existing.Exists)
            RequireActiveAccount(existing);
        if (existing.Exists
            && !AccountIdentityNeedsUpdate(existing.ToDictionary(), email, displayName))
        {
            return;
        }

        async Task ExecuteAccountIdentityTransactionAsync() => await _db.RunTransactionAsync(async transaction =>
        {
            var snapshot = await transaction.GetSnapshotAsync(accountRef, cancellationToken);
            var now = Ts(_clock.UtcNow);
            if (!snapshot.Exists)
            {
                transaction.Create(accountRef, new Dictionary<string, object>
                {
                    ["uid"] = caller.UserId,
                    ["email"] = email,
                    ["displayName"] = displayName,
                    ["status"] = "active",
                    ["planId"] = "none",
                    ["subscriptionStatus"] = "none",
                    ["availableCreditMicros"] = 0L,
                    ["reservedCreditMicros"] = 0L,
                    ["creditDebtMicros"] = 0L,
                    ["cycleUsageCreditMicros"] = 0L,
                    ["allowanceRulesVersion"] = AllowanceWallet.CurrentRulesVersion,
                    ["allowanceBuckets"] = new Dictionary<string, object>(),
                    ["activeRequests"] = 0L,
                    ["activeLiveRequests"] = 0L,
                    ["rateWindowCount"] = 0L,
                    ["rateWindowStartedAt"] = now,
                    ["createdAt"] = now,
                    ["updatedAt"] = now,
                });
            }
            else
            {
                RequireActiveAccount(snapshot);
                if (AccountIdentityNeedsUpdate(snapshot.ToDictionary(), email, displayName))
                    transaction.Update(accountRef, new Dictionary<string, object>
                    {
                        ["email"] = email,
                        ["displayName"] = displayName,
                        ["updatedAt"] = now,
                    });
            }
            return 0;
        }, cancellationToken: cancellationToken);

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await ExecuteAccountIdentityTransactionAsync();
                return;
            }
            catch (RpcException exception) when (
                exception.StatusCode == StatusCode.Aborted
                && attempt < _options.MaxRetries)
            {
                // The transaction was fully rolled back. Retry from the fresh
                // identity snapshot so concurrent first-logins remain safe.
                await Task.Delay(
                    TimeSpan.FromMilliseconds(40 * (attempt + 1)),
                    cancellationToken);
            }
        }
    }

    internal static bool AccountIdentityNeedsUpdate(
        IDictionary<string, object> account,
        string normalizedEmail,
        string displayName) =>
        !string.Equals(
            String(account, "email", ""),
            normalizedEmail ?? "",
            StringComparison.Ordinal)
        || !string.Equals(
            String(account, "displayName", ""),
            displayName ?? "",
            StringComparison.Ordinal);

    // Admission must be checked in the transaction that writes user data.
    // Preserve legacy existing accounts whose status was not yet recorded.
    internal static void RequireActiveAccount(DocumentSnapshot snapshot)
    {
        if (!snapshot.Exists
            || !string.Equals(String(snapshot.ToDictionary(), "status", "active"), "active", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The account is not active.");
    }

    public async Task<AccountSnapshot> GetAccountAsync(string userId, CancellationToken cancellationToken)
    {
        await NormalizeAllowanceWalletAsync(userId, cancellationToken);
        var accountSnapshot = await Account(userId).GetSnapshotAsync(cancellationToken);
        if (!accountSnapshot.Exists)
            throw new KeyNotFoundException("Account not found.");

        var devicesTask = Account(userId).Collection("devices")
            .OrderByDescending("lastSeenAt").Limit(RecentDeviceLimit).GetSnapshotAsync(cancellationToken);
        var usageTask = Account(userId).Collection("usage")
            .OrderByDescending("createdAt").Limit(RecentUsageLimit).GetSnapshotAsync(cancellationToken);
        var purchasesTask = Account(userId).Collection("purchases")
            .OrderByDescending("createdAt").Limit(RecentPurchaseLimit).GetSnapshotAsync(cancellationToken);
        await Task.WhenAll(devicesTask, usageTask, purchasesTask);

        var account = accountSnapshot.ToDictionary();
        var devices = devicesTask.Result.Documents.Select(document =>
        {
            var data = document.ToDictionary();
            return new DeviceSnapshot(
                document.Id,
                String(data, "name", "Windows device"),
                String(data, "status", "active"),
                Date(data, "lastSeenAt"),
                Date(data, "createdAt"),
                String(data, "platform", "Windows"));
        }).ToArray();
        var usage = usageTask.Result.Documents
            .Where(document => IsUsageDetailVisible(document, _clock.UtcNow))
            .Select(UsageFrom)
            .ToArray();
        var stalePurchaseUpdates = new List<Task>();
        var purchases = purchasesTask.Result.Documents.Select(document =>
        {
            var data = document.ToDictionary();
            var currentStatus = String(data, "status", "pending");
            var displayStatus = StatusAfterAbandonment(
                currentStatus,
                Bool(data, "credited", false),
                Date(data, "createdAt"),
                _clock.UtcNow);
            if (!string.Equals(displayStatus, currentStatus, StringComparison.Ordinal))
            {
                stalePurchaseUpdates.Add(document.Reference.UpdateAsync(
                    new Dictionary<string, object>
                    {
                        ["status"] = displayStatus,
                        ["updatedAt"] = Ts(_clock.UtcNow),
                    },
                    cancellationToken: cancellationToken));
            }
            return new PurchaseSnapshot(
                document.Id,
                String(data, "productSku", ""),
                String(data, "provider", "razorpay"),
                displayStatus,
                Long(data, "amountMinor"),
                String(data, "currency", "INR"),
                Date(data, "createdAt"),
                Bool(data, "credited", false));
        }).ToArray();

        if (stalePurchaseUpdates.Count > 0)
            await Task.WhenAll(stalePurchaseUpdates);

        return Snapshot(accountSnapshot.Id, account, devices, usage, purchases);
    }

    public async Task<UsagePageSnapshot> GetUsagePageAsync(
        string userId,
        int limit,
        string? cursor,
        CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 50)
            throw new ArgumentOutOfRangeException(nameof(limit));
        var collection = Account(userId).Collection("usage");
        Query query = collection.OrderByDescending("createdAt");
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            var cursorSnapshot = await collection.Document(SafeId(cursor))
                .GetSnapshotAsync(cancellationToken);
            if (!cursorSnapshot.Exists)
                throw new ArgumentException("The usage cursor is no longer valid.", nameof(cursor));
            query = query.StartAfter(cursorSnapshot);
        }
        var page = await query.Limit(limit + 1).GetSnapshotAsync(cancellationToken);
        var hasMore = page.Count > limit;
        var scannedDocuments = page.Documents.Take(limit).ToArray();
        var visibleDocuments = scannedDocuments
            .Where(document => IsUsageDetailVisible(document, _clock.UtcNow))
            .ToArray();
        var items = visibleDocuments.Select(UsageFrom).ToArray();
        var nextCursor = hasMore && scannedDocuments.Length > 0
            ? scannedDocuments[^1].Id
            : null;
        return new UsagePageSnapshot(items, nextCursor);
    }

    public async Task<PurchasePageSnapshot> GetPurchasePageAsync(
        string userId,
        int limit,
        string? cursor,
        CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 50)
            throw new ArgumentOutOfRangeException(nameof(limit));
        var collection = Account(userId).Collection("purchases");
        Query query = collection.OrderByDescending("createdAt");
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            var cursorSnapshot = await collection.Document(SafeId(cursor))
                .GetSnapshotAsync(cancellationToken);
            if (!cursorSnapshot.Exists)
                throw new ArgumentException("The purchase cursor is no longer valid.", nameof(cursor));
            query = query.StartAfter(cursorSnapshot);
        }
        var page = await query.Limit(limit + 1).GetSnapshotAsync(cancellationToken);
        var hasMore = page.Count > limit;
        var visibleDocuments = page.Documents.Take(limit).ToArray();
        var items = visibleDocuments.Select(PurchaseFrom).ToArray();
        var nextCursor = hasMore ? visibleDocuments[^1].Id : null;
        return new PurchasePageSnapshot(items, nextCursor);
    }

    private Task NormalizeAllowanceWalletAsync(string userId, CancellationToken cancellationToken)
    {
        var accountRef = Account(userId);
        var now = _clock.UtcNow;
        return _db.RunTransactionAsync(async transaction =>
        {
            var snapshot = await transaction.GetSnapshotAsync(accountRef, cancellationToken);
            if (!snapshot.Exists)
                throw new KeyNotFoundException("Account not found.");
            var account = snapshot.ToDictionary();
            var wallet = AllowanceWallet.FromAccount(account, now);
            var requiresMigration = !string.Equals(
                String(account, "allowanceRulesVersion", ""),
                AllowanceWallet.CurrentRulesVersion,
                StringComparison.Ordinal);
            var aggregateChanged = wallet.AvailableCreditMicros != Long(account, "availableCreditMicros")
                || wallet.ReservedCreditMicros != Long(account, "reservedCreditMicros");
            if (!requiresMigration && !aggregateChanged && wallet.NewlyExpired.Count == 0)
                return 0;

            foreach (var expiration in wallet.NewlyExpired)
            {
                var eventId = $"expire_{expiration.BucketId}";
                transaction.Create(accountRef.Collection("ledger").Document(eventId), LedgerMap(new LedgerEntry(
                    eventId,
                    LedgerTypes.Expiration,
                    -expiration.CreditMicros,
                    0,
                    "allowance-bucket",
                    expiration.BucketId,
                    Crypto.Sha256($"allowance-expiration:{userId}:{expiration.BucketId}"),
                    "system",
                    "Subscription allowance expired at its billing-cycle boundary",
                    now)));
            }
            transaction.Update(accountRef, new Dictionary<string, object>
            {
                ["availableCreditMicros"] = wallet.AvailableCreditMicros,
                ["reservedCreditMicros"] = wallet.ReservedCreditMicros,
                ["allowanceRulesVersion"] = AllowanceWallet.CurrentRulesVersion,
                ["allowanceBuckets"] = wallet.ToFirestoreMap(),
                ["updatedAt"] = Ts(now),
            });
            return 0;
        }, cancellationToken: cancellationToken);
    }

    public async Task<ReservationResult> ReserveAsync(
        CallerIdentity caller,
        string requestId,
        string model,
        string pricingVersion,
        long reserveCreditMicros,
        long taskCapCreditMicros,
        int requestsPerMinute,
        int maxConcurrent,
        long globalDailyCap,
        CancellationToken cancellationToken,
        string activityType = "ai_request",
        int? reservationLifetimeSeconds = null)
    {
        if (reserveCreditMicros <= 0 || reserveCreditMicros > taskCapCreditMicros)
            throw new InvalidOperationException("The request exceeds its approved task budget.");

        var now = _clock.UtcNow;
        var cleanedActivityType = CleanActivityType(activityType);
        var liveReservation = IsLiveActivity(cleanedActivityType);
        await RecoverExpiredReservationsAsync(caller.UserId, now, cancellationToken);
        var accountRef = Account(caller.UserId);
        var reservationRef = accountRef.Collection("reservations").Document(SafeId(requestId));
        var idempotencyRef = _db.Collection("idempotency")
            .Document(Crypto.Sha256($"usage-reserve:{caller.UserId}:{requestId}"));
        var leaseSeconds = ReservationLeaseSeconds(
            liveReservation,
            _options.MaxRequestSeconds,
            _options.LiveSessionMaxSeconds,
            _options.ReservationLeaseGraceSeconds,
            reservationLifetimeSeconds);
        var expiresAt = now.AddSeconds(leaseSeconds);
        var globalUsageDate = now.ToString("yyyyMMdd");
        var shardedDailyCap = await EnsureGlobalUsageShardsAsync(
            globalUsageDate,
            globalDailyCap,
            _options.GlobalUsageShardCount,
            cancellationToken);
        if (_options.DailySpendingCapsEnabled && shardedDailyCap <= 0)
            throw new InvalidOperationException("The daily spending cap has been reached.");
        var globalShardIndex = GlobalUsageShardIndex(
            globalUsageDate,
            caller.UserId,
            requestId,
            _options.GlobalUsageShardCount);
        var globalShardId = $"{globalUsageDate}-{globalShardIndex:D3}";
        var globalRef = _db.Collection("globalUsageShards").Document(globalShardId);
        var globalMarkerRef = _db.Collection("globalUsageMigrations").Document(globalUsageDate);
        var legacyGlobalRef = _db.Collection("globalUsage").Document(globalUsageDate);
        var userDailyRef = accountRef.Collection("dailyUsage").Document(globalUsageDate);

        async Task<bool> ExecuteReservationTransactionAsync() =>
            await _db.RunTransactionAsync(async transaction =>
        {
            var accountSnapshot = await transaction.GetSnapshotAsync(accountRef, cancellationToken);
            if (!accountSnapshot.Exists)
                throw new KeyNotFoundException("Account not found.");
            var idempotencySnapshot = await transaction.GetSnapshotAsync(idempotencyRef, cancellationToken);
            if (idempotencySnapshot.Exists)
                return false;

            var account = accountSnapshot.ToDictionary();
            if (!string.Equals(String(account, "status", "active"), "active", StringComparison.Ordinal))
                throw new InvalidOperationException("The account is not active.");
            if (liveReservation)
                RequireManagedLiveLossAdmission(account);
            var wallet = AllowanceWallet.FromAccount(account, now);
            var allowanceAllocations = wallet.Reserve(reserveCreditMicros, initialReservation: true);

            var activeRequests = Long(account, "activeRequests");
            var activeLiveRequests = Math.Min(
                activeRequests,
                Long(account, "activeLiveRequests"));
            var activeOneShotRequests = Math.Max(0, activeRequests - activeLiveRequests);
            if (liveReservation)
            {
                if (activeLiveRequests >= _options.MaxConcurrentLiveSessionsPerUser)
                    throw new InvalidOperationException("Another Jarvis Live session is already running for this account.");
            }
            else if (activeOneShotRequests >= maxConcurrent)
            {
                throw new InvalidOperationException("Another one-shot AI request is already running for this account.");
            }

            var windowStartedAt = Date(account, "rateWindowStartedAt");
            var rateCount = Long(account, "rateWindowCount");
            if (now - windowStartedAt >= TimeSpan.FromMinutes(1))
            {
                windowStartedAt = now;
                rateCount = 0;
            }
            if (rateCount >= requestsPerMinute)
                throw new InvalidOperationException("The per-minute request limit has been reached. Try again shortly.");

            var globalMarkerSnapshot = await transaction.GetSnapshotAsync(globalMarkerRef, cancellationToken);
            var legacyGlobalSnapshot = await transaction.GetSnapshotAsync(legacyGlobalRef, cancellationToken);
            var globalSnapshot = await transaction.GetSnapshotAsync(globalRef, cancellationToken);
            if (!globalMarkerSnapshot.Exists)
                throw new InvalidOperationException("The daily quota shard migration marker is unavailable.");
            var effectiveShardedCap = EffectiveShardedDailyCap(
                globalMarkerSnapshot.ToDictionary(),
                legacyGlobalSnapshot.Exists ? legacyGlobalSnapshot.ToDictionary() : [],
                globalDailyCap,
                _options.GlobalUsageShardCount);
            if (_options.DailySpendingCapsEnabled && effectiveShardedCap <= 0)
                throw new InvalidOperationException("The daily spending cap has been reached.");
            var globalShardCap = effectiveShardedCap <= 0 ? 0 : GlobalUsageShardCap(
                effectiveShardedCap,
                _options.GlobalUsageShardCount,
                globalShardIndex);
            var global = globalSnapshot.Exists ? globalSnapshot.ToDictionary() : [];
            var globalCommitted = CommittedIncludingCompanyLoss(global);
            if (_options.DailySpendingCapsEnabled && globalCommitted + reserveCreditMicros > globalShardCap)
                throw new InvalidOperationException("The daily spending cap has been reached.");
            var userDailySnapshot = await transaction.GetSnapshotAsync(userDailyRef, cancellationToken);
            var userDaily = userDailySnapshot.Exists ? userDailySnapshot.ToDictionary() : [];
            var userDailyCommitted = CommittedIncludingCompanyLoss(userDaily);
            if (_options.DailySpendingCapsEnabled && userDailyCommitted + reserveCreditMicros > _options.PerUserDailyCreditMicrosCap)
                throw new InvalidOperationException("The account daily AI spending cap has been reached.");

            var eventId = $"reserve_{SafeId(requestId)}";
            var reservationDocument = new Dictionary<string, object>
            {
                ["requestId"] = requestId,
                ["userId"] = caller.UserId,
                ["reservedCreditMicros"] = reserveCreditMicros,
                ["model"] = model,
                ["pricingVersion"] = pricingVersion,
                ["activityType"] = cleanedActivityType,
                ["globalUsageDate"] = globalUsageDate,
                ["globalUsageShardId"] = globalShardId,
                ["taskCapCreditMicros"] = taskCapCreditMicros,
                ["accountingVersion"] = 2,
                ["concurrencyReleased"] = false,
                ["allowanceRulesVersion"] = AllowanceWallet.CurrentRulesVersion,
                ["allowanceAllocations"] = AllowanceWallet.AllocationMap(allowanceAllocations),
                ["status"] = "reserved",
                ["expiresAt"] = Ts(expiresAt),
                ["createdAt"] = Ts(now),
                ["updatedAt"] = Ts(now),
            };
            if (liveReservation && _options.ManagedLiveUnknownUsageLossEnabled)
            {
                reservationDocument["managedLiveLossPolicyVersion"] = ManagedLiveLossPolicyVersion;
                reservationDocument["managedLiveLossEnrolledAt"] = Ts(now);
            }
            transaction.Create(reservationRef, reservationDocument);
            transaction.Create(accountRef.Collection("ledger").Document(eventId), LedgerMap(new LedgerEntry(
                eventId,
                LedgerTypes.UsageReservation,
                -reserveCreditMicros,
                reserveCreditMicros,
                "usage",
                requestId,
                idempotencyRef.Id,
                caller.UserId,
                "AI request budget reserved",
                now)));
            transaction.Create(idempotencyRef, new Dictionary<string, object>
            {
                ["scope"] = "usage-reserve",
                ["userId"] = caller.UserId,
                ["resourceId"] = requestId,
                ["createdAt"] = Ts(now),
            });
            transaction.Set(globalRef, new Dictionary<string, object>
            {
                ["reservedCreditMicros"] = FieldValue.Increment(reserveCreditMicros),
                ["settledCreditMicros"] = Long(global, "settledCreditMicros"),
                ["dailyCapCreditMicros"] = globalShardCap,
                ["shardIndex"] = globalShardIndex,
                ["updatedAt"] = Ts(now),
            }, SetOptions.MergeAll);
            transaction.Set(userDailyRef, new Dictionary<string, object>
            {
                ["userId"] = caller.UserId,
                ["date"] = globalUsageDate,
                ["reservedCreditMicros"] = FieldValue.Increment(reserveCreditMicros),
                ["settledCreditMicros"] = Long(userDaily, "settledCreditMicros"),
                ["dailyCapCreditMicros"] = _options.PerUserDailyCreditMicrosCap,
                ["updatedAt"] = Ts(now),
            }, SetOptions.MergeAll);
            transaction.Update(accountRef, new Dictionary<string, object>
            {
                ["availableCreditMicros"] = wallet.AvailableCreditMicros,
                ["reservedCreditMicros"] = wallet.ReservedCreditMicros,
                ["allowanceRulesVersion"] = AllowanceWallet.CurrentRulesVersion,
                ["allowanceBuckets"] = wallet.ToFirestoreMap(),
                ["activeRequests"] = activeRequests + 1,
                ["activeLiveRequests"] = liveReservation
                    ? activeLiveRequests + 1
                    : activeLiveRequests,
                ["rateWindowStartedAt"] = Ts(windowStartedAt),
                ["rateWindowCount"] = rateCount + 1,
                ["updatedAt"] = Ts(now),
            });
            return true;
        }, cancellationToken: cancellationToken);

        var created = false;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                created = await ExecuteReservationTransactionAsync();
                break;
            }
            catch (RpcException exception) when (
                exception.StatusCode == StatusCode.Aborted
                && attempt < _options.MaxRetries)
            {
                // Firestore has already rolled the transaction back. A short,
                // bounded retry re-reads the authoritative balance and either
                // reserves safely or returns the real account-level blocker.
                await Task.Delay(TimeSpan.FromMilliseconds(40 * (attempt + 1)), cancellationToken);
            }
        }

        var accountResult = await GetAccountAsync(caller.UserId, cancellationToken);
        var reservationSnapshot = await reservationRef.GetSnapshotAsync(cancellationToken);
        var reservation = ReservationFrom(reservationSnapshot);
        return new ReservationResult(created, reservation, accountResult);
    }

    internal static int ReservationLeaseSeconds(
        bool liveReservation,
        int maxRequestSeconds,
        int configuredLiveSessionMaxSeconds,
        int graceSeconds,
        int? authoritativeLifetimeSeconds = null)
    {
        var operationLifetimeSeconds = liveReservation
            ? authoritativeLifetimeSeconds ?? configuredLiveSessionMaxSeconds
            : maxRequestSeconds;
        if (operationLifetimeSeconds <= 0 || operationLifetimeSeconds > 900)
            throw new ArgumentOutOfRangeException(nameof(authoritativeLifetimeSeconds));
        if (graceSeconds < 0 || graceSeconds > 600)
            throw new ArgumentOutOfRangeException(nameof(graceSeconds));
        return checked(operationLifetimeSeconds + graceSeconds);
    }

    public async Task<SettlementResult> SettleAsync(
        string userId,
        string requestId,
        ProviderUsage usage,
        long actualCreditMicros,
        long providerCostUsdMicros,
        string outcome,
        CancellationToken cancellationToken)
    {
        var accountRef = Account(userId);
        var reservationRef = accountRef.Collection("reservations").Document(SafeId(requestId));
        var idempotencyRef = _db.Collection("idempotency")
            .Document(Crypto.Sha256($"usage-settle:{userId}:{requestId}"));
        var now = _clock.UtcNow;

        var result = await _db.RunTransactionAsync(async transaction =>
        {
            var accountSnapshot = await transaction.GetSnapshotAsync(accountRef, cancellationToken);
            var reservationSnapshot = await transaction.GetSnapshotAsync(reservationRef, cancellationToken);
            var idempotencySnapshot = await transaction.GetSnapshotAsync(idempotencyRef, cancellationToken);
            if (!accountSnapshot.Exists || !reservationSnapshot.Exists)
                throw new InvalidOperationException("The usage reservation no longer exists.");
            var account = accountSnapshot.ToDictionary();
            var reservation = reservationSnapshot.ToDictionary();
            var globalShardId = String(reservation, "globalUsageShardId", "");
            var globalRef = string.IsNullOrWhiteSpace(globalShardId)
                ? _db.Collection("globalUsage").Document(
                    String(reservation, "globalUsageDate", now.ToString("yyyyMMdd")))
                : _db.Collection("globalUsageShards").Document(globalShardId);
            var userDailyRef = accountRef.Collection("dailyUsage").Document(
                String(reservation, "globalUsageDate", now.ToString("yyyyMMdd")));
            var userDailySnapshot = await transaction.GetSnapshotAsync(userDailyRef, cancellationToken);
            var userDaily = userDailySnapshot.Exists ? userDailySnapshot.ToDictionary() : [];
            var reserved = Long(reservation, "reservedCreditMicros");
            // A committed receipt wins over the transient pre-settlement state.
            // The first transaction changes provider_completed to settled;
            // acknowledgement loss must not make its retry fail or debit again.
            if (idempotencySnapshot.Exists
                || string.Equals(String(reservation, "status", ""), "settled", StringComparison.Ordinal))
            {
                return (Settled: false, Charged: Long(reservation, "chargedCreditMicros"), Released: Long(reservation, "releasedCreditMicros"));
            }
            if (UsageReconciliationService.CanReleaseVerifiedProviderRejection(Long(reservation, "providerHttpStatus")))
                throw new InvalidOperationException("A rejected provider request cannot also be charged.");
            if (String(reservation, "status", "") == ManagedLiveLossTerminalStatus)
                throw new InvalidOperationException("A company-absorbed Live loss cannot be charged retroactively.");
            if (!ProviderUsageMetadata.IsAuthoritative(usage))
                throw new InvalidOperationException("Unknown provider usage cannot be settled.");
            if (Long(reservation, "accountingVersion") >= 2)
            {
                if (!Bool(reservation, "providerCompletionRecorded", false)
                    || !string.Equals(
                        String(reservation, "status", ""),
                        "provider_completed",
                        StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "Provider completion evidence must be recorded before settlement.");
                if (Long(reservation, "plannedChargeCreditMicros") != actualCreditMicros
                    || Long(reservation, "providerCostUsdMicros") != providerCostUsdMicros
                    || !UsageEvidenceMatches(reservation, usage))
                    throw new InvalidOperationException(
                        "Settlement evidence does not match the durable provider result.");
            }
            var charged = Math.Max(0, actualCreditMicros);
            var availableBeforeSettlement = Long(account, "availableCreditMicros");
            var allocations = AllowanceWallet.ReadAllocations(reservation);
            var bucketedSettlement = allocations.Count > 0;
            var wallet = AllowanceWallet.FromAccount(account, now);
            long released;
            long debtAdded;
            long availableDelta;
            if (bucketedSettlement)
            {
                (released, debtAdded) = wallet.Settle(allocations, charged);
                availableDelta = wallet.AvailableCreditMicros - availableBeforeSettlement;
            }
            else
            {
                // Backward-compatible settlement for a reservation created before
                // allowance-v1 was introduced. New reservations always carry an
                // immutable bucket allocation.
                var reservedConsumed = Math.Min(charged, reserved);
                released = reserved - reservedConsumed;
                var legacyOverage = Math.Max(0, charged - reserved);
                var additionalAvailableCharge = Math.Min(legacyOverage, availableBeforeSettlement);
                debtAdded = legacyOverage - additionalAvailableCharge;
                availableDelta = released - additionalAvailableCharge;
            }
            var overage = Math.Max(0, charged - reserved);
            var concurrencyReleased = await ConcurrencyAlreadyReleasedAsync(
                transaction, userId, requestId, reservation, cancellationToken);
            var detailExpiresAt = UsageDetailExpiresAt(account, now);
            var eventId = $"settle_{SafeId(requestId)}";
            transaction.Create(accountRef.Collection("ledger").Document(eventId), LedgerMap(new LedgerEntry(
                eventId,
                LedgerTypes.UsageSettlement,
                availableDelta,
                -reserved,
                "usage",
                requestId,
                idempotencyRef.Id,
                "system",
                outcome,
                now,
                new Dictionary<string, object?>
                {
                    ["providerCostUsdMicros"] = providerCostUsdMicros,
                    ["chargedCreditMicros"] = charged,
                    ["reservedCreditMicros"] = reserved,
                    ["overageCreditMicros"] = overage,
                    ["creditDebtAddedMicros"] = debtAdded,
                })));
            transaction.Create(accountRef.Collection("usage").Document(SafeId(requestId)), new Dictionary<string, object>
            {
                ["requestId"] = requestId,
                ["model"] = String(reservation, "model", ""),
                ["pricingVersion"] = String(reservation, "pricingVersion", ""),
                ["activityType"] = String(reservation, "activityType", "ai_request"),
                ["outcome"] = outcome,
                ["inputTokens"] = usage.InputTokens,
                ["outputTokens"] = usage.OutputTokens,
                ["cachedInputTokens"] = usage.CachedInputTokens,
                ["reasoningTokens"] = usage.ReasoningTokens,
                ["imageTokens"] = usage.ImageTokens,
                ["audioTokens"] = usage.AudioTokens,
                ["inputAudioTokens"] = usage.InputAudioTokens,
                ["outputAudioTokens"] = usage.OutputAudioTokens,
                ["inputImageTokens"] = usage.InputImageTokens,
                ["outputImageTokens"] = usage.OutputImageTokens,
                ["totalTokens"] = usage.TotalTokens,
                ["usageEstimated"] = usage.Estimated,
                ["providerRequestId"] = usage.ProviderRequestId ?? "",
                ["providerModelVersion"] = usage.ProviderModelVersion ?? "",
                ["accountingStatus"] = "settled",
                ["providerCostUsdMicros"] = providerCostUsdMicros,
                ["chargedCreditMicros"] = charged,
                ["overageCreditMicros"] = overage,
                ["creditDebtAddedMicros"] = debtAdded,
                ["createdAt"] = Ts(now),
                ["detailExpiresAt"] = Ts(detailExpiresAt),
            });
            transaction.Create(idempotencyRef, new Dictionary<string, object>
            {
                ["scope"] = "usage-settle",
                ["userId"] = userId,
                ["resourceId"] = requestId,
                ["createdAt"] = Ts(now),
            });
            transaction.Update(reservationRef, new Dictionary<string, object>
            {
                ["status"] = "settled",
                ["concurrencyReleased"] = true,
                ["chargedCreditMicros"] = charged,
                ["releasedCreditMicros"] = released,
                ["outcome"] = outcome,
                ["updatedAt"] = Ts(now),
            });
            var accountUpdates = new Dictionary<string, object>
            {
                ["availableCreditMicros"] = bucketedSettlement
                    ? wallet.AvailableCreditMicros
                    : Math.Max(0, availableBeforeSettlement + availableDelta),
                ["reservedCreditMicros"] = bucketedSettlement
                    ? wallet.ReservedCreditMicros
                    : Math.Max(0, Long(account, "reservedCreditMicros") - reserved),
                ["cycleUsageCreditMicros"] = Long(account, "cycleUsageCreditMicros") + charged,
                ["creditDebtMicros"] = Long(account, "creditDebtMicros") + debtAdded,
                ["activeRequests"] = Math.Max(0, Long(account, "activeRequests") - (concurrencyReleased ? 0 : 1)),
                ["activeLiveRequests"] = RemainingLiveRequests(account, reservation, concurrencyReleased),
                ["updatedAt"] = Ts(now),
            };
            if (bucketedSettlement)
            {
                accountUpdates["allowanceRulesVersion"] = AllowanceWallet.CurrentRulesVersion;
                accountUpdates["allowanceBuckets"] = wallet.ToFirestoreMap();
            }
            transaction.Update(accountRef, accountUpdates);
            transaction.Set(globalRef, new Dictionary<string, object>
            {
                ["reservedCreditMicros"] = FieldValue.Increment(-reserved),
                ["settledCreditMicros"] = FieldValue.Increment(charged),
                ["providerCostUsdMicros"] = FieldValue.Increment(providerCostUsdMicros),
                ["providerCostSettlementCount"] = FieldValue.Increment(1L),
                ["providerCostAccountingVersion"] = 1L,
                ["updatedAt"] = Ts(now),
            }, SetOptions.MergeAll);
            transaction.Set(userDailyRef, new Dictionary<string, object>
            {
                ["reservedCreditMicros"] = Math.Max(0, Long(userDaily, "reservedCreditMicros") - reserved),
                ["settledCreditMicros"] = Long(userDaily, "settledCreditMicros") + charged,
                ["providerCostUsdMicros"] = Long(userDaily, "providerCostUsdMicros") + providerCostUsdMicros,
                ["providerCostSettlementCount"] = Long(userDaily, "providerCostSettlementCount") + 1,
                ["providerCostAccountingVersion"] = 1L,
                ["updatedAt"] = Ts(now),
            }, SetOptions.MergeAll);
            return (Settled: true, Charged: charged, Released: released);
        }, cancellationToken: cancellationToken);

        return new SettlementResult(
            result.Settled,
            result.Charged,
            result.Released,
            await GetAccountAsync(userId, cancellationToken));
    }

    public async Task MarkReservationProviderStartedAsync(
        string userId,
        string requestId,
        CancellationToken cancellationToken)
    {
        var reservationRef = Account(userId)
            .Collection("reservations")
            .Document(SafeId(requestId));
        var now = _clock.UtcNow;
        await _db.RunTransactionAsync(async transaction =>
        {
            var snapshot = await transaction.GetSnapshotAsync(reservationRef, cancellationToken);
            if (!snapshot.Exists)
                throw new InvalidOperationException("The usage reservation no longer exists.");
            var data = snapshot.ToDictionary();
            if (!string.Equals(String(data, "status", ""), "reserved", StringComparison.Ordinal))
                throw new InvalidOperationException("The usage reservation is not active.");
            if (data.ContainsKey("providerStartedAt"))
                return 0;
            transaction.Update(reservationRef, new Dictionary<string, object>
            {
                ["providerStartedAt"] = Ts(now),
                ["updatedAt"] = Ts(now),
            });
            return 0;
        }, cancellationToken: cancellationToken);
    }

    public async Task<long> EnsureReservationCapacityAsync(
        string userId,
        string requestId,
        long targetReservedCreditMicros,
        CancellationToken cancellationToken)
    {
        if (targetReservedCreditMicros <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetReservedCreditMicros));
        var accountRef = Account(userId);
        var reservationRef = accountRef.Collection("reservations").Document(SafeId(requestId));
        var now = _clock.UtcNow;
        async Task<long> ExecuteCapacityTransactionAsync() => await _db.RunTransactionAsync(async transaction =>
        {
            var accountSnapshot = await transaction.GetSnapshotAsync(accountRef, cancellationToken);
            var reservationSnapshot = await transaction.GetSnapshotAsync(reservationRef, cancellationToken);
            if (!accountSnapshot.Exists || !reservationSnapshot.Exists)
                throw new InvalidOperationException("The usage reservation no longer exists.");
            var account = accountSnapshot.ToDictionary();
            var reservation = reservationSnapshot.ToDictionary();
            if (!string.Equals(String(reservation, "status", ""), "reserved", StringComparison.Ordinal))
                throw new InvalidOperationException("The usage reservation is no longer active.");

            var current = Long(reservation, "reservedCreditMicros");
            if (targetReservedCreditMicros <= current)
                return current;
            var taskCap = Long(reservation, "taskCapCreditMicros");
            if (targetReservedCreditMicros > taskCap)
                throw new InvalidOperationException("The Live session reached its approved server budget.");
            var additional = targetReservedCreditMicros - current;
            var available = Long(account, "availableCreditMicros");
            if (available < additional)
                throw new InvalidOperationException("The account has insufficient credit to continue this Live session.");
            var currentAllocations = AllowanceWallet.ReadAllocations(reservation);
            var bucketedReservation = currentAllocations.Count > 0;
            var wallet = AllowanceWallet.FromAccount(account, now);
            var updatedAllocations = bucketedReservation
                ? wallet.Extend(currentAllocations, additional)
                : currentAllocations;

            var globalShardId = String(reservation, "globalUsageShardId", "");
            if (string.IsNullOrWhiteSpace(globalShardId))
                throw new InvalidOperationException("The rolling reservation has no global budget shard.");
            var globalRef = _db.Collection("globalUsageShards").Document(globalShardId);
            var globalUsageDate = String(reservation, "globalUsageDate", now.ToString("yyyyMMdd"));
            var globalMarkerRef = _db.Collection("globalUsageMigrations").Document(globalUsageDate);
            var legacyGlobalRef = _db.Collection("globalUsage").Document(globalUsageDate);
            var globalMarkerSnapshot = await transaction.GetSnapshotAsync(globalMarkerRef, cancellationToken);
            var legacyGlobalSnapshot = await transaction.GetSnapshotAsync(legacyGlobalRef, cancellationToken);
            var globalSnapshot = await transaction.GetSnapshotAsync(globalRef, cancellationToken);
            if (!globalSnapshot.Exists || !globalMarkerSnapshot.Exists)
                throw new InvalidOperationException("The rolling reservation budget shard is unavailable.");
            var global = globalSnapshot.ToDictionary();
            var shardIndex = checked((int)Long(global, "shardIndex"));
            var effectiveShardedCap = EffectiveShardedDailyCap(
                globalMarkerSnapshot.ToDictionary(),
                legacyGlobalSnapshot.Exists ? legacyGlobalSnapshot.ToDictionary() : [],
                _options.GlobalDailyCreditMicrosCap,
                _options.GlobalUsageShardCount);
            if (_options.DailySpendingCapsEnabled && effectiveShardedCap <= 0)
                throw new InvalidOperationException("The daily provider spending cap has been reached.");
            var effectiveShardCap = effectiveShardedCap <= 0 ? 0 : GlobalUsageShardCap(
                effectiveShardedCap,
                _options.GlobalUsageShardCount,
                shardIndex);
            var globalCommitted = CommittedIncludingCompanyLoss(global);
            if (_options.DailySpendingCapsEnabled && globalCommitted + additional > effectiveShardCap)
                throw new InvalidOperationException("The daily provider spending cap has been reached.");
            var userDailyRef = accountRef.Collection("dailyUsage").Document(
                globalUsageDate);
            var userDailySnapshot = await transaction.GetSnapshotAsync(userDailyRef, cancellationToken);
            var userDaily = userDailySnapshot.Exists ? userDailySnapshot.ToDictionary() : [];
            var userDailyCommitted = CommittedIncludingCompanyLoss(userDaily);
            if (_options.DailySpendingCapsEnabled && userDailyCommitted + additional > _options.PerUserDailyCreditMicrosCap)
                throw new InvalidOperationException("The account daily AI spending cap has been reached.");

            var eventId = $"reserveext_{SafeId(requestId)}_{targetReservedCreditMicros}";
            transaction.Create(accountRef.Collection("ledger").Document(eventId), LedgerMap(new LedgerEntry(
                eventId,
                LedgerTypes.UsageReservation,
                -additional,
                additional,
                "usage",
                requestId,
                Crypto.Sha256($"usage-reserve-extension:{userId}:{requestId}:{targetReservedCreditMicros}"),
                userId,
                "Live request budget extended",
                now)));
            transaction.Update(reservationRef, new Dictionary<string, object>
            {
                ["reservedCreditMicros"] = targetReservedCreditMicros,
                ["allowanceAllocations"] = bucketedReservation
                    ? AllowanceWallet.AllocationMap(updatedAllocations)
                    : Array.Empty<object>(),
                ["updatedAt"] = Ts(now),
            });
            var accountUpdates = new Dictionary<string, object>
            {
                ["availableCreditMicros"] = bucketedReservation
                    ? wallet.AvailableCreditMicros
                    : available - additional,
                ["reservedCreditMicros"] = bucketedReservation
                    ? wallet.ReservedCreditMicros
                    : Long(account, "reservedCreditMicros") + additional,
                ["updatedAt"] = Ts(now),
            };
            if (bucketedReservation)
            {
                accountUpdates["allowanceRulesVersion"] = AllowanceWallet.CurrentRulesVersion;
                accountUpdates["allowanceBuckets"] = wallet.ToFirestoreMap();
            }
            transaction.Update(accountRef, accountUpdates);
            transaction.Set(globalRef, new Dictionary<string, object>
            {
                ["reservedCreditMicros"] = FieldValue.Increment(additional),
                ["updatedAt"] = Ts(now),
            }, SetOptions.MergeAll);
            transaction.Set(userDailyRef, new Dictionary<string, object>
            {
                ["reservedCreditMicros"] = FieldValue.Increment(additional),
                ["settledCreditMicros"] = Long(userDaily, "settledCreditMicros"),
                ["dailyCapCreditMicros"] = _options.PerUserDailyCreditMicrosCap,
                ["updatedAt"] = Ts(now),
            }, SetOptions.MergeAll);
            return targetReservedCreditMicros;
        }, cancellationToken: cancellationToken);

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await ExecuteCapacityTransactionAsync();
            }
            catch (RpcException exception) when (
                exception.StatusCode == StatusCode.Aborted
                && attempt < _options.MaxRetries)
            {
                // Live traffic can extend the same rolling reservation while
                // other account/global-shard transactions settle. Firestore has
                // rolled the aborted transaction back, so retry from fresh state
                // with a bounded delay. The target amount and ledger event ID are
                // deterministic, keeping a lost-response retry idempotent.
                await Task.Delay(TimeSpan.FromMilliseconds(40 * (attempt + 1)), cancellationToken);
            }
        }
    }

    public Task RecordTerminalProviderRejectionAsync(string userId, string requestId,
        int httpStatus, CancellationToken cancellationToken)
    {
        if (!AiUsageService.IsDefinitivelyNonBillable(httpStatus))
            throw new ArgumentOutOfRangeException(nameof(httpStatus));
        var reservationRef = Account(userId).Collection("reservations").Document(SafeId(requestId));
        var now = _clock.UtcNow;
        return _db.RunTransactionAsync(async transaction =>
        {
            var snapshot = await transaction.GetSnapshotAsync(reservationRef, cancellationToken);
            if (!snapshot.Exists) throw new InvalidOperationException("The usage reservation no longer exists.");
            var data = snapshot.ToDictionary();
            if (!data.ContainsKey("providerStartedAt") || Bool(data, "providerCompletionRecorded", false)
                || Long(data, "checkpointTotalTokens") > 0 || String(data, "status", "") == "settled")
                throw new InvalidOperationException("Terminal rejection conflicts with provider accounting evidence.");
            if (data.ContainsKey("providerHttpStatus"))
            {
                if (Long(data, "providerHttpStatus") != httpStatus)
                    throw new InvalidOperationException("Conflicting terminal provider rejection was rejected.");
                return;
            }
            if (String(data, "status", "") is not ("reserved" or "reconciliation_pending"))
                throw new InvalidOperationException("The reservation cannot record a terminal rejection.");
            transaction.Update(reservationRef, new Dictionary<string, object>
            {
                ["providerHttpStatus"] = (long)httpStatus,
                ["providerRejectedAt"] = Ts(now), ["updatedAt"] = Ts(now),
            });
        }, cancellationToken: cancellationToken);
    }

    public Task RecordProviderCompletionAsync(
        string userId,
        string requestId,
        ProviderUsage usage,
        long actualCreditMicros,
        long providerCostUsdMicros,
        string outcome,
        CancellationToken cancellationToken,
        string? model = null,
        string? pricingVersion = null)
    {
        if (actualCreditMicros < 0 || providerCostUsdMicros < 0)
            throw new ArgumentOutOfRangeException(nameof(actualCreditMicros));
        var reservationRef = Account(userId)
            .Collection("reservations")
            .Document(SafeId(requestId));
        var now = _clock.UtcNow;
        return _db.RunTransactionAsync(async transaction =>
        {
            var snapshot = await transaction.GetSnapshotAsync(reservationRef, cancellationToken);
            if (!snapshot.Exists)
                throw new InvalidOperationException("The usage reservation no longer exists.");
            var data = snapshot.ToDictionary();
            var status = String(data, "status", "");
            if (status is "settled" or "released")
                return;
            if (UsageReconciliationService.CanReleaseVerifiedProviderRejection(Long(data, "providerHttpStatus")))
                throw new InvalidOperationException("Provider completion conflicts with terminal rejection evidence.");
            if (!ProviderUsageMetadata.IsAuthoritative(usage))
                throw new InvalidOperationException("Unknown provider usage cannot be recorded as completed evidence.");
            if (Long(data, "accountingVersion") >= 2
                && !data.ContainsKey("providerStartedAt"))
                throw new InvalidOperationException(
                    "Provider completion cannot precede the durable dispatch marker.");
            if (status is not ("reserved" or "reconciliation_pending" or "provider_completed"))
                throw new InvalidOperationException("The usage reservation cannot record provider completion.");
            if (Bool(data, "providerCompletionRecorded", false))
            {
                if (Long(data, "plannedChargeCreditMicros") != actualCreditMicros
                    || Long(data, "providerCostUsdMicros") != providerCostUsdMicros
                    || !UsageEvidenceMatches(data, usage)
                    || (!string.IsNullOrWhiteSpace(model)
                        && !string.Equals(String(data, "model", ""), model, StringComparison.Ordinal))
                    || (!string.IsNullOrWhiteSpace(pricingVersion)
                        && !string.Equals(String(data, "pricingVersion", ""), pricingVersion, StringComparison.Ordinal)))
                    throw new InvalidOperationException("Conflicting provider completion evidence was rejected.");
                return;
            }
            var updates = UsageEvidenceMap(usage, actualCreditMicros, providerCostUsdMicros);
            if (!string.IsNullOrWhiteSpace(model))
                updates["model"] = CleanProviderValue(model);
            if (!string.IsNullOrWhiteSpace(pricingVersion))
                updates["pricingVersion"] = CleanProviderValue(pricingVersion);
            updates["providerCompletionRecorded"] = true;
            updates["providerCompletedAt"] = Ts(now);
            updates["status"] = "provider_completed";
            updates["outcome"] = CleanOutcome(outcome);
            updates["updatedAt"] = Ts(now);
            transaction.Update(reservationRef, updates);
        }, cancellationToken: cancellationToken);
    }

    public Task CheckpointReservationUsageAsync(
        string userId,
        string requestId,
        ProviderUsage usage,
        long estimatedCreditMicros,
        long providerCostUsdMicros,
        CancellationToken cancellationToken)
    {
        if (estimatedCreditMicros < 0 || providerCostUsdMicros < 0)
            throw new ArgumentOutOfRangeException(nameof(estimatedCreditMicros));
        var reservationRef = Account(userId)
            .Collection("reservations")
            .Document(SafeId(requestId));
        var now = _clock.UtcNow;
        return _db.RunTransactionAsync(async transaction =>
        {
            var snapshot = await transaction.GetSnapshotAsync(reservationRef, cancellationToken);
            if (!snapshot.Exists)
                return;
            var data = snapshot.ToDictionary();
            if (String(data, "status", "") is "settled" or "released" or ManagedLiveLossTerminalStatus)
                return;
            if (UsageReconciliationService.CanReleaseVerifiedProviderRejection(Long(data, "providerHttpStatus")))
                throw new InvalidOperationException("Provider usage conflicts with terminal rejection evidence.");
            if (estimatedCreditMicros < Long(data, "checkpointChargeCreditMicros")
                || usage.TotalTokens < Long(data, "checkpointTotalTokens"))
                return;
            var updates = UsageCheckpointMap(usage, estimatedCreditMicros, providerCostUsdMicros);
            updates["checkpointAt"] = Ts(now);
            updates["updatedAt"] = Ts(now);
            transaction.Update(reservationRef, updates);
        }, cancellationToken: cancellationToken);
    }

    public async Task ReleaseReservationAsync(
        string userId,
        string requestId,
        string outcome,
        CancellationToken cancellationToken)
    {
        var accountRef = Account(userId);
        var reservationRef = accountRef.Collection("reservations").Document(SafeId(requestId));
        var idempotencyRef = _db.Collection("idempotency")
            .Document(Crypto.Sha256($"usage-release:{userId}:{requestId}"));
        var now = _clock.UtcNow;

        async Task ExecuteReleaseTransactionAsync() => await _db.RunTransactionAsync(async transaction =>
        {
            var accountSnapshot = await transaction.GetSnapshotAsync(accountRef, cancellationToken);
            var reservationSnapshot = await transaction.GetSnapshotAsync(reservationRef, cancellationToken);
            var idempotencySnapshot = await transaction.GetSnapshotAsync(idempotencyRef, cancellationToken);
            if (!accountSnapshot.Exists || !reservationSnapshot.Exists || idempotencySnapshot.Exists)
                return 0;
            var reservation = reservationSnapshot.ToDictionary();
            var status = String(reservation, "status", "");
            if (!string.Equals(status, "reserved", StringComparison.Ordinal)
                && !string.Equals(status, "reconciliation_pending", StringComparison.Ordinal))
                return 0;
            var providerStarted = reservation.ContainsKey("providerStartedAt");
            var safelyRejected = string.Equals(
                CleanOutcome(outcome),
                "provider_rejected",
                StringComparison.Ordinal);
            if (providerStarted && (!safelyRejected
                || !UsageReconciliationService.CanReleaseVerifiedProviderRejection(Long(reservation, "providerHttpStatus"))))
                throw new InvalidOperationException(
                    "A reservation cannot be released after provider dispatch without authoritative no-charge evidence.");
            var account = accountSnapshot.ToDictionary();
            var reserved = Long(reservation, "reservedCreditMicros");
            var allocations = AllowanceWallet.ReadAllocations(reservation);
            var bucketedReservation = allocations.Count > 0;
            var wallet = AllowanceWallet.FromAccount(account, now);
            var returnedToAvailable = bucketedReservation ? wallet.Release(allocations) : reserved;
            var globalShardId = String(reservation, "globalUsageShardId", "");
            var globalRef = string.IsNullOrWhiteSpace(globalShardId)
                ? _db.Collection("globalUsage").Document(
                    String(reservation, "globalUsageDate", now.ToString("yyyyMMdd")))
                : _db.Collection("globalUsageShards").Document(globalShardId);
            var userDailyRef = accountRef.Collection("dailyUsage").Document(
                String(reservation, "globalUsageDate", now.ToString("yyyyMMdd")));
            var userDailySnapshot = await transaction.GetSnapshotAsync(userDailyRef, cancellationToken);
            var userDaily = userDailySnapshot.Exists ? userDailySnapshot.ToDictionary() : [];
            var concurrencyReleased = await ConcurrencyAlreadyReleasedAsync(
                transaction, userId, requestId, reservation, cancellationToken);
            var eventId = $"release_{SafeId(requestId)}";
            transaction.Create(accountRef.Collection("ledger").Document(eventId), LedgerMap(new LedgerEntry(
                eventId,
                LedgerTypes.ReservationRelease,
                returnedToAvailable,
                -reserved,
                "usage",
                requestId,
                idempotencyRef.Id,
                "system",
                outcome,
                now)));
            transaction.Create(idempotencyRef, new Dictionary<string, object>
            {
                ["scope"] = "usage-release",
                ["userId"] = userId,
                ["resourceId"] = requestId,
                ["createdAt"] = Ts(now),
            });
            transaction.Update(reservationRef, new Dictionary<string, object>
            {
                ["status"] = "released",
                ["concurrencyReleased"] = true,
                ["releasedCreditMicros"] = returnedToAvailable,
                ["outcome"] = outcome,
                ["updatedAt"] = Ts(now),
            });
            var accountUpdates = new Dictionary<string, object>
            {
                ["availableCreditMicros"] = bucketedReservation
                    ? wallet.AvailableCreditMicros
                    : Long(account, "availableCreditMicros") + reserved,
                ["reservedCreditMicros"] = bucketedReservation
                    ? wallet.ReservedCreditMicros
                    : Math.Max(0, Long(account, "reservedCreditMicros") - reserved),
                ["activeRequests"] = Math.Max(0, Long(account, "activeRequests") - (concurrencyReleased ? 0 : 1)),
                ["activeLiveRequests"] = RemainingLiveRequests(account, reservation, concurrencyReleased),
                ["updatedAt"] = Ts(now),
            };
            if (bucketedReservation)
            {
                accountUpdates["allowanceRulesVersion"] = AllowanceWallet.CurrentRulesVersion;
                accountUpdates["allowanceBuckets"] = wallet.ToFirestoreMap();
            }
            transaction.Update(accountRef, accountUpdates);
            transaction.Set(globalRef, new Dictionary<string, object>
            {
                ["reservedCreditMicros"] = FieldValue.Increment(-reserved),
                ["updatedAt"] = Ts(now),
            }, SetOptions.MergeAll);
            transaction.Set(userDailyRef, new Dictionary<string, object>
            {
                ["reservedCreditMicros"] = Math.Max(0, Long(userDaily, "reservedCreditMicros") - reserved),
                ["settledCreditMicros"] = Long(userDaily, "settledCreditMicros"),
                ["updatedAt"] = Ts(now),
            }, SetOptions.MergeAll);
            return 0;
        }, cancellationToken: cancellationToken);

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await ExecuteReleaseTransactionAsync();
                return;
            }
            catch (RpcException exception) when (
                exception.StatusCode == StatusCode.Aborted
                && attempt < _options.MaxRetries)
            {
                // All release writes are protected by the deterministic
                // release idempotency record and ledger event. Retry only the
                // fully rolled-back Firestore transaction after contention.
                await Task.Delay(
                    TimeSpan.FromMilliseconds(40 * (attempt + 1)),
                    cancellationToken);
            }
        }
    }

    public async Task MarkReservationForReconciliationAsync(
        string userId,
        string requestId,
        string outcome,
        CancellationToken cancellationToken)
    {
        var accountRef = Account(userId);
        var reservationRef = accountRef.Collection("reservations").Document(SafeId(requestId));
        var now = _clock.UtcNow;
        var auditRef = _db.Collection("auditEvents")
            .Document(Crypto.Sha256($"reservation-reconcile:{userId}:{requestId}"));
        await _db.RunTransactionAsync(async transaction =>
        {
            var accountSnapshot = await transaction.GetSnapshotAsync(accountRef, cancellationToken);
            var reservationSnapshot = await transaction.GetSnapshotAsync(reservationRef, cancellationToken);
            if (!accountSnapshot.Exists || !reservationSnapshot.Exists)
                return 0;
            var reservation = reservationSnapshot.ToDictionary();
            var status = String(reservation, "status", "");
            if (!(string.Equals(status, "reserved", StringComparison.Ordinal)
                    || string.Equals(status, "provider_completed", StringComparison.Ordinal))
                || !reservation.ContainsKey("providerStartedAt"))
                return 0;
            var account = accountSnapshot.ToDictionary();
            var concurrencyReleased = await ConcurrencyAlreadyReleasedAsync(
                transaction, userId, requestId, reservation, cancellationToken);
            // The completion write may have committed before its response was
            // lost. Preserve exact durable evidence so maintenance can settle
            // it; quarantine only unmeasured/invalid completion receipts.
            var preserveCompletion = status == "provider_completed"
                && Bool(reservation, "providerCompletionRecorded", false)
                && ProviderUsageMetadata.ReadCompletion(reservation).Known;
            transaction.Update(reservationRef, new Dictionary<string, object>
            {
                ["status"] = preserveCompletion ? "provider_completed" : "reconciliation_pending",
                ["concurrencyReleased"] = true,
                ["outcome"] = outcome,
                ["updatedAt"] = Ts(now),
            });
            transaction.Update(accountRef, new Dictionary<string, object>
            {
                ["activeRequests"] = Math.Max(0, Long(account, "activeRequests") - (concurrencyReleased ? 0 : 1)),
                ["activeLiveRequests"] = RemainingLiveRequests(account, reservation, concurrencyReleased),
                ["updatedAt"] = Ts(now),
            });
            transaction.Set(auditRef, new Dictionary<string, object>
            {
                ["event"] = "reservation_reconciliation_pending",
                ["userId"] = userId,
                ["requestId"] = requestId,
                ["outcome"] = outcome,
                ["createdAt"] = Ts(now),
            }, SetOptions.MergeAll);
            return 0;
        }, cancellationToken: cancellationToken);
    }

    private async Task RecoverExpiredReservationsAsync(
        string userId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var reservations = await Account(userId)
            .Collection("reservations")
            .WhereEqualTo("status", "reserved")
            .Limit(25)
            .GetSnapshotAsync(cancellationToken);
        var fallbackLease = TimeSpan.FromSeconds(
            Math.Max(_options.MaxRequestSeconds, _options.LiveSessionMaxSeconds)
            + _options.ReservationLeaseGraceSeconds);
        foreach (var snapshot in reservations.Documents)
        {
            var data = snapshot.ToDictionary();
            var expiresAt = Date(data, "expiresAt");
            if (expiresAt == DateTimeOffset.UnixEpoch)
                expiresAt = Date(data, "createdAt").Add(fallbackLease);
            if (expiresAt > now)
                continue;

            if (!data.ContainsKey("providerStartedAt"))
            {
                await ReleaseReservationAsync(
                    userId,
                    String(data, "requestId", snapshot.Id),
                    "expired_before_provider_start",
                    cancellationToken);
                continue;
            }

            await MarkReservationForReconciliationAsync(
                userId,
                String(data, "requestId", snapshot.Id),
                "worker_lease_expired_after_provider_start",
                cancellationToken);
        }
    }

    public async Task<DesktopLoginAttempt> CreateLoginAttemptAsync(
        string stateHash,
        string deviceNonceHash,
        string pkceChallenge,
        string callbackUri,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        var attemptId = Crypto.RandomToken(18);
        var attemptRef = _db.Collection("desktopLoginAttempts").Document(attemptId);
        var now = _clock.UtcNow;
        await attemptRef.CreateAsync(new Dictionary<string, object>
        {
            ["stateHash"] = stateHash,
            ["deviceNonceHash"] = deviceNonceHash,
            ["pkceChallenge"] = pkceChallenge,
            ["callbackUri"] = callbackUri,
            ["status"] = "pending",
            ["expiresAt"] = Ts(expiresAt),
            ["createdAt"] = Ts(now),
            ["updatedAt"] = Ts(now),
        }, cancellationToken);
        return new DesktopLoginAttempt(
            attemptId, stateHash, deviceNonceHash, pkceChallenge, callbackUri, "pending", expiresAt);
    }

    public async Task<(DesktopLoginAttempt Attempt, string AuthorizationCode)> AuthorizeLoginAttemptAsync(
        string attemptId,
        string stateHash,
        string deviceNonceHash,
        string userId,
        CancellationToken cancellationToken)
    {
        var attemptRef = _db.Collection("desktopLoginAttempts").Document(SafeId(attemptId));
        var authorizationCode = Crypto.RandomToken(32);
        var codeHash = Crypto.Sha256(authorizationCode);
        var now = _clock.UtcNow;
        var result = await _db.RunTransactionAsync(async transaction =>
        {
            var snapshot = await transaction.GetSnapshotAsync(attemptRef, cancellationToken);
            if (!snapshot.Exists)
                throw new InvalidOperationException("The desktop login request was not found.");
            var data = snapshot.ToDictionary();
            if (!string.Equals(String(data, "status", ""), "pending", StringComparison.Ordinal)
                || Date(data, "expiresAt") <= now
                || !FixedHash(String(data, "stateHash", ""), stateHash)
                || !FixedHash(String(data, "deviceNonceHash", ""), deviceNonceHash))
                throw new InvalidOperationException("The desktop login request is invalid or expired.");
            RequireActiveAccount(await transaction.GetSnapshotAsync(Account(userId), cancellationToken));
            transaction.Update(attemptRef, new Dictionary<string, object>
            {
                ["status"] = "authorized",
                ["userId"] = userId,
                ["authorizationCodeHash"] = codeHash,
                ["authorizationCodeExpiresAt"] = Ts(now.AddMinutes(1)),
                ["updatedAt"] = Ts(now),
            });
            return AttemptFrom(snapshot, "authorized", userId, codeHash);
        }, cancellationToken: cancellationToken);
        return (result, authorizationCode);
    }

    public async Task<DesktopSessionTokens> ExchangeLoginAttemptAsync(
        string attemptId,
        string stateHash,
        string deviceNonceHash,
        string authorizationCodeHash,
        string pkceVerifier,
        string deviceName,
        string deviceInstallationId,
        TimeSpan accessLifetime,
        TimeSpan refreshLifetime,
        CancellationToken cancellationToken)
    {
        var attemptRef = _db.Collection("desktopLoginAttempts").Document(SafeId(attemptId));
        var now = _clock.UtcNow;
        var sessionId = Crypto.RandomToken(18);
        var accessToken = $"kwt_at_{sessionId}.{Crypto.RandomToken(32)}";
        var refreshToken = $"kwt_rt_{sessionId}.{Crypto.RandomToken(48)}";
        var accessExpiresAt = now.Add(accessLifetime);
        var refreshExpiresAt = now.Add(refreshLifetime);

        return await _db.RunTransactionAsync(async transaction =>
        {
            var attemptSnapshot = await transaction.GetSnapshotAsync(attemptRef, cancellationToken);
            if (!attemptSnapshot.Exists)
                throw new InvalidOperationException("The desktop login request was not found.");
            var attempt = attemptSnapshot.ToDictionary();
            var userId = String(attempt, "userId", "");
            if (string.IsNullOrWhiteSpace(userId)
                || !string.Equals(String(attempt, "status", ""), "authorized", StringComparison.Ordinal)
                || Date(attempt, "authorizationCodeExpiresAt") <= now
                || !FixedHash(String(attempt, "stateHash", ""), stateHash)
                || !FixedHash(String(attempt, "deviceNonceHash", ""), deviceNonceHash)
                || !FixedHash(String(attempt, "authorizationCodeHash", ""), authorizationCodeHash)
                || !FixedHash(String(attempt, "pkceChallenge", ""), Crypto.PkceChallenge(pkceVerifier)))
                throw new InvalidOperationException("The desktop authorization code is invalid, expired, or already used.");

            var accountRef = Account(userId);
            var accountSnapshot = await transaction.GetSnapshotAsync(accountRef, cancellationToken);
            if (!accountSnapshot.Exists)
                throw new InvalidOperationException("The account is unavailable.");
            RequireActiveAccount(accountSnapshot);
            var deviceId = Crypto.Sha256($"{userId}:{deviceInstallationId}")[..32];
            var sessionRef = _db.Collection("desktopSessions").Document(sessionId);
            var deviceRef = accountRef.Collection("devices").Document(deviceId);
            transaction.Create(sessionRef, new Dictionary<string, object>
            {
                ["sessionId"] = sessionId,
                ["userId"] = userId,
                ["deviceId"] = deviceId,
                ["accessTokenHash"] = Crypto.Sha256(accessToken),
                ["refreshTokenHash"] = Crypto.Sha256(refreshToken),
                ["accessExpiresAt"] = Ts(accessExpiresAt),
                ["refreshExpiresAt"] = Ts(refreshExpiresAt),
                ["status"] = "active",
                ["createdAt"] = Ts(now),
                ["lastSeenAt"] = Ts(now),
            });
            transaction.Set(deviceRef, new Dictionary<string, object>
            {
                ["name"] = CleanDeviceName(deviceName),
                ["platform"] = "Windows",
                ["status"] = "active",
                ["createdAt"] = Ts(now),
                ["lastSeenAt"] = Ts(now),
            }, SetOptions.MergeAll);
            transaction.Update(attemptRef, new Dictionary<string, object>
            {
                ["status"] = "consumed",
                ["authorizationCodeHash"] = "consumed",
                ["consumedAt"] = Ts(now),
                ["updatedAt"] = Ts(now),
            });
            return new DesktopSessionTokens(
                accessToken, accessExpiresAt, refreshToken, refreshExpiresAt, sessionId, deviceId);
        }, cancellationToken: cancellationToken);
    }

    public async Task<CallerIdentity?> AuthenticateDesktopTokenAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        var sessionId = TokenSessionId(accessToken, "kwt_at_");
        if (sessionId is null)
            return null;
        var sessionRef = _db.Collection("desktopSessions").Document(sessionId);
        var sessionSnapshot = await sessionRef.GetSnapshotAsync(cancellationToken);
        if (!sessionSnapshot.Exists)
            return null;
        var session = sessionSnapshot.ToDictionary();
        if (!string.Equals(String(session, "status", ""), "active", StringComparison.Ordinal)
            || Date(session, "accessExpiresAt") <= _clock.UtcNow
            || !FixedHash(String(session, "accessTokenHash", ""), Crypto.Sha256(accessToken)))
            return null;
        var userId = String(session, "userId", "");
        var accountSnapshot = await Account(userId).GetSnapshotAsync(cancellationToken);
        if (!accountSnapshot.Exists)
            return null;
        var account = accountSnapshot.ToDictionary();
        if (!string.Equals(String(account, "status", "active"), "active", StringComparison.Ordinal))
            return null;
        return new CallerIdentity(
            userId,
            String(account, "email", ""),
            String(account, "displayName", ""),
            "desktop",
            sessionId,
            String(session, "deviceId", ""));
    }

    public async Task<DesktopSessionTokens> RefreshSessionAsync(
        string refreshToken,
        TimeSpan accessLifetime,
        TimeSpan refreshLifetime,
        CancellationToken cancellationToken)
    {
        var sessionId = TokenSessionId(refreshToken, "kwt_rt_")
            ?? throw new InvalidOperationException("The refresh session is invalid.");
        var sessionRef = _db.Collection("desktopSessions").Document(sessionId);
        var now = _clock.UtcNow;
        var newAccess = $"kwt_at_{sessionId}.{Crypto.RandomToken(32)}";
        var newRefresh = $"kwt_rt_{sessionId}.{Crypto.RandomToken(48)}";
        var accessExpires = now.Add(accessLifetime);
        var refreshExpires = now.Add(refreshLifetime);
        return await _db.RunTransactionAsync(async transaction =>
        {
            var snapshot = await transaction.GetSnapshotAsync(sessionRef, cancellationToken);
            if (!snapshot.Exists)
                throw new InvalidOperationException("The refresh session was not found.");
            var data = snapshot.ToDictionary();
            if (!string.Equals(String(data, "status", ""), "active", StringComparison.Ordinal)
                || Date(data, "refreshExpiresAt") <= now
                || !FixedHash(String(data, "refreshTokenHash", ""), Crypto.Sha256(refreshToken)))
                throw new InvalidOperationException("The refresh session is expired or revoked.");
            RequireActiveAccount(await transaction.GetSnapshotAsync(
                Account(String(data, "userId", "")), cancellationToken));
            transaction.Update(sessionRef, new Dictionary<string, object>
            {
                ["accessTokenHash"] = Crypto.Sha256(newAccess),
                ["refreshTokenHash"] = Crypto.Sha256(newRefresh),
                ["accessExpiresAt"] = Ts(accessExpires),
                ["refreshExpiresAt"] = Ts(refreshExpires),
                ["lastSeenAt"] = Ts(now),
            });
            var deviceId = String(data, "deviceId", "");
            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                transaction.Set(
                    Account(String(data, "userId", "")).Collection("devices").Document(SafeId(deviceId)),
                    new Dictionary<string, object>
                    {
                        ["status"] = "active",
                        ["platform"] = "Windows",
                        ["lastSeenAt"] = Ts(now),
                    },
                    SetOptions.MergeAll);
            }
            return new DesktopSessionTokens(
                newAccess,
                accessExpires,
                newRefresh,
                refreshExpires,
                sessionId,
                String(data, "deviceId", ""));
        }, cancellationToken: cancellationToken);
    }

    public async Task RevokeSessionAsync(
        string userId,
        string? sessionId,
        bool allDevices,
        CancellationToken cancellationToken)
    {
        if (!allDevices && !string.IsNullOrWhiteSpace(sessionId))
        {
            var sessionRef = _db.Collection("desktopSessions").Document(SafeId(sessionId));
            var snapshot = await sessionRef.GetSnapshotAsync(cancellationToken);
            if (snapshot.Exists && string.Equals(snapshot.GetValue<string>("userId"), userId, StringComparison.Ordinal))
            {
                var data = snapshot.ToDictionary();
                var logoutBatch = _db.StartBatch();
                logoutBatch.Update(sessionRef, new Dictionary<string, object>
                {
                    ["status"] = "revoked",
                    ["updatedAt"] = Ts(_clock.UtcNow),
                });
                var deviceId = String(data, "deviceId", "");
                if (!string.IsNullOrWhiteSpace(deviceId))
                    logoutBatch.Set(Account(userId).Collection("devices").Document(SafeId(deviceId)),
                        new Dictionary<string, object>
                        {
                            ["status"] = "revoked",
                            ["updatedAt"] = Ts(_clock.UtcNow),
                        },
                        SetOptions.MergeAll);
                await logoutBatch.CommitAsync(cancellationToken);
            }
            return;
        }

        var sessions = await _db.Collection("desktopSessions")
            .WhereEqualTo("userId", userId)
            .WhereEqualTo("status", "active")
            .GetSnapshotAsync(cancellationToken);
        var batch = _db.StartBatch();
        foreach (var session in sessions.Documents)
        {
            batch.Update(session.Reference, "status", "revoked");
            var data = session.ToDictionary();
            var deviceId = String(data, "deviceId", "");
            if (!string.IsNullOrWhiteSpace(deviceId))
                batch.Set(Account(userId).Collection("devices").Document(SafeId(deviceId)),
                    new Dictionary<string, object>
                    {
                        ["status"] = "revoked",
                        ["updatedAt"] = Ts(_clock.UtcNow),
                    },
                    SetOptions.MergeAll);
        }
        await batch.CommitAsync(cancellationToken);
    }

    public async Task RevokeDeviceAsync(string userId, string deviceId, CancellationToken cancellationToken)
    {
        var sessions = await _db.Collection("desktopSessions")
            .WhereEqualTo("userId", userId)
            .WhereEqualTo("deviceId", deviceId)
            .WhereEqualTo("status", "active")
            .GetSnapshotAsync(cancellationToken);
        var batch = _db.StartBatch();
        foreach (var session in sessions.Documents)
            batch.Update(session.Reference, "status", "revoked");
        batch.Set(Account(userId).Collection("devices").Document(SafeId(deviceId)),
            new Dictionary<string, object> { ["status"] = "revoked", ["updatedAt"] = Ts(_clock.UtcNow) },
            SetOptions.MergeAll);
        await batch.CommitAsync(cancellationToken);
    }

    public Task<AccountDeletionSubscriptions> BeginAccountDeletionAsync(
        string userId,
        CancellationToken cancellationToken) => BeginAccountDeletionCoreAsync(userId, null, cancellationToken);

    public Task<AccountDeletionSubscriptions> BeginConfirmedAccountDeletionAsync(
        CallerIdentity caller, CancellationToken cancellationToken)
    {
        if (caller.AuthenticationKind != "firebase"
            || !AccountDeletionService.IsRecentAuthentication(caller.AuthenticatedAt, _clock.UtcNow))
            throw new UnauthorizedAccessException("Account deletion requires a fresh Google sign-in.");
        return BeginAccountDeletionCoreAsync(caller.UserId, caller, cancellationToken);
    }

    private Task<AccountDeletionSubscriptions> BeginAccountDeletionCoreAsync(
        string userId, CallerIdentity? confirmation, CancellationToken cancellationToken)
    {
        var accountRef = Account(userId);
        var now = _clock.UtcNow;
        return _db.RunTransactionAsync(async transaction =>
        {
            var snapshot = await transaction.GetSnapshotAsync(accountRef, cancellationToken);
            var subscriptions = await ReadAccountDeletionSubscriptionsAsync(
                transaction, accountRef, snapshot, cancellationToken);
            if (!snapshot.Exists)
            {
                if (subscriptions.Pending.Count > 0 || subscriptions.HasUnresolvedSubscription)
                    throw new InvalidOperationException("An orphan subscription requires review before account deletion.");
                if (confirmation is not null)
                    throw new KeyNotFoundException("The account was not found. Contact support to verify identity cleanup.");
                return subscriptions;
            }
            var account = snapshot.ToDictionary();
            if (Long(account, "activeRequests") > 0)
                throw new InvalidOperationException(
                    "An AI request is still being reconciled. Cancel it or wait for cleanup before deleting the account.");
            if (String(account, "status", "active") is not ("active" or "deleting" or "deleted"))
                throw new InvalidOperationException("The account requires review before deletion.");
            if (String(account, "status", "active") != "deleted" || confirmation is not null)
            {
                var changes = new Dictionary<string, object>
                {
                    ["status"] = String(account, "status", "active") == "deleted" ? "deleted" : "deleting",
                    ["deletionRequestedAt"] = account.TryGetValue("deletionRequestedAt", out var requestedAt) ? requestedAt : Ts(now),
                    ["updatedAt"] = Ts(now),
                };
                if (confirmation is not null && !HasConfirmedDeletionIntent(account, userId))
                {
                    changes["deletionIntentVersion"] = 1L;
                    changes["deletionRequestedBy"] = userId;
                    changes["deletionConfirmedAt"] = Ts(now);
                    changes["deletionAuthenticatedAt"] = Ts(confirmation.AuthenticatedAt!.Value);
                }
                transaction.Update(accountRef, changes);
            }
            return subscriptions;
        }, cancellationToken: cancellationToken);
    }

    public async Task<AccountDeletionProgress> GetConfirmedAccountDeletionProgressAsync(
        string userId, CancellationToken cancellationToken)
    {
        var snapshot = await Account(userId).GetSnapshotAsync(cancellationToken);
        if (!snapshot.Exists) return AccountDeletionProgress.Unknown;
        var data = snapshot.ToDictionary();
        if (!HasConfirmedDeletionIntent(data, userId)) return AccountDeletionProgress.Unknown;
        if (String(data, "status", "") == "deleting") return AccountDeletionProgress.Pending;
        return String(data, "status", "") == "deleted"
            && data.TryGetValue("operationalCleanupCompletedAt", out var cleaned) && cleaned is Timestamp
            && data.TryGetValue("deletedAt", out var deleted) && deleted is Timestamp
            && data.TryGetValue("personalDataDeleted", out var done) && done is true
            ? AccountDeletionProgress.Complete : AccountDeletionProgress.Unknown;
    }

    private static bool HasConfirmedDeletionIntent(IDictionary<string, object> data, string userId) =>
        data.TryGetValue("deletionIntentVersion", out var version) && version is long value && value == 1
        && String(data, "deletionRequestedBy", "") == userId
        && data.TryGetValue("deletionConfirmedAt", out var confirmed) && confirmed is Timestamp confirmedAt
        && data.TryGetValue("deletionAuthenticatedAt", out var authenticated) && authenticated is Timestamp authenticatedAt
        && AccountDeletionService.IsRecentAuthentication(
            new DateTimeOffset(authenticatedAt.ToDateTime()), new DateTimeOffset(confirmedAt.ToDateTime()));

    public Task RecordAccountDeletionSubscriptionStoppedAsync(
        string userId,
        AccountDeletionSubscription subscription,
        string providerStatus,
        CancellationToken cancellationToken)
    {
        if (!AccountDeletionService.IsStopped(providerStatus))
            throw new InvalidOperationException("A nonterminal subscription is not cancellation proof.");
        var accountRef = Account(userId);
        var purchaseRef = accountRef.Collection("purchases").Document(SafeId(subscription.PurchaseId));
        return _db.RunTransactionAsync(async transaction =>
        {
            var account = await transaction.GetSnapshotAsync(accountRef, cancellationToken);
            var purchase = await transaction.GetSnapshotAsync(purchaseRef, cancellationToken);
            if (!account.Exists || !purchase.Exists
                || String(account.ToDictionary(), "status", "") is not ("deleting" or "deleted")
                || DeletionSubscription(purchase, userId) != subscription)
                throw new InvalidOperationException("The account subscription changed during cancellation.");
            if (!HasVerifiedSubscriptionTerminalProof(purchase.ToDictionary()))
                transaction.Update(purchaseRef, new Dictionary<string, object>
                {
                    ["subscriptionTerminalProviderStatus"] = providerStatus,
                    ["subscriptionTerminalProviderObjectId"] = subscription.ProviderSubscriptionId,
                    ["subscriptionTerminalVerifiedAt"] = Ts(_clock.UtcNow),
                });
            return 0;
        }, cancellationToken: cancellationToken);
    }

    private async Task<AccountDeletionSubscriptions> ReadAccountDeletionSubscriptionsAsync(
        Transaction transaction,
        DocumentReference accountRef,
        DocumentSnapshot accountSnapshot,
        CancellationToken cancellationToken)
    {
        // Same bound as checkout's legacy-history guard. Overflow cannot imply
        // an empty/complete history and requires explicit reconciliation.
        var history = await transaction.GetSnapshotAsync(accountRef.Collection("purchases")
            .WhereEqualTo("kind", "subscription").Limit(65), cancellationToken);
        if (history.Count > 64)
            throw new InvalidOperationException("Subscription history needs review before account deletion.");
        var pending = new List<AccountDeletionSubscription>();
        var unresolved = false;
        var all = new List<AccountDeletionSubscription>();
        foreach (var purchase in history.Documents)
        {
            var target = DeletionSubscription(purchase, accountRef.Id);
            if (target is null) { unresolved = true; continue; }
            all.Add(target);
            if (!HasVerifiedSubscriptionTerminalProof(purchase.ToDictionary()))
                pending.Add(target);
        }
        if (all.Select(target => target.ProviderSubscriptionId).Distinct(StringComparer.Ordinal).Count() != all.Count)
            return new AccountDeletionSubscriptions([], true); // Conflicting purchase ownership.
        if (accountSnapshot.Exists)
        {
            var account = accountSnapshot.ToDictionary();
            var activeId = String(account, "activeSubscriptionProviderObjectId", "");
            if (activeId.Length > 0 && !all.Any(target => target.ProviderSubscriptionId == activeId))
                unresolved = true;
            if (history.Count == 0 && !SubscriptionCheckoutPolicy.CanStart(String(account, "subscriptionStatus", "none")))
                unresolved = true;
            var checkoutId = String(account, "subscriptionCheckoutPurchaseId", "");
            if (checkoutId.Length > 0 && !history.Documents.Any(document => document.Id == checkoutId))
                unresolved = true;
        }
        else if (history.Count > 0)
            unresolved = true; // A missing root cannot safely fence orphan mandates.
        return new AccountDeletionSubscriptions(pending, unresolved);
    }

    private AccountDeletionSubscription? DeletionSubscription(DocumentSnapshot purchase, string userId)
    {
        var data = purchase.ToDictionary();
        var providerId = String(data, "providerObjectId", "");
        var sku = String(data, "productSku", "");
        var environment = String(data, "environment", "");
        if (String(data, "kind", "") != "subscription"
            || String(data, "userId", "") != userId
            || String(data, "provider", "") != "razorpay"
            || String(data, "providerObjectType", "") != "subscription"
            || !providerId.StartsWith("sub_", StringComparison.Ordinal) || providerId.Length > 100
            || providerId.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_')
            || string.IsNullOrWhiteSpace(sku)
            || environment != DeploymentSafety.ProviderEnvironment(_options.DeploymentEnvironment))
            return null;
        return new AccountDeletionSubscription(purchase.Id, providerId, sku, environment);
    }

    public async Task DeleteAccountOperationalDataAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        var accountRef = Account(userId);
        var now = _clock.UtcNow;
        await _db.RunTransactionAsync(async transaction =>
        {
            var snapshot = await transaction.GetSnapshotAsync(accountRef, cancellationToken);
            var subscriptions = await ReadAccountDeletionSubscriptionsAsync(
                transaction, accountRef, snapshot, cancellationToken);
            if (subscriptions.Pending.Count > 0 || subscriptions.HasUnresolvedSubscription)
                throw new InvalidOperationException("Subscription cancellation must be confirmed before deleting the account.");
            if (!snapshot.Exists)
                return 0;
            var account = snapshot.ToDictionary();
            if (Long(account, "activeRequests") > 0)
                throw new InvalidOperationException(
                    "An AI request is still being reconciled. Cancel it or wait for cleanup before deleting the account.");
            transaction.Update(accountRef, new Dictionary<string, object>
            {
                ["email"] = "",
                ["displayName"] = "Deleted user",
                ["status"] = String(account, "status", "active") == "deleted" ? "deleted" : "deleting",
                ["updatedAt"] = Ts(now),
            });
            return 0;
        }, cancellationToken: cancellationToken);

        await DeleteQueryAsync(
            accountRef.Collection("devices").Limit(400),
            cancellationToken);
        await DeleteQueryAsync(
            _db.Collection("desktopSessions").WhereEqualTo("userId", userId).Limit(400),
            cancellationToken);
        await DeleteQueryAsync(
            _db.Collection("desktopLoginAttempts").WhereEqualTo("userId", userId).Limit(400),
            cancellationToken);
        // Reuse the owner-scoped transaction so public content and its rating
        // aggregate disappear exactly once, including a cleanup retry.
        await new FirestoreReviewService(_db, _clock, Options.Create(_options))
            .DeleteOwnAsync(userId, cancellationToken);
        await _db.RunTransactionAsync(async transaction =>
        {
            var snapshot = await transaction.GetSnapshotAsync(accountRef, cancellationToken);
            if (snapshot.Exists)
                transaction.Update(accountRef, new Dictionary<string, object>
                {
                    ["operationalCleanupCompletedAt"] = Ts(_clock.UtcNow),
                    ["updatedAt"] = Ts(_clock.UtcNow),
                });
            return 0;
        }, cancellationToken: cancellationToken);
    }

    public Task CompleteAccountDeletionAsync(string userId, CancellationToken cancellationToken)
    {
        var accountRef = Account(userId);
        return _db.RunTransactionAsync(async transaction =>
        {
            var snapshot = await transaction.GetSnapshotAsync(accountRef, cancellationToken);
            if (!snapshot.Exists) throw new InvalidOperationException("The deletion checkpoint is missing.");
            var data = snapshot.ToDictionary();
            if (!HasConfirmedDeletionIntent(data, userId)
                || !data.TryGetValue("operationalCleanupCompletedAt", out var cleaned) || cleaned is not Timestamp
                || String(data, "status", "") is not ("deleting" or "deleted") || Long(data, "activeRequests") > 0)
                throw new InvalidOperationException("Account deletion cleanup has not been confirmed.");
            var subscriptions = await ReadAccountDeletionSubscriptionsAsync(transaction, accountRef, snapshot, cancellationToken);
            if (subscriptions.Pending.Count > 0 || subscriptions.HasUnresolvedSubscription)
                throw new InvalidOperationException("Subscription cancellation must be confirmed before final deletion.");
            if (String(data, "status", "") == "deleted"
                && data.TryGetValue("deletedAt", out var deleted) && deleted is Timestamp
                && data.TryGetValue("personalDataDeleted", out var complete) && complete is true)
                return 0;
            var now = Ts(_clock.UtcNow);
            transaction.Update(accountRef, new Dictionary<string, object>
            {
                ["status"] = "deleted", ["personalDataDeleted"] = true,
                ["deletedAt"] = now, ["updatedAt"] = now,
            });
            transaction.Set(accountRef.Collection("audit").Document("account_deleted"), new Dictionary<string, object>
            {
                ["type"] = "ACCOUNT_DELETED", ["actor"] = "account-owner",
                ["reason"] = "User-requested account deletion", ["createdAt"] = now,
            });
            return 0;
        }, cancellationToken: cancellationToken);
    }

    private async Task DeleteQueryAsync(Query query, CancellationToken cancellationToken)
    {
        while (true)
        {
            var snapshot = await query.GetSnapshotAsync(cancellationToken);
            if (snapshot.Count == 0)
                return;
            var batch = _db.StartBatch();
            foreach (var document in snapshot.Documents)
                batch.Delete(document.Reference);
            await batch.CommitAsync(cancellationToken);
            if (snapshot.Count < 400)
                return;
        }
    }

    public async Task<string?> FindUserIdByEmailAsync(string email, CancellationToken cancellationToken)
    {
        var results = await _db.Collection("accounts")
            .WhereEqualTo("email", email.Trim().ToLowerInvariant())
            .Limit(1)
            .GetSnapshotAsync(cancellationToken);
        return results.Documents.FirstOrDefault()?.Id;
    }

    public async Task<AccountSnapshot> GrantCreditsAsync(
        string userId,
        long creditMicros,
        string reason,
        string idempotencyKey,
        string actor,
        CancellationToken cancellationToken)
    {
        if (creditMicros <= 0 || creditMicros > 10_000_000_000)
            throw new ArgumentOutOfRangeException(nameof(creditMicros));
        var accountRef = Account(userId);
        var idempotencyRef = _db.Collection("idempotency")
            .Document(Crypto.Sha256($"admin-grant:{userId}:{idempotencyKey}"));
        var now = _clock.UtcNow;
        await _db.RunTransactionAsync(async transaction =>
        {
            var accountSnapshot = await transaction.GetSnapshotAsync(accountRef, cancellationToken);
            var idempotencySnapshot = await transaction.GetSnapshotAsync(idempotencyRef, cancellationToken);
            if (!accountSnapshot.Exists)
                throw new KeyNotFoundException("Recipient account not found.");
            if (idempotencySnapshot.Exists)
                return 0;
            var account = accountSnapshot.ToDictionary();
            if (Long(account, "availableCreditMicros") + creditMicros > _options.MaxAccountCreditMicros)
                throw new InvalidOperationException("The account balance cap would be exceeded.");
            var wallet = AllowanceWallet.FromAccount(account, now);
            var debt = Long(account, "creditDebtMicros");
            wallet.AddCredit(
                "administrative",
                "admin-grant",
                idempotencyRef.Id,
                creditMicros,
                0,
                now,
                ref debt);
            var eventId = $"grant_{Crypto.RandomToken(12)}";
            transaction.Create(accountRef.Collection("ledger").Document(eventId), LedgerMap(new LedgerEntry(
                eventId,
                LedgerTypes.AdminTestCredit,
                creditMicros,
                0,
                "admin-grant",
                idempotencyKey,
                idempotencyRef.Id,
                actor,
                reason,
                now)));
            transaction.Create(idempotencyRef, new Dictionary<string, object>
            {
                ["scope"] = "admin-grant",
                ["userId"] = userId,
                ["actor"] = actor,
                ["resourceId"] = eventId,
                ["createdAt"] = Ts(now),
            });
            transaction.Update(accountRef, new Dictionary<string, object>
            {
                ["availableCreditMicros"] = wallet.AvailableCreditMicros,
                ["reservedCreditMicros"] = wallet.ReservedCreditMicros,
                ["creditDebtMicros"] = debt,
                ["allowanceRulesVersion"] = AllowanceWallet.CurrentRulesVersion,
                ["allowanceBuckets"] = wallet.ToFirestoreMap(),
                ["updatedAt"] = Ts(now),
            });
            return 0;
        }, cancellationToken: cancellationToken);
        return await GetAccountAsync(userId, cancellationToken);
    }

    public async Task<bool> BeginPurchaseAsync(
        string userId,
        string purchaseId,
        CatalogProduct product,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var accountRef = Account(userId);
        var purchaseRef = accountRef.Collection("purchases").Document(SafeId(purchaseId));
        var idempotencyRef = _db.Collection("idempotency")
            .Document(Crypto.Sha256($"checkout:{userId}:{idempotencyKey}"));
        var now = _clock.UtcNow;
        return await _db.RunTransactionAsync(async transaction =>
        {
            var account = await transaction.GetSnapshotAsync(accountRef, cancellationToken);
            RequireActiveAccount(account);
            var purchase = await transaction.GetSnapshotAsync(purchaseRef, cancellationToken);
            var idempotency = await transaction.GetSnapshotAsync(idempotencyRef, cancellationToken);
            if (purchase.Exists || idempotency.Exists)
                return false;
            var subscription = string.Equals(product.Kind, "subscription", StringComparison.Ordinal);
            if (subscription)
            {
                var data = account.ToDictionary();
                if (!SubscriptionCheckoutPolicy.CanStart(String(data, "subscriptionStatus", "none"))
                    || !string.IsNullOrWhiteSpace(String(data, "subscriptionCheckoutPurchaseId", "")))
                    return false;

                // Old revisions had no account lock. Do not mistake a local
                // cancelled/expired/failed status for remote non-renewal proof.
                // Read a bounded history; overflow requires explicit review.
                const int historyLimit = 64;
                var history = await transaction.GetSnapshotAsync(accountRef.Collection("purchases")
                    .WhereEqualTo("kind", "subscription").Limit(historyLimit + 1), cancellationToken);
                if (history.Count > historyLimit)
                    throw new InvalidOperationException("Subscription history needs review before another checkout.");
                var unresolved = history.Documents.OrderBy(document => document.Id, StringComparer.Ordinal)
                    .FirstOrDefault(document => !HasVerifiedSubscriptionTerminalProof(document.ToDictionary()));
                if (unresolved is not null)
                {
                    transaction.Update(accountRef, new Dictionary<string, object>
                    {
                        ["subscriptionCheckoutPurchaseId"] = unresolved.Id,
                        ["updatedAt"] = Ts(now),
                    });
                    return false; // Commit legacy adoption rather than roll it back by throwing.
                }
            }
            transaction.Create(purchaseRef, new Dictionary<string, object>
            {
                ["purchaseId"] = purchaseId,
                ["userId"] = userId,
                ["productSku"] = product.Sku,
                ["kind"] = product.Kind,
                ["provider"] = "razorpay",
                ["environment"] = DeploymentSafety.ProviderEnvironment(
                    _options.DeploymentEnvironment),
                ["providerObjectId"] = "",
                ["providerObjectType"] = "",
                ["status"] = "creating",
                ["amountMinor"] = product.AmountMinor,
                ["currency"] = product.Currency,
                ["creditMicros"] = product.CreditMicros,
                ["credited"] = false,
                ["createdAt"] = Ts(now),
                ["updatedAt"] = Ts(now),
            });
            transaction.Create(idempotencyRef, new Dictionary<string, object>
            {
                ["scope"] = "checkout",
                ["userId"] = userId,
                ["resourceId"] = purchaseId,
                ["createdAt"] = Ts(now),
            });
            if (subscription)
                transaction.Update(accountRef, new Dictionary<string, object>
                {
                    ["subscriptionCheckoutPurchaseId"] = purchaseId,
                    ["updatedAt"] = Ts(now),
                });
            return true;
        }, cancellationToken: cancellationToken);
    }

    public Task AttachPurchaseProviderObjectAsync(
        string userId,
        string purchaseId,
        string providerObjectId,
        string providerObjectType,
        CancellationToken cancellationToken)
    {
        var purchaseRef = Account(userId).Collection("purchases").Document(SafeId(purchaseId));
        return _db.RunTransactionAsync(async transaction =>
        {
            var purchase = await transaction.GetSnapshotAsync(purchaseRef, cancellationToken);
            if (!purchase.Exists)
                throw new KeyNotFoundException("The checkout purchase does not exist for this account.");
            var data = purchase.ToDictionary();
            var expectedType = String(data, "kind", "") == "subscription" ? "subscription" : "order";
            if (string.IsNullOrWhiteSpace(providerObjectId) || providerObjectType != expectedType)
                throw new InvalidOperationException("The checkout provider identity is invalid.");
            var attached = String(data, "providerObjectId", "");
            if (attached.Length > 0)
            {
                if (attached != providerObjectId || String(data, "providerObjectType", "") != providerObjectType)
                    throw new InvalidOperationException("The checkout is already bound to another provider object.");
                return 0; // Same identity retry must not downgrade webhook/proof progress.
            }
            var updates = new Dictionary<string, object>
            {
                ["providerObjectId"] = providerObjectId,
                ["providerObjectType"] = providerObjectType,
                ["updatedAt"] = Ts(_clock.UtcNow),
            };
            if (String(data, "status", "creating") == "creating")
                updates["status"] = "pending";
            // Deliberately allow pre-fence provider creation to finish attaching.
            transaction.Update(purchaseRef, updates);
            return 0;
        }, cancellationToken: cancellationToken);
    }

    public Task MarkPurchaseFailedAsync(
        string userId,
        string purchaseId,
        string outcome,
        CancellationToken cancellationToken) =>
        Account(userId).Collection("purchases").Document(SafeId(purchaseId)).UpdateAsync(
            new Dictionary<string, object>
            {
                ["status"] = "provider_failed",
                ["failureCategory"] = outcome.Length > 80 ? outcome[..80] : outcome,
                ["updatedAt"] = Ts(_clock.UtcNow),
            },
            cancellationToken: cancellationToken);

    public Task CancelPendingPurchaseAsync(
        string userId,
        string purchaseId,
        CancellationToken cancellationToken)
    {
        var purchaseRef = Account(userId).Collection("purchases").Document(SafeId(purchaseId));
        return _db.RunTransactionAsync(async transaction =>
        {
            var purchase = await transaction.GetSnapshotAsync(purchaseRef, cancellationToken);
            if (!purchase.Exists)
                throw new KeyNotFoundException("The checkout purchase does not exist for this account.");
            var data = purchase.ToDictionary();
            var currentStatus = String(data, "status", "pending");
            var nextStatus = StatusAfterCheckoutCancellation(
                currentStatus,
                Bool(data, "credited", false));
            if (string.Equals(currentStatus, nextStatus, StringComparison.Ordinal))
                return;
            transaction.Update(purchaseRef, new Dictionary<string, object>
            {
                ["status"] = nextStatus,
                ["updatedAt"] = Ts(_clock.UtcNow),
            });
        }, cancellationToken: cancellationToken);
    }

    internal static string StatusAfterCheckoutCancellation(string? currentStatus, bool credited)
    {
        if (credited)
            return string.IsNullOrWhiteSpace(currentStatus) ? "paid" : currentStatus;
        return currentStatus?.Trim().ToLowerInvariant() switch
        {
            null or "" or "creating" or "pending" or "verification_pending" => "cancelled",
            _ => currentStatus!,
        };
    }

    internal static string StatusAfterAbandonment(
        string? currentStatus,
        bool credited,
        DateTimeOffset createdAt,
        DateTimeOffset now)
    {
        if (credited || createdAt == DateTimeOffset.UnixEpoch || now - createdAt < TimeSpan.FromHours(24))
            return string.IsNullOrWhiteSpace(currentStatus) ? "pending" : currentStatus;
        return currentStatus?.Trim().ToLowerInvariant() switch
        {
            null or "" or "creating" or "pending" or "verification_pending" or "authorized" => "expired",
            _ => currentStatus!,
        };
    }

    public Task MarkCheckoutSignatureVerifiedAsync(
        string userId,
        string purchaseId,
        string providerObjectId,
        string providerPaymentId,
        CancellationToken cancellationToken)
    {
        var purchaseRef = Account(userId).Collection("purchases").Document(SafeId(purchaseId));
        return _db.RunTransactionAsync(async transaction =>
        {
            var purchase = await transaction.GetSnapshotAsync(purchaseRef, cancellationToken);
            if (!purchase.Exists)
                throw new KeyNotFoundException("The checkout purchase does not exist for this account.");
            var purchaseData = purchase.ToDictionary();
            if (!string.Equals(
                    String(purchaseData, "providerObjectId", ""),
                    providerObjectId,
                    StringComparison.Ordinal))
                throw new UnauthorizedAccessException("The checkout does not match this purchase.");
            var currentStatus = String(purchaseData, "status", "pending");
            transaction.Update(purchaseRef, new Dictionary<string, object>
            {
                ["checkoutSignatureVerified"] = true,
                ["providerPaymentId"] = providerPaymentId,
                // Razorpay can deliver an authoritative webhook before Checkout.js
                // posts its signature confirmation. Never move an already advanced
                // purchase back to a waiting state in that race.
                ["status"] = StatusAfterCheckoutSignature(currentStatus),
                ["updatedAt"] = Ts(_clock.UtcNow),
            });
        }, cancellationToken: cancellationToken);
    }

    internal static string StatusAfterCheckoutSignature(string? currentStatus) =>
        currentStatus?.Trim().ToLowerInvariant() switch
        {
            null or "" or "creating" or "pending" or "verification_pending" => "verification_pending",
            _ => currentStatus!,
        };

    public async Task<PaymentWebhookResult> ApplyPaymentWebhookAsync(
        string eventId,
        string eventType,
        string providerObjectId,
        string? providerPaymentId,
        string? providerOrderId,
        string? providerSubscriptionId,
        string? purchaseId,
        string? providerEventObjectId,
        long? providerAmountMinor,
        string status,
        CancellationToken cancellationToken,
        DateTimeOffset? subscriptionCycleEndsAt = null,
        string? providerCurrency = null,
        string? providerProductSku = null)
    {
        var eventRef = _db.Collection("paymentEvents").Document(Crypto.Sha256($"razorpay:{eventId}"));
        var existingEventSnapshot = await eventRef.GetSnapshotAsync(cancellationToken);
        if (existingEventSnapshot.Exists
            && !string.Equals(
                String(existingEventSnapshot.ToDictionary(), "status", ""),
                "unmatched",
                StringComparison.Ordinal))
            return new PaymentWebhookResult(true, eventId, "duplicate");

        var candidateIds = new[] { providerObjectId, providerOrderId, providerSubscriptionId }
            .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToArray();
        DocumentSnapshot? purchaseSnapshot = null;
        if (!string.IsNullOrWhiteSpace(purchaseId))
        {
            var query = await _db.CollectionGroup("purchases")
                .WhereEqualTo("purchaseId", purchaseId)
                .Limit(1)
                .GetSnapshotAsync(cancellationToken);
            purchaseSnapshot = query.Documents.FirstOrDefault();
        }
        foreach (var candidate in candidateIds)
        {
            if (purchaseSnapshot is not null)
                break;
            var query = await _db.CollectionGroup("purchases")
                .WhereEqualTo("providerObjectId", candidate)
                .Limit(1)
                .GetSnapshotAsync(cancellationToken);
            purchaseSnapshot = query.Documents.FirstOrDefault();
            if (purchaseSnapshot is not null)
                break;
        }
        if (purchaseSnapshot is null && !string.IsNullOrWhiteSpace(providerPaymentId))
        {
            var query = await _db.CollectionGroup("purchases")
                .WhereEqualTo("providerPaymentId", providerPaymentId)
                .Limit(1)
                .GetSnapshotAsync(cancellationToken);
            purchaseSnapshot = query.Documents.FirstOrDefault();
        }

        if (purchaseSnapshot is null)
        {
            await eventRef.SetAsync(new Dictionary<string, object>
            {
                ["provider"] = "razorpay",
                ["eventId"] = eventId,
                ["eventType"] = eventType,
                ["status"] = "unmatched",
                ["providerObjectId"] = providerObjectId,
                ["createdAt"] = Ts(_clock.UtcNow),
                ["providerEventObjectId"] = providerEventObjectId ?? "",
                ["providerAmountMinor"] = providerAmountMinor ?? 0,
            }, SetOptions.MergeAll, cancellationToken);
            return new PaymentWebhookResult(false, eventId, "unmatched");
        }

        var accountRef = purchaseSnapshot.Reference.Parent.Parent
            ?? throw new InvalidOperationException("The payment account reference is invalid.");
        var userId = accountRef.Id;
        var purchaseRef = purchaseSnapshot.Reference;
        var mismatchIssueRef = _db.Collection("reconciliationIssues").Document(
            Crypto.Sha256($"payment-webhook:{purchaseRef.Path}:{eventId}"));
        var recurringFulfillmentId = RecurringFulfillmentId(
            eventType,
            providerPaymentId,
            providerEventObjectId);
        var recurringFulfillmentRef = recurringFulfillmentId is null
            ? null
            : _db.Collection("paymentFulfillments").Document(recurringFulfillmentId);
        var now = _clock.UtcNow;
        var outcome = await _db.RunTransactionAsync(async transaction =>
        {
            var existingEvent = await transaction.GetSnapshotAsync(eventRef, cancellationToken);
            if (existingEvent.Exists
                && !string.Equals(
                    String(existingEvent.ToDictionary(), "status", ""),
                    "unmatched",
                    StringComparison.Ordinal))
                return "duplicate";
            var purchase = await transaction.GetSnapshotAsync(purchaseRef, cancellationToken);
            var account = await transaction.GetSnapshotAsync(accountRef, cancellationToken);
            var recurringFulfillment = recurringFulfillmentRef is null
                ? null
                : await transaction.GetSnapshotAsync(recurringFulfillmentRef, cancellationToken);
            if (!purchase.Exists || !account.Exists)
                throw new InvalidOperationException("The payment account is unavailable.");
            var purchaseData = purchase.ToDictionary();
            var accountData = account.ToDictionary();
            var purchaseKind = String(purchaseData, "kind", "credits");
            var recurringCharge = string.Equals(eventType, "subscription.charged", StringComparison.Ordinal);
            var subscriptionLifecycleEvent = eventType.StartsWith("subscription.", StringComparison.Ordinal)
                && !recurringCharge;
            var recurringAlreadyFulfilled = recurringFulfillment?.Exists == true
                || IsPreviouslyFulfilledRecurringCharge(
                    eventType,
                    providerPaymentId,
                    String(purchaseData, "providerPaymentId", ""),
                    Bool(purchaseData, "credited", false));
            var purchaseCredited = Bool(purchaseData, "credited", false);
            var creditAlreadyFulfilled = recurringCharge
                ? recurringAlreadyFulfilled
                : purchaseCredited;
            var evidenceRejection = PaymentEvidenceRejection(
                eventType,
                purchaseKind,
                userId,
                purchaseData,
                providerObjectId,
                providerOrderId,
                providerSubscriptionId,
                providerPaymentId,
                providerAmountMinor,
                providerCurrency,
                providerProductSku,
                DeploymentSafety.ProviderEnvironment(_options.DeploymentEnvironment),
                subscriptionCycleEndsAt,
                Long(accountData, "availableCreditMicros"),
                _options.MaxAccountCreditMicros,
                creditAlreadyFulfilled);
            if (evidenceRejection is not null)
            {
                transaction.Update(purchaseRef, new Dictionary<string, object>
                {
                    ["reconciliationStatus"] = "payment_review",
                    ["lastPaymentRejection"] = evidenceRejection,
                    ["updatedAt"] = Ts(now),
                });
                transaction.Set(eventRef, new Dictionary<string, object>
                {
                    ["provider"] = "razorpay",
                    ["eventId"] = eventId,
                    ["eventType"] = eventType,
                    ["status"] = "rejected",
                    ["outcome"] = evidenceRejection,
                    ["userId"] = userId,
                    ["purchaseId"] = purchase.Id,
                    ["providerObjectId"] = providerObjectId,
                    ["providerEventObjectId"] = providerEventObjectId ?? "",
                    ["providerAmountMinor"] = providerAmountMinor ?? 0,
                    ["createdAt"] = Ts(now),
                });
                transaction.Set(mismatchIssueRef, new Dictionary<string, object>
                {
                    ["kind"] = "payment",
                    ["category"] = evidenceRejection,
                    ["resourcePathHash"] = Crypto.Sha256(purchaseRef.Path),
                    ["purchaseIdHash"] = Crypto.Sha256(purchase.Id),
                    ["providerEventIdHash"] = Crypto.Sha256(eventId),
                    ["status"] = "open",
                    ["firstObservedAt"] = Ts(now),
                    ["lastObservedAt"] = Ts(now),
                }, SetOptions.MergeAll);
                return evidenceRejection;
            }
            var shouldCredit = IsCreditEvent(eventType, purchaseKind)
                && (recurringCharge
                    ? !recurringAlreadyFulfilled
                    : !purchaseCredited);
            var incomingSubscriptionId = string.Equals(purchaseKind, "subscription", StringComparison.Ordinal)
                ? providerSubscriptionId ?? providerObjectId
                : "";
            var existingSubscriptionId = String(accountData, "activeSubscriptionProviderObjectId", "");
            var isNewSubscriptionIdentity = !string.IsNullOrWhiteSpace(incomingSubscriptionId)
                && !string.Equals(existingSubscriptionId, incomingSubscriptionId, StringComparison.Ordinal);
            var shouldApplySubscriptionState = string.Equals(purchaseKind, "subscription", StringComparison.Ordinal)
                // Verified remote closure is monotonic. A delayed activation
                // callback must not resurrect the old account lifecycle. Real
                // late payment/refund evidence is still accounted for below.
                && (!HasVerifiedSubscriptionTerminalProof(purchaseData)
                    || SubscriptionCheckoutPolicy.IsVerifiedTerminalEvent(eventType, status))
                && ShouldApplySubscriptionAccountState(
                    existingSubscriptionId,
                    incomingSubscriptionId,
                    status,
                    purchaseCredited || shouldCredit,
                    Date(accountData, "cycleEndsAt"),
                    subscriptionCycleEndsAt);
            var recurringReversalHasEvidence = purchaseKind != "subscription"
                || recurringFulfillment?.Exists == true
                || IsPreviouslyFulfilledRecurringCharge(
                    "subscription.charged",
                    providerPaymentId,
                    String(purchaseData, "providerPaymentId", ""),
                    purchaseCredited);
            var shouldReverse = IsReversalEvent(eventType)
                && purchaseCredited
                && recurringReversalHasEvidence;
            var creditMicros = Long(purchaseData, "creditMicros");
            var reversalBasisCreditMicros = purchaseKind == "subscription"
                && recurringFulfillment?.Exists == true
                    ? Long(recurringFulfillment.ToDictionary(), "creditMicros", creditMicros)
                    : creditMicros;
            var previouslyReversedCreditMicros = purchaseKind == "subscription"
                && recurringFulfillment?.Exists == true
                    ? Long(recurringFulfillment.ToDictionary(), "reversedCreditMicros")
                    : Long(purchaseData, "reversedCreditMicros");
            var purchaseAmountMinor = Long(purchaseData, "amountMinor");
            var ledgerType = eventType.Contains("refund", StringComparison.OrdinalIgnoreCase)
                ? LedgerTypes.Refund
                : eventType.Contains("dispute", StringComparison.OrdinalIgnoreCase)
                    ? LedgerTypes.Chargeback
                    : purchaseKind == "subscription"
                        ? LedgerTypes.MonthlyAllowance
                        : LedgerTypes.PurchaseCredit;
            var requestedReversalCreditMicros = shouldReverse
                ? ReversalCreditMicros(eventType, providerAmountMinor, purchaseAmountMinor, creditMicros)
                : 0;
            var wallet = AllowanceWallet.FromAccount(accountData, now);
            var availableBeforePayment = wallet.AvailableCreditMicros;
            var creditDebtMicros = Long(accountData, "creditDebtMicros");
            var allowanceSourceId = !string.IsNullOrWhiteSpace(providerPaymentId)
                ? providerPaymentId!
                : providerEventObjectId ?? purchase.Id;
            string? allowanceBucketId = null;
            if (shouldCredit)
            {
                allowanceBucketId = wallet.AddCredit(
                    purchaseKind == "subscription" ? "subscription" : "topup",
                    "razorpay-payment",
                    allowanceSourceId,
                    creditMicros,
                    purchaseKind == "subscription"
                        ? subscriptionCycleEndsAt?.ToUnixTimeSeconds() ?? 0
                        : 0,
                    now,
                    ref creditDebtMicros);
            }
            var reversalCreditMicros = shouldReverse && !string.IsNullOrWhiteSpace(providerPaymentId)
                ? wallet.Reverse(
                    "razorpay-payment",
                    providerPaymentId!,
                    Math.Min(
                        requestedReversalCreditMicros,
                        Math.Max(0, reversalBasisCreditMicros - previouslyReversedCreditMicros)),
                    ref creditDebtMicros)
                : 0;
            if (shouldReverse && requestedReversalCreditMicros > 0 && reversalCreditMicros == 0)
            {
                const string category = "allowance_source_missing";
                transaction.Update(purchaseRef, new Dictionary<string, object>
                {
                    ["reconciliationStatus"] = "payment_review",
                    ["lastPaymentRejection"] = category,
                    ["updatedAt"] = Ts(now),
                });
                transaction.Set(eventRef, new Dictionary<string, object>
                {
                    ["provider"] = "razorpay",
                    ["eventId"] = eventId,
                    ["eventType"] = eventType,
                    ["status"] = "rejected",
                    ["outcome"] = category,
                    ["userId"] = userId,
                    ["purchaseId"] = purchase.Id,
                    ["providerObjectId"] = providerObjectId,
                    ["providerEventObjectId"] = providerEventObjectId ?? "",
                    ["providerAmountMinor"] = providerAmountMinor ?? 0,
                    ["createdAt"] = Ts(now),
                });
                transaction.Set(mismatchIssueRef, new Dictionary<string, object>
                {
                    ["kind"] = "payment",
                    ["category"] = category,
                    ["resourcePathHash"] = Crypto.Sha256(purchaseRef.Path),
                    ["purchaseIdHash"] = Crypto.Sha256(purchase.Id),
                    ["providerEventIdHash"] = Crypto.Sha256(eventId),
                    ["status"] = "open",
                    ["firstObservedAt"] = Ts(now),
                    ["lastObservedAt"] = Ts(now),
                }, SetOptions.MergeAll);
                return category;
            }
            var delta = wallet.AvailableCreditMicros - availableBeforePayment;
            if (shouldCredit || reversalCreditMicros > 0)
            {
                var ledgerId = $"payment_{Crypto.Sha256(eventId)[..24]}";
                transaction.Create(accountRef.Collection("ledger").Document(ledgerId), LedgerMap(new LedgerEntry(
                    ledgerId,
                    ledgerType,
                    delta,
                    0,
                    "razorpay-event",
                    eventId,
                    eventRef.Id,
                    "razorpay",
                    eventType,
                    now,
                    new Dictionary<string, object?>
                    {
                        ["purchaseId"] = purchase.Id,
                        ["providerEventObjectId"] = providerEventObjectId,
                        ["providerAmountMinor"] = providerAmountMinor,
                        ["grossCreditMicros"] = shouldCredit ? creditMicros : 0,
                        ["creditDebtMicrosAfter"] = creditDebtMicros,
                        ["allowanceBucketId"] = allowanceBucketId ?? "",
                    })));
            }
            if (shouldCredit && recurringFulfillmentRef is not null)
                transaction.Create(recurringFulfillmentRef, new Dictionary<string, object>
                {
                    ["provider"] = "razorpay",
                    ["eventType"] = eventType,
                    ["eventId"] = eventId,
                    ["userId"] = userId,
                    ["purchaseId"] = purchase.Id,
                    ["providerPaymentId"] = providerPaymentId ?? "",
                    ["creditMicros"] = creditMicros,
                    ["reversedCreditMicros"] = 0L,
                    ["allowanceBucketId"] = allowanceBucketId ?? "",
                    ["cycleEndsAtUnix"] = subscriptionCycleEndsAt?.ToUnixTimeSeconds() ?? 0,
                    ["createdAt"] = Ts(now),
                });

            if (reversalCreditMicros > 0 && recurringFulfillmentRef is not null
                && recurringFulfillment?.Exists == true)
            {
                transaction.Update(recurringFulfillmentRef, new Dictionary<string, object>
                {
                    ["reversedCreditMicros"] = previouslyReversedCreditMicros + reversalCreditMicros,
                    ["updatedAt"] = Ts(now),
                });
            }

            if (shouldCredit || reversalCreditMicros > 0
                || (purchaseKind == "subscription" && subscriptionLifecycleEvent)
                || (shouldApplySubscriptionState && recurringCharge && recurringAlreadyFulfilled))
            {
                var accountUpdates = new Dictionary<string, object>
                {
                    ["updatedAt"] = Ts(now),
                };
                if (shouldCredit || reversalCreditMicros > 0)
                {
                    accountUpdates["availableCreditMicros"] = wallet.AvailableCreditMicros;
                    accountUpdates["reservedCreditMicros"] = wallet.ReservedCreditMicros;
                    accountUpdates["creditDebtMicros"] = creditDebtMicros;
                    accountUpdates["allowanceRulesVersion"] = AllowanceWallet.CurrentRulesVersion;
                    accountUpdates["allowanceBuckets"] = wallet.ToFirestoreMap();
                }
                if (recurringCharge && shouldCredit)
                {
                    accountUpdates["cycleUsageCreditMicros"] = 0L;
                    if (subscriptionCycleEndsAt is DateTimeOffset cycleEnd)
                        accountUpdates["cycleEndsAt"] = Ts(cycleEnd);
                }
                if (shouldApplySubscriptionState
                    && (subscriptionLifecycleEvent || recurringCharge))
                {
                    var subscriptionFulfilled = purchaseCredited || shouldCredit;
                    var currentPlan = String(accountData, "planId", "none");
                    var accountCycleEnd = Date(accountData, "cycleEndsAt");
                    var retainedCycleEnd = subscriptionCycleEndsAt
                        ?? (accountCycleEnd == DateTimeOffset.UnixEpoch ? null : accountCycleEnd);
                    var retainCancelledCycle = ShouldRetainCancelledSubscriptionCycle(
                        status,
                        subscriptionFulfilled,
                        retainedCycleEnd,
                        now);
                    accountUpdates["planId"] = retainCancelledCycle
                        ? currentPlan
                        : PlanAfterSubscriptionStatus(
                            currentPlan,
                            String(purchaseData, "productSku", "none"),
                            status,
                            subscriptionFulfilled);
                    accountUpdates["subscriptionStatus"] = retainCancelledCycle ? "active" : status;
                    if (string.Equals(status, "active", StringComparison.OrdinalIgnoreCase)
                        && subscriptionFulfilled)
                    {
                        accountUpdates["activeSubscriptionProviderObjectId"] = incomingSubscriptionId;
                        accountUpdates["activeSubscriptionPurchaseId"] = purchase.Id;
                        if (isNewSubscriptionIdentity)
                        {
                            accountUpdates["subscriptionCancellationStatus"] = "none";
                            accountUpdates["subscriptionCancelsAt"] = FieldValue.Delete;
                            accountUpdates["subscriptionCancellationRequestId"] = FieldValue.Delete;
                            accountUpdates["subscriptionCancellationRequestedAt"] = FieldValue.Delete;
                        }
                        if (subscriptionCycleEndsAt is DateTimeOffset activeCycleEnd)
                            accountUpdates["cycleEndsAt"] = Ts(activeCycleEnd);
                    }
                    else if (retainCancelledCycle && retainedCycleEnd is DateTimeOffset retainedEnd)
                    {
                        accountUpdates["subscriptionCancellationStatus"] = "scheduled";
                        accountUpdates["subscriptionCancellationProviderStatus"] = status;
                        accountUpdates["subscriptionCancelsAt"] = Ts(retainedEnd);
                        accountUpdates["cycleEndsAt"] = Ts(retainedEnd);
                    }
                    else if (IsTerminalSubscriptionStatus(status))
                    {
                        accountUpdates["activeSubscriptionProviderObjectId"] = FieldValue.Delete;
                        accountUpdates["activeSubscriptionPurchaseId"] = FieldValue.Delete;
                        accountUpdates["cycleEndsAt"] = FieldValue.Delete;
                        accountUpdates["subscriptionCancellationStatus"] =
                            string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase)
                                ? "cancelled"
                                : status;
                        if (subscriptionCycleEndsAt is DateTimeOffset terminalDate)
                            accountUpdates["subscriptionCancelsAt"] = Ts(terminalDate);
                    }
                }
                if (purchaseKind == "subscription"
                    && SubscriptionCheckoutPolicy.IsVerifiedTerminalEvent(eventType, status)
                    && string.Equals(String(purchaseData, "providerObjectId", ""), incomingSubscriptionId, StringComparison.Ordinal)
                    && string.Equals(String(accountData, "subscriptionCheckoutPurchaseId", ""), purchase.Id, StringComparison.Ordinal))
                    accountUpdates["subscriptionCheckoutPurchaseId"] = FieldValue.Delete;
                transaction.Update(accountRef, accountUpdates);
            }
            var purchaseStatus = StatusAfterWebhook(
                String(purchaseData, "status", "pending"),
                status,
                purchaseKind,
                purchaseCredited,
                eventType);
            var purchaseUpdates = new Dictionary<string, object>
            {
                ["status"] = purchaseStatus,
                ["providerPaymentId"] = providerPaymentId ?? String(purchaseData, "providerPaymentId", ""),
                ["credited"] = Bool(purchaseData, "credited", false) || shouldCredit,
                ["reversedCreditMicros"] = Long(purchaseData, "reversedCreditMicros") + reversalCreditMicros,
                ["reversed"] = Bool(purchaseData, "reversed", false)
                    || (reversalCreditMicros > 0
                        && Long(purchaseData, "reversedCreditMicros") + reversalCreditMicros >= creditMicros),
                ["updatedAt"] = Ts(now),
            };
            if (purchaseKind == "subscription" && subscriptionCycleEndsAt is DateTimeOffset purchaseCycleEnd)
                purchaseUpdates["cycleEndsAt"] = Ts(purchaseCycleEnd);
            if (purchaseKind == "subscription"
                && SubscriptionCheckoutPolicy.IsVerifiedTerminalEvent(eventType, status)
                && string.Equals(String(purchaseData, "providerObjectId", ""), incomingSubscriptionId, StringComparison.Ordinal))
            {
                purchaseUpdates["subscriptionTerminalProviderStatus"] = status;
                purchaseUpdates["subscriptionTerminalProviderObjectId"] = incomingSubscriptionId;
                purchaseUpdates["subscriptionTerminalVerifiedAt"] = Ts(now);
            }
            transaction.Update(purchaseRef, purchaseUpdates);
            transaction.Set(eventRef, new Dictionary<string, object>
            {
                ["provider"] = "razorpay",
                ["eventId"] = eventId,
                ["eventType"] = eventType,
                ["status"] = status,
                ["userId"] = userId,
                ["purchaseId"] = purchase.Id,
                ["providerObjectId"] = providerObjectId,
                ["providerEventObjectId"] = providerEventObjectId ?? "",
                ["providerAmountMinor"] = providerAmountMinor ?? 0,
                ["providerCurrency"] = providerCurrency?.Trim().ToUpperInvariant() ?? "",
                ["providerProductSku"] = providerProductSku?.Trim() ?? "",
                ["subscriptionCycleEndsAtUnix"] = subscriptionCycleEndsAt?.ToUnixTimeSeconds() ?? 0,
                ["createdAt"] = Ts(now),
            });
            return delta switch
            {
                > 0 => "credited",
                < 0 => "reversed",
                _ when recurringAlreadyFulfilled => "duplicate",
                _ => "recorded",
            };
        }, cancellationToken: cancellationToken);
        return new PaymentWebhookResult(outcome == "duplicate", eventId, outcome);
    }

    private static bool HasVerifiedSubscriptionTerminalProof(IDictionary<string, object> data) =>
        String(data, "subscriptionTerminalProviderStatus", "") is "cancelled" or "completed" or "expired"
        && !string.IsNullOrWhiteSpace(String(data, "providerObjectId", ""))
        && string.Equals(String(data, "subscriptionTerminalProviderObjectId", ""),
            String(data, "providerObjectId", ""), StringComparison.Ordinal)
        && Date(data, "subscriptionTerminalVerifiedAt") != DateTimeOffset.UnixEpoch;

    internal static string? PaymentEvidenceRejection(
        string eventType,
        string purchaseKind,
        string accountUserId,
        IDictionary<string, object> purchase,
        string providerObjectId,
        string? providerOrderId,
        string? providerSubscriptionId,
        string? providerPaymentId,
        long? providerAmountMinor,
        string? providerCurrency,
        string? providerProductSku,
        string? expectedEnvironment = null,
        DateTimeOffset? subscriptionCycleEndsAt = null,
        long? currentAvailableCreditMicros = null,
        long? maxAccountCreditMicros = null,
        bool creditAlreadyFulfilled = false)
    {
        var recordedUserId = String(purchase, "userId", accountUserId);
        if (!string.Equals(recordedUserId, accountUserId, StringComparison.Ordinal))
            return "purchase_owner_mismatch";

        var recordedEnvironment = String(purchase, "environment", "");
        if (!string.IsNullOrWhiteSpace(expectedEnvironment))
        {
            if (string.IsNullOrWhiteSpace(recordedEnvironment)
                && string.Equals(expectedEnvironment, "production", StringComparison.Ordinal))
                return "purchase_environment_missing";
            if (!string.IsNullOrWhiteSpace(recordedEnvironment)
                && !string.Equals(recordedEnvironment, expectedEnvironment, StringComparison.Ordinal))
                return "purchase_environment_mismatch";
        }

        var attachedObjectId = String(purchase, "providerObjectId", "");
        var attachedPaymentId = String(purchase, "providerPaymentId", "");
        var objectMatches = !string.IsNullOrWhiteSpace(attachedObjectId)
            && new[] { providerObjectId, providerOrderId, providerSubscriptionId }
                .Any(candidate => string.Equals(candidate, attachedObjectId, StringComparison.Ordinal));
        var paymentMatches = !string.IsNullOrWhiteSpace(attachedPaymentId)
            && !string.IsNullOrWhiteSpace(providerPaymentId)
            && string.Equals(providerPaymentId, attachedPaymentId, StringComparison.Ordinal);
        if (!objectMatches && !paymentMatches)
            return "provider_identity_mismatch";

        var creditEvent = IsCreditEvent(eventType, purchaseKind);
        if (creditEvent)
        {
            if (!creditAlreadyFulfilled
                && currentAvailableCreditMicros is long available
                && maxAccountCreditMicros is long maximum
                && maximum > 0
                && available + Long(purchase, "creditMicros") > maximum)
                return "account_balance_cap_exceeded";
            if (string.Equals(eventType, "subscription.charged", StringComparison.Ordinal)
                && (subscriptionCycleEndsAt is null
                    || subscriptionCycleEndsAt <= DateTimeOffset.UnixEpoch))
                return "subscription_cycle_end_missing";
            if (providerAmountMinor is null || providerAmountMinor <= 0)
                return "payment_amount_missing";
            if (providerAmountMinor.Value != Long(purchase, "amountMinor"))
                return "payment_amount_mismatch";
            if (string.IsNullOrWhiteSpace(providerCurrency))
                return "payment_currency_missing";
            if (!string.Equals(
                    providerCurrency.Trim(),
                    String(purchase, "currency", "INR"),
                    StringComparison.OrdinalIgnoreCase))
                return "payment_currency_mismatch";
            if (string.IsNullOrWhiteSpace(providerProductSku))
                return "payment_sku_missing";
            if (!string.Equals(
                    providerProductSku.Trim(),
                    String(purchase, "productSku", ""),
                    StringComparison.Ordinal))
                return "payment_sku_mismatch";
        }
        else if (IsReversalEvent(eventType))
        {
            if (providerAmountMinor is null || providerAmountMinor <= 0)
                return "reversal_amount_missing";
            if (providerAmountMinor.Value > Long(purchase, "amountMinor"))
                return "reversal_amount_exceeds_purchase";
            if (!string.IsNullOrWhiteSpace(providerCurrency)
                && !string.Equals(
                    providerCurrency.Trim(),
                    String(purchase, "currency", "INR"),
                    StringComparison.OrdinalIgnoreCase))
                return "reversal_currency_mismatch";
            if (!string.IsNullOrWhiteSpace(providerProductSku)
                && !string.Equals(
                    providerProductSku.Trim(),
                    String(purchase, "productSku", ""),
                    StringComparison.Ordinal))
                return "reversal_sku_mismatch";
        }

        return null;
    }

    private async Task<long> EnsureGlobalUsageShardsAsync(
        string date,
        long globalDailyCap,
        int shardCount,
        CancellationToken cancellationToken)
    {
        var markerRef = _db.Collection("globalUsageMigrations").Document(date);
        var legacyRef = _db.Collection("globalUsage").Document(date);
        var shardedCap = await _db.RunTransactionAsync(async transaction =>
        {
            var marker = await transaction.GetSnapshotAsync(markerRef, cancellationToken);
            if (marker.Exists)
            {
                var markerData = marker.ToDictionary();
                if (Long(markerData, "globalDailyCapCreditMicros") != globalDailyCap
                    || Long(markerData, "shardCount") != shardCount)
                    throw new InvalidOperationException("The daily quota shard configuration changed during an active day.");
                // During a rolling deployment an older revision may briefly
                // continue writing the legacy counter. Re-read it and reduce
                // new-shard capacity by any post-cutover increase rather than
                // allowing the aggregate daily commitment to exceed the cap.
                var currentLegacy = await transaction.GetSnapshotAsync(legacyRef, cancellationToken);
                var currentLegacyData = currentLegacy.Exists ? currentLegacy.ToDictionary() : [];
                return EffectiveShardedDailyCap(
                    markerData,
                    currentLegacyData,
                    globalDailyCap,
                    shardCount);
            }

            var legacy = await transaction.GetSnapshotAsync(legacyRef, cancellationToken);
            var legacyData = legacy.Exists ? legacy.ToDictionary() : [];
            var legacyReserved = Long(legacyData, "reservedCreditMicros");
            var legacySettled = Long(legacyData, "settledCreditMicros");
            var legacyCommitted = checked(Math.Max(0, legacyReserved) + Math.Max(0, legacySettled)
                + CompanyLossExposure(legacyData));
            if (_options.DailySpendingCapsEnabled && legacyCommitted > globalDailyCap)
                throw new InvalidOperationException("Legacy daily usage exceeds the configured global cap.");
            // Preserve the entire legacy commitment as a conservative cutover
            // allocation. If an old reservation is later released, that spare
            // capacity intentionally remains unused until the next UTC day.
            var availableForShards = Math.Max(0, globalDailyCap - legacyCommitted);
            for (var index = 0; index < shardCount; index++)
            {
                var shardCap = availableForShards == 0
                    ? 0
                    : GlobalUsageShardCap(availableForShards, shardCount, index);
                transaction.Set(
                    _db.Collection("globalUsageShards").Document($"{date}-{index:D3}"),
                    new Dictionary<string, object>
                    {
                        ["date"] = date,
                        ["shardIndex"] = index,
                        ["dailyCapCreditMicros"] = shardCap,
                        ["reservedCreditMicros"] = 0L,
                        ["settledCreditMicros"] = 0L,
                        ["providerCostUsdMicros"] = 0L,
                        ["providerCostSettlementCount"] = 0L,
                        ["providerCostAccountingVersion"] = 1L,
                        ["schemaVersion"] = 2L,
                        ["createdAt"] = Ts(_clock.UtcNow),
                        ["updatedAt"] = Ts(_clock.UtcNow),
                    },
                    SetOptions.MergeAll);
            }
            transaction.Create(markerRef, new Dictionary<string, object>
            {
                ["date"] = date,
                ["globalDailyCapCreditMicros"] = globalDailyCap,
                ["shardCount"] = shardCount,
                ["legacySettledCreditMicros"] = legacySettled,
                ["legacyReservedCreditMicrosAtCutover"] = legacyReserved,
                ["legacyCommittedCreditMicrosAtCutover"] = legacyCommitted,
                ["shardedDailyCapCreditMicros"] = availableForShards,
                ["createdAt"] = Ts(_clock.UtcNow),
            });
            return availableForShards;
        }, cancellationToken: cancellationToken);
        return shardedCap;
    }

    internal static long EffectiveShardedDailyCap(
        IDictionary<string, object> marker,
        IDictionary<string, object> legacy,
        long configuredGlobalDailyCap,
        int configuredShardCount)
    {
        if (Long(marker, "globalDailyCapCreditMicros") != configuredGlobalDailyCap
            || Long(marker, "shardCount") != configuredShardCount)
            throw new InvalidOperationException(
                "The daily quota shard configuration changed during an active day.");
        var currentLegacyCommitted = checked(
            Math.Max(0, Long(legacy, "reservedCreditMicros"))
            + Math.Max(0, Long(legacy, "settledCreditMicros"))
            + CompanyLossExposure(legacy));
        var cutoverLegacyCommitted = Long(marker, "legacyCommittedCreditMicrosAtCutover");
        var postCutoverLegacyIncrease = Math.Max(0, currentLegacyCommitted - cutoverLegacyCommitted);
        return Math.Max(
            0,
            Long(marker, "shardedDailyCapCreditMicros") - postCutoverLegacyIncrease);
    }

    internal static int GlobalUsageShardIndex(
        string date,
        string userId,
        string requestId,
        int shardCount)
    {
        if (shardCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(shardCount));
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{date}:{userId}:{requestId}"));
        return (int)(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(hash) % (uint)shardCount);
    }

    internal static long GlobalUsageShardCap(long globalDailyCap, int shardCount, int shardIndex)
    {
        if (globalDailyCap <= 0)
            throw new ArgumentOutOfRangeException(nameof(globalDailyCap));
        if (shardCount <= 0 || shardIndex < 0 || shardIndex >= shardCount)
            throw new ArgumentOutOfRangeException(nameof(shardIndex));
        var baseCap = globalDailyCap / shardCount;
        var remainder = globalDailyCap % shardCount;
        return checked(baseCap + (shardIndex < remainder ? 1 : 0));
    }

    private static Dictionary<string, object> UsageEvidenceMap(
        ProviderUsage usage,
        long actualCreditMicros,
        long providerCostUsdMicros)
    {
        var result = UsageMap(usage, "completed");
        result["plannedChargeCreditMicros"] = actualCreditMicros;
        result["providerCostUsdMicros"] = providerCostUsdMicros;
        return result;
    }

    private static bool UsageEvidenceMatches(
        IDictionary<string, object> data,
        ProviderUsage usage)
    {
        foreach (var pair in UsageMap(usage, "completed"))
        {
            if (!data.TryGetValue(pair.Key, out var actual))
            {
                if (pair.Key == "completedKnown" && ProviderUsageMetadata.ReadCompletion(data).Known)
                    continue;
                return false;
            }
            if (pair.Value is long expectedLong
                && Convert.ToInt64(actual) != expectedLong)
                return false;
            if (pair.Value is bool expectedBool
                && Convert.ToBoolean(actual) != expectedBool)
                return false;
            if (pair.Value is string expectedString
                && !string.Equals(Convert.ToString(actual), expectedString, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private static Dictionary<string, object> UsageCheckpointMap(
        ProviderUsage usage,
        long estimatedCreditMicros,
        long providerCostUsdMicros)
    {
        var result = UsageMap(usage, "checkpoint");
        result["checkpointChargeCreditMicros"] = estimatedCreditMicros;
        result["checkpointProviderCostUsdMicros"] = providerCostUsdMicros;
        result["checkpointTotalTokens"] = usage.TotalTokens;
        return result;
    }

    private static Dictionary<string, object> UsageMap(ProviderUsage usage, string prefix) => new()
    {
        [$"{prefix}InputTokens"] = usage.InputTokens,
        [$"{prefix}OutputTokens"] = usage.OutputTokens,
        [$"{prefix}CachedInputTokens"] = usage.CachedInputTokens,
        [$"{prefix}ReasoningTokens"] = usage.ReasoningTokens,
        [$"{prefix}ImageTokens"] = usage.ImageTokens,
        [$"{prefix}AudioTokens"] = usage.AudioTokens,
        [$"{prefix}TotalTokens"] = usage.TotalTokens,
        [$"{prefix}InputAudioTokens"] = usage.InputAudioTokens,
        [$"{prefix}OutputAudioTokens"] = usage.OutputAudioTokens,
        [$"{prefix}InputImageTokens"] = usage.InputImageTokens,
        [$"{prefix}OutputImageTokens"] = usage.OutputImageTokens,
        [$"{prefix}ProviderRequestId"] = CleanProviderValue(usage.ProviderRequestId),
        [$"{prefix}ProviderModelVersion"] = CleanProviderValue(usage.ProviderModelVersion),
        [$"{prefix}Estimated"] = usage.Estimated,
        [$"{prefix}Known"] = usage.Known,
    };

    private static string CleanProviderValue(string? value)
    {
        var cleaned = value?.Trim() ?? "";
        return cleaned.Length <= 200 ? cleaned : cleaned[..200];
    }

    private static string CleanOutcome(string? value)
    {
        var cleaned = string.IsNullOrWhiteSpace(value) ? "provider_completed" : value.Trim();
        return cleaned.Length <= 100 ? cleaned : cleaned[..100];
    }

    private static string CleanActivityType(string? value)
    {
        var cleaned = string.IsNullOrWhiteSpace(value) ? "ai_request" : value.Trim().ToLowerInvariant();
        if (cleaned.Length > 50
            || cleaned.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-')))
            throw new ArgumentException("The activity type is invalid.", nameof(value));
        return cleaned;
    }

    internal static DateTimeOffset UsageDetailExpiresAt(
        IDictionary<string, object> account,
        DateTimeOffset settledAt)
    {
        var cycleEndsAt = Date(account, "cycleEndsAt");
        var retentionAnchor = cycleEndsAt > settledAt ? cycleEndsAt : settledAt;
        return retentionAnchor.Add(SettledUsageDetailRetention);
    }

    internal static bool IsUsageDetailVisible(
        DocumentSnapshot document,
        DateTimeOffset now) =>
        IsUsageDetailVisible(document.ToDictionary(), now);

    internal static bool IsUsageDetailVisible(
        IDictionary<string, object> data,
        DateTimeOffset now)
    {
        var expiresAt = Date(data, "detailExpiresAt");
        return expiresAt == DateTimeOffset.UnixEpoch || expiresAt > now;
    }

    private static bool IsLiveActivity(string? activityType) =>
        string.Equals(activityType, "live_audio", StringComparison.Ordinal);

    private async Task<bool> ConcurrencyAlreadyReleasedAsync(
        Transaction transaction, string userId, string requestId,
        IDictionary<string, object> reservation, CancellationToken cancellationToken)
    {
        if (reservation.ContainsKey("concurrencyReleased"))
            return Bool(reservation, "concurrencyReleased", false);
        // Older reconciliation released the slot but had no marker. Completion
        // can replace that status, so its existing durable audit event is the
        // authority for legacy completed records, not the current status alone.
        if (String(reservation, "status", "") == "reconciliation_pending")
            return true;
        var audit = _db.Collection("auditEvents")
            .Document(Crypto.Sha256($"reservation-reconcile:{userId}:{requestId}"));
        return (await transaction.GetSnapshotAsync(audit, cancellationToken)).Exists;
    }

    private static long RemainingLiveRequests(
        IDictionary<string, object> account,
        IDictionary<string, object> reservation,
        bool concurrencyReleased = false)
    {
        var activeLive = Math.Min(
            Long(account, "activeRequests"),
            Long(account, "activeLiveRequests"));
        return !concurrencyReleased && IsLiveActivity(String(reservation, "activityType", ""))
            ? Math.Max(0, activeLive - 1)
            : activeLive;
    }

    private DocumentReference Account(string userId) =>
        _db.Collection("accounts").Document(SafeId(userId));

    private AccountSnapshot Snapshot(
        string userId,
        IDictionary<string, object> data,
        IReadOnlyList<DeviceSnapshot> devices,
        IReadOnlyList<UsageSnapshot> usage,
        IReadOnlyList<PurchaseSnapshot> purchases)
    {
        var wallet = AllowanceWallet.FromAccount(data, _clock.UtcNow);
        var activeSubscription = wallet.Buckets
            .Where(bucket => bucket.Kind == "subscription" && !bucket.Expired)
            .ToArray();
        var subscription = activeSubscription
            .Sum(bucket => bucket.AvailableCreditMicros);
        var subscriptionReserved = activeSubscription
            .Sum(bucket => bucket.ReservedCreditMicros);
        var subscriptionOriginal = activeSubscription
            .Sum(bucket => Math.Max(0, bucket.OriginalCreditMicros - bucket.ReversedCreditMicros));
        var subscriptionConsumed = activeSubscription
            .Sum(bucket => Math.Min(
                bucket.ConsumedCreditMicros,
                Math.Max(0, bucket.OriginalCreditMicros - bucket.ReversedCreditMicros)));
        var topups = wallet.Buckets
            .Where(bucket => bucket.Kind == "topup")
            .Sum(bucket => bucket.AvailableCreditMicros);
        return new AccountSnapshot(
            userId,
            String(data, "email", ""),
            String(data, "displayName", ""),
            String(data, "status", "active"),
            String(data, "planId", "none"),
            wallet.AvailableCreditMicros,
            wallet.ReservedCreditMicros,
            Long(data, "cycleUsageCreditMicros"),
            data.TryGetValue("cycleEndsAt", out var cycle) && cycle is Timestamp cycleTimestamp
                ? new DateTimeOffset(cycleTimestamp.ToDateTime())
                : null,
            devices,
            usage,
            purchases,
            String(data, "subscriptionStatus", "none"),
            subscription,
            topups,
            wallet.AvailableCreditMicros - subscription - topups,
            AllowanceWallet.CurrentRulesVersion,
            subscriptionOriginal,
            subscriptionReserved,
            subscriptionConsumed,
            String(data, "subscriptionCancellationStatus", "none"),
            Date(data, "subscriptionCancelsAt") is var cancellationDate
                && cancellationDate != DateTimeOffset.UnixEpoch
                    ? cancellationDate
                    : null);
    }

    private PurchaseSnapshot PurchaseFrom(DocumentSnapshot document)
    {
        var data = document.ToDictionary();
        return new PurchaseSnapshot(
            document.Id,
            String(data, "productSku", ""),
            String(data, "provider", "razorpay"),
            StatusAfterAbandonment(
                String(data, "status", "pending"),
                Bool(data, "credited", false),
                Date(data, "createdAt"),
                _clock.UtcNow),
            Long(data, "amountMinor"),
            String(data, "currency", "INR"),
            Date(data, "createdAt"),
            Bool(data, "credited", false));
    }

    private static UsageSnapshot UsageFrom(DocumentSnapshot document)
    {
        var data = document.ToDictionary();
        return new UsageSnapshot(
            document.Id,
            String(data, "model", ""),
            String(data, "outcome", ""),
            Long(data, "chargedCreditMicros"),
            Long(data, "inputTokens"),
            Long(data, "outputTokens"),
            Date(data, "createdAt"),
            String(data, "activityType", "ai_request"),
            String(data, "pricingVersion", ""),
            Long(data, "cachedInputTokens"),
            Long(data, "reasoningTokens"),
            Long(data, "inputAudioTokens"),
            Long(data, "outputAudioTokens"),
            Long(data, "inputImageTokens"),
            Long(data, "outputImageTokens"),
            Bool(data, "usageEstimated", false),
            String(data, "providerModelVersion", ""),
            String(data, "accountingStatus", "settled"));
    }

    private static Reservation ReservationFrom(DocumentSnapshot snapshot)
    {
        if (!snapshot.Exists)
            throw new InvalidOperationException("The reservation was not created.");
        var data = snapshot.ToDictionary();
        return new Reservation(
            String(data, "requestId", snapshot.Id),
            String(data, "userId", ""),
            Long(data, "reservedCreditMicros"),
            String(data, "model", ""),
            String(data, "pricingVersion", ""),
            String(data, "status", ""),
            Date(data, "createdAt"),
            Date(data, "expiresAt") is var expiresAt && expiresAt != DateTimeOffset.UnixEpoch
                ? expiresAt
                : null,
            data.ContainsKey("providerStartedAt"));
    }

    private static DesktopLoginAttempt AttemptFrom(
        DocumentSnapshot snapshot,
        string? statusOverride = null,
        string? userId = null,
        string? codeHash = null)
    {
        var data = snapshot.ToDictionary();
        return new DesktopLoginAttempt(
            snapshot.Id,
            String(data, "stateHash", ""),
            String(data, "deviceNonceHash", ""),
            String(data, "pkceChallenge", ""),
            String(data, "callbackUri", ""),
            statusOverride ?? String(data, "status", ""),
            Date(data, "expiresAt"),
            userId ?? String(data, "userId", null),
            codeHash ?? String(data, "authorizationCodeHash", null));
    }

    private static Dictionary<string, object> LedgerMap(LedgerEntry entry) => new()
    {
        ["eventId"] = entry.EventId,
        ["type"] = entry.Type,
        ["availableDeltaMicros"] = entry.AvailableDeltaMicros,
        ["reservedDeltaMicros"] = entry.ReservedDeltaMicros,
        ["referenceType"] = entry.ReferenceType,
        ["referenceId"] = entry.ReferenceId,
        ["idempotencyKey"] = entry.IdempotencyKey,
        ["actor"] = entry.Actor,
        ["reason"] = entry.Reason,
        ["metadata"] = entry.Metadata?.ToDictionary(
                pair => pair.Key,
                pair => (object)(pair.Value ?? ""))
            ?? new Dictionary<string, object>(),
        ["createdAt"] = Ts(entry.CreatedAt),
    };

    internal static bool IsCreditEvent(string eventType, string purchaseKind) =>
        string.Equals(purchaseKind, "subscription", StringComparison.Ordinal)
            ? string.Equals(eventType, "subscription.charged", StringComparison.Ordinal)
            : eventType is "order.paid" or "payment.captured";

    internal static string PlanAfterSubscriptionStatus(
        string currentPlan,
        string productSku,
        string? status,
        bool fulfilled) =>
        status?.Trim().ToLowerInvariant() switch
        {
            "active" when fulfilled => productSku,
            "cancelled" or "completed" or "expired" or "halted" => "none",
            _ => currentPlan,
        };

    internal static bool ShouldRetainCancelledSubscriptionCycle(
        string? providerStatus,
        bool fulfilled,
        DateTimeOffset? cycleEndsAt,
        DateTimeOffset now) =>
        fulfilled
        && string.Equals(providerStatus?.Trim(), "cancelled", StringComparison.OrdinalIgnoreCase)
        && cycleEndsAt is DateTimeOffset boundary
        && boundary > now;

    internal static bool ShouldApplySubscriptionAccountState(
        string currentSubscriptionId,
        string incomingSubscriptionId,
        string? status,
        bool fulfilled,
        DateTimeOffset currentCycleEndsAt,
        DateTimeOffset? incomingCycleEndsAt)
    {
        if (string.IsNullOrWhiteSpace(incomingSubscriptionId))
            return false;
        if (string.Equals(currentSubscriptionId, incomingSubscriptionId, StringComparison.Ordinal))
            return true;
        if (!string.Equals(status?.Trim(), "active", StringComparison.OrdinalIgnoreCase)
            || !fulfilled)
            return false;
        if (string.IsNullOrWhiteSpace(currentSubscriptionId))
            return true;
        return incomingCycleEndsAt is DateTimeOffset incomingEnd
            && incomingEnd > currentCycleEndsAt;
    }

    private static bool IsTerminalSubscriptionStatus(string? status) =>
        status?.Trim().ToLowerInvariant() is "cancelled" or "completed" or "expired" or "halted";

    internal static string? RecurringFulfillmentId(
        string eventType,
        string? providerPaymentId,
        string? providerEventObjectId)
    {
        if (!string.Equals(eventType, "subscription.charged", StringComparison.Ordinal)
            && !IsReversalEvent(eventType))
            return null;
        var providerChargeId = string.IsNullOrWhiteSpace(providerPaymentId)
            ? providerEventObjectId
            : providerPaymentId;
        return string.IsNullOrWhiteSpace(providerChargeId)
            ? null
            : Crypto.Sha256($"razorpay:subscription-charge:{providerChargeId}");
    }

    internal static bool IsPreviouslyFulfilledRecurringCharge(
        string eventType,
        string? providerPaymentId,
        string? recordedPaymentId,
        bool purchaseCredited) =>
        purchaseCredited
        && string.Equals(eventType, "subscription.charged", StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(providerPaymentId)
        && string.Equals(providerPaymentId, recordedPaymentId, StringComparison.Ordinal);

    internal static string StatusAfterWebhook(
        string currentStatus,
        string incomingStatus,
        string purchaseKind,
        bool purchaseCredited,
        string eventType)
    {
        if (eventType.Contains("refund", StringComparison.OrdinalIgnoreCase))
            return "refunded";
        if (eventType.Contains("dispute", StringComparison.OrdinalIgnoreCase)
            || eventType.Contains("chargeback", StringComparison.OrdinalIgnoreCase))
            return "disputed";
        if (string.Equals(purchaseKind, "subscription", StringComparison.Ordinal))
        {
            if (string.Equals(eventType, "subscription.charged", StringComparison.Ordinal))
                return "paid";
            if (purchaseCredited && eventType.StartsWith("subscription.", StringComparison.Ordinal))
                return currentStatus.Trim().ToLowerInvariant() is "refunded" or "disputed"
                    ? currentStatus
                    : "paid";
        }
        if (!string.Equals(purchaseKind, "credits", StringComparison.Ordinal)
            || !purchaseCredited
            || IsReversalEvent(eventType))
            return incomingStatus;
        return incomingStatus.Trim().ToLowerInvariant() switch
        {
            "created" or "authorized" or "pending" or "verification_pending" or "failed" => currentStatus,
            _ => incomingStatus,
        };
    }

    private static bool IsReversalEvent(string eventType) =>
        eventType.Contains("refund.processed", StringComparison.OrdinalIgnoreCase)
        || eventType.Contains("dispute.lost", StringComparison.OrdinalIgnoreCase)
        || eventType.Contains("chargeback", StringComparison.OrdinalIgnoreCase);

    internal static long ReversalCreditMicros(
        string eventType,
        long? providerAmountMinor,
        long purchaseAmountMinor,
        long purchasedCreditMicros)
    {
        if (!IsReversalEvent(eventType) || purchasedCreditMicros <= 0)
            return 0;
        if (providerAmountMinor is null || providerAmountMinor <= 0 || purchaseAmountMinor <= 0)
            return 0;
        var boundedAmount = Math.Min(providerAmountMinor.Value, purchaseAmountMinor);
        return Math.Max(1, checked(purchasedCreditMicros * boundedAmount / purchaseAmountMinor));
    }

    internal static PaymentBalanceChange CalculatePaymentBalanceChange(
        long availableCreditMicros,
        long existingDebtMicros,
        long grossCreditMicros,
        long requestedReversalMicros,
        long previouslyReversedMicros,
        long purchasedCreditMicros)
    {
        var available = Math.Max(0, availableCreditMicros);
        var debt = Math.Max(0, existingDebtMicros);
        var grossCredit = Math.Max(0, grossCreditMicros);
        var debtPaid = Math.Min(grossCredit, debt);
        var availableCredit = grossCredit - debtPaid;
        var debtAfterCredit = debt - debtPaid;

        var remainingReversible = Math.Max(0, purchasedCreditMicros - previouslyReversedMicros);
        var reversal = Math.Min(Math.Max(0, requestedReversalMicros), remainingReversible);
        var availableAfterCredit = checked(available + availableCredit);
        var removedFromAvailable = Math.Min(reversal, availableAfterCredit);
        var debtCreated = reversal - removedFromAvailable;

        return new PaymentBalanceChange(
            availableCredit - removedFromAvailable,
            checked(debtAfterCredit + debtCreated),
            reversal);
    }

    private static string? TokenSessionId(string token, string prefix)
    {
        if (!token.StartsWith(prefix, StringComparison.Ordinal))
            return null;
        var separator = token.IndexOf('.', prefix.Length);
        if (separator <= prefix.Length)
            return null;
        var sessionId = token[prefix.Length..separator];
        return sessionId.Length is >= 12 and <= 64 ? sessionId : null;
    }

    private static string CleanDeviceName(string name)
    {
        var cleaned = string.IsNullOrWhiteSpace(name) ? "Windows device" : name.Trim();
        return cleaned.Length <= 80 ? cleaned : cleaned[..80];
    }

    internal static string SafeId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Contains('/'))
            throw new ArgumentException("Invalid resource identifier.", nameof(value));
        return value;
    }

    private static Timestamp Ts(DateTimeOffset value) => Timestamp.FromDateTime(value.UtcDateTime);

    private static string String(IDictionary<string, object> data, string key, string? fallback) =>
        data.TryGetValue(key, out var value) && value is not null
            ? Convert.ToString(value) ?? fallback ?? ""
            : fallback ?? "";

    private static long Long(IDictionary<string, object> data, string key, long fallback = 0) =>
        data.TryGetValue(key, out var value) && value is not null ? Convert.ToInt64(value) : fallback;

    private static bool Bool(IDictionary<string, object> data, string key, bool fallback) =>
        data.TryGetValue(key, out var value) && value is not null ? Convert.ToBoolean(value) : fallback;

    private static DateTimeOffset Date(IDictionary<string, object> data, string key)
    {
        if (!data.TryGetValue(key, out var value) || value is null)
            return DateTimeOffset.UnixEpoch;
        return value switch
        {
            Timestamp timestamp => new DateTimeOffset(timestamp.ToDateTime()),
            DateTime dateTime => new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
            _ => DateTimeOffset.TryParse(Convert.ToString(value), out var parsed)
                ? parsed
                : DateTimeOffset.UnixEpoch,
        };
    }

    private static bool FixedHash(string expected, string actual)
    {
        if (expected.Length != actual.Length)
            return false;
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(expected),
            System.Text.Encoding.ASCII.GetBytes(actual));
    }
}

internal sealed record PaymentBalanceChange(
    long AvailableDeltaMicros,
    long NewDebtMicros,
    long ReversalCreditMicros);
