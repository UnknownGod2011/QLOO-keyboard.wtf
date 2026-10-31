using System.Security.Cryptography;

namespace KeyboardWtf.Services.Updates;

// Consent binds to the server's verified tuple, never client URL/hash fields.
// One ephemeral offer per Settings host; no profile/token persistence or timer.
internal sealed class VerifiedUpdateConsent
{
    private static readonly TimeSpan MaximumLifetime = TimeSpan.FromMinutes(30);
    private readonly object _sync = new();
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly TimeSpan _lifetime;
    private PendingOffer _offer;

    internal VerifiedUpdateConsent(Func<DateTimeOffset> utcNow = null, TimeSpan? lifetime = null)
    {
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _lifetime = lifetime ?? MaximumLifetime;
        if (_lifetime <= TimeSpan.Zero || _lifetime > MaximumLifetime)
            throw new ArgumentOutOfRangeException(nameof(lifetime));
    }

    internal string Issue(VerifiedUpdate update)
    {
        lock (_sync)
        {
            if (update is null)
            {
                _offer = null;
                return null;
            }
            var bytes = RandomNumberGenerator.GetBytes(32);
            _offer = new PendingOffer(update, bytes, _utcNow());
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }
    }

    internal VerifiedUpdate Take(string token, string channel)
    {
        lock (_sync)
        {
            var offer = _offer;
            if (offer is null)
                throw InvalidConsent();
            var now = _utcNow();
            if (now < offer.IssuedAt || now - offer.IssuedAt >= _lifetime)
            {
                _offer = null;
                throw InvalidConsent();
            }
            if (!string.Equals(offer.Update.Channel, SecureUpdateService.NormalizeChannel(channel), StringComparison.Ordinal))
                throw InvalidConsent();
            if (token is null || token.Length != 64 || !token.All(Uri.IsHexDigit)
                || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(token), offer.Token))
                throw InvalidConsent();
            _offer = null;
            return offer.Update;
        }
    }

    internal void Clear()
    {
        lock (_sync) _offer = null;
    }

    private static InvalidOperationException InvalidConsent() =>
        new("The update approval is missing, expired or no longer matches. Check for updates again before installing.");

    private sealed record PendingOffer(VerifiedUpdate Update, byte[] Token, DateTimeOffset IssuedAt);
}
