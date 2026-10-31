using System.Net;
using System.Net.Http;
using KeyboardWtf.Services.Ai.Backends;
using Xunit;

namespace KeyboardWtf.Tests;

public sealed class PersonalCredentialRetryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "personal-retry-" + Guid.NewGuid());
    private readonly PersonalApiVault _vault;
    public PersonalCredentialRetryTests()
    {
        _vault = new(Path.Combine(_directory, "credentials.bin"));
        var view = _vault.GetView();
        for (var i = 0; i < 4; i++)
            view = _vault.Add(view.Revision, "fake " + i, "synthetic-retry-key-000" + i, PersonalKeyValidation.AuthenticationValidated);
        _vault.SetPolicy(view.Revision, AiBackendPolicy.PersonalOnly);
    }

    [Fact]
    public async Task Dns_failure_retries_same_key_before_success_without_managed_calls()
    {
        var seen = new List<string>();
        using var transport = Transport(request =>
        {
            seen.Add(Key(request));
            if (seen.Count == 1) throw new HttpRequestException(HttpRequestError.NameResolutionError);
            return Complete();
        });
        var delays = new List<TimeSpan>();
        var router = new AiBackendRouter(_vault, _vault, (delay, _) => { delays.Add(delay); return Task.CompletedTask; });
        using var operation = router.BeginOperation();
        await Run(router, operation, transport);
        Assert.Equal(2, seen.Count);
        Assert.Equal(seen[0], seen[1]);
        Assert.Single(delays);
    }

    [Fact]
    public async Task Retry_budget_is_three_total_not_three_per_key()
    {
        var calls = 0;
        var seen = new List<string>();
        using var transport = Transport(request =>
        {
            seen.Add(Key(request));
            if (++calls == 1) throw new HttpRequestException(HttpRequestError.NameResolutionError);
            return new(HttpStatusCode.Unauthorized);
        });
        var router = new AiBackendRouter(_vault, _vault, (_, _) => Task.CompletedTask);
        using var operation = router.BeginOperation();
        Assert.Equal(PersonalAiFailure.Credential, (await Assert.ThrowsAsync<PersonalAiException>(() => Run(router, operation, transport))).Failure);
        Assert.Equal(3, calls);
        Assert.Equal(seen[0], seen[1]);
        Assert.NotEqual(seen[1], seen[2]);
        Assert.Equal(2, _vault.GetView().Keys.Count(key => !key.Enabled));
    }

    [Fact]
    public async Task Definitive_rejection_selects_next_key_and_freezes_it_for_secondary_and_reconnect()
    {
        var seen = new List<string>();
        using var transport = Transport(request =>
        {
            seen.Add(Key(request));
            return seen.Count is 1 or 4 ? new(HttpStatusCode.Unauthorized) : Complete();
        });
        var router = new AiBackendRouter(_vault, _vault);
        using var operation = router.BeginOperation();
        await Run(router, operation, transport);
        using var child = operation.CreateChild();
        await Run(router, child, transport);
        await Assert.ThrowsAsync<PersonalAiException>(() => Run(router, operation, transport));
        Assert.Equal(4, seen.Count); // No fifth call with another key after progress.
        Assert.NotEqual(seen[0], seen[1]);
        Assert.All(seen.Skip(1), value => Assert.Equal(seen[1], value));
    }

    [Theory]
    [InlineData(429)]
    [InlineData(503)]
    [InlineData(403)]
    public async Task Quota_service_or_capability_failure_does_not_migrate_keys(int status)
    {
        var calls = 0;
        using var transport = Transport(_ => { calls++; return new((HttpStatusCode)status) { Content = new StringContent("{}") }; });
        var router = new AiBackendRouter(_vault, _vault);
        using var operation = router.BeginOperation();
        await Assert.ThrowsAsync<PersonalAiException>(() => Run(router, operation, transport));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Project_access_denial_is_not_retried_rotated_or_misclassified_as_invalid_key()
    {
        // Exact sanitized shape observed in the authorized Personal-only smoke.
        var calls = 0;
        using var transport = Transport(_ =>
        {
            calls++;
            return new(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("""{"error":{"code":403,"message":"Your project has been denied access. Please contact support.","status":"PERMISSION_DENIED"}}"""),
            };
        });
        var router = new AiBackendRouter(_vault, _vault, (_, _) => throw new Xunit.Sdk.XunitException("No retry for project denial"));
        using var operation = router.BeginOperation();
        var originalSlot = operation.Binding.Credential.SlotId;
        var error = await Assert.ThrowsAsync<PersonalAiException>(() => Run(router, operation, transport));
        Assert.Equal(PersonalAiFailure.Capability, error.Failure);
        Assert.Equal(1, calls);
        Assert.Equal(originalSlot, operation.Binding.Credential.SlotId);
        Assert.All(_vault.GetView().Keys, key => Assert.True(key.Enabled && key.AuthenticationValidated));
        Assert.Null(_vault.GetView().CooldownUntil);
    }

    [Fact]
    public async Task Cancellation_during_backoff_prevents_second_attempt()
    {
        var calls = 0;
        using var cancellation = new CancellationTokenSource();
        using var transport = Transport(_ => { calls++; throw new HttpRequestException(HttpRequestError.NameResolutionError); });
        var router = new AiBackendRouter(_vault, _vault, (_, token) =>
        { cancellation.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; });
        using var operation = router.BeginOperation(cancellation.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(router, operation, transport));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task All_initial_attempts_share_one_deadline()
    {
        var calls = 0;
        using var transport = Transport(_ => { calls++; throw new HttpRequestException(HttpRequestError.NameResolutionError); });
        var router = new AiBackendRouter(_vault, _vault, (_, token) => Task.Delay(Timeout.Infinite, token), TimeSpan.FromMilliseconds(30));
        using var operation = router.BeginOperation();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(router, operation, transport));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Rotation_does_not_skip_healthy_earlier_slots_or_accept_unknown_rejected_identity()
    {
        var config = _vault.ReadConfiguration();
        var keys = _vault.GetView().Keys;
        using var last = _vault.AcquireForValidation(config.Revision, keys[3].Id, keys[3].Revision);
        _vault.RecordCredentialRejection(last);
        using var next = _vault.AcquireNext(config, last.SlotId);
        Assert.Equal(keys[0].Id, next.SlotId);
        Assert.Throws<PersonalApiConfigurationException>(() => _vault.AcquireNext(config, Guid.NewGuid()));
    }

    private PersonalGeminiHttpTransport Transport(Func<HttpRequestMessage, HttpResponseMessage> send) => new(_vault, new Handler(send));
    private static string Key(HttpRequestMessage request) => Assert.Single(request.Headers.GetValues("x-goog-api-key"));
    private static HttpResponseMessage Complete() => new(HttpStatusCode.OK) { Content = new StringContent("{\"candidates\":[{\"finishReason\":\"STOP\",\"content\":{\"parts\":[{\"text\":\"ok\"}]}}]}") };
    private static Task<PersonalGenerationResult> Run(AiBackendRouter router, AiBackendOperation operation, PersonalGeminiHttpTransport transport) =>
        router.ExecuteAsync(operation, AiCapability.Text,
            _ => throw new Xunit.Sdk.XunitException("Managed is forbidden"),
            (key, model, token) => transport.GenerateAsync(key, model, new("test", null, 16), token));
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(send(request));
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
