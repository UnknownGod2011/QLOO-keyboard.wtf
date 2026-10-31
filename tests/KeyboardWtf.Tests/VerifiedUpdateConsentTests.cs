using System.Reflection;
using System.Runtime.ExceptionServices;
using KeyboardWtf.Services.Updates;
using Xunit;

namespace KeyboardWtf.Tests;

// Reflection deliberately keeps this test-first gate compilable before the
// internal consent class exists. All offers and clocks are synthetic/in-memory.
public sealed class VerifiedUpdateConsentTests
{
    private static readonly DateTimeOffset InitialTime = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    [Fact]
    public void Issue_returns_a_256_bit_hex_token_and_take_returns_the_exact_verified_record()
    {
        var consent = Consent.Create(() => InitialTime);
        var update = Update();

        var token = consent.Issue(update);
        Assert.Matches(@"\A[0-9a-fA-F]{64}\z", token);

        var taken = consent.Take(token, "stable");
        Assert.Same(update, taken);
        Assert.Equal(update, taken);
    }

    [Fact]
    public void Default_clock_and_lifetime_accept_an_immediate_take()
    {
        var consent = Consent.Create();
        var update = Update();

        Assert.Same(update, consent.Take(consent.Issue(update), "stable"));
    }

    [Fact]
    public void Successful_take_is_single_use()
    {
        var consent = Consent.Create(() => InitialTime);
        var update = Update();
        var token = consent.Issue(update);

        Assert.Same(update, consent.Take(token, "stable"));
        Assert.Throws<InvalidOperationException>(() => consent.Take(token, "stable"));
    }

