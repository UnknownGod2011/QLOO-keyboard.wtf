using Google.Cloud.Firestore;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace KeyboardWtf.Staging.Api.Services;

/// <summary>Durable, bounded pet-only admission. No balance/reservation/ledger updates.</summary>
public sealed class FirestoreLumaGrantAdmission(FirestoreDb db, IOptions<LumaLiveOptions> options) : ILumaGrantAdmission
{
    public async Task AdmitAsync(string userId, DateTimeOffset now, DateTimeOffset expiresAt, CancellationToken token)
    {
        var settings = options.Value;
        if (!settings.Enabled || !settings.IsSafe() || !settings.RetentionPolicyVerified
            || !settings.ModelEligibilityVerified || string.IsNullOrWhiteSpace(userId) || userId.Length > 128
            || expiresAt <= now || expiresAt > now.AddSeconds(settings.SessionSeconds))
            throw new LumaVoiceUnavailableException("Luma voice is not configured yet.");
        var owner = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("luma-owner:" + userId))).ToLowerInvariant();
        var grants = db.Collection("lumaAdmission");
        var globalRef = grants.Document("global");
        var ownerRef = grants.Document("owner-" + owner);
        var accountRef = db.Collection("accounts").Document(FirestoreAccountStore.SafeId(userId));
        var day = now.UtcDateTime.Date;
        var leasedUntil = expiresAt.AddSeconds(30); // Do not trust stop callbacks or release early.
        await db.RunTransactionAsync(async transaction =>
        {
            var account = await transaction.GetSnapshotAsync(accountRef, token);
            var global = await transaction.GetSnapshotAsync(globalRef, token);
            var user = await transaction.GetSnapshotAsync(ownerRef, token);
            if (!account.Exists || account.ToDictionary().TryGetValue("status", out var status)
                && status is not null && status is not "active")
                throw new UnauthorizedAccessException("The account is not active.");
            var globalData = global.Exists ? global.ToDictionary() : new Dictionary<string, object>();
            var userData = user.Exists ? user.ToDictionary() : new Dictionary<string, object>();
            var globalCount = DayCount(globalData, day);
            var ownerCount = DayCount(userData, day);
            var leases = ReadLeases(globalData, now, settings.MaxConcurrentSessions);
            if (leases.ContainsKey(owner) || leases.Count >= settings.MaxConcurrentSessions
                || globalCount >= settings.MaxGrantsGlobalPerDay || ownerCount >= settings.MaxGrantsPerOwnerPerDay)
                throw new LumaVoiceUnavailableException("Luma voice has reached its short-session limit. Please try later.");
            leases.Add(owner, Timestamp.FromDateTime(leasedUntil.UtcDateTime));
            var expiry = Timestamp.FromDateTime(day.AddDays(2));
            transaction.Set(globalRef, new Dictionary<string, object>
            {
                ["day"] = Timestamp.FromDateTime(day), ["count"] = globalCount + 1,
                ["leases"] = leases, ["expiresAt"] = expiry,
            });
            transaction.Set(ownerRef, new Dictionary<string, object>
            {
                ["day"] = Timestamp.FromDateTime(day), ["count"] = ownerCount + 1,
                ["expiresAt"] = expiry,
            });
        }, cancellationToken: token);
    }

    private static long DayCount(Dictionary<string, object> data, DateTime day)
    {
        if (data.Count == 0) return 0;
        if (!data.TryGetValue("day", out var date) || date is not Timestamp timestamp
            || !data.TryGetValue("count", out var count) || count is not long number || number < 0)
            throw new LumaVoiceUnavailableException("Luma admission state is unavailable.");
        return timestamp.ToDateTime() == day ? number : 0;
    }

    private static Dictionary<string, object> ReadLeases(Dictionary<string, object> data, DateTimeOffset now, int cap)
    {
        if (!data.TryGetValue("leases", out var value))
        {
            if (data.Count > 0) throw new LumaVoiceUnavailableException("Luma admission state is unavailable.");
            return new(StringComparer.Ordinal);
        }
        if (value is not Dictionary<string, object> existing || existing.Count > cap)
            throw new LumaVoiceUnavailableException("Luma admission state is unavailable.");
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (owner, expiration) in existing)
        {
            if (owner.Length != 64 || owner.Any(c => !Uri.IsHexDigit(c)) || expiration is not Timestamp stamp)
                throw new LumaVoiceUnavailableException("Luma admission state is unavailable.");
            if (new DateTimeOffset(stamp.ToDateTime()) > now) result.Add(owner, stamp);
        }
        return result;
    }
}
