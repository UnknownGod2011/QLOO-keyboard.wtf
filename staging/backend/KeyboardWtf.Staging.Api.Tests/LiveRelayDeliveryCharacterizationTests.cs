using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using KeyboardWtf.Staging.Api.Services;
using Xunit;

namespace KeyboardWtf.Staging.Api.Tests;

// These fixtures execute the exact private production relay with in-memory peers.
// They characterize local ordering, not provider acceptance, audible delivery,
// complete invoices or authority to settle any real account/reservation.
[Trait("Category", "LiveBoundaryCharacterization")]
public sealed class LiveRelayDeliveryCharacterizationTests
{
    private const string Question = """{"clientContent":{"turns":[{"parts":[{"text":"arithmetic"}]}],"turnComplete":true}}""";
    private const string InputAudio = """{"realtimeInput":{"audio":{"data":"AQID"}}}""";
    private const string TerminalReceipt = """{"serverContent":{"modelTurn":{"parts":[{"inlineData":{"data":"AQID"}}]},"turnComplete":true},"usageMetadata":{"promptTokenCount":92,"candidatesTokenCount":58,"totalTokenCount":150,"promptTokensDetails":[{"modality":"TEXT","tokenCount":92}],"candidatesTokensDetails":[{"modality":"TEXT","tokenCount":6},{"modality":"AUDIO","tokenCount":52}]}}""";
    private static readonly TimeSpan TestDeadline = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Relay_waits_for_observation_before_attempting_one_successful_forward()
    {
        using var source = Peer.WithText(Question);
        using var destination = new Peer { CloseAfterSuccessfulSend = true };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var usage = new LiveUsageAccumulator(1_000_000);
        var observations = 0;
        var relay = Relay(source, destination, false, async (message, token) =>
        {
            usage.ObserveClient(message);
            observations++;
            entered.SetResult();
            await release.Task.WaitAsync(token);
        });
        try
        {
            await entered.Task.WaitAsync(TestDeadline);
            Assert.Equal(1, observations);
            Assert.Equal(1, usage.CloseoutEvidence().InputGeneration);
            Assert.Equal(0, destination.SendAttempts);
            Assert.Empty(destination.Sent);
        }
        finally { release.TrySetResult(); }

        await relay.WaitAsync(TestDeadline);
        Assert.Equal(1, observations);
        Assert.Equal(1, source.ReceiveAttempts);
        Assert.Equal(1, destination.SendAttempts);
        var sent = Assert.Single(destination.Sent);
        Assert.Equal(Question, Encoding.UTF8.GetString(sent.Bytes));
        Assert.Equal(WebSocketMessageType.Text, sent.Type);
        Assert.True(sent.EndOfMessage);
        Assert.False(usage.Snapshot().Known); // A successful send is not a provider receipt.
    }

    [Theory]
    [InlineData("text", "send-failure")]
    [InlineData("audio", "send-failure")]
    [InlineData("text", "cancel-during-send")]
    [InlineData("audio", "cancel-during-send")]
    public async Task Failed_or_cancelled_forward_retains_observed_input_without_provider_acceptance(string input, string failure)
    {
        var message = input == "text" ? Question : InputAudio;
        using var source = Peer.WithText(message);
        using var cancellation = new CancellationTokenSource();
        using var destination = new Peer
        {
            SendBehavior = (_, token) =>
            {
                if (failure == "cancel-during-send")
                {
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                }
                return Task.FromException(new WebSocketException("Synthetic send failure."));
            },
        };
        var usage = new LiveUsageAccumulator(1_000_000);
        var observations = 0;

        var relay = Relay(source, destination, false, (observed, _) =>
        {
            usage.ObserveClient(observed);
            observations++;
            return Task.CompletedTask;
        }, cancellation.Token);
        if (failure == "cancel-during-send")
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => relay.WaitAsync(TestDeadline));
        else await Assert.ThrowsAsync<WebSocketException>(() => relay.WaitAsync(TestDeadline));

        Assert.Equal(1, observations);
        Assert.Equal(1, destination.SendAttempts);
        Assert.Empty(destination.Sent);
        Assert.Equal(input == "audio" ? 3 : 0, usage.CloseoutEvidence().InputAudioBytes);
        Assert.Equal(input == "text" ? 1 : 0, usage.CloseoutEvidence().InputGeneration);
        Assert.False(usage.Snapshot().Known);
        Assert.True(usage.Snapshot().Estimated);
        Assert.Equal(LiveAccountingDisposition.Reconcile, Disposition(usage));
        // Observation precedes the transport attempt. It is not an ACK that these
        // client bytes reached Gemini or belong in its final invoice.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Budget_or_cancellation_failure_in_observer_prevents_all_forwarding(bool cancelled)
    {
        using var source = Peer.WithText(InputAudio);
        using var destination = new Peer();
        using var cancellation = new CancellationTokenSource();
        var usage = new LiveUsageAccumulator(1_000_000);
        var observations = 0;
        var relay = Relay(source, destination, false, (message, token) =>
        {
            usage.ObserveClient(message);
            observations++;
            if (cancelled)
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
            }
            throw new InvalidOperationException("Synthetic approved-budget boundary.");
        }, cancellation.Token);

