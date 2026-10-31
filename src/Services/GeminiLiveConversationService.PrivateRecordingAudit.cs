#if KEYBOARD_WTF_PERSONAL_API_CANDIDATE || KEYBOARD_WTF_CONNECTOR_STAGING_CANDIDATE
namespace KeyboardWtf.Services;

using KeyboardWtf.Services.Ai.Backends;
using KeyboardWtf.Models;

public sealed partial class GeminiLiveConversationService
{
    private PrivatePersonalRecordingInput _privateRecordingInput;
    internal bool PrivateRecordingAuditActive => _privateRecordingInput != null;

    internal async Task<object> StartPrivateRecordingAuditAsync()
    {
        if (IsActive || IsStarting || _privateRecordingInput != null)
            throw new InvalidOperationException("Stop the current session before the private recording audit.");
        if (_paidAccount?.Enabled != true) throw new InvalidOperationException("Private Personal backend is required.");
        // Uses the same startup, account authorization, microphone, Live sender,
        // playback and dispatcher. The flag only replaces diagnostic input.
        await StartCoreAsync(privateRecordingAudit: true);
        if (!IsActive || _privateRecordingInput is null)
            throw new InvalidOperationException("Private recording audit did not start.");
        RequirePrivateRecordingOwner();
        return PrivateRecordingState();
    }

    internal async Task<object> ReplayPrivateRecordingAsync(byte[] pcm, CancellationToken token)
    {
        RequirePrivateRecordingOwner();
        var receipt = await _privateRecordingInput.Queue(pcm).WaitAsync(token);
        RequirePrivateRecordingOwner();
        AppLog.Info($"Jarvis private recording sent: logicalSession={_logicalSessionId} " +
            $"recording={receipt.RecordingId:N} pcmBytes={receipt.PcmBytes} frames={receipt.CaptureFrames} " +
            $"inputStartedUtc={receipt.FirstFrameAt:O} inputSentUtc={receipt.LastFrameSentAt:O}");
        return new { state = PrivateRecordingState(), receipt };
    }

    internal Task StopPrivateRecordingAuditAsync() => _privateRecordingInput is null
        ? Task.CompletedTask : StopAsync();

    // Explicit private diagnostic only: inspect the owned recording session's
    // existing in-memory transcript without logging or retaining speech. This
    // never observes a normal microphone session or changes turn ownership.
    internal object ObservePrivateRecording(string expectedLogicalSession)
    {
        var recording = _privateRecordingInput;
        RequirePrivateRecordingOwner();
        lock (_conversationContextLock)
        {
            RequirePrivateRecordingOwner();
            if (!ReferenceEquals(recording, _privateRecordingInput)
                || !string.Equals(expectedLogicalSession, _logicalSessionId, StringComparison.Ordinal))
                throw new InvalidOperationException("Private recording session ownership changed.");
            var text = _currentUserTranscript.Length > 0 ? _currentUserTranscript.ToString()
                : _conversationContext.LastOrDefault(entry => entry.Role == "User")?.Text ?? "";
            return new { state = PrivateRecordingState(), userTurn = text[..Math.Min(text.Length, 1000)],
                truncated = text.Length > 1000 };
        }
    }

    private object PrivateRecordingState() => new
    {
        logicalSession = _logicalSessionId, configuredPolicy = "PersonalOnly", runtimeOwner = "Personal",
        model = PersonalGeminiModels.Live, physicalSocket = _providerSocketSequence,
        input = "supplied-recording-on-real-WaveIn-callback", roomAudioPublished = false,
        replyCompleted = _turnComplete.Task.IsCompleted,
        playbackBufferedBytes = _playbackBuffer?.BufferedBytes ?? 0,
        playbackState = _speaker?.PlaybackState.ToString() ?? "Stopped",
        uiPhase = KeyboardWtfState.UiPhase.ToString(),
        longTaskActive = KeyboardWtfState.HasLongTaskUiOwner,
    };

    private void RequirePrivateRecordingOwner()
    {
        // Stop cancels before detaching WaveIn. A final in-flight audit frame
        // must take the normal cancellation path, not emit a false mic failure.
        SessionCancellation.ThrowIfCancellationRequested();
        if (_privateRecordingInput is null || !IsActive
            || _backendOperation?.Configuration.Policy != AiBackendPolicy.PersonalOnly
            || _backendOperation.Owner != AiBackendOwner.Personal)
            throw new InvalidOperationException("Private recording audit requires an active PersonalOnly owner.");
    }
}
#endif
