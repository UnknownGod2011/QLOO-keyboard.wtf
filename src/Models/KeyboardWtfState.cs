namespace KeyboardWtf.Models;

public enum SpeechEngine
{
    Vosk,
    Whisper
}

public enum RecordingMode
{
    None,
    Dictate,
    VoiceNote,
    PushToTalk,
    QuickSend,
    VoiceAssistant,
    Command,
    JarvisPreflight
}

public enum VoiceUiPhase
{
    Idle,
    Listening,
    Transcribing,
    Thinking,
    Executing,
    Speaking,
    Done,
    Cancelled,
    Error
}

public enum WhisperModelSize
{
    Base,   // ~140 MB - fast, lower accuracy with accents
    Small   // ~460 MB - slower, much better with accents
}

public enum DestinationCategory
{
    Raw,   // Clipboard, Type: transcript sent as-is, no AI filtering
    Ai     // Email, Slack, Discord, Teams, Calendar: AI reformats per destination's AiPrompt
}

public enum CaseTransform
{
    None,
    Upper,
    Lower,
    Title
}

public enum AssistantTone
{
    Balanced,
    Concise,
    Friendly,
    Professional
}

public enum AiProvider
{
    Claude,
    OpenAi,
    Gemini,
    DeepSeek,
    Perplexity
}

public class ChatMessage
{
    public string Role { get; set; }
    public string Content { get; set; }
}

