using Google.Cloud.Firestore;
using Grpc.Core;
using KeyboardWtf.Staging.Api.Domain;
using KeyboardWtf.Staging.Api.Infrastructure;

namespace KeyboardWtf.Staging.Api.Services;

public sealed partial class FirestoreAccountStore
{
    internal const long ManagedLiveLossPolicyVersion = 1;
    internal const string ManagedLiveLossTerminalStatus = "company_loss_closed";
    internal const long ManagedLiveLossDailyReleaseLimit = 100_000_000;
    internal const long ManagedLiveLossDailyAccountLimit = 2;
    internal static readonly TimeSpan ManagedLiveLossWait = TimeSpan.FromHours(24);

    // Call only after the server has disposed the provider socket. This proves
    // local shutdown, NOT remote acceptance, a final invoice, or zero cost.
    public Task RecordManagedLiveSessionClosedAsync(string userId, string requestId, CancellationToken cancellationToken)
    {
        if (!_options.ManagedLiveUnknownUsageLossEnabled)
            return Task.CompletedTask;
        var reference = Account(userId).Collection("reservations").Document(SafeId(requestId));
        var now = _clock.UtcNow;
        return _db.RunTransactionAsync(async transaction =>
        {
            var snapshot = await transaction.GetSnapshotAsync(reference, cancellationToken);
            if (!snapshot.Exists) return;
            var data = snapshot.ToDictionary();
            if (!IsEnrolledManagedLiveLoss(data, userId) || !data.TryGetValue("providerStartedAt", out var started)
                || started is not Timestamp providerStarted || providerStarted.ToDateTimeOffset() > now)
                return;
            if (String(data, "status", "") is not ("reserved" or "reconciliation_pending" or "provider_completed"))
                return;
            if (data.TryGetValue("managedLiveClosedAt", out var recorded))
            {
                if (recorded is not Timestamp stamp || stamp < providerStarted)
                    throw new InvalidOperationException("The Managed Live closure evidence is invalid.");
                return;
            }
            transaction.Update(reference, new Dictionary<string, object>
            {
                ["managedLiveClosedAt"] = Ts(now), ["updatedAt"] = Ts(now),
            });
        }, cancellationToken: cancellationToken);
    }