    [Fact]
    public void Take_without_a_pending_offer_rejects()
    {
        var consent = Consent.Create(() => InitialTime);

        Assert.Throws<InvalidOperationException>(() => consent.Take(new string('a', 64), "stable"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("not-a-consent-token")]
    [InlineData("GGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGG")]
    public void Missing_or_malformed_token_rejects_without_consuming_the_valid_offer(string invalidToken)
    {
        var consent = Consent.Create(() => InitialTime);
        var update = Update();
        var token = consent.Issue(update);

        Assert.Throws<InvalidOperationException>(() => consent.Take(invalidToken, "stable"));
        Assert.Same(update, consent.Take(token, "stable"));
    }

    [Theory]
    [InlineData(63)]
    [InlineData(65)]
    public void Wrong_length_hex_token_rejects_without_consuming_the_valid_offer(int length)
    {
        var consent = Consent.Create(() => InitialTime);
        var update = Update();
        var token = consent.Issue(update);

        Assert.Throws<InvalidOperationException>(() => consent.Take(new string('a', length), "stable"));
        Assert.Same(update, consent.Take(token, "stable"));
    }

    [Fact]
    public void Wrong_256_bit_token_rejects_without_consuming_the_valid_offer()
    {
        var consent = Consent.Create(() => InitialTime);
        var update = Update();
        var token = consent.Issue(update);
        var wrong = (token[0] == '0' ? "1" : "0") + token[1..];

        Assert.Throws<InvalidOperationException>(() => consent.Take(wrong, "stable"));
        Assert.Same(update, consent.Take(token, "stable"));
    }

    [Fact]
    public void Uppercase_hex_is_either_equivalent_or_rejected_without_consuming_the_original_token()
    {
        var consent = Consent.Create(() => InitialTime);
        var update = Update();
        var token = consent.Issue(update);
        VerifiedUpdate taken = null;

        var error = Record.Exception(() => taken = consent.Take(token.ToUpperInvariant(), "stable"));

        if (error is null)
            Assert.Same(update, taken);
        else
        {
            Assert.IsType<InvalidOperationException>(error);
            Assert.Same(update, consent.Take(token, "stable"));
        }
    }

    [Theory]
    [InlineData("stable", "stable")]
    [InlineData("stable", "STABLE")]
    [InlineData("stable", null)]
    [InlineData("stable", "")]
    [InlineData("stable", "preview")]
    [InlineData("stable", " beta ")]
    [InlineData("beta", "beta")]
    [InlineData("beta", "BETA")]
    public void Take_normalizes_the_requested_channel_exactly_like_secure_update_service(
        string offeredChannel, string requestedChannel)
    {
        var consent = Consent.Create(() => InitialTime);
        var update = Update(offeredChannel);

        Assert.Same(update, consent.Take(consent.Issue(update), requestedChannel));
    }

    [Theory]
    [InlineData("stable", "beta")]
    [InlineData("beta", "stable")]
    [InlineData("beta", null)]
    public void Wrong_normalized_channel_rejects(string offeredChannel, string requestedChannel)
    {
        var consent = Consent.Create(() => InitialTime);
        var token = consent.Issue(Update(offeredChannel));

        Assert.Throws<InvalidOperationException>(() => consent.Take(token, requestedChannel));
    }

    [Fact]
    public void A_second_artifact_with_the_same_version_supersedes_the_first_exact_tuple()
    {
        var consent = Consent.Create(() => InitialTime);
        var first = Update();
        var second = first with
        {
            PackageUri = new Uri("https://github.com/UnknownGod2011/keyboard.wtf/releases/download/v9.0.0/other-synthetic-setup.exe"),
            Sha256 = new string('b', 64),
            SizeBytes = first.SizeBytes + 1,
            ReleaseNotes = "Different synthetic signed release notes",
            KeyId = "synthetic-other-signing-key",
            PublishedAt = first.PublishedAt.AddSeconds(1),
            MinimumVersion = new Version(1, 4, 18),
        };
        var oldToken = consent.Issue(first);
        var newToken = consent.Issue(second);

        Assert.Equal(first.Version, second.Version);
        Assert.NotEqual(oldToken, newToken);
        Assert.Throws<InvalidOperationException>(() => consent.Take(oldToken, "stable"));
        Assert.Same(second, consent.Take(newToken, "stable"));
    }

    [Fact]
    public void Reissuing_the_same_record_still_supersedes_the_old_token()
    {
        var consent = Consent.Create(() => InitialTime);
        var update = Update();
        var oldToken = consent.Issue(update);
        var newToken = consent.Issue(update);

        Assert.NotEqual(oldToken, newToken);
        Assert.Throws<InvalidOperationException>(() => consent.Take(oldToken, "stable"));
        Assert.Same(update, consent.Take(newToken, "stable"));
    }

    [Fact]
    public void Repeated_issues_keep_only_the_latest_offer_authorized_and_tokens_do_not_repeat()
    {
        var consent = Consent.Create(() => InitialTime);
        var issued = new List<(string Token, VerifiedUpdate Update)>();
        for (var index = 0; index < 32; index++)
        {
            var update = Update() with { ReleaseNotes = "Synthetic offer " + index };
            var token = consent.Issue(update);
            Assert.Matches(@"\A[0-9a-fA-F]{64}\z", token);
            issued.Add((token, update));
        }

        Assert.Equal(32, issued.Select(value => value.Token).Distinct(StringComparer.Ordinal).Count());
        foreach (var old in issued.Take(31))
            Assert.Throws<InvalidOperationException>(() => consent.Take(old.Token, "stable"));
        Assert.Same(issued[^1].Update, consent.Take(issued[^1].Token, "stable"));
    }

    [Fact]
    public void Clear_is_idempotent_invalidates_the_offer_and_allows_a_new_offer()
    {
        var consent = Consent.Create(() => InitialTime);
        var oldToken = consent.Issue(Update());

        consent.Clear();
        consent.Clear();
        Assert.Throws<InvalidOperationException>(() => consent.Take(oldToken, "stable"));
        var next = Update("beta");
        Assert.Same(next, consent.Take(consent.Issue(next), "beta"));
    }

    [Fact]
    public void Issue_null_clears_the_old_offer_and_returns_no_token()
    {
        var consent = Consent.Create(() => InitialTime);
        Assert.Null(consent.Issue(null));
        var oldToken = consent.Issue(Update());

        Assert.Null(consent.Issue(null));
        Assert.Throws<InvalidOperationException>(() => consent.Take(oldToken, "stable"));
        var next = Update();
        Assert.Same(next, consent.Take(consent.Issue(next), "stable"));
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(0L)]
    [InlineData(1L)]
    public void Default_lifetime_expires_at_exactly_thirty_minutes(long ticksFromBoundary)
    {
        var now = InitialTime;
        var consent = Consent.Create(() => now);
        var update = Update();
        var token = consent.Issue(update);
        now = InitialTime.AddMinutes(30).AddTicks(ticksFromBoundary);

        if (ticksFromBoundary < 0)
            Assert.Same(update, consent.Take(token, "stable"));
        else
            Assert.Throws<InvalidOperationException>(() => consent.Take(token, "stable"));
    }

    [Theory]
    [InlineData(59, true)]
    [InlineData(60, false)]
    public void A_shorter_configured_lifetime_is_honored(int elapsedSeconds, bool valid)
    {
        var now = InitialTime;
        var consent = Consent.Create(() => now, TimeSpan.FromMinutes(1));
        var update = Update();
        var token = consent.Issue(update);
        now = InitialTime.AddSeconds(elapsedSeconds);

        if (valid)
            Assert.Same(update, consent.Take(token, "stable"));
        else
            Assert.Throws<InvalidOperationException>(() => consent.Take(token, "stable"));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(18_000_000_001L)]
    [InlineData(long.MaxValue)]
    public void Invalid_lifetime_configuration_rejects(long lifetimeTicks)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Consent.Create(() => InitialTime, TimeSpan.FromTicks(lifetimeTicks)));
    }