// Central runtime state shared by the tray app, hotkeys, capture pipeline, and settings service.
public static class KeyboardWtfState
{
    private static readonly object UiStateLock = new();
    private static long _uiRevision;
    private static string _longTaskOperationId = "";
    private static string _longTaskDetail = "";
    private static string _longTaskConnectorId = "";
    public static SpeechEngine SelectedEngine { get; set; } = SpeechEngine.Whisper;
    public static string SelectedLanguage { get; set; } = "auto";
    public static int SelectedMicrophoneIndex { get; set; } = -1; // -1 = system default
    public static int MaxRecordingSeconds { get; set; } = 30;
    public static WhisperModelSize SelectedWhisperModel { get; set; } = WhisperModelSize.Small;
    public static string VoiceNoteSavePath { get; set; } // null/empty = use default
    public static string EffectiveVoiceNoteSavePath =>
        string.IsNullOrWhiteSpace(VoiceNoteSavePath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), QlooProduct.Name)
            : VoiceNoteSavePath;

    public const string DefaultVoiceNoteFilenamePattern = "yyyy-MM-dd_HH-mm-ss";
    public static string VoiceNoteFilenamePattern { get; set; } // null/empty = use default

    // 5 entries is enough context for "go back and resend that" use cases without
    // making transcript history unnecessarily large.
    public const int MaxHistorySize = 5;
    public static List<string> TranscriptHistory { get; } = new();
    public static bool AppendMode { get; set; }

    private static string _currentTranscript = string.Empty;
    public static string CurrentTranscript
    {
        get => _currentTranscript;
        set
        {
            if (!string.IsNullOrWhiteSpace(value) &&
                (TranscriptHistory.Count == 0 || TranscriptHistory[0] != value))
            {
                TranscriptHistory.Insert(0, value);
                if (TranscriptHistory.Count > MaxHistorySize)
                    TranscriptHistory.RemoveAt(TranscriptHistory.Count - 1);
            }
            _currentTranscript = value;
            TranscriptChanged?.Invoke();
        }
    }
    public static event Action TranscriptChanged;

    // Append mode is intentionally single-level: recordings concatenate into one flat string
    // rather than forming a list. This keeps Send/AI formatting simple — one prompt, one result.
    // Future: multi-segment transcripts with per-segment timestamps would enable selective editing.
    public static void SetTranscript(string transcript)
    {
        if (AppendMode && !string.IsNullOrWhiteSpace(_currentTranscript) && !string.IsNullOrWhiteSpace(transcript))
            CurrentTranscript = _currentTranscript.TrimEnd() + " " + transcript;
        else
            CurrentTranscript = transcript;
    }

    // Two independent indices, one per dial. Each cycles within its own category's destinations.
    public static int SelectedRawIndex { get; set; }
    public static int SelectedAiIndex { get; set; }
    // Whichever dial the user last scrolled is the "active" one that SendAction targets.
    public static DestinationCategory ActiveCategory { get; set; } = DestinationCategory.Ai;
    public static Dictionary<string, string> FormattedOutputs { get; } = new();
    public static bool IsRecording { get; set; }
    public static RecordingMode CurrentRecordingMode { get; set; } = RecordingMode.None;
    public static string ActiveQuickSendTarget { get; set; } // destination name during a QuickSend recording
    public static bool IsProcessing { get; set; }
    public static bool IsProcessingAi { get; set; }
    public static VoiceUiPhase UiPhase { get; private set; } = VoiceUiPhase.Idle;
    public static string UiTitle { get; private set; } = QlooProduct.Name;
    public static string UiDetail { get; private set; } = "Ready";
    public static DateTime UiPhaseStartedUtc { get; private set; } = DateTime.UtcNow;
    public static event Action UiChanged;
    public static double InputLevelDb { get; set; } = -96;
    public static bool IsDownloadingModels { get; set; }
    public static string ModelDownloadStatus { get; set; }
    public static bool IsInstallingPiper { get; set; }
    public static string PiperInstallStatus { get; set; }
    public static string LastSendResult { get; set; }
    public static string LastSendDestination { get; set; }
    public static string AssistantName { get; set; } = "Jarvis";
    public static AssistantTone AssistantTone { get; set; } = AssistantTone.Balanced;
    public static string GeminiVoice { get; set; } = "Kore";

    // Background task feedback is independent of speech/listening state.
    // A late completion may clear only its own operation's visible detail.
    internal static string LongTaskDetail { get { lock (UiStateLock) return _connectorBetweenSteps ? "" : _longTaskDetail; } }
    // Visibility follows the retained visual owner, not the response-health
    // detail (which is intentionally empty between connector steps).
    internal static bool HasLongTaskUiOwner { get { lock (UiStateLock) return _longTaskOperationId.Length > 0 && _longTaskDetail.Length > 0; } }
    // Keep the connector's visual owner between dependent reads without
    // pretending a provider call is running or suppressing response health.
    private static bool _connectorBetweenSteps;

    // Display grace does not delay or hide ownership from response health.
    // The overlay's existing refresh timer reads elapsed real task time.
    private static long _longTaskStartedTimestamp;
    private static int _longTaskDisplayDelayMilliseconds;

    internal static string LongTaskDisplayDetail(TimeSpan? elapsedOverride = null)
        => LongTaskDisplayState(elapsedOverride).Detail;

    // Provider decoration is part of the existing UI owner, not a new task or
    // voice phase. Read detail and provider together to avoid mismatched icons.
    internal static (string Detail, string ConnectorId) LongTaskDisplayState(TimeSpan? elapsedOverride = null)
    {
        lock (UiStateLock)
        {
            if (_longTaskDetail.Length == 0) return ("", "");
            var elapsed = elapsedOverride ?? System.Diagnostics.Stopwatch.GetElapsedTime(_longTaskStartedTimestamp);
            return elapsed.TotalMilliseconds >= _longTaskDisplayDelayMilliseconds
                ? (_longTaskDetail, _longTaskConnectorId) : ("", "");
        }
    }

    internal static void BeginLongTaskUi(string operationId, string detail, int displayDelayMilliseconds = 0)
    {
        if (string.IsNullOrWhiteSpace(operationId) || string.IsNullOrWhiteSpace(detail)) return;
        lock (UiStateLock)
        {
            _longTaskOperationId = operationId;
            _longTaskDetail = detail;
            _longTaskConnectorId = "";
            _connectorBetweenSteps = false;
            _longTaskStartedTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            _longTaskDisplayDelayMilliseconds = Math.Max(0, displayDelayMilliseconds);
        }
        try { UiChanged?.Invoke(); }
        catch { /* Visual feedback cannot affect the owned task. */ }
    }

    // New task owners may acquire idle feedback without displacing an
    // accepted translation/browser task. Existing callers retain their
    // original lifecycle and replacement behavior unchanged.
    internal static bool TryBeginLongTaskUiIfIdle(string operationId, string detail, int displayDelayMilliseconds = 0,
        string connectorId = null)
    {
        if (string.IsNullOrWhiteSpace(operationId) || string.IsNullOrWhiteSpace(detail)) return false;
        lock (UiStateLock)
        {
            if (_longTaskOperationId.Length != 0 && _longTaskOperationId != operationId) return false;
            var sameOwner = _longTaskOperationId == operationId;
            _longTaskOperationId = operationId;
            _longTaskDetail = detail;
            _longTaskConnectorId = connectorId ?? "";
            _connectorBetweenSteps = false;
            if (!sameOwner)
            {
                _longTaskStartedTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
                _longTaskDisplayDelayMilliseconds = Math.Max(0, displayDelayMilliseconds);
            }
        }
        try { UiChanged?.Invoke(); }
        catch { /* Visual feedback cannot affect the owned task. */ }
        return true;
    }

    internal static bool TryTransferConnectorTaskUi(string previousOwner, string taskOwner, string detail, string connectorId)
    {
        if (string.IsNullOrWhiteSpace(taskOwner) || string.IsNullOrWhiteSpace(connectorId)) return false;
        lock (UiStateLock)
        {
            if (_longTaskOperationId != previousOwner || _longTaskConnectorId != connectorId) return false;
            _longTaskOperationId = taskOwner; _longTaskDetail = detail; _connectorBetweenSteps = true;
            // Preserve the display clock; no hide/show or second grace window.
        }
        try { UiChanged?.Invoke(); } catch { }
        return true;
    }

    internal static bool TryUpdateLongTaskUi(string operationId, string detail, bool connectorBetweenSteps = false)
    {
        lock (UiStateLock)
        {
            if (string.IsNullOrWhiteSpace(operationId) || _longTaskOperationId != operationId) return false;
            _longTaskDetail = detail ?? "";
            _connectorBetweenSteps = connectorBetweenSteps && _longTaskConnectorId.Length > 0 && _longTaskDetail.Length > 0;
            if (_longTaskDetail.Length == 0) { _longTaskOperationId = ""; _longTaskConnectorId = ""; }
        }
        try { UiChanged?.Invoke(); }
        catch { /* Visual feedback cannot affect the owned task. */ }
        return true;
    }

    internal static bool ClearLongTaskUi(string operationId) => TryUpdateLongTaskUi(operationId, null);

    public static long SetUi(VoiceUiPhase phase, string title, string detail)
    {
        long revision;
        lock (UiStateLock)
        {
            UiPhase = phase;
            UiTitle = title ?? QlooProduct.Name;
            UiDetail = detail ?? "";
            UiPhaseStartedUtc = DateTime.UtcNow;
            revision = ++_uiRevision;
        }
        try { UiChanged?.Invoke(); }
        catch { /* UI feedback must never break the active operation. */ }
        return revision;
    }

    internal static long UiRevision { get { lock (UiStateLock) return _uiRevision; } }

    internal static long TrySetListeningAdvisory(long expectedRevision, string title, string detail)
    {
        long revision;
        lock (UiStateLock)
        {
            if (_uiRevision != expectedRevision || UiPhase != VoiceUiPhase.Listening
                || (_longTaskDetail.Length != 0 && !_connectorBetweenSteps)) return 0;
            UiTitle = title;
            UiDetail = detail;
            revision = ++_uiRevision;
        }
        try { UiChanged?.Invoke(); }
        catch { /* Diagnostic feedback must not affect the conversation. */ }
        return revision;
    }

    public static bool TrySetUiIfRevision(
        long expectedRevision,
        VoiceUiPhase phase,
        string title,
        string detail)
    {
        lock (UiStateLock)
        {
            if (_uiRevision != expectedRevision)
                return false;
            UiPhase = phase;
            UiTitle = title ?? QlooProduct.Name;
            UiDetail = detail ?? "";
            UiPhaseStartedUtc = DateTime.UtcNow;
            _uiRevision++;
        }
        try { UiChanged?.Invoke(); }
        catch { /* UI feedback must never break the active operation. */ }
        return true;
    }

    // Noise gate: silence frames below -38 dBFS before sending to speech engines
    public static bool UseNoiseGate { get; set; } = true;

    // Transcription post-processing
    // Empty string means "off" rather than null so settings serialization is clean (no null checks in GET /api/settings).
    public static string TranslateTargetLanguage { get; set; } = ""; // "" = off, "en"/"de"/"fr"/"es"/"it" etc.
    public static bool UseFillerWordCleaner { get; set; } = false;
    public static CaseTransform SelectedCaseTransform { get; set; } = CaseTransform.None;
    public static string DetectedLanguage { get; set; }

    // AI toggle + custom prompts per destination
    public static bool UseAi { get; set; } = true;
    public static Dictionary<string, string> CustomPrompts { get; } = new();

    public static string GetEffectivePrompt(string destinationName, string defaultPrompt)
    {
        if (CustomPrompts.TryGetValue(destinationName, out var custom) && !string.IsNullOrWhiteSpace(custom))
            return custom;
        return defaultPrompt;
    }

    // AI provider selection + keys
    public static AiProvider SelectedAiProvider { get; set; } = AiProvider.Claude;
    public static string ClaudeApiKey { get; set; }
    public static string OpenAiApiKey { get; set; }
    public static string GeminiApiKey { get; set; }
    public static string DeepSeekApiKey { get; set; }
    public static string PerplexityApiKey { get; set; }
    public static bool IsClaudeAvailable => !string.IsNullOrEmpty(ClaudeApiKey);

    // Destination API keys/URLs
    public static string SlackWebhookUrl { get; set; }
    public static string DiscordWebhookUrl { get; set; }
    public static string TeamsWebhookUrl { get; set; }
}
