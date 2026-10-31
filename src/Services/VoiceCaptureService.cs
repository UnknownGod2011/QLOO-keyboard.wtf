namespace KeyboardWtf.Services;

using System.Diagnostics;
using NAudio.Wave;
using KeyboardWtf.Helpers;
using KeyboardWtf.Models;
using KeyboardWtf.Services.Ai;
using KeyboardWtf.Services.Updates;

public sealed class VoiceCaptureService
{
    internal static readonly TimeSpan SmartWritingFinalSilence = TimeSpan.FromSeconds(8);
    private readonly AudioRecorderService _audioRecorder;
    private readonly SpeechRecognitionService _speechRecognition;
    private readonly NotificationService _notifications;
    private readonly DestinationRouter _router;
    private readonly UpdateWorkAdmissionGate _updateWorkAdmission;

    private DateTime _recordingStartUtc;
    private CancellationTokenSource _autoStopCts;
    private CancellationTokenSource _silenceCts;
    private CancellationTokenSource _processingCts;
    private Func<string, Task> _completionHandler;
    private Func<SmartWritingAudioCapture, Task> _audioCompletionHandler;
    private bool _preferFastRecognition;
    private VoskStreamingSession _streamingSession;
    private DateTime _lastPreviewUtc;
    private string _lastPreview = "";
    private int _autoCompleteInProgress;
    private int _operationId;
    private readonly object _recordingStartGate = new();
    private bool _recordingStartInProgress;
    private TextInsertionTarget _deliveryTarget;

    internal TextInsertionTarget CurrentDeliveryTarget => _deliveryTarget;
    internal CancellationToken CurrentOperationToken =>
        _processingCts?.Token ?? CancellationToken.None;
    internal long LastRecordingDurationMs { get; private set; }
    internal long LastTranscriptionDurationMs { get; private set; }
    internal int? SmartWritingCompletionOperationId
    {
        get
        {
            lock (_recordingStartGate)
                return KeyboardWtfState.IsRecording && KeyboardWtfState.CurrentRecordingMode == RecordingMode.VoiceAssistant
                    && !KeyboardWtfState.IsProcessing && !KeyboardWtfState.IsProcessingAi
                    && !_recordingStartInProgress && _autoCompleteInProgress == 0 ? _operationId : null;
        }
    }

    // A popup action carries the recording it was rendered for. It must never
    // behave like the shortcut toggle, which can start or cancel another cycle.
    internal Task<bool> TryCompleteSmartWritingAsync(int expectedOperationId) =>
        CompleteOwnedRecordingAsync(expectedOperationId, RecordingMode.VoiceAssistant, smartWritingAction: true);

    public VoiceCaptureService(
        AudioRecorderService audioRecorder,
        SpeechRecognitionService speechRecognition,
        NotificationService notifications,
        DestinationRouter router)
        : this(audioRecorder, speechRecognition, notifications, router, null)
    {
    }

    internal VoiceCaptureService(
        AudioRecorderService audioRecorder,
        SpeechRecognitionService speechRecognition,
        NotificationService notifications,
        DestinationRouter router,
        UpdateWorkAdmissionGate updateWorkAdmission)
    {
        _audioRecorder = audioRecorder;
        _speechRecognition = speechRecognition;
        _notifications = notifications;
        _router = router;
        _updateWorkAdmission = updateWorkAdmission;
    }

