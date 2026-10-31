namespace KeyboardWtf.Services;

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using KeyboardWtf.Models;
using NAudio.Wave;

internal readonly record struct LiveMicrophoneActionState(string SessionId, long Revision, bool Muted);

public sealed partial class GeminiLiveConversationService
{
    // The existing activation gate owns microphone publication as well as its
    // pre-roll. Capture, playback, tools and the logical session remain alive.
    private bool _microphoneMuted;
    private long _microphonePublicationRevision;

    private sealed record MicrophonePublicationOwner(CancellationTokenSource Session,
        WaveInEvent Microphone, ClientWebSocket Socket, long Revision);

    internal LiveMicrophoneActionState? GetMicrophoneActionState()
    {
        lock (_paidActivationLock)
            return !_disposed && IsActive && _logicalSessionId.Length > 0
                ? new(_logicalSessionId, _microphonePublicationRevision, _microphoneMuted) : null;
    }

    internal async Task<bool> TrySetMicrophoneMutedAsync(string sessionId, long revision, bool muted)
    {
        IDisposable updateWork = null;
        if (_updateWorkAdmission is not null && !_updateWorkAdmission.TryAcquire(out updateWork)) return false;
        using var ownedPublication = updateWork;
        MicrophonePublicationOwner owner;
        lock (_paidActivationLock)
        {
            if (_disposed || !IsActive || _logicalSessionId != sessionId
                || _microphonePublicationRevision != revision || _microphoneMuted == muted) return false;
            _microphoneMuted = muted;
            _microphonePublicationRevision++;
            _paidActivationAudio.Clear();
            owner = CaptureMicrophonePublication();
            if (muted) KeyboardWtfState.InputLevelDb = -96;
        }
        if (muted)
        {
            // Automatic activity detection stays enabled. Tell Live that this
            // microphone stream paused so cached audio can finish its turn;
            // this is not a conversation/session end or a reconnect.
            try
            {
                await SendMicrophoneJsonAsync(new { realtimeInput = new { audioStreamEnd = true } },
                    owner, muted: true, owner.Session.Token);
            }
            catch (Exception exception) when (exception is OperationCanceledException or WebSocketException
                or InvalidOperationException)
            {
                // Local mute is already authoritative. Its obsolete/closed
                // transport must not undo it or create another session.
                AppLog.Warning("Jarvis microphone pause publication ended: " + exception.GetType().Name);
            }
        }
        return true;
    }

    private MicrophonePublicationOwner CaptureMicrophonePublication()
    {
        lock (_paidActivationLock)
            return new(_sessionCts, _microphone, _socket, _microphonePublicationRevision);
    }

    // Call while holding the activation gate. A microphone callback retains its
    // original identity through both send locks and rapid mute/unmute changes.
    private bool OwnsMicrophoneCapture(MicrophonePublicationOwner owner) => !_disposed && !_microphoneMuted
        && owner.Revision == _microphonePublicationRevision
        && ReferenceEquals(owner.Session, _sessionCts) && owner.Session?.IsCancellationRequested != true
        && ReferenceEquals(owner.Microphone, _microphone);

    private async Task<bool> SendMicrophoneJsonAsync(object message, MicrophonePublicationOwner owner,
        bool muted, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
        await _sendLock.WaitAsync(token);
        try
        {
            Task send;
            lock (_paidActivationLock)
            {
                if (_disposed || owner.Session is null || owner.Session.IsCancellationRequested
                    || owner.Microphone is null || !ReferenceEquals(owner.Session, _sessionCts)
                    || !ReferenceEquals(owner.Microphone, _microphone) || owner.Revision != _microphonePublicationRevision
                    || _microphoneMuted != muted || !_paidLiveReady
                    || !ReferenceEquals(owner.Socket, _socket) || owner.Socket?.State != WebSocketState.Open) return false;
                // Synchronize the final send initiation with mute transitions.
                // A frame already dispatched cannot be recalled; queued frames
                // cannot borrow a later unmuted epoch or a replacement socket.
                send = owner.Socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);
            }
            await send;
            return true;
        }
        finally { _sendLock.Release(); }
    }
}
