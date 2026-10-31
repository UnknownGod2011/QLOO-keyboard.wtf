using System.Net;
using System.Text.Json;
using KeyboardWtf.Staging.Api.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace KeyboardWtf.Staging.Api.Tests;

public sealed class LumaLiveTokenTests
{
    [Fact]
    public void Grant_serialization_uses_the_exact_browser_wire_contract()
    {
        var now = new Clock().UtcNow;
        var grant = new LumaTokenGrant("auth_tokens/synthetic-wire-contract", "gemini-3.8-live",
            now.AddSeconds(120), now.AddSeconds(45), 120, LumaLiveTokenService.WebSocketUrl);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(grant,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var root = json.RootElement;
        Assert.Equal(LumaLiveTokenService.WebSocketUrl, root.GetProperty("websocketUrl").GetString());
        Assert.False(root.TryGetProperty("webSocketUrl", out _));
        Assert.Equal(new[] { "expiresAt", "model", "newSessionExpiresAt", "sessionSeconds", "token", "websocketUrl" },
            root.EnumerateObject().Select(property => property.Name).OrderBy(name => name).ToArray());
        Assert.DoesNotContain(grant.Token, grant.ToString());
    }

    private static LumaLiveOptions Safe() => new()
    {
        Enabled = true, ModelEligibilityVerified = true, RetentionPolicyVerified = true,
        AllowedOrigins = ["https://keyboard.wtf"],
    };

    [Theory]
    [InlineData("disabled")]
    [InlineData("model-unverified")]
    [InlineData("ttl-unverified")]
    [InlineData("secret-missing")]
    [InlineData("origin-insecure")]
    public async Task Disabled_or_missing_public_security_gate_does_not_admit_or_dispatch(string gate)
    {
        var options = Safe();
        var secret = new Secret();
        if (gate == "disabled") options.Enabled = false;
        if (gate == "model-unverified") options.ModelEligibilityVerified = false;
        if (gate == "ttl-unverified") options.RetentionPolicyVerified = false;
        if (gate == "secret-missing") secret.Value = null;
        if (gate == "origin-insecure") options.AllowedOrigins = ["http://keyboard.wtf"];
        var admission = new Admission();
        var handler = new Handler();
        var service = new LumaLiveTokenService(new HttpClient(handler), secret, admission, Options.Create(options), new Clock());
        Assert.False(service.Available);
        await Assert.ThrowsAsync<LumaVoiceUnavailableException>(() => service.IssueAsync("synthetic-owner", "overview", CancellationToken.None));
        Assert.Equal(0, handler.Calls);
        Assert.Equal(0, admission.Calls);
    }

    [Fact]
    public async Task Public_grant_commits_durable_admission_before_one_constrained_mint_no_user_billing()
    {
        var admission = new Admission();
        var handler = new Handler { Admission = admission };
        var clock = new Clock();
        var service = new LumaLiveTokenService(new HttpClient(handler), new Secret(), admission, Options.Create(Safe()), clock);
        var grant = await service.IssueAsync("synthetic-owner", "installation", CancellationToken.None);
        Assert.Equal(1, admission.Calls);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(clock.UtcNow.AddSeconds(120), grant.ExpiresAt);
        Assert.Equal(clock.UtcNow.AddSeconds(45), grant.NewSessionExpiresAt);
        Assert.DoesNotContain(grant.Token, grant.ToString());
        Assert.DoesNotContain("synthetic-owner", handler.Body);
        Assert.DoesNotContain(Secret.Synthetic, handler.Body);
        using var json = JsonDocument.Parse(handler.Body!);
        var root = json.RootElement;
        Assert.Equal(1, root.GetProperty("uses").GetInt32());
        Assert.False(root.TryGetProperty("fieldMask", out _));
        Assert.False(root.TryGetProperty("liveConnectConstraints", out _));
        var setup = root.GetProperty("bidiGenerateContentSetup");
        Assert.Empty(setup.GetProperty("tools").EnumerateArray());
        Assert.Equal(512, setup.GetProperty("generationConfig").GetProperty("maxOutputTokens").GetInt32());
        Assert.Contains("cannot run actions", setup.GetProperty("systemInstruction").GetRawText());
        Assert.Contains("keep this welcome under 25 words", setup.GetProperty("systemInstruction").GetRawText());
        Assert.Contains("Give detailed guidance when asked", setup.GetProperty("systemInstruction").GetRawText());
        Assert.Contains("https://chromewebstore.google.com/detail/gojdebdikepkpapmnggeljneamjdigbh", setup.GetProperty("systemInstruction").GetRawText());
        Assert.DoesNotContain("Then wait", setup.GetProperty("systemInstruction").GetRawText());
        Assert.DoesNotContain("thinkingConfig", root.GetRawText());
        Assert.DoesNotContain("proactiveAudio", root.GetRawText());
    }

    [Theory]
    [InlineData(30)]
    [InlineData(120)]
    [InlineData(300)]
    public void Connect_window_is_always_inside_bounded_session_expiry(int seconds)
    {
        var now = new Clock().UtcNow;
        using var json = JsonDocument.Parse(LumaLiveTokenService.CreateTokenRequest("gemini-3.8-live", "overview", now, seconds));
        var root = json.RootElement;
        Assert.True(DateTimeOffset.Parse(root.GetProperty("newSessionExpireTime").GetString()!)
            < DateTimeOffset.Parse(root.GetProperty("expireTime").GetString()!));
    }

    [Theory]
    [InlineData(429)]
    [InlineData(503)]
    [InlineData(401)]
    public async Task Ambiguous_or_failed_mint_keeps_admission_no_replay_release_or_provider_body_echo(int status)
    {
        var admission = new Admission();
        var handler = new Handler { Admission = admission, Status = (HttpStatusCode)status };
        var service = new LumaLiveTokenService(new HttpClient(handler), new Secret(), admission, Options.Create(Safe()), new Clock());
        var exception = await Assert.ThrowsAsync<LumaVoiceUnavailableException>(() => service.IssueAsync("synthetic-owner", "support", CancellationToken.None));
        Assert.Equal(1, admission.Calls);
        Assert.Equal(1, handler.Calls);
        Assert.DoesNotContain(Secret.Synthetic, exception.Message);
    }

    [Fact]
    public async Task Rejected_admission_is_zero_provider_calls()
    {
        var admission = new Admission { Reject = true };
        var handler = new Handler();
        var service = new LumaLiveTokenService(new HttpClient(handler), new Secret(), admission, Options.Create(Safe()), new Clock());
        await Assert.ThrowsAsync<LumaVoiceUnavailableException>(() => service.IssueAsync("synthetic-owner", "support", CancellationToken.None));
        Assert.Equal(0, handler.Calls);
    }

    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.Parse("2026-10-06T12:00:00Z");
    }
    private sealed class Secret : ILumaServerCredential
    {
        internal const string Synthetic = "synthetic-only-no-provider-credential";
        internal string? Value = Synthetic;
        public string? GetSecret() => Value;
    }
    private sealed class Admission : ILumaGrantAdmission
    {
        internal int Calls;
        internal bool Reject;
        public Task AdmitAsync(string userId, DateTimeOffset now, DateTimeOffset expiresAt, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Calls++;
            if (Reject) throw new LumaVoiceUnavailableException("Synthetic admission limit.");
            return Task.CompletedTask;
        }
    }
    private sealed class Handler : HttpMessageHandler
    {
        internal int Calls;
        internal string? Body;
        internal Admission? Admission;
        internal HttpStatusCode Status = HttpStatusCode.OK;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal(1, Admission?.Calls ?? 1);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1beta/auth_tokens", request.RequestUri!.AbsolutePath);
            Assert.Equal(Secret.Synthetic, request.Headers.GetValues("x-goog-api-key").Single());
            Calls++;
            Body = await request.Content!.ReadAsStringAsync(token);
            return new(Status)
            {
                Content = new StringContent(Status == HttpStatusCode.OK
                    ? "{\"name\":\"auth_tokens/synthetic-public-token\"}" : Secret.Synthetic),
            };
        }
    }
}