    [Fact]
    public void Explicit_maximum_thirty_minute_lifetime_is_accepted()
    {
        var consent = Consent.Create(() => InitialTime, TimeSpan.FromMinutes(30));
        var update = Update();

        Assert.Same(update, consent.Take(consent.Issue(update), "stable"));
    }

    [Fact]
    public void Clock_before_issue_time_rejects_fail_closed()
    {
        var now = InitialTime;
        var consent = Consent.Create(() => now);
        var token = consent.Issue(Update());
        now = InitialTime.AddTicks(-1);

        Assert.Throws<InvalidOperationException>(() => consent.Take(token, "stable"));
    }

    [Fact]
    public void An_expired_offer_cannot_be_revived_by_clock_rollback()
    {
        var now = InitialTime;
        var consent = Consent.Create(() => now);
        var token = consent.Issue(Update());
        now = InitialTime.AddMinutes(30);
        Assert.Throws<InvalidOperationException>(() => consent.Take(token, "stable"));
        now = InitialTime.AddMinutes(1);

        Assert.Throws<InvalidOperationException>(() => consent.Take(token, "stable"));
    }

    [Fact]
    public async Task Concurrent_take_has_exactly_one_winner_for_each_offer()
    {
        var consent = Consent.Create(() => InitialTime);
        for (var cycle = 0; cycle < 10; cycle++)
        {
            var update = Update() with { ReleaseNotes = "Synthetic concurrent offer " + cycle };
            var token = consent.Issue(update);
            var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var attempts = Enumerable.Range(0, 8).Select(async _ =>
            {
                await start.Task;
                try { return (Update: consent.Take(token, "stable"), Rejected: false); }
                catch (InvalidOperationException) { return (Update: (VerifiedUpdate)null, Rejected: true); }
            }).ToArray();
            start.SetResult(true);

            var results = await Task.WhenAll(attempts);

            Assert.Same(update, Assert.Single(results, result => !result.Rejected).Update);
            Assert.Equal(7, results.Count(result => result.Rejected));
            Assert.All(results.Where(result => result.Rejected), result => Assert.Null(result.Update));
            Assert.Throws<InvalidOperationException>(() => consent.Take(token, "stable"));
        }
    }

    private static VerifiedUpdate Update(string channel = "stable") => new(
        new Version(9, 0, 0), new Version(1, 4, 17), channel, InitialTime.AddDays(-1),
        new Uri("https://github.com/UnknownGod2011/keyboard.wtf/releases/download/v9.0.0/synthetic-setup.exe"),
        new string('a', 64), 1024, "Synthetic signed release notes", "synthetic-signing-key");

    private sealed class Consent
    {
        private readonly object _instance;
        private readonly MethodInfo _issue;
        private readonly MethodInfo _take;
        private readonly MethodInfo _clear;

        private Consent(object instance, Type type)
        {
            _instance = instance;
            _issue = RequireMethod(type, "Issue", typeof(string), typeof(VerifiedUpdate));
            _take = RequireMethod(type, "Take", typeof(VerifiedUpdate), typeof(string), typeof(string));
            _clear = RequireMethod(type, "Clear", typeof(void));
        }

        internal static Consent Create(Func<DateTimeOffset> utcNow = null, TimeSpan? lifetime = null)
        {
            var type = typeof(VerifiedUpdate).Assembly.GetType(
                "KeyboardWtf.Services.Updates.VerifiedUpdateConsent", throwOnError: false);
            Assert.True(type is not null, "The internal VerifiedUpdateConsent implementation is missing.");
            var constructor = type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null, types: new[] { typeof(Func<DateTimeOffset>), typeof(TimeSpan?) }, modifiers: null);
            Assert.True(constructor is not null, "VerifiedUpdateConsent must expose the planned clock/lifetime constructor.");
            var instance = Invoke(() => constructor.Invoke(new object[] { utcNow, lifetime }));
            return new Consent(instance, type);
        }

        internal string Issue(VerifiedUpdate update) => (string)Invoke(() => _issue.Invoke(_instance, new object[] { update }));
        internal VerifiedUpdate Take(string token, string channel) =>
            (VerifiedUpdate)Invoke(() => _take.Invoke(_instance, new object[] { token, channel }));
        internal void Clear() => Invoke(() => _clear.Invoke(_instance, Array.Empty<object>()));

        private static MethodInfo RequireMethod(Type type, string name, Type returnType, params Type[] parameters)
        {
            var method = type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null, types: parameters, modifiers: null);
            Assert.True(method is not null, "VerifiedUpdateConsent is missing its planned " + name + " method.");
            Assert.Equal(returnType, method.ReturnType);
            return method;
        }

        private static object Invoke(Func<object> action)
        {
            try { return action(); }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
        }
    }
}