        if (cancelled) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => relay.WaitAsync(TestDeadline));
        else await Assert.ThrowsAsync<InvalidOperationException>(() => relay.WaitAsync(TestDeadline));
        Assert.Equal(1, observations);
        Assert.Equal(3, usage.CloseoutEvidence().InputAudioBytes);
        Assert.Equal(0, destination.SendAttempts);
        Assert.Empty(destination.Sent);
        Assert.False(usage.Snapshot().Known);
    }

    [Fact]
    public async Task Precancelled_relay_does_not_receive_observe_or_send()
    {
        using var source = Peer.WithText(Question);
        using var destination = new Peer();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var observations = 0;

        await Relay(source, destination, false, (_, _) => { observations++; return Task.CompletedTask; }, cancellation.Token);

        Assert.Equal(0, source.ReceiveAttempts);
        Assert.Equal(0, observations);
        Assert.Equal(0, destination.SendAttempts);
    }

    [Theory]
    [InlineData(WebSocketMessageType.Text, false)]
    [InlineData(WebSocketMessageType.Text, true)]
    [InlineData(WebSocketMessageType.Binary, true)]
    public async Task Complete_fragmented_utf8_is_observed_once_and_forwarded_as_one_text_message(WebSocketMessageType type, bool acceptBinary)
    {
        const string message = """{"clientContent":{"turns":[{"parts":[{"text":"crystal 雪 🌙"}]}],"turnComplete":true}}""";
        var bytes = Encoding.UTF8.GetBytes(message);
        var multibyteAt = Array.IndexOf(bytes, (byte)0xe9);
        Assert.True(multibyteAt > 0);
        using var source = new Peer();
        source.Add(bytes[..(multibyteAt + 1)], type, false);
        source.Add(bytes[(multibyteAt + 1)..(multibyteAt + 2)], type, false);
        source.Add(bytes[(multibyteAt + 2)..], type, true);
        using var destination = new Peer { CloseAfterSuccessfulSend = true };
        var observed = new List<string>();

        await Relay(source, destination, acceptBinary, (text, _) => { observed.Add(text); return Task.CompletedTask; });

        Assert.Equal(message, Assert.Single(observed));
        Assert.Equal(3, source.ReceiveAttempts);
        Assert.Equal(1, destination.SendAttempts);
        var sent = Assert.Single(destination.Sent);
        Assert.Equal(bytes, sent.Bytes);
        Assert.Equal(WebSocketMessageType.Text, sent.Type);
        Assert.True(sent.EndOfMessage);
    }

    [Theory]
    [InlineData("binary-client")]
    [InlineData("mixed-fragments")]
    [InlineData("invalid-utf8")]
    [InlineData("oversize")]
    public async Task Rejected_message_is_never_observed_or_forwarded(string defect)
    {
        using var source = new Peer();
        if (defect == "binary-client") source.Add(Encoding.UTF8.GetBytes(Question), WebSocketMessageType.Binary, true);
        else if (defect == "mixed-fragments")
        {
            source.Add(Encoding.UTF8.GetBytes("{"), WebSocketMessageType.Text, false);
            source.Add(Encoding.UTF8.GetBytes("}"), WebSocketMessageType.Binary, true);
        }
        else if (defect == "invalid-utf8") source.Add([0xc3, 0x28], WebSocketMessageType.Text, true);
        else
        {
            var maximum = (int)typeof(VertexLiveProxyService).GetField("MaxMessageBytes", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!;
            var remaining = maximum + 1;
            while (remaining > 0)
            {
                var count = Math.Min(64 * 1024, remaining);
                remaining -= count;
                source.Add(new byte[count], WebSocketMessageType.Text, remaining == 0);
            }
        }
        using var destination = new Peer();
        var observations = 0;

        await Assert.ThrowsAsync<ArgumentException>(() => Relay(source, destination, defect != "binary-client",
            (_, _) => { observations++; return Task.CompletedTask; }).WaitAsync(TestDeadline));

        Assert.Equal(0, observations);
        Assert.Equal(0, destination.SendAttempts);
        Assert.Empty(destination.Sent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Peer_close_before_complete_message_discards_partial_content(bool fragmented)
    {
        using var source = new Peer();
        if (fragmented) source.Add(Encoding.UTF8.GetBytes(Question[..20]), WebSocketMessageType.Text, false);
        source.Add([], WebSocketMessageType.Close, true);
        using var destination = new Peer();
        var observations = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Relay(source, destination, true,
            (_, _) => { observations++; return Task.CompletedTask; }).WaitAsync(TestDeadline));

        Assert.Equal(fragmented ? 2 : 1, source.ReceiveAttempts);
        Assert.Equal(0, observations);
        Assert.Equal(0, destination.SendAttempts);
    }

    [Theory]
    [InlineData("send-failure")]
    [InlineData("cancel-during-send")]
    [InlineData("observer-budget-failure")]
    public async Task Provider_usage_is_observed_even_when_client_delivery_never_completes(string failure)
    {
        using var provider = Peer.WithText(TerminalReceipt);
        using var cancellation = new CancellationTokenSource();
        using var client = new Peer
        {
            SendBehavior = (_, token) =>
            {
                if (failure == "cancel-during-send")
                {
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                }
                return Task.FromException(new WebSocketException("Synthetic client delivery failure."));
            },
        };
        var usage = new LiveUsageAccumulator(1_000_000);
        usage.ObserveClient(Question);
        var observations = 0;
        var relay = Relay(provider, client, true, (message, _) =>
        {
            usage.ObserveProvider(message);
            observations++;
            if (failure == "observer-budget-failure")
                throw new InvalidOperationException("Synthetic observer budget rejection.");
            return Task.CompletedTask;
        }, cancellation.Token);

        if (failure == "cancel-during-send") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => relay.WaitAsync(TestDeadline));
        else if (failure == "observer-budget-failure") await Assert.ThrowsAsync<InvalidOperationException>(() => relay.WaitAsync(TestDeadline));
        else await Assert.ThrowsAsync<WebSocketException>(() => relay.WaitAsync(TestDeadline));

        Assert.Equal(1, observations);
        Assert.Equal(failure == "observer-budget-failure" ? 0 : 1, client.SendAttempts);
        Assert.Empty(client.Sent);
        Assert.Equal(3, usage.CloseoutEvidence().OutputAudioBytes);
        Assert.Equal(150, usage.Snapshot().TotalTokens);
        Assert.True(usage.Snapshot().Known);
        Assert.False(usage.Snapshot().Estimated);
        Assert.Equal(LiveAccountingDisposition.Settle, Disposition(usage));
        // The current disposition follows its synthetic complete provider vector,
        // not successful downstream delivery. It neither proves the user heard
        // anything nor settles a real account or validates close-time finality.
    }

    private static LiveAccountingDisposition Disposition(LiveUsageAccumulator usage) =>
        VertexLiveProxyService.DetermineAccountingDisposition(true, usage.Snapshot().TotalTokens, usage.Snapshot().Known);

    private static Task Relay(WebSocket source, WebSocket destination, bool acceptBinary,
        Func<string, CancellationToken, Task> observer, CancellationToken token = default)
    {
        var method = typeof(VertexLiveProxyService).GetMethod("RelayAsync", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsAssignableFrom<Task>(method.Invoke(null, [source, destination, acceptBinary, observer, token]));
    }

    private sealed record Frame(byte[] Bytes, WebSocketMessageType Type, bool EndOfMessage);

    private sealed class Peer : WebSocket
    {
        private readonly Queue<Frame> _frames = [];
        private WebSocketState _state = WebSocketState.Open;
        internal int ReceiveAttempts { get; private set; }
        internal int SendAttempts { get; private set; }
        internal List<Frame> Sent { get; } = [];
        internal bool CloseAfterSuccessfulSend { get; init; }
        internal Func<byte[], CancellationToken, Task>? SendBehavior { get; init; }
        public override WebSocketCloseStatus? CloseStatus => _state == WebSocketState.Closed ? WebSocketCloseStatus.NormalClosure : null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        internal static Peer WithText(string message)
        {
            var peer = new Peer();
            peer.Add(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true);
            return peer;
        }
        internal void Add(byte[] bytes, WebSocketMessageType type, bool end) => _frames.Enqueue(new(bytes, type, end));

        private WebSocketReceiveResult Receive(Span<byte> buffer, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            ReceiveAttempts++;
            Assert.NotEmpty(_frames); // Unexpected loops fail instead of hanging.
            var frame = _frames.Dequeue();
            Assert.True(frame.Bytes.Length <= buffer.Length);
            frame.Bytes.CopyTo(buffer);
            if (frame.Type == WebSocketMessageType.Close) _state = WebSocketState.Closed;
            return new(frame.Bytes.Length, frame.Type, frame.EndOfMessage);
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
            Task.FromResult(Receive(buffer.AsSpan(), cancellationToken));
        public override ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            var result = Receive(buffer.Span, cancellationToken);
            return ValueTask.FromResult(new ValueWebSocketReceiveResult(result.Count, result.MessageType, result.EndOfMessage));
        }
        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType,
            bool endOfMessage, CancellationToken cancellationToken)
        {
            SendAttempts++;
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = buffer.ToArray();
            if (SendBehavior is not null) await SendBehavior(bytes, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Sent.Add(new(bytes, messageType, endOfMessage));
            if (CloseAfterSuccessfulSend) _state = WebSocketState.Closed;
        }
        public override ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType,
            bool endOfMessage, CancellationToken cancellationToken) =>
            new(SendAsync(new ArraySegment<byte>(buffer.ToArray()), messageType, endOfMessage, cancellationToken));
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        { _state = WebSocketState.Closed; return Task.CompletedTask; }
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        { _state = WebSocketState.Closed; return Task.CompletedTask; }
        public override void Abort() => _state = WebSocketState.Aborted;
        public override void Dispose() => _state = WebSocketState.Closed;
    }
}