    public bool Start(RecordingMode mode, string quickTarget = null)
    {
        if (KeyboardWtfState.IsProcessing
            || (KeyboardWtfState.IsDownloadingModels && !_speechRecognition.IsVoskLoaded && !_speechRecognition.IsWhisperLoaded))
        {
            _notifications.Warning("keyboard.wtf", KeyboardWtfState.ModelDownloadStatus ?? "Still processing");
            return false;
        }

        using var updateWork = AcquireUpdateWork();
        if (!IsMicrophoneAvailable(out var reason))
        {
            KeyboardWtfState.LastSendResult = "No mic";
            _notifications.Error("No microphone", reason);
            return false;
        }

        int startOperationId;
        lock (_recordingStartGate)
        {
            if (KeyboardWtfState.IsRecording || _recordingStartInProgress || Volatile.Read(ref _autoCompleteInProgress) != 0)
                return false;

            _recordingStartInProgress = true;
            startOperationId = Interlocked.Increment(ref _operationId);
        }

        var microphoneStarted = Stopwatch.GetTimestamp();
        try
        {
            _deliveryTarget = TextInsertionTarget.Capture();
            KeyboardWtfState.SetUi(
                VoiceUiPhase.Listening,
                ModeTitle(mode),
                "Starting microphone...");
            StartLivePreview();
            _audioRecorder.StartRecording();
        }
        catch (Exception ex)
        {
            StopLivePreview();
            bool stillOwned;
            lock (_recordingStartGate)
            {
                stillOwned = RecordingStartStillOwnsOperation(startOperationId, _operationId);
                _recordingStartInProgress = false;
                _deliveryTarget = null;
                if (stillOwned)
                {
                    KeyboardWtfState.IsRecording = false;
                    KeyboardWtfState.CurrentRecordingMode = RecordingMode.None;
                    KeyboardWtfState.LastSendResult = "Microphone unavailable";
                    KeyboardWtfState.SetUi(
                        VoiceUiPhase.Error,
                        "Microphone unavailable",
                        "Microphone access is required. Check Windows privacy settings and the selected input device.");
                }
            }

            if (!stillOwned)
            {
                AppLog.Info("Microphone startup ended after cancellation; no recording was retained.");
                return false;
            }

            _notifications.Error(
                "Microphone unavailable",
                "Microphone access is required. Check Windows privacy settings and the selected input device.");
            AppLog.Warning(ex, "Microphone recording could not start");
            return false;
        }

        lock (_recordingStartGate)
        {
            if (!RecordingStartStillOwnsOperation(startOperationId, _operationId))
            {
                if (_audioRecorder.IsRecording)
                    _audioRecorder.CancelRecording();
                StopLivePreview();
                _deliveryTarget = null;
                _recordingStartInProgress = false;
                KeyboardWtfState.IsRecording = false;
                KeyboardWtfState.CurrentRecordingMode = RecordingMode.None;
                KeyboardWtfState.ActiveQuickSendTarget = null;
                KeyboardWtfState.SetUi(
                    VoiceUiPhase.Cancelled,
                    "Cancelled",
                    "Nothing was typed or executed.");
                AppLog.Info("Microphone startup discarded because cancellation owns the operation.");
                return false;
            }

            AppLog.Info(
                $"Microphone capture started: mode={mode} startupMs=" +
                $"{(long)Stopwatch.GetElapsedTime(microphoneStarted).TotalMilliseconds}");
            KeyboardWtfState.IsRecording = true;
            KeyboardWtfState.CurrentRecordingMode = mode;
            KeyboardWtfState.ActiveQuickSendTarget = quickTarget;
            if (!KeyboardWtfState.AppendMode && mode != RecordingMode.Command)
                KeyboardWtfState.CurrentTranscript = string.Empty;
            KeyboardWtfState.FormattedOutputs.Clear();
            KeyboardWtfState.LastSendResult = null;
            _recordingStartUtc = DateTime.UtcNow;
            KeyboardWtfState.SetUi(
                VoiceUiPhase.Listening,
                ModeTitle(mode),
                "Speak now. Press the same shortcut to finish.");

            _autoStopCts?.Cancel();
            _autoStopCts = null;
            if (ShouldScheduleAutoStop(mode, KeyboardWtfState.MaxRecordingSeconds))
            {
                _autoStopCts = new CancellationTokenSource();
                _ = AutoStopAfterLimitAsync(
                    mode,
                    KeyboardWtfState.MaxRecordingSeconds,
                    _autoStopCts.Token);
            }
            if (UsesSilenceAutoStop(mode))
                StartSilenceWatcher(mode);

            _recordingStartInProgress = false;
            _notifications.Info(ModeTitle(mode), "Listening");
        }
        return true;
    }

    internal static bool RecordingStartStillOwnsOperation(
        int startOperationId,
        int currentOperationId) =>
        startOperationId == currentOperationId;

