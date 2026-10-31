using System.Reflection;
using KeyboardWtf.Services;
using KeyboardWtf.Services.Ai.Backends;
using KeyboardWtf.Services.Connectors;
using Xunit;

namespace KeyboardWtf.Tests;

public sealed class ConnectorCountOwnerTests
{
    private const string Session = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static ConnectorCountInterpretationInput Input(string session = Session) => new(
        "Count winning notification emails", [new("Example <updates@example.test>", "You are a winner")], session);

    [Fact]
    public async Task No_active_live_owner_cannot_open_a_new_ai_route_for_connector_recovery()
    {
        using var service = new GeminiLiveConversationService(null, null, null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.InterpretConnectorMailCountAsync(Input(), default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_stale_or_ended_session_cannot_borrow_another_bound_owner(bool ended)
    {
        using var session = new CancellationTokenSource();
        var router = new AiBackendRouter(new Config(), new Pool());
        using var owner = router.BeginOperation(session.Token);
        using var service = new GeminiLiveConversationService(null, null, null);
        Set(service, "_backendOperation", owner); Set(service, "_sessionCts", session); Set(service, "_logicalSessionId", Session);
        Assert.True(service.HasBoundSession);
        if (ended) session.Cancel();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.InterpretConnectorMailCountAsync(
            Input(ended ? Session : "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"), default));
    }

    [Fact]
    public async Task Cancelled_connector_input_is_rejected_before_any_account_or_ai_lookup()
    {
        using var service = new GeminiLiveConversationService(null, null, null);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.InterpretConnectorMailCountAsync(Input(), cancellation.Token));
    }

    private static void Set(object target, string field, object value) => target.GetType()
        .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
    private sealed class Config : IAiBackendConfigurationSource
    {
        public AiBackendConfiguration ReadConfiguration() => new(1, AiBackendPolicy.PersonalOnly);
    }
    private sealed class Pool : IPersonalCredentialPool
    {
        public IPersonalCredentialLease Acquire(AiBackendConfiguration expectedConfiguration) => new Lease();
        private sealed class Lease : IPersonalCredentialLease
        {
            public Guid SlotId { get; } = Guid.NewGuid();
            public long CredentialRevision => 1;
            public void Dispose() { }
        }
    }
}