    public async Task<ManagedLiveLossResolution> ResolveManagedLiveUnknownUsageLossAsync(
        string userId, string requestId, CancellationToken cancellationToken)
    {
        if (!_options.ManagedLiveUnknownUsageLossEnabled)
            return ManagedLiveLossResolution.NotEligible;
        var accountRef = Account(userId);
        var reservationRef = accountRef.Collection("reservations").Document(SafeId(requestId));
        var key = Crypto.Sha256($"managed-live-unknown-loss-v1:{userId}:{requestId}");
        var decisionRef = _db.Collection("managedLiveLossDecisions").Document(key);
        var idempotencyRef = _db.Collection("idempotency").Document(key);
        var ledgerRef = accountRef.Collection("ledger").Document($"live_loss_{SafeId(requestId)}");
        var now = _clock.UtcNow;
        var day = now.ToUniversalTime().ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
        var globalLossRef = _db.Collection("managedLiveLossDaily").Document(day);
        var ownerLossRef = accountRef.Collection("managedLiveLossDaily").Document(day);

        async Task<ManagedLiveLossResolution> ResolveTransactionAsync() => await _db.RunTransactionAsync(async transaction =>
        {
            var accountSnapshot = await transaction.GetSnapshotAsync(accountRef, cancellationToken);
            var snapshot = await transaction.GetSnapshotAsync(reservationRef, cancellationToken);
            if (!accountSnapshot.Exists || !snapshot.Exists)
                return ManagedLiveLossResolution.NotEligible;
            var data = snapshot.ToDictionary();
            if (!IsEnrolledManagedLiveLoss(data, userId))
                return ManagedLiveLossResolution.NotEligible;
            var decision = await transaction.GetSnapshotAsync(decisionRef, cancellationToken);
            var idempotency = await transaction.GetSnapshotAsync(idempotencyRef, cancellationToken);
            var ledger = await transaction.GetSnapshotAsync(ledgerRef, cancellationToken);
            if (String(data, "status", "") == ManagedLiveLossTerminalStatus)
            {
                if (!decision.Exists || !idempotency.Exists || !ledger.Exists)
                    throw new InvalidOperationException("The Managed Live loss decision is incomplete.");
                ValidateResolvedManagedLiveLoss(data, decision.ToDictionary(), idempotency.ToDictionary(),
                    ledger.ToDictionary(), userId, requestId, key);
                return ManagedLiveLossResolution.AlreadyResolved;
            }
            if (decision.Exists || idempotency.Exists || ledger.Exists)
                throw new InvalidOperationException("Conflicting Managed Live loss evidence was rejected.");
            if (!EligibleManagedLiveLoss(data, now))
                return ManagedLiveLossResolution.NotEligible;

            var reserved = StrictLossCount(data, "reservedCreditMicros");
            if (reserved <= 0 || reserved > StrictLossCount(data, "taskCapCreditMicros"))
                throw new InvalidOperationException("The Managed Live loss reservation is invalid.");
            var globalLoss = await transaction.GetSnapshotAsync(globalLossRef, cancellationToken);
            var ownerLoss = await transaction.GetSnapshotAsync(ownerLossRef, cancellationToken);
            var globalLossData = globalLoss.Exists ? globalLoss.ToDictionary() : [];
            var ownerLossData = ownerLoss.Exists ? ownerLoss.ToDictionary() : [];
            var releasedToday = globalLoss.Exists ? StrictLossCount(globalLossData, "releasedReservationCreditMicros") : 0;
            var globalDecisionCount = globalLoss.Exists ? StrictLossCount(globalLossData, "decisionCount") : 0;
            var ownerDecisionCount = ownerLoss.Exists ? StrictLossCount(ownerLossData, "decisionCount") : 0;
            if ((globalLoss.Exists && String(globalLossData, "date", "") != day)
                || (ownerLoss.Exists && String(ownerLossData, "date", "") != day))
                throw new InvalidOperationException("The Managed Live loss day identity is invalid.");
            if (releasedToday > ManagedLiveLossDailyReleaseLimit
                || ownerDecisionCount > ManagedLiveLossDailyAccountLimit)
                throw new InvalidOperationException("The Managed Live loss limits are invalid.");
            // Count the FULL retired reservation, including expired allowance;
            // otherwise expired buckets could bypass the company-loss limit.
            if (reserved > ManagedLiveLossDailyReleaseLimit - releasedToday)
                return ManagedLiveLossResolution.GlobalDailyLimit;
            if (ownerDecisionCount >= ManagedLiveLossDailyAccountLimit)
                return ManagedLiveLossResolution.AccountDailyLimit;

            var account = accountSnapshot.ToDictionary();
            var allocations = StrictLossAllocations(data, account, reserved);
            var wallet = AllowanceWallet.FromAccount(account, now);
            var expiredAvailable = wallet.NewlyExpired.Sum(expiration => expiration.CreditMicros);
            if (checked(wallet.AvailableCreditMicros + expiredAvailable) != StrictLossCount(account, "availableCreditMicros")
                || wallet.ReservedCreditMicros != StrictLossCount(account, "reservedCreditMicros"))
                throw new InvalidOperationException("The Managed Live loss wallet is inconsistent.");
            var originalWalletReserved = wallet.ReservedCreditMicros;
            var restored = wallet.Release(allocations);
            if (originalWalletReserved - wallet.ReservedCreditMicros != reserved)
                throw new InvalidOperationException("The Managed Live loss allocation is inconsistent.");

            var originalDay = String(data, "globalUsageDate", "");
            var shardId = String(data, "globalUsageShardId", "");
            if (originalDay.Length != 8 || !originalDay.All(char.IsAsciiDigit)
                || !shardId.StartsWith(originalDay + "-", StringComparison.Ordinal) || shardId.Contains('/'))
                throw new InvalidOperationException("The Managed Live loss budget identity is invalid.");
            var globalRef = _db.Collection("globalUsageShards").Document(shardId);
            var userDailyRef = accountRef.Collection("dailyUsage").Document(originalDay);
            var global = await transaction.GetSnapshotAsync(globalRef, cancellationToken);
            var userDaily = await transaction.GetSnapshotAsync(userDailyRef, cancellationToken);
            if (!global.Exists || !userDaily.Exists)
                throw new InvalidOperationException("The Managed Live loss budget evidence is missing.");
            var globalData = global.ToDictionary();
            var dailyData = userDaily.ToDictionary();
            var globalReserved = StrictLossCount(globalData, "reservedCreditMicros");
            var dailyReserved = StrictLossCount(dailyData, "reservedCreditMicros");
            if (globalReserved < reserved || dailyReserved < reserved)
                throw new InvalidOperationException("The Managed Live loss budget is inconsistent.");
            var globalExposure = checked(CompanyLossExposure(globalData) + reserved);
            var dailyExposure = checked(CompanyLossExposure(dailyData) + reserved);
            var requireReview = ownerDecisionCount + 1 >= ManagedLiveLossDailyAccountLimit;

            // All reads precede writes. Every effect commits with the immutable
            // decision, ledger and idempotency record; ambiguous ACKs can retry.
            foreach (var expiration in wallet.NewlyExpired)
            {
                var expiryId = $"expire_{expiration.BucketId}";
                transaction.Create(accountRef.Collection("ledger").Document(expiryId), LedgerMap(new LedgerEntry(
                    expiryId, LedgerTypes.Expiration, -expiration.CreditMicros, 0,
                    "allowance-bucket", expiration.BucketId,
                    Crypto.Sha256($"allowance-expiration:{userId}:{expiration.BucketId}"),
                    "system", "Subscription allowance expired at its billing-cycle boundary", now)));
            }
            transaction.Create(ledgerRef, LedgerMap(new LedgerEntry(
                ledgerRef.Id, LedgerTypes.ManagedLiveUnknownCostHoldRelease, restored, -reserved,
                "managed-live-loss", requestId, key, "maintenance",
                "Company absorbed unknown provider cost; no customer usage charge", now,
                new Dictionary<string, object?>
                {
                    ["policyVersion"] = ManagedLiveLossPolicyVersion,
                    ["retiredReservationCreditMicros"] = reserved,
                    ["restoredAvailableCreditMicros"] = restored,
                    ["expiredReservedCreditMicros"] = reserved - restored,
                    ["providerCostKnown"] = false, ["decisionId"] = key,
                })));
            transaction.Create(decisionRef, new Dictionary<string, object>
            {
                ["policyVersion"] = ManagedLiveLossPolicyVersion,
                ["userId"] = userId, ["requestId"] = requestId,
                ["disposition"] = "company_absorbed_unknown_provider_cost",
                ["providerCostKnown"] = false, ["customerRetroactiveChargeAllowed"] = false,
                ["retiredReservationCreditMicros"] = reserved,
                ["restoredAvailableCreditMicros"] = restored,
                ["expiredReservedCreditMicros"] = reserved - restored,
                ["originalUsageDate"] = originalDay, ["resolutionDate"] = day,
                ["serverClosedAt"] = data["managedLiveClosedAt"], ["reservationExpiresAt"] = data["expiresAt"],
                ["createdAt"] = Ts(now),
            });
            transaction.Create(idempotencyRef, new Dictionary<string, object>
            {
                ["scope"] = "managed-live-unknown-loss-v1", ["userId"] = userId,
                ["resourceId"] = requestId, ["decisionId"] = key, ["createdAt"] = Ts(now),
            });
            transaction.Update(reservationRef, new Dictionary<string, object>
            {
                ["status"] = ManagedLiveLossTerminalStatus, ["companyLossDecisionId"] = key,
                ["releasedCreditMicros"] = restored, ["companyLossExposureCreditMicros"] = reserved,
                ["providerCostKnown"] = false, ["customerRetroactiveChargeAllowed"] = false,
                ["outcome"] = "company_absorbed_unknown_provider_cost", ["updatedAt"] = Ts(now),
            });
            var accountUpdates = new Dictionary<string, object>
            {
                ["availableCreditMicros"] = wallet.AvailableCreditMicros,
                ["reservedCreditMicros"] = wallet.ReservedCreditMicros,
                ["allowanceBuckets"] = wallet.ToFirestoreMap(), ["updatedAt"] = Ts(now),
            };
            if (requireReview)
            {
                accountUpdates["managedLiveLossReviewRequired"] = true;
                accountUpdates["managedLiveLossReviewDecisionId"] = key;
                accountUpdates["managedLiveLossReviewPolicyVersion"] = ManagedLiveLossPolicyVersion;
                accountUpdates["managedLiveLossReviewStartedAt"] = Ts(now);
            }
            // Concurrency was already released; never decrement a newer session.
            transaction.Update(accountRef, accountUpdates);
            transaction.Update(globalRef, new Dictionary<string, object>
            {
                ["reservedCreditMicros"] = globalReserved - reserved,
                ["companyLossExposureCreditMicros"] = globalExposure, ["updatedAt"] = Ts(now),
            });
            transaction.Update(userDailyRef, new Dictionary<string, object>
            {
                ["reservedCreditMicros"] = dailyReserved - reserved,
                ["companyLossExposureCreditMicros"] = dailyExposure, ["updatedAt"] = Ts(now),
            });
            transaction.Set(globalLossRef, new Dictionary<string, object>
            {
                ["date"] = day, ["releasedReservationCreditMicros"] = checked(releasedToday + reserved),
                ["decisionCount"] = checked(globalDecisionCount + 1), ["updatedAt"] = Ts(now),
            });
            transaction.Set(ownerLossRef, new Dictionary<string, object>
            {
                ["date"] = day, ["decisionCount"] = checked(ownerDecisionCount + 1), ["updatedAt"] = Ts(now),
            });
            return ManagedLiveLossResolution.Released;
        }, cancellationToken: cancellationToken);

        for (var attempt = 0; ; attempt++)
        {
            try { return await ResolveTransactionAsync(); }
            catch (RpcException exception) when (exception.StatusCode == StatusCode.Aborted && attempt < _options.MaxRetries)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(40 * (attempt + 1)), cancellationToken);
            }
        }
    }

    private static bool IsEnrolledManagedLiveLoss(IDictionary<string, object> data, string userId) =>
        data.TryGetValue("managedLiveLossPolicyVersion", out var version) && version is long number && number == ManagedLiveLossPolicyVersion
        && data.TryGetValue("accountingVersion", out var accounting) && accounting is long generation && generation >= 2
        && String(data, "activityType", "") == "live_audio" && String(data, "userId", "") == userId
        && data.TryGetValue("createdAt", out var created) && created is Timestamp creation
        && data.TryGetValue("managedLiveLossEnrolledAt", out var enrolled) && enrolled is Timestamp enrollment && enrollment == creation;

    private static bool EligibleManagedLiveLoss(IDictionary<string, object> data, DateTimeOffset now) =>
        String(data, "status", "") == "reconciliation_pending"
        && data.TryGetValue("concurrencyReleased", out var released) && released is true
        && data.TryGetValue("providerStartedAt", out var started) && started is Timestamp providerStarted
        && data.TryGetValue("managedLiveClosedAt", out var closed) && closed is Timestamp serverClosed && serverClosed >= providerStarted
        && serverClosed.ToDateTimeOffset() <= now
        && data.TryGetValue("expiresAt", out var expiry) && expiry is Timestamp expires
        && expires.ToDateTimeOffset() >= providerStarted.ToDateTimeOffset()
        && expires.ToDateTimeOffset() <= now - ManagedLiveLossWait
        && (!data.TryGetValue("providerCompletionRecorded", out var completion) || completion is false)
        && !data.ContainsKey("providerCompletedAt") && !data.ContainsKey("providerHttpStatus")
        && !ProviderUsageMetadata.ReadCompletion(data).Known;

    private static void RequireManagedLiveLossAdmission(IDictionary<string, object> account)
    {
        if (account.TryGetValue("managedLiveLossReviewRequired", out var review) && review is not false)
            throw new InvalidOperationException("Managed Jarvis Live needs account review after repeated accounting failures. Other requests and Personal API are unaffected.");
    }

    private static void ValidateResolvedManagedLiveLoss(IDictionary<string, object> reservation,
        IDictionary<string, object> decision, IDictionary<string, object> idempotency,
        IDictionary<string, object> ledger, string userId, string requestId, string key)
    {
        var reserved = StrictLossCount(reservation, "reservedCreditMicros");
        var restored = StrictLossCount(reservation, "releasedCreditMicros");
        if (reserved <= 0 || restored > reserved
            || String(reservation, "requestId", "") != requestId
            || String(reservation, "companyLossDecisionId", "") != key
            || StrictLossCount(reservation, "companyLossExposureCreditMicros") != reserved
            || !reservation.TryGetValue("providerCostKnown", out var known) || known is not false
            || !reservation.TryGetValue("customerRetroactiveChargeAllowed", out var retroactive) || retroactive is not false
            || String(decision, "userId", "") != userId || String(decision, "requestId", "") != requestId
            || StrictLossCount(decision, "policyVersion") != ManagedLiveLossPolicyVersion
            || StrictLossCount(decision, "retiredReservationCreditMicros") != reserved
            || StrictLossCount(decision, "restoredAvailableCreditMicros") != restored
            || StrictLossCount(decision, "expiredReservedCreditMicros") != reserved - restored
            || String(decision, "disposition", "") != "company_absorbed_unknown_provider_cost"
            || !decision.TryGetValue("providerCostKnown", out known) || known is not false
            || !decision.TryGetValue("customerRetroactiveChargeAllowed", out retroactive) || retroactive is not false
            || String(idempotency, "scope", "") != "managed-live-unknown-loss-v1"
            || String(idempotency, "userId", "") != userId || String(idempotency, "resourceId", "") != requestId
            || String(idempotency, "decisionId", "") != key
            || String(ledger, "type", "") != LedgerTypes.ManagedLiveUnknownCostHoldRelease
            || String(ledger, "referenceType", "") != "managed-live-loss" || String(ledger, "referenceId", "") != requestId
            || String(ledger, "idempotencyKey", "") != key
            || StrictLossCount(ledger, "availableDeltaMicros") != restored
            || !ledger.TryGetValue("reservedDeltaMicros", out var delta) || delta is not long reservedDelta || reservedDelta != -reserved
            || !ledger.TryGetValue("metadata", out var rawMetadata) || rawMetadata is not IDictionary<string, object> metadata
            || StrictLossCount(metadata, "policyVersion") != ManagedLiveLossPolicyVersion
            || StrictLossCount(metadata, "retiredReservationCreditMicros") != reserved
            || StrictLossCount(metadata, "restoredAvailableCreditMicros") != restored
            || StrictLossCount(metadata, "expiredReservedCreditMicros") != reserved - restored
            || String(metadata, "decisionId", "") != key
            || !metadata.TryGetValue("providerCostKnown", out known) || known is not false)
            throw new InvalidOperationException("The Managed Live loss decision does not match its immutable financial evidence.");
    }

    private static long CommittedIncludingCompanyLoss(IDictionary<string, object> data) =>
        checked(Long(data, "settledCreditMicros") + Long(data, "reservedCreditMicros") + CompanyLossExposure(data));

    private static long CompanyLossExposure(IDictionary<string, object> data) => OptionalLossCount(data, "companyLossExposureCreditMicros");

    private static long OptionalLossCount(IDictionary<string, object> data, string field) =>
        data.ContainsKey(field) ? StrictLossCount(data, field) : 0;

    private static long StrictLossCount(IDictionary<string, object> data, string field) =>
        data.TryGetValue(field, out var value) && value is long count && count >= 0
            ? count : throw new InvalidOperationException("Managed Live loss accounting contains an invalid counter.");

    private static IReadOnlyList<AllowanceAllocation> StrictLossAllocations(
        IDictionary<string, object> data, IDictionary<string, object> account, long reserved)
    {
        if (String(account, "allowanceRulesVersion", "") != AllowanceWallet.CurrentRulesVersion
            || !account.TryGetValue("allowanceBuckets", out var rawBuckets) || rawBuckets is not IDictionary<string, object> buckets
            || !data.TryGetValue("allowanceAllocations", out var raw) || raw is not IEnumerable<object> rows)
            throw new InvalidOperationException("Managed Live loss requires original allowance allocations.");
        long representedAvailable = 0, representedReserved = 0;
        foreach (var pair in buckets)
        {
            if (pair.Value is not IDictionary<string, object> bucket || String(bucket, "id", "") != pair.Key
                || String(bucket, "rulesVersion", "") != AllowanceWallet.CurrentRulesVersion
                || !bucket.TryGetValue("expired", out var expired) || expired is not bool)
                throw new InvalidOperationException("The Managed Live loss allowance bucket is invalid.");
            representedAvailable = checked(representedAvailable + StrictLossCount(bucket, "availableCreditMicros"));
            representedReserved = checked(representedReserved + StrictLossCount(bucket, "reservedCreditMicros"));
            foreach (var field in new[] { "originalCreditMicros", "consumedCreditMicros", "reversedCreditMicros", "expiresAtUnix", "createdAtUnix" })
                _ = StrictLossCount(bucket, field);
        }
        if (representedAvailable != StrictLossCount(account, "availableCreditMicros")
            || representedReserved != StrictLossCount(account, "reservedCreditMicros"))
            throw new InvalidOperationException("The Managed Live loss allowance totals are inconsistent.");
        var allocations = new List<AllowanceAllocation>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long total = 0;
        foreach (var row in rows)
        {
            if (row is not IDictionary<string, object> allocation || allocation.Count != 2
                || !allocation.TryGetValue("bucketId", out var rawId) || rawId is not string bucketId || !seen.Add(bucketId)
                || !buckets.TryGetValue(bucketId, out var value) || value is not IDictionary<string, object> bucket)
                throw new InvalidOperationException("The Managed Live loss allocation is invalid.");
            var amount = StrictLossCount(allocation, "creditMicros");
            if (amount <= 0 || StrictLossCount(bucket, "reservedCreditMicros") < amount)
                throw new InvalidOperationException("The Managed Live loss allocation is invalid.");
            total = checked(total + amount);
            allocations.Add(new(bucketId, amount));
        }
        if (total != reserved || allocations.Count == 0)
            throw new InvalidOperationException("The Managed Live loss allocation does not match its reservation.");
        return allocations;
    }
}