    public async Task<string> StopAndTranscribeAsync(
        bool updateTranscript = true,
        bool preferFast = false,
        bool keepProcessingUntilCompletionHandler = false)
    {
        if (!KeyboardWtfState.IsRecording)
            return string.Empty;

        using var updateWork = AcquireUpdateWork();

        _autoStopCts?.Cancel();
        _silenceCts?.Cancel();
        var mode = KeyboardWtfState.CurrentRecordingMode;
        // Claim processing before awaiting the final microphone callback. Cancellation
        // must invalidate this same operation, not a later replacement created after drain.
        KeyboardWtfState.IsRecording = false;
        KeyboardWtfState.IsProcessing = true;
        var operationId = Interlocked.Increment(ref _operationId);
        _processingCts?.Cancel();
        _processingCts?.Dispose();
        _processingCts = new CancellationTokenSource();
        var token = _processingCts.Token;
        var capturedPreview = "";
        try
        {
            var wavData = await _audioRecorder.StopRecordingAsync();
            if (operationId != _operationId || token.IsCancellationRequested)
                return string.Empty;
            ReportCaptureInterruption();
            capturedPreview = (_lastPreview ?? "").Trim();
            LastRecordingDurationMs = Math.Max(
                0,
                (long)(DateTime.UtcNow - _recordingStartUtc).TotalMilliseconds);
            StopLivePreview();
            KeyboardWtfState.IsRecording = false;
            KeyboardWtfState.CurrentRecordingMode = RecordingMode.None;
            KeyboardWtfState.ActiveQuickSendTarget = null;
            KeyboardWtfState.IsProcessing = true;
            KeyboardWtfState.SetUi(
                VoiceUiPhase.Transcribing,
                "Transcribing",
                preferFast ? "Fast local recognition" : "Accurate local recognition");
            _notifications.Info("Processing", "Transcribing audio...");

            var recognitionStarted = Stopwatch.GetTimestamp();
            var transcript = await _speechRecognition.RecognizeFromWavAsync(wavData, token, preferFast);
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(transcript))
            {
                // Local engines can report failure as empty text rather than throw.
                // Preserve only this Raw Dictation's already-recognized words.
                var recovered = RecoverAndReportRawPreview(mode, operationId, capturedPreview, updateTranscript);
                if (!string.IsNullOrWhiteSpace(recovered))
                    return recovered;
            }
            transcript = await PostProcessTranscriptAsync(
                transcript,
                allowAiTranslation: RecordingModeUsesAiPostProcessing(mode),
                token);
            LastTranscriptionDurationMs = (long)Stopwatch
                .GetElapsedTime(recognitionStarted)
                .TotalMilliseconds;
            if (operationId != _operationId)
                return string.Empty;
            if (updateTranscript)
                KeyboardWtfState.SetTranscript(transcript);
            AppLog.Info(
                $"Voice transcription completed: mode={mode} recordingMs={LastRecordingDurationMs} " +
                $"transcriptionMs={LastTranscriptionDurationMs} characters={transcript?.Length ?? 0}");
            _notifications.InfoTransient(
                "Transcribed",
                string.IsNullOrWhiteSpace(transcript) ? "No speech detected." : transcript);
            if (string.IsNullOrWhiteSpace(transcript))
                KeyboardWtfState.SetUi(VoiceUiPhase.Done, "No speech detected", "Press a shortcut to try again.");
            return transcript;
        }
        catch (OperationCanceledException)
        {
            AppLog.Info("Transcription operation cancelled");
            return string.Empty;
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "Transcription failed");
            if (operationId != _operationId)
                return string.Empty;
            var recoveredPreview = RecoverAndReportRawPreview(mode, operationId, capturedPreview, updateTranscript);
            if (!string.IsNullOrWhiteSpace(recoveredPreview))
                return recoveredPreview;
            KeyboardWtfState.SetUi(VoiceUiPhase.Error, "Transcription failed", ex.Message);
            _notifications.Error("Transcription failed", ex.Message);
            return string.Empty;
        }
        finally
        {
            if (operationId == _operationId && !keepProcessingUntilCompletionHandler)
                KeyboardWtfState.IsProcessing = false;
        }
    }

    public Task ToggleDictationAsync(Func<string, Task> handleTranscript) =>
        ToggleModeAsync(RecordingMode.Dictate, handleTranscript, preferFast: true);

    internal Task ToggleSmartModeAsync(Func<SmartWritingAudioCapture, Task> handleAudio) =>
        ToggleAudioModeAsync(RecordingMode.VoiceAssistant, handleAudio);

    public Task ToggleCommandModeAsync(Func<string, Task> handleTranscript) =>
        ToggleModeAsync(RecordingMode.Command, handleTranscript, preferFast: true);

    public Task ToggleJarvisPreflightAsync(Func<string, Task> handleTranscript) =>
        ToggleModeAsync(RecordingMode.JarvisPreflight, handleTranscript, preferFast: true);

    public async Task QuickSendAsync(string destinationName)
    {
        if (KeyboardWtfState.IsProcessing)
        {
            Cancel();
            return;
        }

        if (!KeyboardWtfState.IsRecording)
        {
            _preferFastRecognition = true;
            _completionHandler = async transcript =>
            {
                var ok = await _router.SendAsync(
                    destinationName,
                    transcript,
                    _deliveryTarget,
                    _processingCts?.Token ?? CancellationToken.None);
                _notifications.Outcome(
                    ok,
                    "Sent",
                    "Send failed",
                    KeyboardWtfState.LastSendResult ?? destinationName);
            };
            Start(RecordingMode.QuickSend, destinationName);
            return;
        }

        if (KeyboardWtfState.CurrentRecordingMode == RecordingMode.QuickSend)
            await CompleteRecordingAsync();
    }

    public void Cancel()
    {
        bool stopRecording;
        lock (_recordingStartGate)
        {
            Interlocked.Increment(ref _operationId);
            _autoStopCts?.Cancel();
            _silenceCts?.Cancel();
            _processingCts?.Cancel();
            stopRecording = KeyboardWtfState.IsRecording;
            KeyboardWtfState.IsRecording = false;
            KeyboardWtfState.CurrentRecordingMode = RecordingMode.None;
            KeyboardWtfState.IsProcessing = false;
            KeyboardWtfState.IsProcessingAi = false;
            _completionHandler = null;
            _audioCompletionHandler = null;
            _deliveryTarget = null;
            KeyboardWtfState.LastSendResult = "Cancelled";
            KeyboardWtfState.SetUi(VoiceUiPhase.Cancelled, "Cancelled", "Nothing was typed or executed.");
        }

        if (stopRecording)
            _audioRecorder.CancelRecording();
        StopLivePreview();
        _notifications.Info("Cancelled", "Current voice operation cancelled.");
    }

    public async Task<string> ApplyPromptToCurrentTranscriptAsync(
        string prompt,
        CancellationToken cancellationToken = default)
    {
        var transcript = KeyboardWtfState.CurrentTranscript;
        if (string.IsNullOrWhiteSpace(transcript))
            return string.Empty;
        using var updateWork = AcquireUpdateWork();

        var provider = AiProviderRegistry.Current;
        if (provider == null || !provider.IsAvailable)
        {
            if (provider is PaidCloudAiProvider paidProvider)
                throw new InvalidOperationException(paidProvider.UnavailableMessage);
            _notifications.Warning("AI unavailable", "No configured AI provider; copied raw transcript instead.");
            return transcript;
        }

        KeyboardWtfState.IsProcessingAi = true;
        KeyboardWtfState.SetUi(VoiceUiPhase.Thinking, "Polishing", "Polishing your text...");
        try
        {
            var result = await provider
                .ReformatAsync(
                    transcript,
                    prompt,
                    KeyboardWtfState.SelectedLanguage,
                    cancellationToken);
            if (!string.IsNullOrWhiteSpace(result))
                KeyboardWtfState.SetTranscript(result);
            return string.IsNullOrWhiteSpace(result) ? transcript : result;
        }
        finally
        {
            KeyboardWtfState.IsProcessingAi = false;
        }
    }

    internal async Task<string> TranscribeCapturedAudioAsync(
        byte[] wavData,
        bool preferFast,
        CancellationToken cancellationToken,
        bool allowAiTranslation = true)
    {
        if (wavData is null || wavData.Length < 44)
            return string.Empty;
        using var updateWork = AcquireUpdateWork();
        var started = Stopwatch.GetTimestamp();
        var transcript = await _speechRecognition.RecognizeFromWavAsync(
            wavData,
            cancellationToken,
            preferFast);
        transcript = await PostProcessTranscriptAsync(
            transcript,
            allowAiTranslation,
            cancellationToken);
        LastTranscriptionDurationMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return transcript;
    }

    internal static bool RecordingModeUsesAiPostProcessing(RecordingMode mode) =>
        mode != RecordingMode.Dictate;

    internal static string RecoverRawDictationPreview(
        RecordingMode mode,
        int operationId,
        int currentOperationId,
        string cumulativePreview) =>
        mode == RecordingMode.Dictate && operationId == currentOperationId
            ? (cumulativePreview ?? "").Trim()
            : "";

    private string RecoverAndReportRawPreview(
        RecordingMode mode, int operationId, string preview, bool updateTranscript)
    {
        var recovered = RecoverRawDictationPreview(mode, operationId, _operationId, preview);
        if (string.IsNullOrWhiteSpace(recovered))
            return "";
        if (updateTranscript)
            KeyboardWtfState.SetTranscript(recovered);
        const string detail = "Final recognition did not return text, so the speech already recognized locally was preserved. The final words may be incomplete.";
        KeyboardWtfState.SetUi(VoiceUiPhase.Done, "Partial dictation recovered", detail);
        _notifications.Warning("Partial dictation recovered", detail);
        AppLog.Warning($"Raw Dictation recovered cumulative local preview: characters={recovered.Length}");
        return recovered;
    }

    internal static bool UsesSilenceAutoStop(RecordingMode mode) => true;

    private async Task<string> PostProcessTranscriptAsync(
        string transcript,
        bool allowAiTranslation,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(transcript))
            return transcript;

        if (KeyboardWtfState.UseFillerWordCleaner)
            transcript = FillerWordFilter.Apply(transcript);
        transcript = CaseTransformer.Apply(transcript, KeyboardWtfState.SelectedCaseTransform);

        var targetLang = KeyboardWtfState.TranslateTargetLanguage;
        if (allowAiTranslation && !string.IsNullOrEmpty(targetLang))
        {
            var provider = AiProviderRegistry.Current;
            if (provider?.IsAvailable == true)
            {
                var langName = targetLang == "de" ? "German" : targetLang;
                var translated = await provider.ReformatAsync(
                    transcript,
                    $"Translate the following text to {langName}. Return only the translated text, no explanations.",
                    targetLang,
                    cancellationToken);
                if (!string.IsNullOrWhiteSpace(translated))
                    transcript = translated;
            }
        }

        return transcript;
    }

    internal static bool ShouldScheduleAutoStop(RecordingMode mode, int configuredSeconds) =>
        mode != RecordingMode.VoiceAssistant && configuredSeconds > 0;

    private Task AutoStopAfterLimitAsync(
        RecordingMode mode,
        int maximumSeconds,
        CancellationToken token)
    {
        int operationId;
        lock (_recordingStartGate) operationId = _operationId;
        return AutoStopOwnedRecordingAsync(mode, maximumSeconds, operationId, token);
    }

    private async Task AutoStopOwnedRecordingAsync(
        RecordingMode mode, int maximumSeconds, int operationId, CancellationToken token)
    {
        using var updateWork = AcquireUpdateWork();
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(maximumSeconds), token);
            if (OwnsRecording(mode, operationId, token))
            {
                _notifications.Warning("Auto-stopped", "Recording reached the configured limit.");
                await CompleteOwnedRecordingAsync(operationId, mode);
            }
        }
        catch (TaskCanceledException) { }
    }

    private void StartSilenceWatcher(RecordingMode mode)
    {
        _silenceCts?.Cancel();
        _silenceCts = new CancellationTokenSource();
        _ = MonitorSilenceAsync(mode, _silenceCts.Token);
    }

    private Task MonitorSilenceAsync(RecordingMode mode, CancellationToken token)
    {
        int operationId;
        lock (_recordingStartGate) operationId = _operationId;
        return MonitorOwnedRecordingSilenceAsync(mode, operationId, token);
    }

    private bool OwnsRecording(RecordingMode mode, int operationId, CancellationToken token)
    {
        lock (_recordingStartGate)
            return !token.IsCancellationRequested && operationId == _operationId
                && KeyboardWtfState.IsRecording && KeyboardWtfState.CurrentRecordingMode == mode
                && _autoCompleteInProgress == 0;
    }

    private async Task MonitorOwnedRecordingSilenceAsync(RecordingMode mode, int operationId, CancellationToken token)
    {
        using var updateWork = AcquireUpdateWork();
        var smartWriting = mode == RecordingMode.VoiceAssistant;
        var heardVoice = false;
        var heardStrongVoice = false;
        var lastVoiceUtc = DateTime.UtcNow;
        var lastStrongVoiceUtc = DateTime.UtcNow;
        var lastVeryStrongVoiceUtc = DateTime.UtcNow;

        try
        {
            while (OwnsRecording(mode, operationId, token))
            {
                await Task.Delay(100, token);
                if (!OwnsRecording(mode, operationId, token)) return;
                // The existing capture owner, not another callback lifecycle, recovers
                // the accumulated recording if the input device stops unexpectedly.
                if (_audioRecorder.LastRecordingError != null)
                {
                    await CompleteOwnedRecordingAsync(operationId, mode);
                    return;
                }
                var now = DateTime.UtcNow;
                var elapsed = now - _recordingStartUtc;
                var peakDb = _audioRecorder.CurrentPeakDb;

                if (peakDb > -36)
                {
                    heardVoice = true;
                    lastVoiceUtc = now;
                }
                else if (heardVoice && peakDb > -43)
                {
                    lastVoiceUtc = now;
                }

                if (peakDb > -29)
                {
                    heardStrongVoice = true;
                    lastStrongVoiceUtc = now;
                }
                if (peakDb > -20)
                    lastVeryStrongVoiceUtc = now;

                var quietPause = now - lastVoiceUtc > (smartWriting
                    ? SmartWritingFinalSilence
                    : TimeSpan.FromMilliseconds(800));
                var noisyRoomPause = heardStrongVoice
                    && now - lastStrongVoiceUtc > (smartWriting
                        ? SmartWritingFinalSilence + TimeSpan.FromSeconds(2)
                        : TimeSpan.FromMilliseconds(1200));
                var stablePreviewPause = smartWriting
                    && HasSmartWritingFinalPreviewPause(
                        _lastPreviewUtc,
                        lastVeryStrongVoiceUtc,
                        now);
                var minimumSpeechWindow = smartWriting
                    ? TimeSpan.FromSeconds(2)
                    : TimeSpan.FromMilliseconds(650);
                if (heardVoice
                    && elapsed > minimumSpeechWindow
                    && (quietPause || noisyRoomPause || stablePreviewPause))
                {
                    if (smartWriting)
                    {
                        AppLog.Info(
                            "Smart Writing final silence detected: " +
                            $"recordingMs={(long)elapsed.TotalMilliseconds} " +
                            $"silenceMs={(long)(now - lastVoiceUtc).TotalMilliseconds} " +
                            $"previewQuietMs={(long)(now - _lastPreviewUtc).TotalMilliseconds} " +
                            $"reason={(stablePreviewPause ? "preview_stable" : noisyRoomPause ? "noise_floor" : "quiet")}");
                        _notifications.Info("Processing", "Finishing Smart Writing...");
                    }
                    else
                    {
                        _notifications.Info("Processing", "Pause detected.");
                    }
                    await CompleteOwnedRecordingAsync(operationId, mode);
                    return;
                }

                if (!smartWriting && !heardVoice && elapsed.TotalSeconds > 5)
                {
                    _notifications.Warning("No speech", "Stopped listening after silence.");
                    await CompleteOwnedRecordingAsync(operationId, mode);
                    return;
                }
            }
        }
        catch (TaskCanceledException) { }
        catch (Exception ex)
        {
            AppLog.Warning(ex, "Silence watcher failed");
        }
    }

    private async Task ToggleModeAsync(
        RecordingMode mode,
        Func<string, Task> handleTranscript,
        bool preferFast)
    {
        if (KeyboardWtfState.IsProcessing || KeyboardWtfState.IsProcessingAi)
        {
            Cancel();
            return;
        }

        if (!KeyboardWtfState.IsRecording)
        {
            _audioCompletionHandler = null;
            _completionHandler = handleTranscript;
            _preferFastRecognition = preferFast;
            Start(mode);
            return;
        }

        if (KeyboardWtfState.CurrentRecordingMode == mode)
            await CompleteRecordingAsync();
        else
            _notifications.Warning("Already listening", "Finish or cancel the current voice mode first.");
    }

    private async Task ToggleAudioModeAsync(
        RecordingMode mode,
        Func<SmartWritingAudioCapture, Task> handleAudio)
    {
        if (KeyboardWtfState.IsProcessing || KeyboardWtfState.IsProcessingAi)
        {
            Cancel();
            return;
        }

        if (!KeyboardWtfState.IsRecording)
        {
            _completionHandler = null;
            _audioCompletionHandler = handleAudio;
            _preferFastRecognition = false;
            Start(mode);
            return;
        }

        if (KeyboardWtfState.CurrentRecordingMode == mode)
            await CompleteRecordingAsync();
        else
            _notifications.Warning("Already listening", "Finish or cancel the current voice mode first.");
    }

    private Task CompleteRecordingAsync() => CompleteOwnedRecordingAsync();

    private async Task<bool> CompleteOwnedRecordingAsync(int? expectedOperationId = null, RecordingMode? expectedMode = null,
        bool smartWritingAction = false)
    {
        int recordingOperationId;
        RecordingMode mode;
        Func<string, Task> handler;
        Func<SmartWritingAudioCapture, Task> audioHandler;
        lock (_recordingStartGate)
        {
            if (!KeyboardWtfState.IsRecording || _recordingStartInProgress || _autoCompleteInProgress != 0
                || expectedOperationId.HasValue && expectedOperationId != _operationId
                || expectedMode.HasValue && expectedMode != KeyboardWtfState.CurrentRecordingMode
                || smartWritingAction && (KeyboardWtfState.IsProcessing || KeyboardWtfState.IsProcessingAi))
                return false;
            _autoCompleteInProgress = 1;
            recordingOperationId = _operationId;
            mode = KeyboardWtfState.CurrentRecordingMode;
            handler = _completionHandler;
            audioHandler = _audioCompletionHandler;
        }

        IDisposable updateWork;
        try
        {
            // Keep update admission outside the pre-existing completion-error
            // handler. A frozen update must refuse before audio stop/handler
            // effects, rather than masquerading as a normal voice failure.
            updateWork = AcquireUpdateWork();
        }
        catch
        {
            lock (_recordingStartGate) _autoCompleteInProgress = 0;
            throw;
        }
        try
        {
            // The existing completion chain owns final audio drain, recognition,
            // and the awaited destination/Smart Writing handler. Keep it busy
            // until that original chain actually unwinds; cancellation/UI state
            // alone does not release this finite work owner.
            // Translation and destination formatting from one capture share backend
            // ownership; Raw Dictation remains entirely outside AI configuration.
            using var backendScope = mode == RecordingMode.Dictate ? null : AiProviderRegistry.BeginBackendScope();
            if (mode == RecordingMode.VoiceAssistant && audioHandler != null)
            {
                var capture = await StopAndCaptureSmartWritingAudioAsync(recordingOperationId);
                if (capture.WavData.Length >= 44 && _processingCts?.IsCancellationRequested != true)
                    await audioHandler(capture);
            }
            else
            {
                var transcript = await StopAndTranscribeAsync(
                    updateTranscript: false,
                    preferFast: _preferFastRecognition,
                    keepProcessingUntilCompletionHandler: true);
                if (_processingCts?.IsCancellationRequested == true)
                    return true;
                // Recover writing, but never execute/send a truncated spoken command.
                if (_audioRecorder.LastRecordingError != null
                    && !CanDeliverInterruptedRecording(mode))
                {
                    if (!string.IsNullOrWhiteSpace(transcript))
                        KeyboardWtfState.SetTranscript(transcript);
                    KeyboardWtfState.SetUi(
                        VoiceUiPhase.Error,
                        "Recording interrupted",
                        "Captured words were kept in keyboard.wtf. No command was executed or message sent.");
                    return true;
                }
                if (!string.IsNullOrWhiteSpace(transcript) && handler != null)
                    await handler(transcript);
            }
        }
        catch (OperationCanceledException)
        {
            if (_processingCts?.IsCancellationRequested != true)
                KeyboardWtfState.SetUi(VoiceUiPhase.Cancelled, "Cancelled", "Voice processing was stopped.");
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "Voice completion failed");
            if (_processingCts?.IsCancellationRequested != true)
            {
                KeyboardWtfState.SetUi(VoiceUiPhase.Error, "Voice action failed", ex.Message);
                _notifications.Error("Voice action failed", ex.Message);
            }
        }
        finally
        {
            lock (_recordingStartGate)
            {
                _completionHandler = null;
                _audioCompletionHandler = null;
                _preferFastRecognition = false;
                _deliveryTarget = null;
                KeyboardWtfState.IsProcessing = false;
                _autoCompleteInProgress = 0;
            }
            updateWork?.Dispose();
        }
        return true;
    }

    private IDisposable AcquireUpdateWork()
    {
        if (_updateWorkAdmission is null) return null;
        if (!_updateWorkAdmission.TryAcquire(out var lease))
            throw new InvalidOperationException("An update is preparing installation. Please wait.");
        return lease;
    }

    internal static bool CanDeliverInterruptedRecording(RecordingMode mode) =>
        mode is RecordingMode.Dictate or RecordingMode.VoiceAssistant;

    internal static bool HasSmartWritingFinalPreviewPause(
        DateTime lastPreviewUtc,
        DateTime lastVeryStrongVoiceUtc,
        DateTime nowUtc) =>
        lastPreviewUtc != DateTime.MinValue
        && nowUtc - lastPreviewUtc > SmartWritingFinalSilence
        && nowUtc - lastVeryStrongVoiceUtc > TimeSpan.FromSeconds(2);

    private async Task<SmartWritingAudioCapture> StopAndCaptureSmartWritingAudioAsync(int recordingOperationId)
    {
        int operationId;
        CancellationToken token;
        lock (_recordingStartGate)
        {
            if (!KeyboardWtfState.IsRecording || recordingOperationId != _operationId
                || KeyboardWtfState.CurrentRecordingMode != RecordingMode.VoiceAssistant)
                return new SmartWritingAudioCapture([], 0);
            _autoStopCts?.Cancel();
            _silenceCts?.Cancel();
            KeyboardWtfState.IsRecording = false;
            KeyboardWtfState.IsProcessing = true;
            operationId = Interlocked.Increment(ref _operationId);
            _processingCts?.Cancel();
            _processingCts?.Dispose();
            _processingCts = new CancellationTokenSource();
            token = _processingCts.Token;
        }
        var wavData = await _audioRecorder.StopRecordingAsync();
        lock (_recordingStartGate)
        {
            if (operationId != _operationId || token.IsCancellationRequested)
                return new SmartWritingAudioCapture([], 0);
            ReportCaptureInterruption();
            LastRecordingDurationMs = Math.Max(
                0,
                (long)(DateTime.UtcNow - _recordingStartUtc).TotalMilliseconds);
            LastTranscriptionDurationMs = 0;
            StopLivePreview();
            KeyboardWtfState.IsRecording = false;
            KeyboardWtfState.CurrentRecordingMode = RecordingMode.None;
            KeyboardWtfState.ActiveQuickSendTarget = null;
            KeyboardWtfState.IsProcessing = true;
            KeyboardWtfState.SetUi(
                VoiceUiPhase.Transcribing,
                "Understanding speech",
                "Understanding your recording...");
            return new SmartWritingAudioCapture(wavData, LastRecordingDurationMs);
        }
    }

    private void ReportCaptureInterruption()
    {
        if (_audioRecorder.LastRecordingError != null)
            _notifications.Warning(
                "Microphone interrupted",
                "Processing the audio captured before the microphone stopped. Speech after that point was not recorded.");
    }

    private static string ModeTitle(RecordingMode mode) => mode switch
    {
        RecordingMode.Dictate => "Dictation",
        RecordingMode.VoiceAssistant => "Smart writing",
        RecordingMode.Command => "Execute command",
        RecordingMode.JarvisPreflight => "Jarvis",
        RecordingMode.QuickSend => "Quick send",
        _ => "Listening",
    };

    private void StartLivePreview()
    {
        StopLivePreview();
        _streamingSession = _speechRecognition.CreateStreamingSession();
        if (_streamingSession == null)
            return;

        _lastPreview = "";
        _lastPreviewUtc = DateTime.MinValue;
        _audioRecorder.PcmDataAvailable += OnPcmDataAvailable;
    }

    private void OnPcmDataAvailable(byte[] buffer, int count)
    {
        var operationId = Volatile.Read(ref _operationId);
        try
        {
            var preview = _streamingSession?.AcceptPcm(buffer, count) ?? "";
            if (string.IsNullOrWhiteSpace(preview)
                || string.Equals(preview, _lastPreview, StringComparison.Ordinal)
                || DateTime.UtcNow - _lastPreviewUtc < TimeSpan.FromMilliseconds(180))
                return;

            lock (_recordingStartGate)
            {
                if (operationId != _operationId || !KeyboardWtfState.IsRecording)
                    return;
                _lastPreview = preview;
                _lastPreviewUtc = DateTime.UtcNow;
                KeyboardWtfState.SetUi(
                    VoiceUiPhase.Listening,
                    ModeTitle(KeyboardWtfState.CurrentRecordingMode),
                    preview);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning(ex, "Live Vosk preview failed");
            StopLivePreview();
        }
    }

    private void StopLivePreview()
    {
        _audioRecorder.PcmDataAvailable -= OnPcmDataAvailable;
        _streamingSession?.Dispose();
        _streamingSession = null;
        _lastPreview = "";
    }

    private static bool IsMicrophoneAvailable(out string reason)
    {
        try
        {
            return IsMicrophoneSelectionAvailable(
                WaveInEvent.DeviceCount,
                KeyboardWtfState.SelectedMicrophoneIndex,
                out reason);
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }

    internal static bool IsMicrophoneSelectionAvailable(
        int deviceCount,
        int selectedIndex,
        out string reason)
    {
        if (deviceCount <= 0)
        {
            reason = "No microphone detected.";
            return false;
        }
        if (selectedIndex >= deviceCount)
        {
            reason = $"Selected microphone {selectedIndex} is not available.";
            return false;
        }
        if (selectedIndex < AudioRecorderService.SystemDefaultDeviceNumber)
        {
            reason = $"Selected microphone {selectedIndex} is invalid.";
            return false;
        }
        reason = null;
        return true;
    }
}

internal sealed record SmartWritingAudioCapture(byte[] WavData, long RecordingDurationMs);
