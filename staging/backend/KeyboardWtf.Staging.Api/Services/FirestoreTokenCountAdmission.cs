using Google.Cloud.Firestore;
using KeyboardWtf.Staging.Api.Configuration;
using KeyboardWtf.Staging.Api.Domain;
using KeyboardWtf.Staging.Api.Infrastructure;
using Microsoft.Extensions.Options;

namespace KeyboardWtf.Staging.Api.Services;

// Quota admission only. It never creates a monetary reservation, changes the
// wallet/account, or substitutes token-count evidence for settled usage.
internal sealed class FirestoreTokenCountAdmission : ITokenCountAdmission
{
    private readonly FirestoreDb _db;
    private readonly IClock _clock;
    private readonly PaidSystemOptions _options;

    public FirestoreTokenCountAdmission(FirestoreDb db, IClock clock, IOptions<PaidSystemOptions> options)
    {
        _db = db;
        _clock = clock;
        _options = options.Value;
        if (_options.TokenCountCallsPerMinute is < 1 or > 1_500
            || _options.MaxRequestsPerMinutePerUser is < 1 or > 750
            || _options.MaxConcurrentRequestsPerUser < 1 || _options.DefaultTaskCreditMicrosCap <= 0)
            throw new InvalidOperationException("Token-count admission limits are unsafe.");
    }

    public async Task AdmitAsync(CallerIdentity caller, string requestId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var uid = FirestoreAccountStore.SafeId(caller.UserId);
        FirestoreAccountStore.SafeId(requestId);
        if (requestId.Length > 200) throw new ArgumentException("Invalid request identifier.", nameof(requestId));
        var accountRef = _db.Collection("accounts").Document(uid);
        var idempotencyRef = _db.Collection("idempotency")
            .Document(Crypto.Sha256($"usage-reserve:{uid}:{requestId}"));
        var counters = _db.Collection("providerPreflight");
        var projectRef = counters.Document("count-tokens-project");
        var userRef = counters.Document("user-" + Crypto.Sha256(uid));

        var committedWindow = await _db.RunTransactionAsync(async transaction =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = _clock.UtcNow;
            var window = Minute(now);
            var accountSnapshot = await transaction.GetSnapshotAsync(accountRef, cancellationToken);
            if (!accountSnapshot.Exists) throw new UnauthorizedAccessException("The account is not active.");
            var account = accountSnapshot.ToDictionary();
            if (account.TryGetValue("status", out var status) && status is not null
                && (status is not string text || !string.Equals(text, "active", StringComparison.Ordinal)))
                throw new UnauthorizedAccessException("The account is not active.");

            var idempotency = await transaction.GetSnapshotAsync(idempotencyRef, cancellationToken);
            if (idempotency.Exists) throw new InvalidOperationException("This request ID has already been used.");
            // Detached eligibility check: expiry/migration/reservation happens
            // only in memory. ReserveAsync still rechecks and commits later.
            AllowanceWallet.FromAccount(account, now).Reserve(_options.DefaultTaskCreditMicrosCap,
                initialReservation: true);
            var active = AccountCount(account, "activeRequests");
            var live = Math.Min(active, AccountCount(account, "activeLiveRequests"));
            if (active - live >= _options.MaxConcurrentRequestsPerUser)
                throw new InvalidOperationException("Another one-shot AI request is already running for this account.");

            var project = await transaction.GetSnapshotAsync(projectRef, cancellationToken);
            var user = await transaction.GetSnapshotAsync(userRef, cancellationToken);
            var projectCount = ReadCounter(project, window);
            var userCount = ReadCounter(user, window);
            if (projectCount >= _options.TokenCountCallsPerMinute
                || userCount >= checked(_options.MaxRequestsPerMinutePerUser * 2L))
                throw new InvalidOperationException("The token-count preflight limit has been reached. Try again shortly.");
            transaction.Set(projectRef, Counter(window, now, projectCount + 1));
            transaction.Set(userRef, Counter(window, now, userCount + 1));
            return window;
        }, cancellationToken: cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        // A permit acknowledged after its minute ended is consumed but cannot
        // dispatch a late call into a new window. No automatic re-admission.
        if (Minute(_clock.UtcNow) != committedWindow)
            throw new InvalidOperationException("Token-count admission expired. Please try again.");
    }

    private static long AccountCount(Dictionary<string, object> account, string key)
    {
        if (!account.TryGetValue(key, out var value) || value is null) return 0;
        if (value is not long count || count < 0)
            throw new InvalidOperationException("Account request state is unavailable.");
        return count;
    }

    private static long ReadCounter(DocumentSnapshot snapshot, DateTimeOffset window)
    {
        if (!snapshot.Exists) return 0;
        var data = snapshot.ToDictionary();
        if (!data.TryGetValue("windowStart", out var startValue) || startValue is not Timestamp start
            || !data.TryGetValue("count", out var countValue) || countValue is not long count || count < 0
            || !data.TryGetValue("updatedAt", out var updatedValue) || updatedValue is not Timestamp updated
            || !data.TryGetValue("expiresAt", out var expiresValue) || expiresValue is not Timestamp expires)
            throw new InvalidOperationException("Token-count admission state is unavailable.");
        var started = new DateTimeOffset(start.ToDateTime());
        var updatedAt = new DateTimeOffset(updated.ToDateTime());
        if (Minute(started) != started || started > window
            || updatedAt < started || updatedAt >= started.AddMinutes(1)
            || new DateTimeOffset(expires.ToDateTime()) != started.AddMinutes(3))
            throw new InvalidOperationException("Token-count admission state is unavailable.");
        return started == window ? count : 0;
    }

    private static Dictionary<string, object> Counter(DateTimeOffset window, DateTimeOffset now, long count) => new()
    {
        ["windowStart"] = Timestamp.FromDateTime(window.UtcDateTime), ["count"] = count,
        ["updatedAt"] = Timestamp.FromDateTime(now.UtcDateTime),
        ["expiresAt"] = Timestamp.FromDateTime(window.AddMinutes(3).UtcDateTime),
    };

    private static DateTimeOffset Minute(DateTimeOffset time) =>
        new(time.UtcTicks - time.UtcTicks % TimeSpan.TicksPerMinute, TimeSpan.Zero);
}
