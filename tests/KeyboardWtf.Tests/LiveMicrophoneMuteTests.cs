using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KeyboardWtf.Models;
using KeyboardWtf.Services;
using NAudio.Wave;
using Xunit;

namespace KeyboardWtf.Tests;

// Real service publication over a private loopback WebSocket. WaveInEvent is
// only an unstarted callback identity: no capture/playback device, provider,
// model, credential, profile, or application singleton is opened.
[Collection("Live event integration")]
public sealed class LiveMicrophoneMuteTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly byte[] Pcm = [0x00, 0x40, 0x00, 0x00];

    [Fact]
    public async Task Current_toggle_publishes_one_stream_end_and_rejects_stale_or_duplicate_actions()
    {
        await using var run = await Connection.CreateAsync();
        var initial = run.State;
        Assert.False(initial.Muted);
        Assert.True(await run.Live.TrySetMicrophoneMutedAsync(initial.SessionId, initial.Revision, true));
        AssertStreamEnd(await run.ReceiveAsync());
        var muted = run.State;
        Assert.True(muted.Muted);
        Assert.True(muted.Revision > initial.Revision);
        Assert.False(await run.Live.TrySetMicrophoneMutedAsync(initial.SessionId, initial.Revision, false));
        Assert.False(await run.Live.TrySetMicrophoneMutedAsync(muted.SessionId, muted.Revision, true));
        await run.AssertMarkerIsNextAsync();

        Assert.True(await run.Live.TrySetMicrophoneMutedAsync(muted.SessionId, muted.Revision, false));
        Assert.True(await run.SendAudioAsync(Pcm, run.Capture()));
        AssertAudio(await run.ReceiveAsync(), Pcm);
    }

    [Fact]
    public async Task Rapid_actions_with_one_frozen_revision_accept_exactly_one_owner()
    {
        await using var run = await Connection.CreateAsync();
        var initial = run.State;
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ =>
            run.Live.TrySetMicrophoneMutedAsync(initial.SessionId, initial.Revision, true)));
        Assert.Equal(1, outcomes.Count(value => value));
        Assert.Equal(initial.Revision + 1, run.State.Revision);
        AssertStreamEnd(await run.ReceiveAsync());
        await run.AssertMarkerIsNextAsync();
    }

    [Fact]
    public async Task Ended_and_replaced_sessions_cannot_lend_action_authority_to_the_next_session()
    {
        await using var run = await Connection.CreateAsync();
        var previous = run.State;
        run.Session.Cancel();
        Assert.Null(run.Live.GetMicrophoneActionState());
        Assert.False(await run.Live.TrySetMicrophoneMutedAsync(previous.SessionId, previous.Revision, true));
        run.ReplaceSession();
        var current = run.State;
        Assert.NotEqual(previous.SessionId, current.SessionId);
        Assert.False(current.Muted);
        Assert.False(await run.Live.TrySetMicrophoneMutedAsync(previous.SessionId, current.Revision, true));
        Assert.False(await run.Live.TrySetMicrophoneMutedAsync(current.SessionId, previous.Revision, true));
        await run.AssertMarkerIsNextAsync();
        Assert.True(await run.SendAudioAsync(Pcm, run.Capture()));
        AssertAudio(await run.ReceiveAsync(), Pcm);
    }

    [Theory]
    [InlineData("audio")]
    [InlineData("send")]
    public async Task PCM_queued_before_mute_cannot_borrow_a_later_unmuted_revision(string queue)
    {
        await using var run = await Connection.CreateAsync();
        var gate = run.Field<SemaphoreSlim>(queue == "audio" ? "_audioSendLock" : "_sendLock");
        await gate.WaitAsync();
        var oldAudio = run.SendAudioAsync(Pcm, run.Capture());
        Assert.False(oldAudio.IsCompleted);
        var initial = run.State;
        var mute = run.Live.TrySetMicrophoneMutedAsync(initial.SessionId, initial.Revision, true);
        var muted = run.State;
        Assert.True(muted.Muted);
        try
        {
            if (queue == "audio")
            {
                Assert.True(await mute);
                AssertStreamEnd(await run.ReceiveAsync());
            }
            else Assert.False(mute.IsCompleted);
            Assert.True(await run.Live.TrySetMicrophoneMutedAsync(muted.SessionId, muted.Revision, false));
        }
        finally { gate.Release(); }
        Assert.False(await oldAudio.WaitAsync(Timeout));
        Assert.True(await mute.WaitAsync(Timeout));
        await run.AssertMarkerIsNextAsync();
        Assert.True(await run.SendAudioAsync(Pcm, run.Capture()));
        AssertAudio(await run.ReceiveAsync(), Pcm);
    }

    [Theory]
    [InlineData("audio")]
    [InlineData("send")]
    public async Task Actual_callback_keeps_its_ingress_revision_while_waiting_on_both_publication_locks(string queue)
    {
        await using var run = await Connection.CreateAsync();
        var gate = run.Field<SemaphoreSlim>(queue == "audio" ? "_audioSendLock" : "_sendLock");
        await gate.WaitAsync();
        var callback = run.EmitAsync(run.Microphone, Pcm);
        Assert.False(callback.IsCompleted);
        var initial = run.State;
        var mute = run.Live.TrySetMicrophoneMutedAsync(initial.SessionId, initial.Revision, true);
        var muted = run.State;
        try
        {
            if (queue == "audio")
            {
                Assert.True(await mute);
                AssertStreamEnd(await run.ReceiveAsync());
            }
            Assert.True(await run.Live.TrySetMicrophoneMutedAsync(muted.SessionId, muted.Revision, false));
        }
        finally { gate.Release(); }
        await callback.WaitAsync(Timeout);
        Assert.True(await mute.WaitAsync(Timeout));
        await run.AssertMarkerIsNextAsync();
        await run.EmitAsync(run.Microphone, Pcm);
        AssertAudio(await run.ReceiveAsync(), Pcm);
    }

    [Theory]
    [InlineData("unmute")]
    [InlineData("end")]
    [InlineData("session")]
    [InlineData("microphone")]
    [InlineData("socket")]
    public async Task Waiting_stream_end_is_skipped_after_its_original_authority_expires(string change)
    {
        await using var run = await Connection.CreateAsync();
        var send = run.Field<SemaphoreSlim>("_sendLock");
        await send.WaitAsync();
        var initial = run.State;
        var mute = run.Live.TrySetMicrophoneMutedAsync(initial.SessionId, initial.Revision, true);
        Assert.False(mute.IsCompleted);
        try
        {
            switch (change)
            {
                case "unmute":
                    var muted = run.State;
                    Assert.True(await run.Live.TrySetMicrophoneMutedAsync(muted.SessionId, muted.Revision, false));
                    break;
                case "end": run.Session.Cancel(); break;
                case "session": run.ReplaceSession(); break;
                case "microphone": run.ReplaceMicrophone(); break;
                case "socket": run.Set("_socket", null); break;
            }
        }
        finally { send.Release(); }
        // The accepted local toggle may complete after its transport authority
        // expires. It must neither publish an obsolete end nor undo the state.
        Assert.True(await mute.WaitAsync(Timeout));
        run.RestoreSocket();
        await run.AssertMarkerIsNextAsync();
        if (change == "unmute") Assert.False(run.State.Muted);
        if (change == "end") Assert.Null(run.Live.GetMicrophoneActionState());
    }

    [Fact]
    public async Task Muted_actual_microphone_callbacks_do_not_publish_PCM_or_refill_activation_pre_roll()
    {
        await using var run = await Connection.CreateAsync();
        run.Set("_paidLiveReady", false);
        await run.EmitAsync(run.Microphone, Pcm);
        Assert.Equal(Pcm, Assert.Single(run.PreRoll));
        var initial = run.State;
        Assert.True(await run.Live.TrySetMicrophoneMutedAsync(initial.SessionId, initial.Revision, true));
        Assert.Empty(run.PreRoll);
        await run.EmitAsync(run.Microphone, Pcm);
        Assert.Empty(run.PreRoll);
        Assert.Equal(-96, KeyboardWtfState.InputLevelDb);
        await run.PublishActivationAsync();
        await run.EmitAsync(run.Microphone, Pcm);
        await run.AssertMarkerIsNextAsync();

        var muted = run.State;
        Assert.True(await run.Live.TrySetMicrophoneMutedAsync(muted.SessionId, muted.Revision, false));
        await run.EmitAsync(run.Microphone, Pcm);
        AssertAudio(await run.ReceiveAsync(), Pcm);
        Assert.Empty(run.PreRoll);
    }

    [Fact]
    public async Task Unmuted_activation_preserves_original_pre_roll_order_and_then_clears_the_queue()
    {
        await using var run = await Connection.CreateAsync();
        run.Set("_paidLiveReady", false);
        var second = new byte[] { 0x00, 0x20, 0x00, 0x00 };
        await run.EmitAsync(run.Microphone, Pcm);
        await run.EmitAsync(run.Microphone, second);
        Assert.Equal(2, run.PreRoll.Count);
        await run.PublishActivationAsync();
        AssertAudio(await run.ReceiveAsync(), Pcm);
        AssertAudio(await run.ReceiveAsync(), second);
        Assert.Empty(run.PreRoll);
        Assert.True(run.Field<bool>("_paidLiveReady"));
        await run.AssertMarkerIsNextAsync();
    }

    [Theory]
    [InlineData("audio")]
    [InlineData("send")]
    public async Task Activation_pre_roll_waiting_on_either_lock_cannot_survive_mute_then_unmute(string queue)
    {
        await using var run = await Connection.CreateAsync();
        run.Set("_paidLiveReady", false);
        await run.EmitAsync(run.Microphone, Pcm);
        Assert.Single(run.PreRoll);
        var gate = run.Field<SemaphoreSlim>(queue == "audio" ? "_audioSendLock" : "_sendLock");
        await gate.WaitAsync();
        var activation = run.PublishActivationAsync();
        Assert.False(activation.IsCompleted);
        var initial = run.State;
        var mute = run.Live.TrySetMicrophoneMutedAsync(initial.SessionId, initial.Revision, true);
        Assert.Empty(run.PreRoll);
        var muted = run.State;
        try
        {
            if (queue == "audio") Assert.True(await mute);
            Assert.True(await run.Live.TrySetMicrophoneMutedAsync(muted.SessionId, muted.Revision, false));
        }
        finally { gate.Release(); }
        await activation.WaitAsync(Timeout);
        Assert.True(await mute.WaitAsync(Timeout));
        Assert.True(run.Field<bool>("_paidLiveReady"));
        await run.AssertMarkerIsNextAsync();
        await run.EmitAsync(run.Microphone, Pcm);
        AssertAudio(await run.ReceiveAsync(), Pcm);
    }

    [Fact]
    public async Task Callback_from_previous_WaveInEvent_cannot_publish_into_replacement_session()
    {
        await using var run = await Connection.CreateAsync();
        var previousMicrophone = run.Microphone;
        run.ReplaceSession();
        run.ReplaceMicrophone();
        await run.EmitAsync(previousMicrophone, Pcm);
        Assert.Empty(run.PreRoll);
        await run.AssertMarkerIsNextAsync();
        await run.EmitAsync(run.Microphone, Pcm);
        AssertAudio(await run.ReceiveAsync(), Pcm);
    }

    [Fact]
    public async Task Queued_PCM_cannot_publish_to_replacement_session_even_when_the_socket_is_reused()
    {
        await using var run = await Connection.CreateAsync();
        var gate = run.Field<SemaphoreSlim>("_audioSendLock");
        await gate.WaitAsync();
        var queued = run.SendAudioAsync(Pcm, run.Capture());
        try { run.ReplaceSession(); }
        finally { gate.Release(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(Timeout));
        await run.AssertMarkerIsNextAsync();
        Assert.True(await run.SendAudioAsync(Pcm, run.Capture()));
        AssertAudio(await run.ReceiveAsync(), Pcm);
    }

    [Fact]
    public async Task Mute_retains_capture_session_pending_tool_playback_context_and_non_microphone_messages()
    {
        await using var run = await Connection.CreateAsync();
        var executionEntered = Signal();
        var finishExecution = Signal();
        using var dispatcher = new LiveToolCallDispatcher(run.Session.Token, async (_, _, token) =>
        {
            executionEntered.TrySetResult(true);
            await finishExecution.Task.WaitAsync(token);
            return new Dictionary<string, object> { ["ok"] = true };
        }, async (build, onSent, token) =>
        {
            await run.SendJsonAsync(build(), token);
            onSent();
        });
        run.Set("_toolCalls", dispatcher);
        var guard = run.Field<ToolAttemptGuard>("_toolAttemptGuard");
        var context = run.Field<Queue<GeminiLiveConversationService.ConversationContextEntry>>("_conversationContext");
        var entry = new GeminiLiveConversationService.ConversationContextEntry("user", "Synthetic context remains owned.");
        context.Enqueue(entry);
        var playback = GeminiLiveConversationService.CreatePlaybackBuffer();
        playback.AddSamples(Pcm, 0, Pcm.Length);
        run.Set("_playbackBuffer", playback);
        var ended = 0;
        run.Set("_sessionEndingObserver", (Func<Task>)(() => { ended++; return Task.CompletedTask; }));
        dispatcher.Enqueue(JsonSerializer.SerializeToElement(new
        {
            functionCalls = new[] { new { id = "local-tool", name = "synthetic_local_read", args = new { } } },
        }), guard);
        await executionEntered.Task.WaitAsync(Timeout);
        var ownedCompletion = dispatcher.Completion;
        var session = run.Session;
        var microphone = run.Microphone;
        var socket = run.Field<ClientWebSocket>("_socket");
        var initial = run.State;
        try
        {
            Assert.True(await run.Live.TrySetMicrophoneMutedAsync(initial.SessionId, initial.Revision, true));
            AssertStreamEnd(await run.ReceiveAsync());
            Assert.True(run.Live.IsActive);
            Assert.Same(microphone, run.Field<WaveInEvent>("_microphone"));
            Assert.Same(session, run.Field<CancellationTokenSource>("_sessionCts"));
            Assert.False(session.IsCancellationRequested);
            Assert.Same(socket, run.Field<ClientWebSocket>("_socket"));
            Assert.Equal(WebSocketState.Open, socket.State);
            Assert.Equal(initial.SessionId, run.Live.ConnectorLogicalLiveSessionId);
            Assert.Same(dispatcher, run.Field<LiveToolCallDispatcher>("_toolCalls"));
            Assert.Same(ownedCompletion, dispatcher.Completion);
            Assert.False(ownedCompletion.IsCompleted);
            Assert.Equal(1, dispatcher.PendingCount);
            Assert.Same(guard, run.Field<ToolAttemptGuard>("_toolAttemptGuard"));
            Assert.Same(playback, run.Field<BufferedWaveProvider>("_playbackBuffer"));
            Assert.Equal(Pcm.Length, playback.BufferedBytes);
            Assert.Same(entry, Assert.Single(context));
            Assert.Equal(0, ended);

            await run.SendJsonAsync(new { realtimeInput = new { text = "Synthetic typed input while muted." } });
            using var typed = JsonDocument.Parse(await run.ReceiveAsync());
            Assert.Equal("Synthetic typed input while muted.", typed.RootElement.GetProperty("realtimeInput").GetProperty("text").GetString());
            finishExecution.TrySetResult(true);
            await ownedCompletion.WaitAsync(Timeout);
            using var tool = JsonDocument.Parse(await run.ReceiveAsync());
            Assert.True(tool.RootElement.TryGetProperty("toolResponse", out _));
            Assert.True(run.State.Muted);
            Assert.Equal(0, ended);
        }
        finally
        {
            finishExecution.TrySetResult(true);
            await dispatcher.Completion.WaitAsync(Timeout);
            run.Set("_toolCalls", null);
            run.Set("_playbackBuffer", null);
        }
    }

    private static void AssertStreamEnd(string message)
    {
        using var json = JsonDocument.Parse(message);
        var realtime = json.RootElement.GetProperty("realtimeInput");
        Assert.True(realtime.GetProperty("audioStreamEnd").GetBoolean());
        Assert.False(realtime.TryGetProperty("audio", out _));
    }

    private static void AssertAudio(string message, byte[] expected)
    {
        using var json = JsonDocument.Parse(message);
        var audio = json.RootElement.GetProperty("realtimeInput").GetProperty("audio");
        Assert.Equal("audio/pcm;rate=16000", audio.GetProperty("mimeType").GetString());
        Assert.Equal(expected, Convert.FromBase64String(audio.GetProperty("data").GetString()));
    }

    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class CallbackCompletion : SynchronizationContext
    {
        private readonly TaskCompletionSource<bool> _completed = Signal();
        internal Task Completion => _completed.Task;
        public override void OperationCompleted() => _completed.TrySetResult(true);
        public override void Post(SendOrPostCallback callback, object state) =>
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { callback(state); }
                catch (Exception exception) { _completed.TrySetException(exception); }
            });
    }

    private sealed class Connection : IAsyncDisposable
    {
        internal GeminiLiveConversationService Live { get; } = new(null, null, null);
        internal CancellationTokenSource Session { get; private set; } = new();
        internal WaveInEvent Microphone { get; private set; } = new();
        internal WebSocket Server { get; private set; }
        internal Queue<byte[]> PreRoll => Field<Queue<byte[]>>("_paidActivationAudio");
        internal LiveMicrophoneActionState State => Live.GetMicrophoneActionState()
            ?? throw new InvalidOperationException("The synthetic live session is inactive.");
        private readonly ClientWebSocket _client = new();
        private readonly List<CancellationTokenSource> _sessions = [];
        private readonly List<WaveInEvent> _microphones = [];
        private readonly double _previousMeter = KeyboardWtfState.InputLevelDb;
        private TcpClient _peer;

        public static async Task<Connection> CreateAsync()
        {
            var run = new Connection();
            run._sessions.Add(run.Session);
            run._microphones.Add(run.Microphone);
            using var timeout = new CancellationTokenSource(Timeout);
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            try
            {
                var connecting = run._client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/"), timeout.Token);
                run._peer = await listener.AcceptTcpClientAsync(timeout.Token);
                var stream = run._peer.GetStream();
                var header = new StringBuilder();
                var one = new byte[1];
                while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    if (header.Length >= 8192 || await stream.ReadAsync(one, timeout.Token) != 1)
                        throw new IOException("Incomplete loopback WebSocket handshake.");
                    header.Append((char)one[0]);
                }
                var key = header.ToString().Split("\r\n")
                    .Single(line => line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
                    .Split(':', 2)[1].Trim();
                var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                await stream.WriteAsync(Encoding.ASCII.GetBytes(
                    "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n"), timeout.Token);
                run.Server = WebSocket.CreateFromStream(stream, true, null, System.Threading.Timeout.InfiniteTimeSpan);
                await connecting;
                run.RestoreSocket();
                run.Set("_sessionCts", run.Session);
                run.Set("_microphone", run.Microphone);
                run.Set("_logicalSessionId", "synthetic-live-1");
                run.Set("_paidLiveReady", true);
                return run;
            }
            catch { await run.DisposeAsync(); throw; }
        }

        internal T Field<T>(string name) => (T)typeof(GeminiLiveConversationService)
            .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Live);
        internal void Set(string name, object value) => typeof(GeminiLiveConversationService)
            .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Live, value);
        internal object Capture() => Invoke("CaptureMicrophonePublication");
        internal Task<bool> SendAudioAsync(byte[] bytes, object owner) =>
            (Task<bool>)Invoke("SendAudioChunkAsync", bytes, Session.Token, owner);
        internal Task PublishActivationAsync() =>
            (Task)Invoke("PublishActivationAudioAsync", Session, true, Session.Token);
        internal Task SendJsonAsync(object message, CancellationToken token = default) =>
            (Task)Invoke("SendJsonAsync", message, token);
        internal void RestoreSocket() => Set("_socket", _client);

        internal async Task EmitAsync(WaveInEvent sender, byte[] bytes)
        {
            var completion = new CallbackCompletion();
            var previous = SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(completion);
                Invoke("OnMicrophoneData", sender, new WaveInEventArgs(bytes, bytes.Length));
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
            await completion.Completion.WaitAsync(Timeout);
        }

        internal void ReplaceSession()
        {
            Session.Cancel();
            Invoke("ResetPaidActivation");
            Session = new();
            _sessions.Add(Session);
            Set("_sessionCts", Session);
            Set("_logicalSessionId", "synthetic-live-" + _sessions.Count);
            Set("_paidLiveReady", true);
        }

        internal void ReplaceMicrophone()
        {
            Microphone = new();
            _microphones.Add(Microphone);
            Set("_microphone", Microphone);
        }

        internal async Task AssertMarkerIsNextAsync()
        {
            // A positive ordered frame proves absence of stale preceding PCM
            // or audioStreamEnd without an arbitrary delay or TCP byte poll.
            await SendJsonAsync(new { localTestMarker = "publication-barrier" });
            using var json = JsonDocument.Parse(await ReceiveAsync());
            Assert.Equal("publication-barrier", json.RootElement.GetProperty("localTestMarker").GetString());
        }

        internal async Task<string> ReceiveAsync()
        {
            using var timeout = new CancellationTokenSource(Timeout);
            var bytes = new byte[8192];
            using var message = new MemoryStream();
            WebSocketReceiveResult frame;
            do
            {
                frame = await Server.ReceiveAsync(new ArraySegment<byte>(bytes), timeout.Token);
                Assert.Equal(WebSocketMessageType.Text, frame.MessageType);
                message.Write(bytes, 0, frame.Count);
                Assert.True(message.Length <= 32768, "Synthetic message exceeded the fixture limit.");
            } while (!frame.EndOfMessage);
            return Encoding.UTF8.GetString(message.ToArray());
        }

        private object Invoke(string name, params object[] arguments)
        {
            var method = typeof(GeminiLiveConversationService).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException("Missing original service helper: " + name);
            try { return method.Invoke(Live, arguments); }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }

        public ValueTask DisposeAsync()
        {
            foreach (var session in _sessions) session.Cancel();
            // Detach inert fixture owners before the app's hardware/transport
            // disposal routine; each fixture resource is disposed exactly once.
            Set("_socket", null);
            Set("_microphone", null);
            Set("_sessionCts", null);
            Set("_toolCalls", null);
            Set("_playbackBuffer", null);
            _client.Dispose();
            Server?.Dispose();
            _peer?.Dispose();
            Live.Dispose();
            foreach (var microphone in _microphones) microphone.Dispose();
            foreach (var session in _sessions) session.Dispose();
            KeyboardWtfState.InputLevelDb = _previousMeter;
            return ValueTask.CompletedTask;
        }
    }
}
