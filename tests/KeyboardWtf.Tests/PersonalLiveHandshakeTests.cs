using System.Net;
using System.Net.WebSockets;
using KeyboardWtf.Services.Ai.Backends;
using KeyboardWtf.Services.PaidStaging;
using Xunit;

namespace KeyboardWtf.Tests;

public sealed class PersonalLiveHandshakeTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "personal-live-handshake-" + Guid.NewGuid());
    private readonly PersonalApiVault _vault;
    public PersonalLiveHandshakeTests()
    {
        _vault = new(Path.Combine(_directory, "keys.bin"));
        var view = _vault.Add(0, "fake", "synthetic-live-credential-0000", PersonalKeyValidation.AuthenticationValidated);
        _vault.SetPolicy(view.Revision, AiBackendPolicy.PersonalOnly);
    }

    [Theory]
    [InlineData(401, 0)]
    [InlineData(403, 1)]
    [InlineData(404, 1)]
    [InlineData(429, 2)]
    [InlineData(503, 8)]
    [InlineData(502, 8)]
    [InlineData(504, 8)]
    [InlineData(302, 3)]
    [InlineData(400, 3)]
    public void Only_pre_setup_temporary_handshake_failure_is_retryable(int status, int failure) =>
        Assert.Equal((PersonalAiFailure)failure, PersonalGeminiLiveTransport.ClassifyHandshakeFailure((HttpStatusCode)status));

    [Fact]
    public async Task Pre_setup_connect_failure_retries_same_key_and_never_starts_managed()
    {
        var attempts = 0;
        var slots = new List<Guid>();
        var failedSockets = new List<ClientWebSocket>();
        using var live = new PersonalGeminiLiveTransport(_vault, (socket, endpoint, _, _) =>
        {
            Assert.Empty(endpoint.Query);
            if (++attempts < 3)
            {
                failedSockets.Add(socket);
                throw new WebSocketException("synthetic secret must not escape");
            }
            return Task.CompletedTask;
        });
        var router = new AiBackendRouter(_vault, _vault, (_, _) => Task.CompletedTask);
        using var operation = router.BeginOperation();
        using var connected = await router.ExecuteAsync(operation, AiCapability.Live,
            _ => throw new Xunit.Sdk.XunitException("Managed forbidden"), (key, model, token) =>
            { slots.Add(key.SlotId); return live.ConnectAsync(key, model, token); });
        Assert.Equal(3, attempts);
        Assert.Single(slots.Distinct());
        Assert.All(failedSockets, socket => Assert.Equal(WebSocketState.Closed, socket.State));
    }

    [Fact]
    public async Task Cancelled_connect_does_not_return_a_late_socket()
    {
        using var cancellation = new CancellationTokenSource();
        ClientWebSocket captured = null;
        using var live = new PersonalGeminiLiveTransport(_vault, (socket, _, _, _) =>
        { captured = socket; cancellation.Cancel(); return Task.CompletedTask; });
        using var key = _vault.Acquire(_vault.ReadConfiguration());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => live.ConnectAsync(key, PersonalGeminiModels.Live, cancellation.Token));
        Assert.Equal(WebSocketState.Closed, captured.State);
    }

    [Fact]
    public async Task Unconfirmed_connect_error_is_sanitized_without_inner_secret()
    {
        using var live = new PersonalGeminiLiveTransport(_vault, (_, _, _, _) =>
            throw new WebSocketException("synthetic-secret-marker"));
        using var key = _vault.Acquire(_vault.ReadConfiguration());
        var error = await Assert.ThrowsAsync<PersonalAiException>(() => live.ConnectAsync(key, PersonalGeminiModels.Live, default));
        Assert.DoesNotContain("synthetic-secret-marker", error.ToString());
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData(402, "allowance-pre-dispatch-v1", true)]
    [InlineData(409, "allowance-pre-dispatch-v1", false)]
    [InlineData(402, "allowance-pre-dispatch-v0", false)]
    [InlineData(429, "allowance-pre-dispatch-v1", false)]
    public void Failed_managed_live_handshake_requires_exact_typed_contract(int status, string contract, bool expected) =>
        Assert.Equal(expected, PaidAccountService.IsInitialAllowanceRefusal((HttpStatusCode)status,
            new[] { new KeyValuePair<string, IEnumerable<string>>("X-Keyboard-Wtf-AI-Refusal", new[] { contract }) }));

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
