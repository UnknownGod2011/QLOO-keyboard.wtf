namespace KeyboardWtf.Services;

using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using KeyboardWtf.Destinations;
using KeyboardWtf.Helpers;
using KeyboardWtf.Models;
using System.Windows.Forms;
using KeyboardWtf.Services.Ai;
using KeyboardWtf.Services.BrowserAutomationV2;
using NAudio.Wave;

public sealed class CommandRegistry
{
    internal static readonly TimeSpan SmartWritingTimeout = TimeSpan.FromSeconds(40);
    internal const int SmartWritingProviderSegmentSeconds = 240;
    internal const string SmartWritingSystemInstruction =
        "You are a high-quality speech-to-writing editor. Listen carefully to the attached speech and return " +
        "only the polished text the user intends to type. Perform a real editorial cleanup rather than a " +
        "near-verbatim transcript. Mandatory cleanup: remove non-semantic fillers, hesitation sounds, and empty " +
        "discourse particles such as um, uh, er, hmm, like, you know, or an opening so/well when they carry no " +
        "meaning; collapse accidental repeated words or phrases; and remove or tighten empty spoken lead-ins. Compress " +
        "wordy speech scaffolding without changing intent—for example, 'I was thinking that we should' becomes 'I " +
        "think we should.' When the speaker " +
        "abandons or corrects wording, discard the abandoned clause and correction markers themselves and retain only " +
        "the final intended version—for example, 'Schedule it—no, scratch that—send it tomorrow' becomes 'Send it " +
        "tomorrow.' Rewrite awkward speech-only sentence structure into natural, grammatically correct written " +
        "language with appropriate punctuation and capitalization. Preserve the complete meaning, tone, substantive " +
        "details, names, technical terminology, URLs, numbers, and intentional emphasis or repetition. Do not " +
        "summarize or omit substantive content. Do not make the writing unnecessarily formal. Do not answer the " +
        "user, explain anything, perform actions, or add commentary. After drafting, perform a separate silent final " +
        "editorial pass over every sentence. Remove any remaining standalone um, uh, erm, hmm, meaningless filler, " +
        "accidental duplicate, abandoned clause, or correction marker unless it is intentional quoted content. Output only the " +
        "final text to insert.";
    private readonly KeyboardWtfApp _app;
    private readonly VoiceCaptureService _capture;
    private readonly DestinationRouter _router;
    private readonly SettingsService _settings;
    private readonly NotificationService _notifications;
    private readonly GeminiLiveConversationService _liveConversation;
    private readonly JarvisAutomationService _automation;
    private readonly JarvisActionHistoryService _history;
    private readonly BrowserAutomationCoordinator _browserAutomation;
    private readonly SmartActionPlanner _actionPlanner = new();
    private readonly object _browserPreRouteLock = new();
    private CancellationTokenSource _actionCts;
    private BrowserPreRouteReceipt _browserPreRouteReceipt;
    private string _awaitingLocalTextEcho = "";
    private DateTime _awaitingLocalTextEchoUtc;
    private bool _browserPreRouteOwnsCurrentTurn;
    private bool _completedDirectOpenForCurrentTurn;
    private readonly Dictionary<string, Dictionary<string, object>> _resolvedOpenReceipts = new(StringComparer.Ordinal);
    private string _latestJarvisUserTurn = "";
    private string _lastCompletedJarvisUserTurn = "";
    private DateTime _latestJarvisUserTurnUtc;
    // One conversation-scoped capture, replaced by every newer explicit
    // selection/clipboard read and cleared when Jarvis ends. It is never saved
    // as memory. Browser tasks may consume it when explicitly authorized; the
    // direct selected-text path also uses it to prevent an instruction echo
    // from being published as the requested result.
    private string _capturedBrowserContextText = "";
    private string _capturedBrowserContextSource = "";
    private string _capturedBrowserContextUserTurn = "";
    private DateTime _capturedBrowserContextUtc;
    private bool _capturedBrowserContextTruncated;
    private bool _capturedBrowserContextIsCurrentTurn;
    private bool _capturedBrowserContextAuthorizedForLaterAction;
    // Active conversational source plus bounded immutable session history.
    // BA keeps its separate excerpt budget; answers and clipboard reads never
    // replace selection sources. Session end clears all of them.
    private readonly SessionSelectionHistory _selectionHistory = new();
    private string _originalSelectedText = "";
    private string _originalSelectionReadTurn = "";
    private string _originalSelectionContextId = "";
    private string _originalSelectionCaptureId = "";
    private bool _originalSelectionAllowsImplicitFollowup;
    private readonly Func<string> _currentSelectionContextId;
    private readonly Func<CancellationToken, Task<Dictionary<string, object>>> _captureSelectedText;
    private readonly Func<string> _readClipboardText;
    private SelectedTextTranslationOperation _selectedTranslation;
    private int _smartWritingOperationId;
    internal string ConnectorUserRequest => ConnectorUserRequestSnapshot.Text;

    internal Action ReserveFinalConnectorRead(string text, CancellationToken token) =>
        _automation?.Connectors?.ReserveRetainedRead(text, token,
            (url, cancellation) => _automation.ExecuteAsync("open_url", JsonSerializer.SerializeToElement(new { url }), cancellation));
    private string _connectorContinuingUserTurn = "";
    private DateTime _connectorContinuingUserTurnUtc;
    private long _connectorUserTurnIdentity;
    private bool _connectorUserTurnEnded = true;
    internal string ConnectorUserRequestIdentity
    {
        get { lock (_browserPreRouteLock) return _connectorUserTurnIdentity.ToString(System.Globalization.CultureInfo.InvariantCulture); }
    }
    internal Connectors.ConnectorHumanRequestSnapshot ConnectorUserRequestSnapshot
    {
        get
        {
            lock (_browserPreRouteLock)
            {
                var now = DateTime.UtcNow;
                var request = now - _latestJarvisUserTurnUtc <= TimeSpan.FromSeconds(60) ? _latestJarvisUserTurn : "";
                if (request.Length == 0 && !_connectorUserTurnEnded
                    && now - _connectorContinuingUserTurnUtc <= TimeSpan.FromSeconds(60))
                    request = _connectorContinuingUserTurn;
                return new(request, _connectorUserTurnIdentity.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }
    }

    public CommandRegistry(
        KeyboardWtfApp app,
        VoiceCaptureService capture,
        DestinationRouter router,
        SettingsService settings,
        NotificationService notifications,
        GeminiLiveConversationService liveConversation = null,
        JarvisAutomationService automation = null,
        JarvisActionHistoryService history = null,
        BrowserAutomationCoordinator browserAutomation = null,
        Func<string> currentSelectionContextId = null,
        Func<CancellationToken, Task<Dictionary<string, object>>> captureSelectedText = null,
        Func<string> readClipboardText = null)
    {
        _app = app;
        _capture = capture;
        _router = router;
        _settings = settings;
        _notifications = notifications;
        _liveConversation = liveConversation;
        _automation = automation;
        _history = history;
        _browserAutomation = browserAutomation;
        _currentSelectionContextId = currentSelectionContextId
            ?? (() => _automation?.CurrentSelectionContextId() ?? "");
        _captureSelectedText = captureSelectedText
            ?? (token => _automation.ExecuteAsync("get_selected_text",
                JsonSerializer.SerializeToElement(new { include_text = true }), token));
        _readClipboardText = readClipboardText ?? ClipboardHelper.GetText;
    }

    public void TogglePushToTalk() => _ = TogglePushToTalkAsync();
    public void ToggleSmartMode()
    {
        if (KeyboardWtfState.IsProcessingAi)
        {
            CancelCurrent();
            return;
        }
        if (StopLiveBeforeAnotherVoiceMode("Smart Writing"))
            return;
        // Wake the authenticated REST path while the user is speaking. This
        // endpoint never reserves credits or invokes Vertex, and a failed
        // warmup must never prevent local recording from starting.
        _ = _liveConversation?.WarmPaidBackendAsync();
        _ = _capture.ToggleSmartModeAsync(HandleSmartWritingAsync);
    }
    public void ToggleDictation()
    {
        if (KeyboardWtfState.IsProcessingAi)
        {
            CancelCurrent();
            return;
        }
        if (StopLiveBeforeAnotherVoiceMode("Dictation"))
            return;
        _ = _capture.ToggleDictationAsync(DeliverRawDictationAsync);
    }
    public void ToggleCommandMode()
    {
        if (KeyboardWtfState.IsProcessingAi)
        {
            CancelCurrent();
            return;
        }
        if (StopLiveBeforeAnotherVoiceMode("Command"))
            return;
        _ = _capture.ToggleCommandModeAsync(HandleSmartExecutionAsync);
    }
    public void ToggleConversation() => ToggleJarvisMode();
    public void ToggleJarvisMode()
    {
        if (_liveConversation == null)
            return;
        if (KeyboardWtfState.IsProcessingAi)
        {
            CancelCurrent();
            return;
        }
        if (_liveConversation.IsActive || _liveConversation.IsStarting)
        {
            _ = _liveConversation.StopAsync();
            return;
        }
        // The dedicated Jarvis shortcut is explicit user intent. Start the
        // authenticated Live transport immediately; no local transcription or
        // speech-activity preflight owns this path.
        _ = _liveConversation.StartAsync();
    }
    public void CancelCurrent() => _ = CancelCurrentAsync();
    public Task CancelCurrentAndWaitAsync() => CancelCurrentAsync();
    public void QuickSendDefault() => _ = _capture.QuickSendAsync(_settings.Current.DefaultDestination ?? "Clipboard");
    public void OpenSettings() => _app.OpenSettings();
    public object PendingSensitiveAction => _automation?.PendingSnapshot();
    internal static bool UsesJarvisTranscriptPreflight => false;

    public async Task HandleJarvisSessionEndingAsync()
    {
        Interlocked.Exchange(ref _selectedTranslation, null)?.Dispose();
        lock (_browserPreRouteLock)
        {
            _connectorUserTurnEnded = true;
            _connectorContinuingUserTurn = "";
            _connectorContinuingUserTurnUtc = default;
            _browserPreRouteReceipt = null;
            _browserPreRouteOwnsCurrentTurn = false;
            _completedDirectOpenForCurrentTurn = false;
            _resolvedOpenReceipts.Clear();
            _capturedBrowserContextIsCurrentTurn = false;
            _latestJarvisUserTurn = "";
            _lastCompletedJarvisUserTurn = "";
            _latestJarvisUserTurnUtc = default;
            _awaitingLocalTextEcho = "";
            _awaitingLocalTextEchoUtc = default;
            ClearCapturedBrowserContextLocked();
            _originalSelectedText = "";
            _originalSelectionReadTurn = "";
            _originalSelectionContextId = "";
            _originalSelectionCaptureId = "";
            _originalSelectionAllowsImplicitFollowup = false;
            _selectionHistory.Clear();
        }
        _automation?.ResetConversationTransientContext();
        if (_automation?.Connectors is { } connectors) await connectors.ResetAsync();
        if (_browserAutomation != null)
        {
            // Conversation shutdown owns the user-visible terminal outcome.
            // Browser cleanup must therefore be silent: otherwise a Partial
            // task can publish a late local "Cancelled" response after the
            // Live socket has already closed (including after a farewell).
            await _browserAutomation.CancelAsync(
                "Jarvis conversation ended",
                announceOutcome: false);
            _browserAutomation.ClearRecentTerminalOutcomes();
        }
    }

    private bool StopLiveBeforeAnotherVoiceMode(string requestedMode)
    {
        if (_liveConversation?.IsActive != true && _liveConversation?.IsStarting != true)
            return false;
        _ = _liveConversation.StopAsync();
        _notifications.Info(
            "Jarvis stopping",
            $"Press the {requestedMode} shortcut again after Jarvis closes.");
        return true;
    }
    public void ObserveJarvisTranscript(string text, CancellationToken token) =>
        RecordJarvisTranscript(text, token, fromLocalTextSubmission: false);

    public void ObserveSubmittedJarvisText(string text, CancellationToken token)
    {
        RecordJarvisTranscript(text, token, fromLocalTextSubmission: true);
        ObserveImmediateJarvisControl(text, token)?.Invoke();
    }

    public void CompleteJarvisUserTurn(bool assistantAnswerComplete = true)
    {
        // End action-routing authority, not deferred selection or diagnostic
        // context. Tool-only provider boundaries are not completed answers.
        string completedConnectorTurnIdentity = null;
        lock (_browserPreRouteLock)
        {
            if (assistantAnswerComplete)
            {
                completedConnectorTurnIdentity = _connectorUserTurnIdentity.ToString(System.Globalization.CultureInfo.InvariantCulture);
                _connectorUserTurnEnded = true;
                _connectorContinuingUserTurn = "";
                _connectorContinuingUserTurnUtc = default;
            }
            else if (!string.IsNullOrWhiteSpace(_latestJarvisUserTurn))
            {
                // Connector proposals can span tool-only provider turns. Keep
                // their bounded human input without retaining ordinary routing
                // authority or treating a model callback as fresh user input.
                _connectorContinuingUserTurn = _latestJarvisUserTurn;
                _connectorContinuingUserTurnUtc = _latestJarvisUserTurnUtc;
            }
            // A previous provider response can complete after an explicit
            // browser cancel has already been applied on the receive path.
            // Its turnComplete cannot erase the new turn's one outstanding
            // authoritative result before the model receives that result.
            if (_browserPreRouteReceipt is { Authoritative: true } control
                && control.ToolName is "browser_automation_control" or "selected_text_translation_cancel" or "connector_control"
                && control.IsFresh(DateTime.UtcNow)
                && !control.IsAcknowledged)
                return;

            // A local Settings route is a complete action, but Live can emit
            // a tool-only turnComplete before its spoken answer. That is not
            // a new user request: keep this verified result across that
            // provider boundary so a later model-only website open cannot
            // compete. Release it once the actual answer completes, so a
            // subsequent real request is not held hostage to late ASR.
            // Other ordered action receipts retain their existing behavior.
            if (!assistantAnswerComplete
                && _browserPreRouteReceipt is { Authoritative: true, ToolName: "open_keyboard_wtf_settings" })
                return;

            // A tool-only boundary is not completion of an explicit translation.
            // Keep its existing human authority until the answer, a new request,
            // or session reset, just as for the other owned operations above.
            // Otherwise the next capture/copy proposal loses the translation
            // intent and can publish model narration through ordinary copying.
            if (!assistantAnswerComplete
                && _latestJarvisUserTurn.Contains("translate", StringComparison.OrdinalIgnoreCase)
                && (SelectedTextTranslationOperation.TryParseRequest(_latestJarvisUserTurn,
                        !string.IsNullOrWhiteSpace(_originalSelectedText), out _)
                    || SelectedTextTranslationOperation.TryResolveClipboardTranslationIntent(
                        _latestJarvisUserTurn, out _, out _)))
                return;

            // A saved open already has one local owner and canonical-effect
            // receipts. Retain only that bounded result while Live produces
            // its answer: a model-only retry must not replace a saved project
            // with its homepage or repeat the completed compound destinations.
            // A real answer, new user request or session reset releases the
            // authority; an expired receipt cannot be retained at this boundary.
            // Other tool lifecycles are unchanged.
            if (!assistantAnswerComplete
                && _browserPreRouteReceipt is { Authoritative: true } savedOpen
                && savedOpen.IsFresh(DateTime.UtcNow)
                && (savedOpen.ToolName == "saved_open_set"
                    || savedOpen.ToolName == "open_app"
                        && _resolvedOpenReceipts.Keys.Any(identity => identity.StartsWith("url:", StringComparison.Ordinal))))
                return;

            if (!string.IsNullOrWhiteSpace(_latestJarvisUserTurn))
                _lastCompletedJarvisUserTurn = _latestJarvisUserTurn;
            _browserPreRouteReceipt = null;
            _browserPreRouteOwnsCurrentTurn = false;
            _completedDirectOpenForCurrentTurn = false;
            _resolvedOpenReceipts.Clear();
            _latestJarvisUserTurn = "";
            _latestJarvisUserTurnUtc = default;
            _awaitingLocalTextEcho = "";
            _awaitingLocalTextEchoUtc = default;
        }
        if (assistantAnswerComplete)
        {
            _automation?.CompleteConnectorUserTurn(completedConnectorTurnIdentity);
            _automation?.CompleteUserTurnForFolderChoices();
        }
    }

    // Called on the receive path: only explicit task control is allowed here.
    // The existing coordinator owns cancellation/pause; this closure carries
    // its immutable result into the normal ordered turn/result stream later.
    public Action ObserveImmediateJarvisControl(string text, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var userTurn = (text ?? "").Trim();
        if (userTurn.Length > 0) _automation?.Connectors?.NotifyPendingReadInput(userTurn);
        var translation = _selectedTranslation;
        if (translation?.IsRunning == true
            && (SelectedTextTranslationOperation.IsExplicitCancellation(userTurn)
                || (SelectedTextTranslationOperation.TryParseRequest(userTurn, hasOriginal: true, out _)
                    && !IsSameJarvisUserTurn(translation.Request, userTurn)))
            && translation.Cancel())
        {
            // Only an explicit cancel or a new complete replacement task can
            // stop the job. Ordinary speech/barge-in has no task authority.
            if (!SelectedTextTranslationOperation.IsExplicitCancellation(userTurn))
                return null; // The replacement still needs its normal tool proposal.
            return () =>
            {
                token.ThrowIfCancellationRequested();
                _automation?.ObserveUserTranscript(userTurn);
                lock (_browserPreRouteLock)
                {
                    _latestJarvisUserTurn = BrowserAutomationText.Truncate(userTurn, 4000);
                    _latestJarvisUserTurnUtc = DateTime.UtcNow;
                    _awaitingLocalTextEcho = "";
                    RecordAuthoritativePreRoute(userTurn, "selected_text_translation_cancel",
                        ToolResult(true, "Translation cancelled. No translated result will be copied."));
                }
            };
        }
        if (!TryControlActiveBrowserTask(userTurn, token, out var result))
        {
            // Bare cancellation may own the single connector task only when
            // neither accepted subsystem has an active competing task.
            // Non-control speech never enters connector execution here.
            if (!Connectors.JarvisConnectorController.IsExplicitCancellation(userTurn)
                || translation?.IsRunning == true || _browserAutomation?.Snapshot().CanCancel == true
                || _automation?.Connectors?.TryCancelSingleTask(userTurn, out var connectorResult) != true) return null;
            return () =>
            {
                token.ThrowIfCancellationRequested(); _automation?.ObserveUserTranscript(userTurn);
                lock (_browserPreRouteLock)
                {
                    _latestJarvisUserTurn = BrowserAutomationText.Truncate(userTurn, 4000); _latestJarvisUserTurnUtc = DateTime.UtcNow;
                    _awaitingLocalTextEcho = ""; RecordAuthoritativePreRoute(userTurn, "connector_control", connectorResult);
                }
            };
        }
        return () =>
        {
            token.ThrowIfCancellationRequested();
            _automation?.ObserveUserTranscript(userTurn);
            lock (_browserPreRouteLock)
            {
                _latestJarvisUserTurn = BrowserAutomationText.Truncate(userTurn, 4000);
                _latestJarvisUserTurnUtc = DateTime.UtcNow;
                _awaitingLocalTextEcho = "";
                RecordAuthoritativePreRoute(userTurn, "browser_automation_control", result);
            }
        };
    }

    private bool TryControlActiveBrowserTask(
        string userTurn, CancellationToken token, out Dictionary<string, object> result)
    {
        result = null;
        if (_browserAutomation == null
            || !TryParseExplicitBrowserTaskStopOrPause(userTurn, out var action, out var explicitTarget)
            || !explicitTarget)
            return false;
        var active = _browserAutomation.Snapshot();
        if (!active.CanCancel)
            return false;
        token.ThrowIfCancellationRequested();
        var accepted = action == "pause"
            ? _browserAutomation.Pause()
            : _browserAutomation.RequestCancel(announceOutcome: false);
        result = ToolResult(accepted, accepted
            ? action == "pause" ? "Browser automation paused." : "Browser automation cancelled."
            : "No active browser task accepted that control command.");
        AppLog.Info($"Browser task control accepted by local pre-route: task={active.TaskId} action={action} ok={accepted}");
        return true;
    }

    private void RecordJarvisTranscript(string text, CancellationToken token, bool fromLocalTextSubmission)
    {
        token.ThrowIfCancellationRequested();
        _automation?.ObserveUserTranscript(text);
        var userTurn = (text ?? "").Trim();
        if (userTurn.Length == 0)
            return;

        // Observation updates context, never executes an ordinary action.
        // Keep a completed tool/control receipt across its transcription echo.
        lock (_browserPreRouteLock)
        {
            var now = DateTime.UtcNow;
            _connectorContinuingUserTurn = "";
            _connectorContinuingUserTurnUtc = default;
            // Only actual user input after an existing completed-answer
            // boundary creates fresh connector authority. Tool/lifecycle
            // callbacks and transcription fragments do not release a write
            // replay fence by inventing another task ID.
            if (fromLocalTextSubmission || _connectorUserTurnEnded)
            {
                ++_connectorUserTurnIdentity;
                _connectorUserTurnEnded = false;
            }
            if (!fromLocalTextSubmission
                && _browserPreRouteReceipt?.Authoritative == true
                && _browserPreRouteReceipt.ToolName is "browser_automation_control" or "selected_text_translation_cancel" or "connector_control"
                && _browserPreRouteReceipt.IsEchoOf(userTurn, now))
            {
                // A final transcript echo must retain the already-applied
                // control receipt, not turn Cancelled into "no active task".
                _browserPreRouteOwnsCurrentTurn = true;
                _latestJarvisUserTurn = BrowserAutomationText.Truncate(userTurn, 4000);
                _latestJarvisUserTurnUtc = now;
                return;
            }
            if (!fromLocalTextSubmission
                && IsDurableMutationTool(_browserPreRouteReceipt?.ToolName)
                && _browserPreRouteReceipt?.IsIncrementalContinuationOf(userTurn, now) == true)
            {
                _browserPreRouteOwnsCurrentTurn = true;
                _latestJarvisUserTurn = BrowserAutomationText.Truncate(userTurn, 4000);
                _latestJarvisUserTurnUtc = now;
                return;
            }
            if (!fromLocalTextSubmission
                && string.Equals(
                    _browserPreRouteReceipt?.ToolName,
                    "run_workflow",
                    StringComparison.Ordinal)
                && _browserPreRouteReceipt?.IsEchoOf(userTurn, now) == true)
            {
                _browserPreRouteOwnsCurrentTurn = true;
                _latestJarvisUserTurn = BrowserAutomationText.Truncate(userTurn, 4000);
                _latestJarvisUserTurnUtc = now;
                return;
            }
            if (!fromLocalTextSubmission
                && _browserPreRouteReceipt?.IsEchoOf(userTurn, now) == true
                && !string.IsNullOrWhiteSpace(_awaitingLocalTextEcho)
                && now - _awaitingLocalTextEchoUtc <= TimeSpan.FromSeconds(12))
            {
                _awaitingLocalTextEcho = "";
                _browserPreRouteOwnsCurrentTurn = true;
                _latestJarvisUserTurn = BrowserAutomationText.Truncate(userTurn, 4000);
                _latestJarvisUserTurnUtc = now;
                return;
            }

            _browserPreRouteOwnsCurrentTurn = false;
            // Live input transcription can arrive in several accumulated
            // fragments after an earlier tool in this same turn has run.
            // Only the dispatcher turn-complete boundary (or a new local
            // submitted turn) releases the completed effect's authority.
            if (fromLocalTextSubmission)
                _completedDirectOpenForCurrentTurn = false;
            if (fromLocalTextSubmission || !IsSameResolvedOpenTurn(_latestJarvisUserTurn, userTurn))
                _resolvedOpenReceipts.Clear();
            _browserPreRouteReceipt = null;
            var captureBelongsToOpenTurn = IsSameJarvisUserTurn(
                _capturedBrowserContextUserTurn,
                _latestJarvisUserTurn);
            if (fromLocalTextSubmission || !IsSameJarvisUserTurn(_latestJarvisUserTurn, userTurn))
                _capturedBrowserContextIsCurrentTurn = false;
            _latestJarvisUserTurn = BrowserAutomationText.Truncate(userTurn, 4000);
            _latestJarvisUserTurnUtc = now;
            // A clearly new selection starts a new source contract. Keep the
            // previous immutable source for explicit original/earlier recall,
            // but a failed new capture must not make it the new task's input.
            // A cumulative ASR fragment/echo of the still-open capture turn
            // does not establish another selection. The completed-turn
            // boundary removes that authority even if the words repeat.
            if (RequestsFreshSelectedContext(userTurn)
                && !IsSimpleSelectionFollowup(userTurn)
                && !(!fromLocalTextSubmission
                    && captureBelongsToOpenTurn
                    && _capturedBrowserContextIsCurrentTurn
                    && string.Equals(_capturedBrowserContextSource, "selected text", StringComparison.Ordinal)
                    && IsSameJarvisUserTurn(_capturedBrowserContextUserTurn, userTurn)))
                _originalSelectionAllowsImplicitFollowup = false;
            // Live transcripts can gain the deferred clause after the model
            // has already read the selection. Only the still-open cumulative
            // turn may authorize that capture for a later browser action.
            if (!fromLocalTextSubmission
                && captureBelongsToOpenTurn
                && IsSameJarvisUserTurn(_capturedBrowserContextUserTurn, _latestJarvisUserTurn)
                && BrowserAutomationTaskClassifier.IsDeferredInteractionRequest(_latestJarvisUserTurn)
                && BrowserAutomationTaskClassifier.ReferencesCapturedUserContext(_latestJarvisUserTurn))
            {
                _capturedBrowserContextUserTurn = _latestJarvisUserTurn;
                _capturedBrowserContextAuthorizedForLaterAction = true;
            }
            if (fromLocalTextSubmission)
            {
                _awaitingLocalTextEcho = userTurn;
                _awaitingLocalTextEchoUtc = now;
            }
            else
            {
                _awaitingLocalTextEcho = "";
            }
        }
    }

    private bool TryResolveAuthoritativeToolRoute(string selectedTool, JsonElement arguments, CancellationToken token)
    {
        // The Live tool dispatcher is the sole ordinary execution owner. A
        // transcript alone must not perform an action whose result the model
        // never receives. Local cancel/farewell remain on the immediate path.
        lock (_browserPreRouteLock)
        {
            if (_browserPreRouteReceipt != null)
                return false;
        }
        var userTurn = RecentJarvisUserTurn();
        var text = userTurn;
        if (userTurn.Length == 0)
            return false;
        token.ThrowIfCancellationRequested();

        // Explicit durable corrections use the existing learned mapping or
        // browser preference store. Check them before workflow syntax because
        // "always open URL when I say X" is a durable target preference, not
        // a trigger-only workflow. Temporary "this time" instructions do not
        // enter this path.
        if (_automation != null
            && _automation.TryApplyDurablePreferenceFromTranscript(
                userTurn,
                out var preferenceTool,
                out var preferenceResult))
        {
            RecordAuthoritativePreRoute(
                userTurn,
                preferenceTool,
                preferenceResult);
            AppLog.Info($"Durable correction pre-route: tool={preferenceTool} ok={ToolSucceeded(preferenceResult)}");
            return true;
        }

        // A selected Link mapping has enough local structure to be owned
        // deterministically: an explicit persistence request, one selected web
        // destination, and one exact future trigger. Keep the existing Link
        // tool as the sole capture/validation/persistence owner instead of
        // relying on optional model tool selection or treating it as a Workflow.
        if (_automation != null
            && JarvisAutomationService.TryParseSelectedLinkPreference(userTurn, out var selectedLinkAlias))
        {
            var selectedLinkResult = RememberLinkAliasAsync(
                    JsonSerializer.SerializeToElement(new { alias = selectedLinkAlias }),
                    token)
                .GetAwaiter()
                .GetResult();
            RecordAuthoritativePreRoute(userTurn, "remember_link_alias", selectedLinkResult);
            AppLog.Info($"Selected Link pre-route: ok={ToolSucceeded(selectedLinkResult)}");
            return true;
        }

        // Explicit persistence requests must reach the workflow tool as one
        // complete user turn. In particular, an early "open Gmail" fragment
        // must not start Browser Automation before "remember this" arrives.
        if (_automation != null
            && JarvisAutomationService.IsExplicitWorkflowPersistenceRequest(userTurn))
        {
            if (_automation.TryRememberWorkflowFromTranscript(
                    userTurn,
                    completeTurn: true,
                    out var saveResult))
            {
                var ok = saveResult.TryGetValue("ok", out var okValue)
                    && okValue is bool succeeded
                    && succeeded;
                var message = saveResult.TryGetValue("message", out var messageValue)
                    ? messageValue?.ToString() ?? ""
                    : "";
                lock (_browserPreRouteLock)
                {
                    _browserPreRouteReceipt = new BrowserPreRouteReceipt
                    {
                        UserTurn = userTurn,
                        ToolName = "remember_routine",
                        Ok = ok,
                        Message = message,
                        Result = new Dictionary<string, object>(saveResult),
                    };
                    _browserPreRouteOwnsCurrentTurn = true;
                }
                AppLog.Info($"Explicit workflow persistence pre-route: ok={ok}");
                return true;
            }
            AppLog.Info("Browser pre-route skipped for explicit workflow persistence request.");
            return false;
        }

        // A compound named open has one requested-set owner. Different model
        // proposals must not omit a saved object or open the same resolved
        // destination twice. Ask mode retains its existing confirmation owner.
        if (_automation != null
            && _settings.Current.JarvisPermissionMode == JarvisPermissionMode.AutoExecute
            && _automation.TryResolveSavedOpenSet(userTurn, out var savedOpenPlan))
        {
            var combined = ExecuteSavedOpenSetAsync(savedOpenPlan, token).GetAwaiter().GetResult();
            token.ThrowIfCancellationRequested();
            RecordAuthoritativePreRoute(userTurn, "saved_open_set", combined);
            AppLog.Info($"Saved open set pre-route: requested={savedOpenPlan.RequestedCount} "
                + $"resolved={savedOpenPlan.Actions.Count} unresolved={savedOpenPlan.UnresolvedNames.Count} "
                + $"executed={combined["executed_count"]} ok={ToolSucceeded(combined)}");
            return true;
        }

        // An ambiguous one-time "open X" request must honor the current
        // durable correction locally. Do not let a model-supplied default
        // native hint override the user's verified saved web/app/path choice.
        // Explicit current app/web wording remains outside this path and keeps
        // its higher precedence.
        if (_automation != null
            && _automation.TryResolveDurableOpenPreference(
                userTurn,
                out var preferenceOpenTool,
                out var preferenceOpenArguments))
        {
            Dictionary<string, object> preferenceOpenResult;
            var preferenceOpenIdentity = _automation.ResolvedOpenActionIdentity(preferenceOpenTool, preferenceOpenArguments);
            if (!TryGetResolvedOpenReceipt(preferenceOpenIdentity, out preferenceOpenResult))
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(12));
                    preferenceOpenResult = _automation.ExecuteAsync(
                            preferenceOpenTool,
                            preferenceOpenArguments,
                            timeout.Token)
                        .GetAwaiter()
                        .GetResult();
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    AppLog.Warning(ex, "Durable preference open pre-route failed");
                    preferenceOpenResult = ToolResult(
                        false,
                        ex is OperationCanceledException
                            ? "The saved preference did not finish in time."
                            : ex.Message);
                }
                token.ThrowIfCancellationRequested();
                RememberResolvedOpenReceipt(preferenceOpenIdentity, preferenceOpenResult);
            }
            token.ThrowIfCancellationRequested();
            RecordAuthoritativePreRoute(
                userTurn,
                preferenceOpenTool,
                preferenceOpenResult);
            AppLog.Info(
                $"Durable preference open pre-route: tool={preferenceOpenTool} " +
                $"ok={ToolSucceeded(preferenceOpenResult)}");
            return true;
        }

        // Mutate the one authoritative saved workflow object in place. This
        // runs before browser classification so adding a website is a durable
        // direct URL action, never a reason to start Browser Automation.
        if (_automation != null
            && _automation.TryEditWorkflowFromTranscript(userTurn, out var editResult))
        {
            token.ThrowIfCancellationRequested();
            RecordAuthoritativePreRoute(
                userTurn,
                "edit_workflow",
                editResult);
            AppLog.Info($"Workflow edit pre-route: ok={ToolSucceeded(editResult)}");
            return true;
        }

        // A complete durable workflow trigger is locally authoritative. Run
        // its stored ordered actions once and retain the verified receipt for
        // the matching Live tool call instead of asking the planner to
        // reinterpret the phrase from conversation memory.
        if (_automation != null
            && _automation.TryResolveSavedWorkflowTrigger(userTurn, out var workflowName))
        {
            Dictionary<string, object> workflowResult;
            var workflowArguments = JsonSerializer.SerializeToElement(new { name = workflowName });
            var workflowOpenIdentity = _automation.ResolvedOpenActionIdentity("run_workflow", workflowArguments);
            if (!TryGetResolvedOpenReceipt(workflowOpenIdentity, out workflowResult))
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(20));
                    workflowResult = _automation.ExecuteAsync(
                            "run_workflow",
                            workflowArguments,
                            timeout.Token)
                        .GetAwaiter()
                        .GetResult();
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    AppLog.Warning(ex, "Deterministic saved workflow pre-route failed");
                    workflowResult = ToolResult(false, ex is OperationCanceledException
                        ? "The saved workflow did not finish in time."
                        : ex.Message);
                }
                token.ThrowIfCancellationRequested();
                RememberResolvedOpenReceipt(workflowOpenIdentity, workflowResult);
            }
            token.ThrowIfCancellationRequested();
            var ok = workflowResult.TryGetValue("ok", out var okValue)
                && okValue is bool succeeded
                && succeeded;
            var message = workflowResult.TryGetValue("message", out var messageValue)
                ? messageValue?.ToString() ?? ""
                : "";
            lock (_browserPreRouteLock)
            {
                _browserPreRouteReceipt = new BrowserPreRouteReceipt
                {
                    UserTurn = userTurn,
                    ToolName = "run_workflow",
                    Ok = ok,
                    Message = message,
                    Result = new Dictionary<string, object>(workflowResult),
                };
                _browserPreRouteOwnsCurrentTurn = true;
            }
            AppLog.Info($"Saved workflow pre-route: workflow={workflowName} ok={ok}");
            return true;
        }

        // An exact deleted trigger is a durable tombstone, not a new web
        // search or planner request. Keep deletion authoritative across new
        // conversations and legacy learned-index fallbacks.
        if (_automation?.IsDeletedWorkflowTrigger(userTurn) == true)
        {
            var deletedResult = ToolResult(
                false,
                "That saved workflow was deleted. Save it again before using this trigger.",
                supported: true);
            lock (_browserPreRouteLock)
            {
                _browserPreRouteReceipt = new BrowserPreRouteReceipt
                {
                    UserTurn = userTurn,
                    ToolName = "run_workflow",
                    Ok = false,
                    Message = deletedResult["message"]?.ToString() ?? "",
                    Result = deletedResult,
                };
                _browserPreRouteOwnsCurrentTurn = true;
            }
            AppLog.Info("Deleted workflow trigger blocked from alternate routing.");
            return true;
        }

        // A known keyboard.wtf Settings route is a complete local action. Run
        // it before browser classification and retain its authoritative receipt
        // so Gemini cannot launch a second browser task for the same turn.
        var localSettingsRequest = SettingsSectionRegistry.TryResolveKeyboardWtfNavigationRequest(
            userTurn, out var settingsSection);
        if (localSettingsRequest || selectedTool == "open_keyboard_wtf_settings")
        {
            // Semantic tool selection can resolve a local section even when
            // the user did not repeat the product/settings name. That same
            // local owner must publish the result, not a second Browser task.
            if (!localSettingsRequest)
                settingsSection = GetString(arguments, "section");
            token.ThrowIfCancellationRequested();
            var settingsResult = OpenKeyboardWtfSettings(settingsSection);
            RecordAuthoritativePreRoute(
                userTurn,
                "open_keyboard_wtf_settings",
                settingsResult);
            AppLog.Info(
                $"Local Settings pre-route: section={settingsSection} ok={ToolSucceeded(settingsResult)}");
            return true;
        }

        if (_automation != null
            && JarvisAutomationService.TryGetOrdinalBrowserTabCloseReferences(text, out _))
        {
            Dictionary<string, object> closeResult;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(25));
                closeResult = _automation.ExecuteAsync("browser_action",
                        JsonSerializer.SerializeToElement(new { action = text }), timeout.Token)
                    .GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                AppLog.Warning(ex, "Exact numbered-tab set close pre-route failed");
                closeResult = ToolResult(false, ex is OperationCanceledException
                    ? "Chrome did not confirm the requested exact tab set in time."
                    : ex.Message, supported: false);
            }
            token.ThrowIfCancellationRequested();
            RecordAuthoritativePreRoute(userTurn, "browser_action", closeResult);
            return true;
        }

        // Resolve the reference independently of the operation. An ordinal
        // close names one exact tab, not a group or a select-tab command.
        if (_automation != null
            && JarvisAutomationService.TryGetBrowserTabCloseReference(
                text, out var closeTabReference, out _)
            && closeTabReference != "current")
        {
            Dictionary<string, object> closeResult;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(25));
                var closeArguments = new Dictionary<string, object> { ["action"] = "close tab" };
                if (closeTabReference.StartsWith("index:", StringComparison.Ordinal))
                    closeArguments["tab_index"] = int.Parse(closeTabReference["index:".Length..]);
                else if (closeTabReference.StartsWith("from_end:", StringComparison.Ordinal))
                    closeArguments["tab_from_end"] = int.Parse(closeTabReference["from_end:".Length..]);
                else closeArguments["tab_reference"] = closeTabReference;
                closeResult = _automation.ExecuteAsync("browser_action",
                        JsonSerializer.SerializeToElement(closeArguments),
                        timeout.Token)
                    .GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                AppLog.Warning(ex, "Exact relative-tab close pre-route failed");
                closeResult = ToolResult(false, ex is OperationCanceledException
                    ? "Chrome did not confirm the requested exact tab in time."
                    : ex.Message, supported: false);
            }
            token.ThrowIfCancellationRequested();
            RecordAuthoritativePreRoute(userTurn, "browser_action", closeResult);
            return true;
        }

        if (_automation != null && JarvisAutomationService.TryGetBrowserTabReopenCount(text, out var reopenCount))
        {
            var reopened = _automation.ReopenBrowserTabs(reopenCount, token);
            RecordAuthoritativePreRoute(userTurn, "browser_action", reopened);
            return true;
        }

        // A bounded "first/last N tabs" request names one stable positional
        // set, not N blind Ctrl+W presses. Resolve the complete set to
        // Helper tab IDs first, then close only those session-bound IDs.
        if (_automation != null
            && JarvisAutomationService.TryGetPositionalBrowserTabCloseCount(
                text,
                out var fromEnd,
                out var positionalTabCount))
        {
            Dictionary<string, object> closeResult;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(25));
                closeResult = _automation.ClosePositionalBrowserTabsAsync(
                        positionalTabCount,
                        fromEnd,
                        timeout.Token)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception ex)
            {
                AppLog.Warning(ex, "Exact positional-tab close pre-route failed");
                closeResult = ToolResult(
                    false,
                    ex is OperationCanceledException
                        ? "Chrome did not confirm the requested exact tab set in time."
                        : ex.Message,
                    supported: false);
            }
            token.ThrowIfCancellationRequested();
            RecordAuthoritativePreRoute(userTurn, "browser_action", closeResult);
            AppLog.Info(
                $"Exact positional-tab close pre-route: count={positionalTabCount} fromEnd={fromEnd} "
                + $"ok={ToolSucceeded(closeResult)}");
            return true;
        }

        if (_browserAutomation != null
            && JarvisAutomationService.TryGetBrowserTabSelection(
                text,
                out var tabSelection,
                out var tabDescription))
        {
            Dictionary<string, object> selectionResult;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(6));
                var receipt = _browserAutomation
                    .ActivateExistingTabAsync(tabSelection, timeout.Token)
                    .GetAwaiter()
                    .GetResult();
                selectionResult = ExactTabSelectionToolResult(receipt, tabSelection, tabDescription);
            }
            catch (Exception ex)
            {
                selectionResult = ToolResult(false, ex is OperationCanceledException
                    ? "Chrome did not confirm the tab switch in time."
                    : ex.Message);
            }
            token.ThrowIfCancellationRequested();
            var ok = ToolSucceeded(selectionResult);
            var message = selectionResult["message"]?.ToString() ?? "";

            lock (_browserPreRouteLock)
            {
                _browserPreRouteReceipt = new BrowserPreRouteReceipt
                {
                    UserTurn = userTurn,
                    ToolName = "browser_action",
                    Ok = ok,
                    Message = message,
                    Result = selectionResult,
                };
                _browserPreRouteOwnsCurrentTurn = true;
            }
            if (ok)
                _notifications.InfoTransient("Browser", message);
            else
                _notifications.WarningTransient("Browser", message);
            AppLog.Info(
                $"Chrome Helper tab selection: ok={ok} selection={tabSelection} request={BrowserAutomationText.Truncate(text, 120)}");
            return true;
        }

        if (_automation != null
            && JarvisAutomationService.IsDirectBrowserShortcutRequest(text))
        {
            var shortcut = _automation.ExecuteBrowserShortcut(text, token);
            token.ThrowIfCancellationRequested();
            var ok = shortcut.TryGetValue("ok", out var value)
                && value is bool success
                && success;
            var message = shortcut.TryGetValue("message", out var detail)
                ? detail?.ToString() ?? ""
                : "";
            lock (_browserPreRouteLock)
            {
                _browserPreRouteReceipt = new BrowserPreRouteReceipt
                {
                    UserTurn = userTurn,
                    ToolName = "browser_action",
                    Ok = ok,
                    Message = message,
                };
                _browserPreRouteOwnsCurrentTurn = true;
            }
            if (ok)
                _notifications.InfoTransient("Browser", message);
            else
                _notifications.WarningTransient("Browser", message);
            AppLog.Info(
                $"Deterministic browser shortcut pre-route: ok={ok} request={BrowserAutomationText.Truncate(text, 120)}");
            return true;
        }

        if (_browserAutomation == null || string.IsNullOrWhiteSpace(text))
            return false;

        // A running browser task does not own the whole Jarvis conversation.
        // In particular, normal Chrome/desktop commands must remain normal
        // commands rather than being injected into the controlled webpage.
        var active = _browserAutomation.Snapshot();
        if (_browserAutomation.RecentTerminalOutcomes().Count > 0
            && BrowserAutomationTaskClassifier.IsOutcomeDiagnosticQuestion(userTurn))
        {
            var statusResult = BrowserAutomationStatus();
            RecordAuthoritativePreRoute(
                userTurn,
                "browser_automation_status",
                statusResult);
            AppLog.Info(
                "Browser outcome diagnostic pre-route retained read-only status and did not start or revive a task.");
            return true;
        }
        if (active.CanCancel)
        {
            if (TryControlActiveBrowserTask(userTurn, token, out var controlResult))
            {
                RecordAuthoritativePreRoute(
                    userTurn,
                    "browser_automation_control",
                    controlResult);
                return true;
            }

            // Semantic tool selection owns which task receives the turn.
            // A direct or read-only tool must not be reinterpreted as Browser
            // guidance merely because its text overlaps the current page.
            // Explicit cancellation above remains locally authoritative.
            if (selectedTool is not ("browser_automation" or "browser_automation_control"))
                return false;

            // The existing task owner validates the actual user's guidance;
            // a model paraphrase cannot supply extra action authority.
            var guidanceReason = "";
            if (!IsExplicitBrowserTaskStopOrPause(userTurn)
                && _browserAutomation.TryAddTaskGuidance(userTurn, out guidanceReason))
            {
                lock (_browserPreRouteLock)
                {
                    _browserPreRouteReceipt = new BrowserPreRouteReceipt
                    {
                        UserTurn = userTurn,
                        ToolName = "browser_automation_control",
                        Ok = true,
                        TaskId = active.TaskId,
                        Message = "Guidance accepted; the same browser task is continuing.",
                    };
                    _browserPreRouteOwnsCurrentTurn = true;
                }
                AppLog.Info($"Browser task guidance accepted by pre-route: task={active.TaskId}");
                return true;
            }

            if (active.State == BrowserTaskState.NeedsUserAction
                && !IsExplicitBrowserTaskStopOrPause(userTurn)
                && BrowserAutomationCoordinator.IsSkipInterventionGuidance(userTurn))
            {
                var requiredResult = ToolResult(
                    false,
                    string.IsNullOrWhiteSpace(guidanceReason)
                        ? "That browser requirement is not known to be safely optional, so the same task remains paused."
                        : guidanceReason,
                    supported: true);
                RecordAuthoritativePreRoute(
                    userTurn,
                    "browser_automation_control",
                    requiredResult);
                AppLog.Info(
                    $"Browser task required-step skip rejected authoritatively: task={active.TaskId}");
                return true;
            }

            AppLog.Info(
                $"Browser pre-route skipped while task={active.TaskId}: the live conversation decides normal commands; guidanceRejected={BrowserAutomationText.Truncate(guidanceReason, 120)}");
            return false;
        }

        // An earlier direct open in this same ordered tool batch has already
        // produced an effect. The Browser start owner must scope its task; a
        // whole-turn pre-route would replay that completed step. Keep the
        // active-task guidance branch above intact.
        if (HasCompletedDirectOpenForCurrentTurn()
            && selectedTool == "browser_automation")
            return false;

        // The selected Live tool owns this action. A whole-turn Browser
        // classification must not seize an ordinary direct call simply
        // because a different clause in the same turn mentions a page action.
        if (!ShouldPreRouteBrowserTaskForTool(selectedTool))
            return false;

        var classification = _browserAutomation.Classify(text);
        if (classification.Decision != BrowserAutomationDecision.BrowserAutomation)
            return false;

        // Context-dependent starts must reach the Browser tool with the
        // captured context; do not start from the bare request before that
        // tool has supplied its authoritative selected/deferred content.
        if (ShouldDeferIncrementalBrowserPreRoute(userTurn, fromLocalTextSubmission: true)
            || ShouldDeferBrowserPreRouteForCapturedContext(
                HasFreshCapturedBrowserContext(),
                userTurn))
        {
            AppLog.Info(
                "Browser pre-route deferred until the complete Live turn so the model can authoritatively decide whether fresh user-provided context belongs to this task.");
            return false;
        }

        token.ThrowIfCancellationRequested();
        var result = _browserAutomation.Start(userTurn);
        if (!result.Started || string.IsNullOrWhiteSpace(result.TaskId))
            return false;

        lock (_browserPreRouteLock)
        {
            _browserPreRouteReceipt = new BrowserPreRouteReceipt
            {
                UserTurn = userTurn,
                ToolName = "browser_automation",
                Ok = true,
                Started = true,
                TaskId = result.TaskId,
                Message = result.Message,
            };
            _browserPreRouteOwnsCurrentTurn = true;
        }
        AppLog.Info(
            $"Browser pre-route: task={result.TaskId} started={result.Started} current={classification.RefersToCurrentPage}");
        return true;
    }

    internal static bool ShouldPreRouteBrowserTaskForTool(string selectedTool) =>
        string.Equals(selectedTool, "browser_automation", StringComparison.Ordinal);

    internal static bool ShouldDeferIncrementalBrowserPreRoute(
        string text,
        bool fromLocalTextSubmission) =>
        BrowserAutomationTaskClassifier.RequiresFreshUserContextCapture(text)
        || (!fromLocalTextSubmission
            && BrowserAutomationTaskClassifier.ShouldDeferIncrementalContentRouting(text));

    internal static bool ShouldDeferBrowserPreRouteForCapturedContext(
        bool contextAvailable,
        string request) =>
        contextAvailable
        && BrowserAutomationTaskClassifier.RequiresPageMutation(request);

    internal static bool IsExplicitBrowserTaskStopOrPause(string text) =>
        TryParseExplicitBrowserTaskStopOrPause(text, out _);

    internal static bool TryParseExplicitBrowserTaskStopOrPause(
        string text,
        out string action) =>
        TryParseExplicitBrowserTaskStopOrPause(
            text,
            out action,
            out _);

    internal static bool TryParseExplicitBrowserTaskStopOrPause(
        string text,
        out string action,
        out bool explicitlyTargetsBrowserTask)
    {
        if (TryParseBrowserTaskControlClause(text, out action, out explicitlyTargetsBrowserTask))
            return true;

        // A complete final imperative owns itself even after conversational
        // context. Parse that sentence with the SAME control grammar, not a
        // growing list of acknowledgements such as "never mind". Quoted text
        // and colon-introduced discussion are not new command sentences.
        var sentenceStart = 0;
        var quoteEnd = '\0';
        text ??= "";
        for (var i = 0; i < text.Length; i++)
        {
            var character = text[i];
            if (character is '\'' or '’' && i > 0 && i + 1 < text.Length
                && char.IsLetterOrDigit(text[i - 1]) && char.IsLetterOrDigit(text[i + 1]))
                continue; // Apostrophe within a word, not quotation.
            if (quoteEnd != '\0')
            {
                if (character == quoteEnd) quoteEnd = '\0';
                continue;
            }
            if (character is '"' or '\'' or '`' or '“' or '‘')
                quoteEnd = character switch { '“' => '”', '‘' => '’', _ => character };
            else if (character is '.' or '!' or '?' && i + 1 < text.Length && char.IsWhiteSpace(text[i + 1]))
            {
                var next = i + 1;
                while (next < text.Length && char.IsWhiteSpace(text[next])) next++;
                if (next < text.Length) sentenceStart = next;
            }
        }
        return sentenceStart > 0 && quoteEnd == '\0'
            && TryParseBrowserTaskControlClause(text[sentenceStart..], out action, out explicitlyTargetsBrowserTask);
    }

    private static bool TryParseBrowserTaskControlClause(
        string text,
        out string action,
        out bool explicitlyTargetsBrowserTask)
    {
        action = "";
        explicitlyTargetsBrowserTask = false;
        var remaining = (text ?? "").Trim();
        // A bare preliminary control must not consume the explicitly targeted
        // control that follows it. Reuse the same grammar for control clauses;
        // do not search arbitrary quoted/discussed text for a stop keyword.
        for (var clause = 0; clause < 8 && remaining.Length > 0; clause++)
        {
            var match = Regex.Match(
                remaining,
                @"^\s*(?:(?:okay|ok|alright|yes|no)[,;:.!\s]+)?"
                + @"(?:(?:please\s+)|(?:(?:can|could|would|will)\s+you\s+)|(?:i\s+(?:want|need|would\s+like)\s+you\s+to\s+))*"
                + @"(?<action>pause|cancel|stop)"
                + @"(?<target>\s+(?:the\s+)?(?:browser\s+automation|browser\s+task|automation\s+task|task|b\.?a\.?))?"
                + @"(?:\s+(?:(?:right\s+)?now|immediately))?"
                + @"(?:\s*(?:(?:[,;:.!?]|\band\b|\bthen\b)\s*)+(?<remaining>.*))?$",
                RegexOptions.IgnoreCase);
            if (!match.Success)
                break;
            action = match.Groups["action"].Value.Equals(
                "pause",
                StringComparison.OrdinalIgnoreCase)
                    ? "pause"
                    : "cancel";
            explicitlyTargetsBrowserTask = match.Groups["target"].Success;
            if (explicitlyTargetsBrowserTask)
                return true;
            remaining = match.Groups["remaining"].Value.Trim();
        }
        return action.Length > 0;
    }

    private async Task TogglePushToTalkAsync()
    {
        if (!KeyboardWtfState.IsRecording)
        {
            _capture.Start(RecordingMode.PushToTalk);
            return;
        }

        if (KeyboardWtfState.CurrentRecordingMode == RecordingMode.PushToTalk)
        {
            var transcript = await _capture.StopAndTranscribeAsync();
            if (!string.IsNullOrWhiteSpace(transcript))
            {
                var destination = DestinationRegistry.Current?.Name ?? _settings.Current.DefaultDestination ?? "Clipboard";
                var ok = await _router.SendAsync(
                    destination,
                    transcript,
                    _capture.CurrentDeliveryTarget,
                    _capture.CurrentOperationToken);
                _notifications.Outcome(
                    ok,
                    "Sent",
                    "Send failed",
                    KeyboardWtfState.LastSendResult ?? destination);
            }
        }
    }

    public async Task<Dictionary<string, object>> ExecuteJarvisToolAsync(string name, JsonElement args, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var toolName = (name ?? "").Trim();
        var userTurn = RecentJarvisUserTurn();
        if (toolName == "browser_action" && GetString(args, "action")?.Trim().StartsWith("close", StringComparison.OrdinalIgnoreCase) == true
            && ((JarvisAutomationService.HasExplicitPositionalTabClose(userTurn)
                    && !JarvisAutomationService.TryGetPositionalBrowserTabCloseCount(userTurn, out _, out _))
                || (JarvisAutomationService.HasExplicitOrdinalTabSetClose(userTurn)
                    && !JarvisAutomationService.TryGetOrdinalBrowserTabCloseReferences(userTurn, out _))))
        {
            AppLog.Info("Positional tab close rejected: explicitEdge=true wholeRequestOwned=false noTabClosed=true");
            var rejectedClose = ToolResult(false, "The complete positional close request could not be verified. No tab was closed.");
            rejectedClose["no_tab_closed"] = true;
            rejectedClose["error_code"] = "request_not_resolved";
            rejectedClose["connection_attempted"] = false;
            rejectedClose["needs_clarification"] = true;
            rejectedClose["instruction"] = "The local request parser could not resolve the complete tab instruction. Chrome was not contacted. Ask which exact tab positions to close; do not report a browser connection failure or retry a different edge.";
            return rejectedClose;
        }
        if (_automation?.Connectors is { } admittedConnector
            && await admittedConnector.TryCollectAdmittedReadProposalAsync(toolName, args,
                ConnectorUserRequestSnapshot, token) is { } admittedReceipt)
            return admittedReceipt;
        if (toolName is "get_desktop_context" or "get_selected_text" && _automation?.Connectors is { } contextConnector)
        {
            var retainedRead = await contextConnector.TryReadRetainedContextAsync(token);
            if (retainedRead is not null) return retainedRead;
        }
        // The current human's retained-object opening also owns a model's
        // new-tab/browser-task/stale-read proposal. The proposal contributes no URL or ID;
        // the existing retained opener resolves and deduplicates the whole set.
        // Closing/selecting tabs, writes and task cancellation keep their owners.
        var retainedOpenProposal = toolName == "browser_action"
                && string.Equals(GetString(args, "action")?.Trim(), "new tab", StringComparison.OrdinalIgnoreCase)
                && !args.TryGetProperty("target_ids", out _) && !args.TryGetProperty("tab_index", out _)
                && !args.TryGetProperty("tab_from_end", out _) && !args.TryGetProperty("tab_reference", out _)
                && !args.TryGetProperty("last_tab_count", out _)
            || toolName is "browser_automation" or "connector_resolve"
            || toolName == "connector_execute"
                && KeyboardWtf.Connectors.ConnectorEvidenceTraits.For(GetString(args, "capability_id")) != "unknown"
            || toolName == "connector_control" && GetString(args, "action") is "recall" or "status" or "result" or "guide" or "open";
        if ((toolName is "browser_automation" or "open_url"
                || toolName == "browser_action" && GetString(args, "action") == "new tab"
                || toolName == "connector_execute" && KeyboardWtf.Connectors.ConnectorEvidenceTraits.For(GetString(args, "capability_id")) != "unknown")
            && _automation?.Connectors is { } currentReadConnector
            && await currentReadConnector.TryOpenCurrentReadResultAsync(ConnectorUserRequestSnapshot,
                (url, cancellation) => _automation.ExecuteAsync("open_url",
                    JsonSerializer.SerializeToElement(new { url }), cancellation), token,
                    recoverTerminalRead: toolName == "connector_execute",
                    proposedUrl: toolName == "open_url" ? GetString(args, "url") : null) is { } currentReadOpen)
            return currentReadOpen;
        if (retainedOpenProposal && _automation?.Connectors is { } retainedConnector)
        {
            var exactOpen = await retainedConnector.TryOpenRetainedResourcesAsync(ConnectorUserRequestSnapshot,
                (url, cancellation) => _automation.ExecuteAsync("open_url",
                    JsonSerializer.SerializeToElement(new { url }), cancellation), token,
                    proposedRead: toolName == "connector_execute" ? args : default);
            if (exactOpen is not null)
            {
                if (ToolSucceeded(exactOpen))
                    lock (_browserPreRouteLock) _completedDirectOpenForCurrentTurn = true;
                return exactOpen;
            }
        }
        // Only the tiny connector broker enters this new boundary. Ordinary
        // website, selection, screen and local-tool routing is unchanged.
        if (Connectors.JarvisConnectorController.IsTool(toolName))
        {
            if (toolName == "connector_resolve" && _automation != null
                && (JarvisAutomationService.IsOrdinaryNavigationRequest(userTurn)
                    || _automation.HasExactSavedOpenAuthority(userTurn)))
                TryResolveAuthoritativeToolRoute(toolName, args, token);
            if (TryGetPreRoutedToolResult(toolName, out var connectorControlReceipt)) return connectorControlReceipt;
            if (TryConsumePreRoutedAuthoritativeOutcome(out var connectorOwnedReceipt)) return connectorOwnedReceipt;
            return _automation is null
                ? ToolResult(false, "Connected services are not available in this session.")
                : await _automation.ExecuteAsync(toolName, args, token);
        }
        if (toolName == "browser_automation" && _automation?.Connectors is { } documentConnector
            && await documentConnector.TryReadExplicitDocumentAsync(toolName, token) is { } documentRead)
            return documentRead;
        KeyboardWtfState.SetUi(VoiceUiPhase.Executing, KeyboardWtfState.AssistantName,
            HumanizeTool(ShouldReadSelectionInsteadOfScreen(toolName, userTurn) ? "get_selected_text" : toolName));

        try
        {
            // Exact tab handles retain their owner unless the current whole
            // request explicitly names a new relative target. Never replace
            // an exact set with a blind foreground close-tab shortcut.
            var hasTabTargets = toolName == "browser_action" && args.ValueKind == JsonValueKind.Object && args.TryGetProperty("target_ids", out _);
            // Only the next explicit plural user turn may consume a retained,
            // strong folder ambiguity. A model-supplied single target cannot
            // narrow that authoritative "open both/all" instruction.
            // A background translation remains the sole owner of its output
            // after the provider finishes its start acknowledgement. A late
            // copy proposal without a new user request cannot copy again.
            if (toolName == "copy_text" && _selectedTranslation is { } translation
                && (userTurn.Length == 0 || IsSameJarvisUserTurn(translation.Request, userTurn)))
            {
                var owned = translation.Receipt();
                owned["duplicate_suppressed"] = true;
                return owned;
            }
            if (_automation?.TryOpenPendingFolderChoices(userTurn, token, out var folderChoiceResult) == true)
                RecordAuthoritativePreRoute(userTurn, "open_folder", folderChoiceResult);
            else if (!hasTabTargets
                || (JarvisAutomationService.TryGetBrowserTabCloseReference(userTurn, out var requestedTabReference, out _)
                    && requestedTabReference != "current")
                || JarvisAutomationService.TryGetOrdinalBrowserTabCloseReferences(userTurn, out _)
                || JarvisAutomationService.TryGetPositionalBrowserTabCloseCount(userTurn, out _, out _))
                TryResolveAuthoritativeToolRoute(toolName, args, token);
            if (TryGetPreRoutedToolResult(toolName, out var preRoutedResult))
                return preRoutedResult;

            if (TryConsumePreRoutedAuthoritativeOutcome(out var authoritativeOutcome))
                return authoritativeOutcome;

            // An explicit selection-to-clipboard translation is a task, not
            // a cancellable Live utterance. Both backend owners share the
            // proven executor, source resolver and clipboard permission owner.
            if (_liveConversation?.HasBoundSession == true)
            {
                var hasSource = !string.IsNullOrWhiteSpace(_originalSelectedText)
                    || UsesRecentVerifiedCopiedResult(userTurn);
                if (SelectedTextTranslationOperation.TryParseRequest(userTurn, hasSource, out var translationLanguage))
                    return await StartSelectedTranslationAsync(userTurn, translationLanguage, token);
                // Natural capture-prefaces need not fit the strict imperative
                // fast path. Resolve the originating request at the content
                // boundary, never a model's proposed clipboard payload. No
                // extra Live turn or wait for a later transcript is required.
                if (toolName is "get_selected_text" or "get_clipboard_text" or "copy_text" or "inspect_screen")
                {
                    var resolved = SelectedTextTranslationOperation.TryResolveClipboardTranslationIntent(userTurn,
                        out translationLanguage, out var recognized);
                    if (!resolved && !recognized)
                        resolved = SelectedTextTranslationOperation.TryParseStructuredRequest(userTurn, hasSource,
                            proposedLanguage: "", copyRequested: true, out translationLanguage, out recognized);
                    if (resolved)
                        return await StartSelectedTranslationAsync(userTurn, translationLanguage, token);
                    if (recognized)
                    {
                        var failure = ToolResult(false,
                            "Which language should I translate this text into? No translation task was started and nothing was copied.",
                            needsClarification: true);
                        failure["started"] = false;
                        failure["clipboard_unchanged"] = true;
                        return failure;
                    }
                }
            }
            else if (toolName is "get_selected_text" or "get_clipboard_text" or "copy_text" or "inspect_screen"
                && (SelectedTextTranslationOperation.TryParseRequest(userTurn,
                        !string.IsNullOrWhiteSpace(_originalSelectedText), out _)
                    || SelectedTextTranslationOperation.TryResolveClipboardTranslationIntent(userTurn, out _, out _)))
            {
                var failure = ToolResult(false,
                    "The translation has no active session owner. No translation ran and nothing was copied.");
                failure["started"] = false;
                failure["clipboard_unchanged"] = true;
                return failure;
            }

            // The requested source, not the verb "analyze" or a model tool
            // guess, selects the read boundary. Do not capture a screenshot
            // for an explicit selected-text request. Mixed requests that also
            // explicitly request a visual screen inspection retain that tool.
            if (ShouldReadSelectionInsteadOfScreen(toolName, userTurn))
            {
                KeyboardWtfState.SetUi(VoiceUiPhase.Executing, KeyboardWtfState.AssistantName, "Reading selected text");
                return await GetRecentSelectedTextOrRecaptureAsync(_captureSelectedText, token);
            }

            // A model may misread a one-time compound action as a reusable
            // routine. Persistence needs the user's explicit authority even
            // when the proposed workflow happens to contain valid actions.
            if (IsWorkflowCreationTool(toolName)
                && !JarvisAutomationService.IsExplicitWorkflowPersistenceRequest(RecentJarvisUserTurn()))
            {
                return ToolResult(
                    false,
                    "No saved workflow was requested, so nothing was saved. This is a one-time request: use the direct action tools in the user's requested order and use exact saved Link aliases through their local resolver. Do not claim an action completed until its tool receipt succeeds.",
                    supported: false);
            }

            // A single selected web destination is a Link, not a routine.
            // If the model proposes an unresolved URL action for that current
            // selection, let the existing Link owner capture and validate it
            // before anything can be persisted or opened.
            if (toolName == "remember_routine"
                && TryGetSelectedLinkAliasFromRoutineProposal(
                    args, RecentJarvisUserTurn(), out var selectedAlias))
            {
                AppLog.Info("Single selected-link routine proposal delegated to Link capture owner.");
                return await RememberLinkAliasAsync(
                    JsonSerializer.SerializeToElement(new { alias = selectedAlias }), token);
            }

            if (ShouldBlockCompetingToolForPreRoutedBrowserTurn(toolName))
            {
                AppLog.Info($"Direct tool blocked by whole-turn Browser receipt: tool={toolName}");
                return ToolResult(
                    false,
                    "This user turn is already owned by Browser Automation. No generic active-window or desktop action was sent.");
            }

            if (toolName == "browser_automation")
                return await StartBrowserAutomationAsync(args, token);
            if (toolName == "browser_automation_status")
                return BrowserAutomationStatus();
            if (toolName == "browser_automation_control")
                return await ControlBrowserAutomationAsync(args);
            if (toolName == "get_selected_text")
                return await GetRecentSelectedTextOrRecaptureAsync(
                    _captureSelectedText, token);
            if (toolName == "remember_link_alias")
                return await RememberLinkAliasAsync(args, token);
            if (toolName == "open_keyboard_wtf_settings")
                return OpenKeyboardWtfSettings(GetString(args, "section"));
            if (toolName == "browser_action" && !hasTabTargets
                && string.Equals(GetString(args, "action"), "close tab", StringComparison.OrdinalIgnoreCase)
                && args.ValueKind == JsonValueKind.Object
                && (args.TryGetProperty("tab_index", out _) || args.TryGetProperty("tab_from_end", out _)
                    || args.TryGetProperty("tab_reference", out _)))
            {
                if (_automation == null)
                    return ToolResult(false, "The Chrome Helper is unavailable, so no tab was closed.", supported: false);
                return await _automation.ExecuteAsync(toolName, args, token);
            }
            if (toolName == "browser_action"
                && !hasTabTargets
                && ((args.ValueKind == JsonValueKind.Object
                        && (args.TryGetProperty("tab_index", out _) || args.TryGetProperty("tab_from_end", out _)))
                    || string.Equals(GetString(args, "action"), "select tab", StringComparison.OrdinalIgnoreCase)))
            {
                if (_browserAutomation == null)
                    return ToolResult(false, "The Chrome Helper is unavailable, so no tab was selected.", supported: false);
                if (!TryGetStructuredTabSelection(args, out var selection, out var description))
                    return ToolResult(false,
                        "Select one existing tab with either a positive 1-based tab_index or tab_from_end, not both. No tab was changed.",
                        supported: false);
                var receipt = await _browserAutomation.ActivateExistingTabAsync(selection, token);
                return ExactTabSelectionToolResult(receipt, selection, description);
            }
            if (toolName == "browser_action"
                && !hasTabTargets
                && _browserAutomation != null
                && JarvisAutomationService.TryGetBrowserTabSelection(
                    GetString(args, "action"),
                    out var tabSelection,
                    out var tabDescription))
            {
                var receipt = await _browserAutomation.ActivateExistingTabAsync(tabSelection, token);
                return ExactTabSelectionToolResult(receipt, tabSelection, tabDescription);
            }

            if (_automation == null)
                return ToolResult(false, $"{toolName} is not a supported keyboard.wtf action yet.", supported: false);

            var latestUserTurn = RecentJarvisUserTurn();
            // A legacy workflow and a current Link can share a trigger. In a
            // compound open, the whole-turn shortcut cannot claim the other
            // actions; resolve just this exact requested Link through the same
            // saved-intent owner used for model app/service guesses. An explicit
            // workflow/routine request keeps its original owner.
            if (toolName == "run_workflow"
                && !Regex.IsMatch(latestUserTurn, @"\b(?:workflow|routine)\b", RegexOptions.IgnoreCase)
                && Regex.IsMatch(latestUserTurn,
                    @"^\s*(?:(?:okay|ok|alright)[,\s]+)?(?:please\s+)?(?:(?:can|could|would|will)\s+you\s+(?:please\s+)?)?(?:open|visit|go\s+to)\s+",
                    RegexOptions.IgnoreCase)
                && (_automation.TryResolveRequestedSavedLinkForOpenTool(
                        latestUserTurn, GetString(args, "name"), out var workflowLinkAlias)
                    || _automation.TryResolveNamedSavedLinkInCompoundOpen(
                        latestUserTurn, GetString(args, "name"), out workflowLinkAlias)))
            {
                AppLog.Info("Exact saved Link corrected model workflow collision.");
                toolName = "open_url";
                args = JsonSerializer.SerializeToElement(new { url = workflowLinkAlias });
            }
            if (toolName is "open_url" or "open_app" or "open_service_page"
                && _automation?.Connectors is { } mailConnector)
            {
                var requestedBrowser = toolName == "open_url" ? GetString(args, "browser") : "";
                var exactOpen = await mailConnector.TryOpenRetainedResourcesAsync(ConnectorUserRequestSnapshot,
                    (url, cancellation) => _automation.ExecuteAsync("open_url",
                        JsonSerializer.SerializeToElement(new { url, browser = requestedBrowser }), cancellation), token,
                    proposedUrl: toolName == "open_url" ? GetString(args, "url") : null);
                if (exactOpen is not null)
                {
                    if (ToolSucceeded(exactOpen))
                        lock (_browserPreRouteLock) _completedDirectOpenForCurrentTurn = true;
                    return exactOpen;
                }
            }
            if (toolName is "open_url" or "open_app" or "open_service_page"
                && TryResolveCurrentSelectedWebDestination(latestUserTurn, out var selectedUrl, out var selectionRejection))
            {
                if (selectionRejection != null)
                    return selectionRejection;
                // One explicitly selected destination owns this action, not a
                // URL guessed from provider history or an earlier selection.
                var requestedBrowser = toolName == "open_url" ? GetString(args, "browser") : "";
                toolName = "open_url";
                args = JsonSerializer.SerializeToElement(new { url = selectedUrl, browser = requestedBrowser });
            }
            if (ShouldRetainDeferredBrowserSourceInsteadOfSavingNote(
                    toolName, latestUserTurn))
            {
                return await RetainDeferredBrowserSourceInsteadOfSavingNoteAsync(
                    latestUserTurn, token);
            }
            var proposedOpenName = toolName switch
            {
                "open_app" => GetString(args, "app_name"),
                "open_service_page" => GetString(args, "service"),
                "open_url" => GetString(args, "url"),
                _ => null,
            };
            if (proposedOpenName != null
                && JarvisAutomationService.IsOrdinaryNavigationRequest(latestUserTurn)
                && JarvisAutomationService.TryResolveExplicitWebNavigation(latestUserTurn, out var explicitWebUrl))
            {
                toolName = "open_url";
                args = JsonSerializer.SerializeToElement(new { url = explicitWebUrl });
            }
            else if (proposedOpenName != null
                && _automation.TryResolveRequestedSavedLinkForOpenTool(
                    latestUserTurn, proposedOpenName, out var exactLinkAlias))
            {
                AppLog.Info($"Exact saved Link corrected model open tool: proposedTool={toolName}");
                toolName = "open_url";
                args = JsonSerializer.SerializeToElement(new { url = exactLinkAlias });
            }
            else if (CanCorrectNamedSavedLinkFromTool(toolName)
                && _automation.TryResolveNamedSavedLinkInCompoundOpen(
                    latestUserTurn, proposedOpenName, out var namedLinkAlias,
                    toolName == "open_service_page" ? GetString(args, "page") : null))
            {
                AppLog.Info($"Named saved Link corrected compound open tool: proposedTool={toolName}");
                toolName = "open_url";
                args = JsonSerializer.SerializeToElement(new { url = namedLinkAlias });
            }
            else if (CanCorrectNamedSavedLinkFromTool(toolName)
                && _automation.TryResolveNamedSavedActionForOpenTool(
                    latestUserTurn, proposedOpenName,
                    toolName == "open_service_page" ? GetString(args, "page") : null, out var savedAction))
            {
                AppLog.Info($"Exact saved action corrected model open tool: proposedTool={toolName} savedTool={savedAction.ToolName}");
                toolName = savedAction.ToolName;
                args = savedAction.Arguments;
            }
            else if (proposedOpenName != null
                && _automation.TryResolveRequestedKnownWebOpen(
                    latestUserTurn, toolName, proposedOpenName, out var knownWebUrl))
            {
                AppLog.Info($"Known web destination corrected model open tool: proposedTool={toolName}");
                var requestedBrowser = toolName == "open_url" ? GetString(args, "browser") : "";
                toolName = "open_url";
                args = JsonSerializer.SerializeToElement(new { url = knownWebUrl, browser = requestedBrowser });
            }
            if (_automation.IsUnrequestedSavedIdentityInNamedOpen(latestUserTurn, toolName,
                    toolName == "run_workflow" ? GetString(args, "name") : GetString(args, "url")))
            {
                AppLog.Info($"Unrequested saved identity rejected before local action: proposedTool={toolName} "
                    + _automation.SavedOpenRejectionDiagnostics(latestUserTurn, toolName,
                        toolName == "run_workflow" ? GetString(args, "name") : GetString(args, "url")));
                return ToolResult(false,
                    "No action was taken: the proposed destination does not resolve the requested saved name. Resolve the exact requested name or ask which destination they mean; do not invent a URL or substitute another saved Link or workflow.",
                    supported: false);
            }
            var currentOpenTarget = toolName switch
            {
                "open_app" => GetString(args, "app_name"),
                "open_service_page" => GetString(args, "service"),
                "open_url" => GetString(args, "url"),
                _ => null,
            };
            if (currentOpenTarget != null
                && _automation.IsUnrequestedDestinationInExactWebOpen(
                    latestUserTurn, toolName, currentOpenTarget))
            {
                AppLog.Info($"Unrequested model destination rejected before local action: proposedTool={toolName}");
                return ToolResult(false,
                    "No action was taken: that destination was not in the current list of exact named websites. Use one of the requested names or ask for clarification.",
                    supported: false);
            }
            if (toolName == "open_url"
                && _automation.TryGetUnresolvedRequestedLinkAlias(
                    latestUserTurn,
                    GetString(args, "url"),
                    out var savedLinkAlias))
            {
                AppLog.Info("Saved Link URL proposal rejected before navigation: exact requested Link must resolve locally.");
                var rejected = ToolResult(
                    false,
                    "No tab was opened: the proposed URL was not an explicit destination in this request, and a saved Link was requested. Call open_url with the exact saved Link alias instead of guessing its URL.",
                    supported: false);
                if (!string.IsNullOrWhiteSpace(savedLinkAlias))
                    rejected["required_link_alias"] = savedLinkAlias;
                return rejected;
            }
            var earlierSelection = RequestsOriginalSelectedContext(latestUserTurn);
            var hasCurrentSelectedContext = HasCurrentSelectedContextForTurn()
                || (earlierSelection && HasOriginalSelectionForCurrentTurn());
            if (RequiresFreshSelectedContextBeforeMutation(
                    toolName,
                    latestUserTurn,
                    hasCurrentSelectedContext && !LiveToolCallDispatcher.HasUnpublishedSelectedContext,
                    HasPriorSelectedAssistantResultForCopy(latestUserTurn))
                || (toolName is "copy_text" or "replace_selected_text"
                    && earlierSelection && !hasCurrentSelectedContext))
            {
                var capture = await GetRecentSelectedTextOrRecaptureAsync(_captureSelectedText, token);
                token.ThrowIfCancellationRequested();
                if (!TryGetCapturedContextText(capture, out _))
                    return BrowserContextCaptureRequired(capture);
                earlierSelection = capture.GetValueOrDefault("source")?.ToString() == "recent_selection";
                // Initial explicit transformation uses the complete accepted source;
                // the separate retained BA/recent-context budget remains unchanged.
                var selectedText = capture["text"].ToString();

                if (!earlierSelection)
                    RecordCapturedBrowserContext("get_selected_text", capture);
                AppLog.Info(
                    $"Jarvis acquired {(earlierSelection ? "original" : "fresh")} selected context before {toolName} could publish a result; characters={selectedText.Length}");
                var captureRequired = ToolResult(
                    false,
                    (earlierSelection ? "The complete original selection was retrieved" : "Fresh selected text was captured")
                    + " before the requested output mutation. The clipboard and selection were left unchanged. "
                    + "Use selected_text as the source content, produce the requested result, then retry the output tool with only that final result.",
                    needsClarification: false);
                captureRequired["clipboard_unchanged"] = true;
                captureRequired["selection_unchanged"] = true;
                captureRequired["selected_context_result_required"] = true;
                captureRequired["selected_text"] = selectedText;
                captureRequired["source"] = earlierSelection ? "recent_selection" : "current_selection";
                return captureRequired;
            }

            if (toolName is "copy_text" or "replace_selected_text"
                && TryRejectSelectedContextInstructionEcho(
                    toolName,
                    GetString(args, "text"),
                    out var selectedContextRejection))
            {
                return selectedContextRejection;
            }

            var result = toolName == "get_selected_text"
                ? await _captureSelectedText(token)
                : await _automation.ExecuteAsync(toolName, args, token);
            if (ToolSucceeded(result) && IsDirectOpenTool(toolName))
            {
                lock (_browserPreRouteLock)
                    _completedDirectOpenForCurrentTurn = true;
            }
            if (toolName is "get_selected_text" or "get_clipboard_text")
                RecordCapturedBrowserContext(toolName, result);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Warning(ex, $"Jarvis tool failed: {toolName}");
            KeyboardWtfState.SetUi(VoiceUiPhase.Error, "Action failed", ex.Message);
            return ToolResult(false, ex.Message);
        }
    }

    private bool ShouldBlockCompetingToolForPreRoutedBrowserTurn(string toolName)
    {
        lock (_browserPreRouteLock)
        {
            if (!_browserPreRouteOwnsCurrentTurn)
                return false;

            return _browserPreRouteReceipt?.BlocksCompetingTool(toolName, DateTime.UtcNow, ownsCurrentTurn: true)
                ?? !IsBrowserTaskLifecycleTool(toolName);
        }
    }

    internal static bool IsBrowserTaskLifecycleTool(string toolName) =>
        (toolName ?? "").Trim() is
            "browser_automation"
            or "browser_automation_status"
            or "browser_automation_control";

    internal static bool ShouldReadSelectionInsteadOfScreen(string toolName, string request) =>
        toolName == "inspect_screen"
        && BrowserAutomationTaskClassifier.ReferencesSelectedUserContext(request)
        && !Regex.IsMatch(request ?? "",
            @"\b(?:analy[sz]e|inspect|describe|capture|look\s+at|read)\s+(?:(?:my|the|this|current|entire|whole)\s+)*(?:screen|display|desktop)\b",
            RegexOptions.IgnoreCase);

    private bool HasCompletedDirectOpenForCurrentTurn()
    {
        lock (_browserPreRouteLock)
            return _completedDirectOpenForCurrentTurn;
    }

    private static bool IsDirectOpenTool(string toolName) =>
        toolName is "open_app" or "open_url" or "open_service_page" or "open_folder" or "open_path";

    internal static bool CanCorrectNamedSavedLinkFromTool(string toolName) =>
        toolName is "open_app" or "open_service_page" or "open_url";

    internal static bool IsWorkflowCreationTool(string toolName) =>
        (toolName ?? "").Trim() is "create_workflow" or "remember_routine";

    public async Task HandleVoiceCommandAsync(string transcript)
    {
        var parsed = VoiceCommandParser.Parse(transcript);
        AppLog.Info($"Voice command parsed: kind={parsed.Kind} argumentCharacters={parsed.Argument?.Length ?? 0}");

        switch (parsed.Kind)
        {
            case ParsedCommandKind.Copy:
                await SendToAsync("Clipboard", TargetText(parsed));
                break;
            case ParsedCommandKind.TypeOut:
                await SendToAsync("Type Out", TargetText(parsed));
                break;
            case ParsedCommandKind.SendTo:
                await SendToAsync(parsed.Argument, TargetText(parsed));
                break;
            case ParsedCommandKind.MakeProfessional:
                ApplyInlineContent(parsed);
                await TransformAndCopyAsync("Rewrite this transcript to sound professional. Return only the rewritten text.");
                break;
            case ParsedCommandKind.MakeCasual:
                ApplyInlineContent(parsed);
                await TransformAndCopyAsync("Rewrite this transcript to sound casual, clear, and friendly. Return only the rewritten text.");
                break;
            case ParsedCommandKind.MakeShorter:
                ApplyInlineContent(parsed);
                await TransformAndCopyAsync("Make this transcript shorter while preserving the meaning. Return only the shortened text.");
                break;
            case ParsedCommandKind.Summarize:
                ApplyInlineContent(parsed);
                await TransformAndCopyAsync("Summarize this transcript into the fewest useful words. Return only the summary.");
                break;
            case ParsedCommandKind.BulletList:
                ApplyInlineContent(parsed);
                await TransformAndCopyAsync("Turn this transcript into concise bullet points. Return only the bullet list.");
                break;
            case ParsedCommandKind.FixGrammar:
                ApplyInlineContent(parsed);
                await TransformAndCopyAsync("Fix grammar, punctuation, and obvious speech recognition errors. Preserve meaning. Return only the corrected text.");
                break;
            case ParsedCommandKind.Email:
                ApplyInlineContent(parsed);
                await TransformAndCopyAsync("Turn this transcript into a polished email. Return only the email text.");
                break;
            case ParsedCommandKind.TranslateGerman:
                ApplyInlineContent(parsed);
                await TransformAndCopyAsync("Translate this transcript to German. Return only the translated text.");
                break;
            case ParsedCommandKind.SaveNote:
                SaveCurrentNote();
                break;
            case ParsedCommandKind.ReadBack:
                ReadBack();
                break;
            case ParsedCommandKind.ReplayLast:
                ReplayLast();
                break;
            case ParsedCommandKind.Cancel:
                _capture.Cancel();
                break;
            case ParsedCommandKind.OpenSettings:
                _app.OpenSettings();
                break;
            case ParsedCommandKind.OpenNotepad:
                OpenNotepad();
                break;
            case ParsedCommandKind.OpenUrl:
                OpenUrl(parsed.Argument);
                break;
            case ParsedCommandKind.GmailDraft:
                OpenGmailDraft(parsed);
                break;
            case ParsedCommandKind.SwitchWhisper:
                _settings.SaveSpeechEngine(SpeechEngine.Whisper);
                _notifications.Info("Speech engine", "Switched to Whisper.");
                break;
            case ParsedCommandKind.SwitchVosk:
                _settings.SaveSpeechEngine(SpeechEngine.Vosk);
                _notifications.Info("Speech engine", "Switched to Vosk.");
                break;
            case ParsedCommandKind.EnableAi:
                _settings.SaveUseAi(true);
                _notifications.Info("AI formatting", "Enabled.");
                break;
            case ParsedCommandKind.DisableAi:
                _settings.SaveUseAi(false);
                _notifications.Info("AI formatting", "Disabled.");
                break;
            default:
                KeyboardWtfState.SetTranscript(transcript);
                await DeliverTextAsync(transcript);
                break;
        }
    }

    public async Task HandleSmartExecutionAsync(string transcript)
    {
        ResetActionCancellation();
        var token = _actionCts.Token;
        KeyboardWtfState.IsProcessingAi = true;
        KeyboardWtfState.SetUi(VoiceUiPhase.Thinking, "Understanding command", "Planning the requested action...");

        try
        {
            using var backendScope = AiProviderRegistry.BeginBackendScope(token);
            var plan = await _actionPlanner.PlanAsync(transcript, token);
            token.ThrowIfCancellationRequested();
            AppLog.Info($"Smart action planned: {plan.Action}");
            KeyboardWtfState.SetUi(VoiceUiPhase.Executing, "Executing", DescribePlan(plan));
            await ExecutePlanAsync(plan, token);
        }
        catch (OperationCanceledException)
        {
            KeyboardWtfState.SetUi(VoiceUiPhase.Cancelled, "Cancelled", "The action was not executed.");
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "Smart command failed");
            KeyboardWtfState.SetUi(VoiceUiPhase.Error, "Command failed", ex.Message);
            _notifications.Error("Command failed", ex.Message);
        }
        finally
        {
            KeyboardWtfState.IsProcessingAi = false;
        }
    }

    private async Task HandleSmartWritingAsync(SmartWritingAudioCapture capture)
    {
        if (AiProviderRegistry.Current is not PaidCloudAiProvider paidProvider)
        {
            var localTranscript = await _capture.TranscribeCapturedAudioAsync(
                capture.WavData,
                preferFast: true,
                _capture.CurrentOperationToken);
            if (!string.IsNullOrWhiteSpace(localTranscript))
                await HandleSmartWritingTranscriptAsync(localTranscript);
            return;
        }

        ResetActionCancellation();
        var actionToken = _actionCts.Token;
        var ownedOperationId = Interlocked.Increment(ref _smartWritingOperationId);
        var operation = Guid.NewGuid().ToString("N")[..12];
        var operationStarted = Stopwatch.GetTimestamp();
        long polishMs = 0;
        long insertionMs = 0;
        var usedLocalRecovery = false;
        var partialRecovery = false;
        try
        {
            KeyboardWtfState.IsProcessingAi = true;
            KeyboardWtfState.SetUi(
                VoiceUiPhase.Thinking,
                "Polishing",
                "Polishing your text...");
            AppLog.Info(
                $"Smart Writing stage: operation={operation} stage=audio_ready recordingMs={capture.RecordingDurationMs} bytes={capture.WavData.Length}");
            var polishStarted = Stopwatch.GetTimestamp();
            using var backendScope = paidProvider.BeginBackendScope(actionToken);
            var segments = SplitSmartWritingAudio(capture.WavData);
            var polishedSegments = new List<string>(segments.Count);
            Exception providerFailure = null;
            for (var index = 0; index < segments.Count; index++)
            {
                actionToken.ThrowIfCancellationRequested();
                ThrowIfSmartWritingOperationWasReplaced(ownedOperationId, actionToken);
                var segment = segments[index];
                var timeoutLimit = SmartWritingTimeoutFor(segment.RecordingDurationMs);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(actionToken);
                timeout.CancelAfter(timeoutLimit);
                try
                {
                    var segmentResult = await paidProvider.ReformatAudioAsync(
                        segment.WavData,
                        SmartWritingSystemInstruction,
                        KeyboardWtfState.SelectedLanguage,
                        timeout.Token);
                    timeout.Token.ThrowIfCancellationRequested();
                    ThrowIfSmartWritingOperationWasReplaced(ownedOperationId, actionToken);
                    segmentResult = FinalizeSmartWritingText(segmentResult);
                    if (string.IsNullOrWhiteSpace(segmentResult))
                        throw new InvalidOperationException("No text was returned for an audio segment.");
                    polishedSegments.Add(segmentResult);
                    AppLog.Info(
                        $"Smart Writing stage: operation={operation} stage=segment_completed " +
                        $"segment={index + 1}/{segments.Count} recordingMs={segment.RecordingDurationMs} " +
                        $"characters={segmentResult.Length}");
                }
                catch (OperationCanceledException) when (
                    actionToken.IsCancellationRequested || !IsCurrentSmartWritingOperation(ownedOperationId))
                {
                    throw;
                }
                catch (Exception ex)
                {
                    providerFailure = ex;
                    AppLog.Warning(
                        ex,
                        $"Smart Writing provider segment failed: operation={operation} " +
                        $"segment={index + 1}/{segments.Count} limitMs={(long)timeoutLimit.TotalMilliseconds}");
                    break;
                }
            }

            string result;
            if (providerFailure is null)
            {
                result = JoinSmartWritingSegments(polishedSegments);
            }
            else
            {
                KeyboardWtfState.SetUi(
                    VoiceUiPhase.Transcribing,
                    "Preserving your speech",
                    "The cloud polish stopped; recovering the complete recording locally.");
                string localTranscript = "";
                try
                {
                    localTranscript = await _capture.TranscribeCapturedAudioAsync(
                        capture.WavData,
                        preferFast: false,
                        actionToken,
                        allowAiTranslation: false);
                }
                catch (OperationCanceledException) when (actionToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    AppLog.Warning(ex, $"Smart Writing local recovery failed: operation={operation}");
                }

                result = ResolveSmartWritingRecoveryText(
                    localTranscript,
                    polishedSegments,
                    out partialRecovery);
                usedLocalRecovery = !string.IsNullOrWhiteSpace(localTranscript);
                if (string.IsNullOrWhiteSpace(result))
                    throw new InvalidOperationException(
                        "Smart Writing could not recover usable text from the captured audio.",
                        providerFailure);
            }

            polishMs = (long)Stopwatch.GetElapsedTime(polishStarted).TotalMilliseconds;
            AppLog.Info(
                $"Smart Writing stage: operation={operation} stage=audio_polish_completed " +
                $"elapsedMs={polishMs} characters={result?.Length ?? 0} segments={segments.Count} " +
                $"localRecovery={usedLocalRecovery} partialRecovery={partialRecovery}");
            actionToken.ThrowIfCancellationRequested();
            ThrowIfSmartWritingOperationWasReplaced(ownedOperationId, actionToken);
            result = FinalizeSmartWritingText(result);
            if (string.IsNullOrWhiteSpace(result))
                throw new InvalidOperationException("No text was returned to insert.");
            KeyboardWtfState.SetTranscript(result);
            var insertionStarted = Stopwatch.GetTimestamp();
            await DeliverTextAsync(result, actionToken, retainFinalTextOnClipboard: true);
            insertionMs = (long)Stopwatch.GetElapsedTime(insertionStarted).TotalMilliseconds;
            if (partialRecovery)
            {
                _notifications.Warning(
                    "Smart Writing partially recovered",
                    "Inserted the usable completed portion. The unprocessed tail remains unavailable.");
            }
            else if (usedLocalRecovery)
            {
                _notifications.Warning(
                    "Smart Writing recovered",
                    "Cloud polishing stopped, so the complete captured speech was transcribed locally and inserted.");
            }
            AppLog.Info(
                $"Smart Writing audio timing: operation={operation} recordingMs={capture.RecordingDurationMs} " +
                $"polishMs={polishMs} insertionMs={insertionMs} totalAfterRecordingMs=" +
                $"{(long)Stopwatch.GetElapsedTime(operationStarted).TotalMilliseconds}");
        }
        catch (OperationCanceledException) when (
            actionToken.IsCancellationRequested || !IsCurrentSmartWritingOperation(ownedOperationId))
        {
            AppLog.Info($"Smart Writing stage: operation={operation} stage=cancelled");
            if (IsCurrentSmartWritingOperation(ownedOperationId))
                KeyboardWtfState.SetUi(VoiceUiPhase.Cancelled, "Cancelled", "Smart writing was stopped.");
        }
        catch (Exception ex)
        {
            AppLog.Warning(ex, "Paid audio Smart Writing failed; no text was inserted");
            if (IsCurrentSmartWritingOperation(ownedOperationId))
            {
                KeyboardWtfState.SetUi(VoiceUiPhase.Error, "Smart Writing unavailable", ex.Message);
                _notifications.Warning("Smart Writing unavailable", ex.Message);
            }
        }
        finally
        {
            if (IsCurrentSmartWritingOperation(ownedOperationId))
            {
                KeyboardWtfState.IsProcessingAi = false;
                if (NeedsSmartWritingTerminalCleanup(KeyboardWtfState.UiPhase, KeyboardWtfState.UiTitle))
                {
                    AppLog.Warning($"Smart Writing stage: operation={operation} stage=terminal_cleanup_recovery");
                    KeyboardWtfState.SetUi(
                        VoiceUiPhase.Error,
                        "Smart Writing did not finish",
                        "The operation ended without a delivery result. Please try again.");
                }
            }
        }
    }

    internal static TimeSpan SmartWritingTimeoutFor(long recordingDurationMs) =>
        TimeSpan.FromSeconds(Math.Clamp(40d + recordingDurationMs / 20_000d, 40d, 55d));

    internal static IReadOnlyList<SmartWritingAudioCapture> SplitSmartWritingAudio(byte[] wavData)
    {
        if (wavData is null || wavData.Length < 44)
            throw new ArgumentException("Smart Writing audio is empty.", nameof(wavData));

        using var input = new MemoryStream(wavData, writable: false);
        using var reader = new WaveFileReader(input);
        var format = reader.WaveFormat;
        if (format.Encoding != WaveFormatEncoding.Pcm
            || format.SampleRate != 16_000
            || format.Channels != 1
            || format.BitsPerSample != 16)
        {
            throw new InvalidDataException("Smart Writing audio must be 16 kHz mono 16-bit PCM WAV.");
        }

        var maximumPcmBytes = checked(format.AverageBytesPerSecond * SmartWritingProviderSegmentSeconds);
        maximumPcmBytes -= maximumPcmBytes % format.BlockAlign;
        var copyBuffer = new byte[64 * 1024];
        var segments = new List<SmartWritingAudioCapture>();
        while (true)
        {
            var written = 0;
            byte[] segmentBytes;
            using (var output = new MemoryStream())
            {
                using (var writer = new WaveFileWriter(output, format))
                {
                    while (written < maximumPcmBytes)
                    {
                        var requested = Math.Min(copyBuffer.Length, maximumPcmBytes - written);
                        requested -= requested % format.BlockAlign;
                        var read = reader.Read(copyBuffer, 0, requested);
                        if (read <= 0)
                            break;
                        writer.Write(copyBuffer, 0, read);
                        written += read;
                    }
                }
                segmentBytes = output.ToArray();
            }

            if (written == 0)
                break;
            var durationMs = Math.Max(
                1,
                (long)Math.Round(written * 1000d / format.AverageBytesPerSecond));
            segments.Add(new SmartWritingAudioCapture(segmentBytes, durationMs));
        }

        if (segments.Count == 0)
            throw new InvalidDataException("Smart Writing audio contains no PCM samples.");
        return segments;
    }

    internal static string JoinSmartWritingSegments(IEnumerable<string> segments) =>
        string.Join(
            Environment.NewLine + Environment.NewLine,
            (segments ?? Array.Empty<string>())
                .Select(FinalizeSmartWritingText)
                .Where(value => !string.IsNullOrWhiteSpace(value)));

    internal static string ResolveSmartWritingRecoveryText(
        string localTranscript,
        IReadOnlyList<string> completedSegments,
        out bool partial)
    {
        var local = FinalizeSmartWritingText(localTranscript);
        if (!string.IsNullOrWhiteSpace(local))
        {
            partial = false;
            return local;
        }

        var completed = JoinSmartWritingSegments(completedSegments);
        partial = !string.IsNullOrWhiteSpace(completed);
        return completed;
    }

    internal static string FinalizeSmartWritingText(string value) =>
        SmartWritingFinalEditor.Clean(value);

    private async Task HandleSmartWritingTranscriptAsync(string transcript)
    {
        ResetActionCancellation();
        var actionToken = _actionCts.Token;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(actionToken);
        timeout.CancelAfter(SmartWritingTimeout);
        var token = timeout.Token;
        var ownedOperationId = Interlocked.Increment(ref _smartWritingOperationId);
        var operation = Guid.NewGuid().ToString("N")[..12];
        var operationStarted = Stopwatch.GetTimestamp();
        long polishMs = 0;
        long insertionMs = 0;
        var deliveryStarted = false;
        try
        {
            using var backendScope = AiProviderRegistry.BeginBackendScope(token);
            AppLog.Info($"Smart Writing stage: operation={operation} stage=transcript_ready");
            KeyboardWtfState.SetTranscript(transcript);
            var polishStarted = Stopwatch.GetTimestamp();
            AppLog.Info($"Smart Writing stage: operation={operation} stage=polish_started");
            var result = await _capture.ApplyPromptToCurrentTranscriptAsync(
                "Clean this spoken dictation into ready-to-send writing. Remove filler words and false starts, " +
                "honor self-corrections such as 'actually' or 'scratch that', add natural punctuation, and keep " +
                "the speaker's meaning and tone. Return only the final text.",
                token);
            polishMs = (long)Stopwatch.GetElapsedTime(polishStarted).TotalMilliseconds;
            AppLog.Info(
                $"Smart Writing stage: operation={operation} stage=polish_completed elapsedMs={polishMs}");
            token.ThrowIfCancellationRequested();
            ThrowIfSmartWritingOperationWasReplaced(ownedOperationId, token);
            var deliveryText = FinalizeSmartWritingText(
                string.IsNullOrWhiteSpace(result) ? transcript : result);
            var insertionStarted = Stopwatch.GetTimestamp();
            AppLog.Info($"Smart Writing stage: operation={operation} stage=insertion_started");
            deliveryStarted = true;
            await DeliverTextAsync(
                deliveryText,
                actionToken,
                retainFinalTextOnClipboard: true);
            insertionMs = (long)Stopwatch.GetElapsedTime(insertionStarted).TotalMilliseconds;
            AppLog.Info(
                $"Smart Writing timing: operation={operation} recordingMs={_capture.LastRecordingDurationMs} " +
                $"transcriptionMs={_capture.LastTranscriptionDurationMs} polishMs={polishMs} " +
                $"insertionMs={insertionMs} totalAfterTranscriptMs=" +
                $"{(long)Stopwatch.GetElapsedTime(operationStarted).TotalMilliseconds}");
        }
        catch (OperationCanceledException) when (
            actionToken.IsCancellationRequested || !IsCurrentSmartWritingOperation(ownedOperationId))
        {
            AppLog.Info($"Smart Writing stage: operation={operation} stage=cancelled");
            if (IsCurrentSmartWritingOperation(ownedOperationId))
                KeyboardWtfState.SetUi(VoiceUiPhase.Cancelled, "Cancelled", "Smart writing was stopped.");
        }
        catch (OperationCanceledException)
        {
            AppLog.Warning(
                $"Smart Writing stage: operation={operation} stage=timeout limitMs={(long)SmartWritingTimeout.TotalMilliseconds}");
            if (!deliveryStarted)
                await DeliverSmartWritingTranscriptRecoveryAsync(
                    transcript,
                    ownedOperationId,
                    actionToken,
                    "Smart Writing timed out",
                    "Polishing took too long, so keyboard.wtf preserved and delivered the captured transcript without the final AI edit.");
        }
        catch (Exception ex)
        {
            AppLog.Warning(ex, "Smart Writing failed");
            if (!deliveryStarted)
            {
                await DeliverSmartWritingTranscriptRecoveryAsync(
                    transcript,
                    ownedOperationId,
                    actionToken,
                    "AI polish unavailable",
                    "keyboard.wtf preserved and delivered the captured transcript without the final AI edit.");
            }
            else if (IsCurrentSmartWritingOperation(ownedOperationId))
            {
                KeyboardWtfState.SetUi(
                    VoiceUiPhase.Error,
                    "Smart Writing delivery failed",
                    "The final text remains available in keyboard.wtf and on the clipboard when Windows allowed it.");
                _notifications.Warning(
                    "Smart Writing delivery failed",
                    "The final text remains available in keyboard.wtf and on the clipboard when Windows allowed it.");
            }
        }
        finally
        {
            // Every Smart Writing operation owns one terminal UI transition.
            // This guards against a completed provider/delivery path leaving
            // the stopwatch overlay in Polishing indefinitely.
            if (IsCurrentSmartWritingOperation(ownedOperationId)
                && NeedsSmartWritingTerminalCleanup(
                    KeyboardWtfState.UiPhase,
                    KeyboardWtfState.UiTitle))
            {
                AppLog.Warning(
                    $"Smart Writing stage: operation={operation} stage=terminal_cleanup_recovery");
                KeyboardWtfState.SetUi(
                    VoiceUiPhase.Error,
                    "Smart Writing did not finish",
                    "The operation ended without a delivery result. Please try again.");
            }
        }
    }

    private async Task DeliverSmartWritingTranscriptRecoveryAsync(
        string transcript,
        int ownedOperationId,
        CancellationToken actionToken,
        string title,
        string message)
    {
        actionToken.ThrowIfCancellationRequested();
        ThrowIfSmartWritingOperationWasReplaced(ownedOperationId, actionToken);
        var recovery = ResolveSmartWritingTranscriptRecovery(transcript);
        if (string.IsNullOrWhiteSpace(recovery))
            throw new InvalidOperationException("No captured transcript was available to recover.");

        KeyboardWtfState.SetTranscript(recovery);
        await DeliverTextAsync(
            recovery,
            actionToken,
            retainFinalTextOnClipboard: true);
        _notifications.Warning(title, message);
    }

    internal static string ResolveSmartWritingTranscriptRecovery(string transcript) =>
        FinalizeSmartWritingText(transcript);

    private async Task<Dictionary<string, object>> RememberLinkAliasAsync(
        JsonElement args,
        CancellationToken token)
    {
        if (_automation == null)
            return ToolResult(false, "No action executor is available.", supported: false);
        var alias = GetString(args, "alias");
        var url = GetString(args, "url");
        if (string.IsNullOrWhiteSpace(url))
        {
            var selection = await _automation.ExecuteAsync(
                "get_selected_text",
                JsonSerializer.SerializeToElement(new { }),
                token);
            url = TryGetSelectedHttpUrl(selection);
            if (string.IsNullOrWhiteSpace(url))
            {
                return ToolResult(
                    false,
                    "The selected text is not a valid web link. Select one URL or hostname and try again.",
                    supported: false);
            }
            args = JsonSerializer.SerializeToElement(new { alias, url });
        }
        else if (!BrowserLauncherService.TryNormalizeUrl(url, out var normalizedUrl))
        {
            return ToolResult(
                false,
                "The supplied destination is not a valid web link.",
                supported: false);
        }
        else
        {
            args = JsonSerializer.SerializeToElement(new { alias, url = normalizedUrl.AbsoluteUri });
        }
        return await _automation.ExecuteAsync("remember_link_alias", args, token);
    }

    internal static string TryGetSelectedHttpUrl(Dictionary<string, object> result)
    {
        if (result == null
            || !result.TryGetValue("ok", out var ok)
            || ok is not true
            || !result.TryGetValue("text", out var textValue))
            return "";
        var text = textValue?.ToString()?.Trim() ?? "";
        return BrowserLauncherService.TryNormalizeUrl(text, out var uri)
            ? uri.AbsoluteUri
            : "";
    }

    internal static bool TryGetSelectedLinkAliasFromRoutineProposal(
        JsonElement args, string userTurn, out string alias)
    {
        alias = "";
        if (args.ValueKind != JsonValueKind.Object
            || !Regex.IsMatch(userTurn ?? "",
                @"\b(?:selected|highlighted|selection)\b|\b(?:this|that)\s+(?:web\s+)?(?:url|link|website|site)\b",
                RegexOptions.IgnoreCase)
            || !args.TryGetProperty("actions", out var actions)
            || actions.ValueKind != JsonValueKind.Array
            || actions.GetArrayLength() != 1)
            return false;
        var action = actions[0];
        if (action.ValueKind != JsonValueKind.Object
            || !string.Equals(GetString(action, "kind") ?? GetString(action, "type"),
                "url", StringComparison.OrdinalIgnoreCase)
            || BrowserLauncherService.TryNormalizeUrl(
                GetString(action, "target") ?? GetString(action, "value"), out _))
            return false;
        alias = GetString(args, "trigger")?.Trim() ?? "";
        return alias.Length > 0;
    }

    internal static bool NeedsSmartWritingTerminalCleanup(
        VoiceUiPhase phase,
        string title) =>
        phase == VoiceUiPhase.Thinking
        && string.Equals(title, "Polishing", StringComparison.Ordinal);

    private bool IsCurrentSmartWritingOperation(int operationId) =>
        Volatile.Read(ref _smartWritingOperationId) == operationId;

    private void ThrowIfSmartWritingOperationWasReplaced(
        int operationId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsCurrentSmartWritingOperation(operationId))
            throw new OperationCanceledException("A newer Smart Writing operation replaced this result.");
    }

    private async Task DeliverRawDictationAsync(string transcript)
    {
        KeyboardWtfState.SetTranscript(transcript);
        await DeliverTextAsync(transcript, _capture.CurrentOperationToken);
    }

    private async Task ExecutePlanAsync(SmartActionPlan plan, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        switch (plan.Action)
        {
            case "open_notepad":
                OpenNotepad();
                break;
            case "open_settings":
                OpenWindowsSettings();
                break;
            case "open_url":
                OpenUrl(plan.Content);
                break;
            case "gmail_draft":
                OpenGmailDraft(plan.Recipient, plan.Subject, plan.Content);
                break;
            case "copy":
                await SendToAsync("Clipboard", plan.Content);
                break;
            case "ask":
                KeyboardWtfState.LastSendResult = "Needs clarification";
                break;
            default:
                KeyboardWtfState.SetTranscript(plan.Content);
                await DeliverTextAsync(plan.Content);
                break;
        }

        token.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(plan.SpokenResponse))
        {
            KeyboardWtfState.SetUi(VoiceUiPhase.Speaking, "keyboard.wtf", plan.SpokenResponse);
            TtsService.Instance.Speak(plan.SpokenResponse, "en", _app?.UpdateWorkAdmission);
            var wordCount = plan.SpokenResponse.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
            var speakingMs = Math.Clamp(wordCount * 330, 1200, 5500);
            await Task.Delay(speakingMs, token);
            KeyboardWtfState.SetUi(
                VoiceUiPhase.Done,
                KeyboardWtfState.LastSendResult ?? "Done",
                Shorten(plan.Content, 90));
        }
        else
        {
            KeyboardWtfState.SetUi(
                VoiceUiPhase.Done,
                KeyboardWtfState.LastSendResult ?? "Done",
                Shorten(plan.Content, 90));
        }
    }

    private Dictionary<string, object> OpenKeyboardWtfSettings(string section)
    {
        var canonical = SettingsSectionRegistry.Resolve(section);
        if (!string.IsNullOrWhiteSpace(section) && string.IsNullOrWhiteSpace(canonical))
            return ToolResult(false, "That keyboard.wtf Settings section does not exist.", supported: false);
        _app.OpenSettings(canonical);
        KeyboardWtfState.LastSendResult = "Opened keyboard.wtf Settings";
        return ToolResult(
            true,
            string.IsNullOrWhiteSpace(canonical)
                ? "Opened keyboard.wtf Settings."
                : $"Opened keyboard.wtf Settings at {canonical.Replace('-', ' ')}.");
    }

    private static Dictionary<string, object> OpenWindowsSettings()
    {
        Process.Start(new ProcessStartInfo { FileName = "ms-settings:", UseShellExecute = true });
        KeyboardWtfState.LastSendResult = "Opened Windows Settings";
        return ToolResult(true, "Opened Windows Settings.");
    }

    private Dictionary<string, object> OpenAllowedApp(string appName)
    {
        var normalized = NormalizeAppName(appName);
        if (string.IsNullOrWhiteSpace(normalized))
            return ToolResult(false, "Which app should I open?", needsClarification: true);

        switch (normalized)
        {
            case "notepad":
                OpenNotepad();
                return ToolResult(true, "Opened Notepad.");
            case "calculator":
                return StartAllowedProcess("calc.exe", "Opened Calculator.");
            case "paint":
                return StartAllowedProcess("mspaint.exe", "Opened Paint.");
            case "file explorer":
                return StartAllowedProcess("explorer.exe", "Opened File Explorer.");
            case "settings":
                return OpenWindowsSettings();
            case "gmail":
                return OpenSafeUrl("https://mail.google.com/mail/u/0/#inbox");
            case "whatsapp":
                return OpenWhatsAppHome("Opened WhatsApp.");
            default:
                return ToolResult(
                    false,
                    $"I cannot open {appName} yet. Supported apps are Notepad, Calculator, Paint, File Explorer, Settings, Gmail, and WhatsApp.",
                    supported: false);
        }
    }

    private Dictionary<string, object> OpenSafeUrl(string url)
    {
        if (!IsSafeHttpUrl(url, out var uri))
        {
            KeyboardWtfState.LastSendResult = "Unsafe URL blocked";
            _notifications.Warning("URL blocked", "Only http and https links can be opened from voice.");
            return ToolResult(false, "Only http and https links can be opened from voice.", supported: false);
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = uri.ToString(),
            UseShellExecute = true,
        });
        KeyboardWtfState.LastSendResult = $"Opened {uri.Host}";
        _notifications.Info("Opened", uri.Host);
        return ToolResult(true, $"Opened {uri.Host}.", detail: uri.ToString());
    }

    private Dictionary<string, object> OpenGmailDraftTool(JsonElement args)
    {
        var recipient = GetString(args, "recipient") ?? GetString(args, "to") ?? "";
        var subject = GetString(args, "subject") ?? "";
        var body = GetString(args, "body") ?? GetString(args, "message") ?? "";

        if (string.IsNullOrWhiteSpace(recipient))
            return ToolResult(false, "Who should I address the email to?", needsClarification: true);
        if (string.IsNullOrWhiteSpace(body))
            return ToolResult(false, "What should the email say?", needsClarification: true);

        OpenGmailDraft(recipient, subject, body);
        return ToolResult(true, "Gmail draft opened. Review it before sending.", detail: recipient);
    }

    private Dictionary<string, object> PrepareWhatsAppMessage(JsonElement args)
    {
        var message = GetString(args, "message") ?? GetString(args, "text") ?? "";
        var contactName = GetString(args, "contact_name") ?? GetString(args, "contactName") ?? "";
        var phoneNumber = GetString(args, "phone_number") ?? GetString(args, "phoneNumber") ?? "";

        if (string.IsNullOrWhiteSpace(message))
            return ToolResult(false, "What should the WhatsApp message say?", needsClarification: true);

        ClipboardHelper.SetText(message);
        if (!string.IsNullOrWhiteSpace(phoneNumber))
        {
            var phone = NormalizePhoneNumber(phoneNumber);
            if (string.IsNullOrWhiteSpace(phone))
                return ToolResult(false, "That phone number does not look usable.", needsClarification: true);

            var encoded = Uri.EscapeDataString(message);
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = $"whatsapp://send?phone={Uri.EscapeDataString(phone)}&text={encoded}",
                    UseShellExecute = true,
                });
            }
            catch
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = $"https://wa.me/{phone.TrimStart('+')}?text={encoded}",
                    UseShellExecute = true,
                });
            }

            KeyboardWtfState.LastSendResult = "WhatsApp draft opened; manual send required";
            _notifications.Info("WhatsApp", "Message prepared. Review and send manually.");
            return ToolResult(true, "WhatsApp message prepared. Review it before sending.", detail: phone);
        }

        var result = OpenWhatsAppHome("WhatsApp opened; message copied.");
        var contact = string.IsNullOrWhiteSpace(contactName) ? "that contact" : contactName.Trim();
        result["ok"] = false;
        result["supported"] = false;
        result["message"] = $"I cannot choose the WhatsApp contact {contact} automatically yet. I copied the message and opened WhatsApp so you can paste it.";
        result["contact_lookup_supported"] = false;
        return result;
    }

    private async Task<Dictionary<string, object>> CopyTextToolAsync(string text, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(text))
            return ToolResult(false, "What should I copy?", needsClarification: true);
        await SendToAsync("Clipboard", text);
        token.ThrowIfCancellationRequested();
        return ToolResult(true, "Copied to clipboard.");
    }

    private Dictionary<string, object> StartAllowedProcess(string fileName, string message)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = true,
        });
        KeyboardWtfState.LastSendResult = message.TrimEnd('.');
        _notifications.Info("Opened", message.TrimEnd('.'));
        KeyboardWtfState.SetUi(VoiceUiPhase.Done, message.TrimEnd('.'), "Ready.");
        return ToolResult(true, message);
    }

    private Dictionary<string, object> OpenWhatsAppHome(string message)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = "whatsapp://", UseShellExecute = true });
        }
        catch
        {
            Process.Start(new ProcessStartInfo { FileName = "https://web.whatsapp.com", UseShellExecute = true });
        }

        KeyboardWtfState.LastSendResult = message.TrimEnd('.');
        _notifications.Info("WhatsApp", message.TrimEnd('.'));
        return ToolResult(true, message);
    }

    private void OpenNotepad()
    {
        var systemNotepad = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "notepad.exe");
        var executable = File.Exists(systemNotepad) ? systemNotepad : "notepad.exe";

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            });
            KeyboardWtfState.LastSendResult = "Opened Notepad";
            KeyboardWtfState.SetUi(VoiceUiPhase.Done, "Opened Notepad", "Ready for dictation.");
            _notifications.Info("Opened", "Notepad is ready.");
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "Notepad launch failed");
            KeyboardWtfState.SetUi(VoiceUiPhase.Error, "Could not open Notepad", ex.Message);
            _notifications.Error("Notepad failed", ex.Message);
        }
    }

    private void OpenUrl(string url)
    {
        if (!IsSafeHttpUrl(url, out var uri))
        {
            KeyboardWtfState.LastSendResult = "Unsafe URL blocked";
            _notifications.Warning("URL blocked", "Only http and https links can be opened from voice.");
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = uri.ToString(),
            UseShellExecute = true,
        });
        KeyboardWtfState.LastSendResult = $"Opened {uri.Host}";
        _notifications.Info("Opened", uri.Host);
    }

    private void OpenGmailDraft(ParsedVoiceCommand parsed)
    {
        var recipient = parsed.Argument ?? "";
        var body = !string.IsNullOrWhiteSpace(parsed.Content)
            ? parsed.Content
            : KeyboardWtfState.CurrentTranscript;

        OpenGmailDraft(recipient, "", body);
    }

    private void OpenGmailDraft(string recipient, string subject, string body)
    {
        recipient ??= "";
        subject ??= "";
        body ??= "";

        if (!string.IsNullOrWhiteSpace(body))
            ClipboardHelper.SetText(body);

        var query = new List<string> { "view=cm", "fs=1" };
        if (!string.IsNullOrWhiteSpace(recipient))
            query.Add($"to={Uri.EscapeDataString(recipient)}");
        if (!string.IsNullOrWhiteSpace(subject))
            query.Add($"su={Uri.EscapeDataString(subject)}");
        if (!string.IsNullOrWhiteSpace(body))
            query.Add($"body={Uri.EscapeDataString(body)}");

        var url = "https://mail.google.com/mail/?" + string.Join("&", query);
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true,
        });

        KeyboardWtfState.LastSendResult = "Gmail draft opened; manual send required";
        KeyboardWtfState.SetUi(
            VoiceUiPhase.Done,
            "Gmail draft ready",
            "Review the draft, then click Send.");
        _notifications.Info("Gmail draft", "Opened Gmail compose. Review and send manually.");
    }

    private async Task SendToAsync(string destination, string text = null)
    {
        text ??= KeyboardWtfState.CurrentTranscript;
        if (!string.IsNullOrWhiteSpace(text))
            KeyboardWtfState.SetTranscript(text);
        var ok = await _router.SendAsync(destination, text);
        _notifications.Outcome(
            ok,
            "Sent",
            "Send failed",
            KeyboardWtfState.LastSendResult ?? destination);
    }

    private static string TargetText(ParsedVoiceCommand parsed) =>
        !string.IsNullOrWhiteSpace(parsed.Content)
            ? parsed.Content
            : KeyboardWtfState.CurrentTranscript;

    private static void ApplyInlineContent(ParsedVoiceCommand parsed)
    {
        if (!string.IsNullOrWhiteSpace(parsed.Content))
            KeyboardWtfState.SetTranscript(parsed.Content);
    }

    private async Task TransformAndCopyAsync(string prompt)
    {
        var text = await _capture.ApplyPromptToCurrentTranscriptAsync(prompt);
        if (!string.IsNullOrWhiteSpace(text))
        {
            ClipboardHelper.SetText(text);
            _notifications.Info("Copied", "Transformed text copied to clipboard.");
        }
    }

    private async Task DeliverTextAsync(
        string text,
        CancellationToken cancellationToken = default,
        bool retainFinalTextOnClipboard = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (retainFinalTextOnClipboard
            && !EnsureFinalTextOnClipboard(text))
        {
            AppLog.Warning("Smart Writing could not place the final text on the clipboard before delivery");
            KeyboardWtfState.SetUi(
                VoiceUiPhase.Error,
                "Clipboard unavailable",
                "The final text remains visible in keyboard.wtf. Please try again.");
            _notifications.Warning(
                "Clipboard unavailable",
                "Smart Writing could not safely copy or paste the final text.");
            return;
        }
        if (retainFinalTextOnClipboard)
            AppLog.Info($"Smart Writing prepared {text.Length} final characters on the clipboard");

        var destination = _settings.Current.TextDeliveryMode switch
        {
            VoiceTextDeliveryMode.ClipboardOnly => "Clipboard",
            VoiceTextDeliveryMode.AskEveryTime => ChooseDeliveryDestination(),
            _ => "Type Out",
        };
        if (string.IsNullOrWhiteSpace(destination))
        {
            KeyboardWtfState.SetUi(VoiceUiPhase.Cancelled, "Delivery cancelled", "The transcript was not typed or copied.");
            return;
        }

        var insertionTarget = _capture.CurrentDeliveryTarget;
        if (destination == "Type Out"
            && (insertionTarget is null || !insertionTarget.IsCurrent()))
        {
            await _router.SendAsync("Clipboard", text);
            _notifications.Warning(
                "Focus changed — copied instead",
                "The original field is no longer active, so keyboard.wtf did not type into another app.");
            KeyboardWtfState.SetUi(
                VoiceUiPhase.Done,
                "Copied — focus changed",
                "Return to the intended field and paste when ready.");
            return;
        }

        var ok = await _router.SendAsync(
            destination,
            text,
            insertionTarget,
            cancellationToken);
        if (ok && retainFinalTextOnClipboard)
        {
            if (EnsureFinalTextOnClipboard(text))
            {
                AppLog.Info($"Smart Writing retained {text.Length} final characters on the clipboard");
            }
            else
            {
                AppLog.Warning("Smart Writing inserted the final text but could not retain it on the clipboard");
                KeyboardWtfState.SetUi(
                    VoiceUiPhase.Error,
                    "Inserted — clipboard unavailable",
                    "The text was inserted, but Windows did not retain the clipboard copy.");
                _notifications.Warning(
                    "Inserted — clipboard unavailable",
                    "The text was inserted, but Windows did not retain the clipboard copy.");
                return;
            }
        }
        if (!ok && destination == "Type Out")
        {
            var copied = await _router.SendAsync("Clipboard", text);
            _notifications.Warning(
                copied ? "Copied instead" : "Delivery failed",
                copied
                    ? "Typing failed, so the transcript was copied to clipboard."
                    : "Typing and clipboard delivery both failed. The transcript remains in keyboard.wtf.");
            KeyboardWtfState.SetUi(
                copied ? VoiceUiPhase.Done : VoiceUiPhase.Error,
                copied ? "Copied instead" : "Delivery failed",
                copied
                    ? "Typing failed, so the final text is on the clipboard."
                    : "The final text remains available in keyboard.wtf.");
        }
        else
        {
            _notifications.Outcome(
                ok,
                "Done",
                "Failed",
                KeyboardWtfState.LastSendResult ?? destination);
            KeyboardWtfState.SetUi(
                ok ? VoiceUiPhase.Done : VoiceUiPhase.Error,
                ok ? "Done" : "Delivery failed",
                ok ? Shorten(text, 90) : "The transcript remains on the clipboard.");
        }
    }

    private static string ChooseDeliveryDestination()
    {
        DialogResult ShowChoice() => MessageBox.Show(
            "Type this transcript into the active app?\n\nChoose No to copy it to the clipboard instead.",
            "keyboard.wtf delivery",
            MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button1);

        var owner = Application.OpenForms.Cast<Form>().FirstOrDefault();
        DialogResult choice;
        if (owner?.InvokeRequired == true)
            choice = (DialogResult)owner.Invoke(ShowChoice);
        else
            choice = ShowChoice();
        return choice switch
        {
            DialogResult.Yes => "Type Out",
            DialogResult.No => "Clipboard",
            _ => "",
        };
    }

    internal static bool RetainFinalTextOnClipboard(
        string text,
        Func<string, bool> setter = null) =>
        (setter ?? ClipboardHelper.SetText)(text ?? "");

    internal static bool EnsureFinalTextOnClipboard(
        string text,
        Func<string, bool> setter = null,
        Func<string> getter = null)
    {
        text ??= "";
        setter ??= ClipboardHelper.SetText;
        getter ??= ClipboardHelper.GetText;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (setter(text)
                && string.Equals(getter(), text, StringComparison.Ordinal))
                return true;
            if (attempt < 2)
                Thread.Sleep(35 * (attempt + 1));
        }
        return false;
    }

    private async Task CancelCurrentAsync()
    {
        _actionCts?.Cancel();
        _capture.Cancel();
        if (_browserAutomation != null)
            await _browserAutomation.CancelAsync("Global Ctrl+Alt+X cancellation");
        _automation?.ObserveUserTranscript("cancel");
        if (_liveConversation?.IsActive == true)
            await _liveConversation.StopAsync();
    }

    private async Task<Dictionary<string, object>> StartBrowserAutomationAsync(
        JsonElement args,
        CancellationToken token)
    {
        if (_browserAutomation == null)
            return ToolResult(false, "Browser Automation is unavailable.", supported: false);

        var modelTask = GetString(args, "task");
        var userTurn = RecentJarvisUserTurn();
        var directOpenCompleted = HasCompletedDirectOpenForCurrentTurn();
        if (directOpenCompleted
            && !TryResolveScopedBrowserSubtask(modelTask, userTurn, out _))
        {
            AppLog.Info(
                $"Browser scoped handoff rejected after direct open: modelChars={(modelTask ?? "").Length} "
                + $"userChars={userTurn.Length} modelHasUrl={Regex.IsMatch(modelTask ?? "", @"https?://", RegexOptions.IgnoreCase)} "
                + $"modelStartsOpen={Regex.IsMatch(modelTask ?? "", @"^\s*(?:open|launch|go\s+to|navigate\s+to|visit)\b", RegexOptions.IgnoreCase)}");
            return ToolResult(
                false,
                "The earlier direct open already completed. No Browser task was started because its proposed task could replay that action. Call browser_automation with only the verbatim remaining page-interaction clause, including all later constraints.",
                supported: false);
        }
        var request = directOpenCompleted
            ? modelTask.Trim().TrimEnd('.', '!', '?')
            : ResolveBrowserAutomationTask(modelTask, userTurn);
        var classification = _browserAutomation.Classify(request);
        if (classification.Decision != BrowserAutomationDecision.BrowserAutomation)
        {
            AppLog.Info(
                $"Browser tool rejected by authoritative capability routing: decision={classification.Decision} reason={classification.Reason}");
            var explicitSelection = classification.Decision == BrowserAutomationDecision.AskUser
                && classification.PrefersSelectedContent
                && BrowserAutomationTaskClassifier.ReferencesSelectedUserContext(request);
            var rejected = new Dictionary<string, object>
            {
                ["ok"] = false,
                ["started"] = false,
                ["state"] = "idle",
                ["supported"] = false,
                ["needs_clarification"] = !explicitSelection && classification.Decision == BrowserAutomationDecision.AskUser,
                ["message"] = explicitSelection
                    ? "Browser Automation was not started: the user requested processing selected content, not operating the webpage. Use the full selected text captured for this request, or get_selected_text if it has not been captured. Perform the requested transformation normally, then use copy_text or replace_selected_text only if the user requested that output. This routing rejection is not a selection-length error; do not apply Browser Automation's excerpt budget to the full selection."
                    : "Browser Automation was not started because this request can use a direct app, URL, or dedicated capability.",
                ["routing_reason"] = classification.Reason,
            };
            if (explicitSelection)
                rejected["required_capability"] = "selected_text";
            return rejected;
        }

        var attachedCapturedContext = TryAttachCapturedBrowserContext(
            request,
            GetBoolean(args, "use_captured_context"),
            out var groundedRequest);
        request = groundedRequest;

        // Selected-context ownership cannot depend on optional model tool
        // order. This existing task-start owner captures the explicitly named
        // source only when no retained context already satisfies the request.
        // A failed capture leaves Browser Automation idle instead of starting
        // a task with silently missing or stale information.
        if (!attachedCapturedContext
            && BrowserAutomationTaskClassifier.RequiresFreshUserContextCapture(request))
        {
            var captureTool = BrowserAutomationTaskClassifier.FreshContextCaptureTool(request);
            if (_automation == null || string.IsNullOrWhiteSpace(captureTool))
                return BrowserContextCaptureRequired();

            var capture = await _automation.ExecuteAsync(
                captureTool,
                JsonSerializer.SerializeToElement(new { }),
                token);
            token.ThrowIfCancellationRequested();
            if (!TryGetCapturedContextText(capture, out var capturedText))
                return BrowserContextCaptureRequired(capture);

            request = BuildBrowserTaskWithCapturedContext(
                request,
                captureTool == "get_selected_text" ? "selected text" : "clipboard text",
                capturedText,
                CapturedContextIsPartial(capture, capturedText));
            attachedCapturedContext = true;
            AppLog.Info(
                $"Browser task captured fresh {captureTool} context at its authoritative start boundary; characters={capturedText.Length}");
        }

        token.ThrowIfCancellationRequested();
        var result = _browserAutomation.Start(request);
        if (result.Started && attachedCapturedContext)
            ConsumeCapturedBrowserContext();
        var lifecycleState = result.Started
            ? "starting"
            : NormalizeBrowserTaskState(_browserAutomation.Snapshot());
        return new Dictionary<string, object>
        {
            ["ok"] = result.Started,
            ["started"] = result.Started,
            ["state"] = lifecycleState,
            ["supported"] = true,
            ["needs_clarification"] = result.NeedsClarification,
            ["needs_consent"] = result.NeedsConsent,
            ["task_id"] = result.TaskId,
            ["message"] = result.Message,
            ["routing_reason"] = result.RoutingReason,
            ["lifecycle_note"] = "This response describes only this invocation. For any later browser request, call browser_automation again; do not assume this task is still active.",
        };
    }

    private string RecentJarvisUserTurn()
    {
        lock (_browserPreRouteLock)
        {
            return DateTime.UtcNow - _latestJarvisUserTurnUtc <= TimeSpan.FromSeconds(60)
                ? _latestJarvisUserTurn
                : "";
        }
    }

    internal static string ResolveBrowserAutomationTask(
        string modelTask,
        string latestUserTurn)
    {
        var model = BrowserAutomationText.Truncate((modelTask ?? "").Trim(), 4000);
        var user = BrowserAutomationText.Truncate((latestUserTurn ?? "").Trim(), 4000);
        if (user.Length == 0)
            return model;

        // The raw user turn is the authoritative task contract. Gemini may
        // summarize a tool argument, and generic summaries such as "Browser
        // automation" lose entities, dates, constraints, and terminal intent.
        // A short context-only acknowledgement is the exception: in that case
        // the model must resolve the referenced task from the conversation.
        if (Regex.IsMatch(
            user,
            @"^\s*(?:yes[, ]*)?(?:please\s+)?(?:do (?:it|that)|go ahead|proceed|continue|resume|same as before|try again)\s*[.!?]*\s*$",
            RegexOptions.IgnoreCase)
            || BrowserAutomationCoordinator.IsExplicitPageHandoffGuidance(user))
        {
            return model;
        }

        return user;
    }

    internal static bool TryResolveScopedBrowserSubtask(
        string modelTask,
        string userTurn,
        out string scopedTask)
    {
        scopedTask = (modelTask ?? "").Trim().TrimEnd('.', '!', '?');
        var user = (userTurn ?? "").Trim().TrimEnd('.', '!', '?');
        return scopedTask.Length > 0
            && scopedTask.Length < user.Length
            // The suffix must be the remaining page interaction, not another
            // destination open that belongs to the direct resolver. Ambiguous
            // "open" page controls can be retried as a concrete click/choose
            // request; they must not cause a saved Link to be guessed by BA.
            && !Regex.IsMatch(
                scopedTask,
                @"^(?:open|launch|go\s+to|navigate\s+to|visit)\b",
                RegexOptions.IgnoreCase)
            && user.EndsWith(scopedTask, StringComparison.OrdinalIgnoreCase);
    }

    private void RecordCapturedBrowserContext(
        string toolName,
        Dictionary<string, object> result)
    {
        if (toolName == "get_selected_text")
        {
            var full = result?.GetValueOrDefault("text")?.ToString() ?? "";
            var complete = result?.GetValueOrDefault("ok") is true
                && result.GetValueOrDefault("truncated") is not true
                && !string.IsNullOrWhiteSpace(full);
            if (complete)
            {
                lock (_browserPreRouteLock)
                {
                    // Only a complete accepted selection replaces the source.
                    // Failed recapture cannot dispose a valid earlier snapshot.
                    var accepted = _selectionHistory.Accept(result);
                    _originalSelectedText = accepted.Text;
                    _originalSelectionReadTurn = _latestJarvisUserTurn;
                    _originalSelectionContextId = result.GetValueOrDefault("source_context_id")?.ToString() ?? "";
                    _originalSelectionCaptureId = result.GetValueOrDefault("capture_id")?.ToString() ?? "";
                    _originalSelectionAllowsImplicitFollowup = true;
                }
            }
            else if (result?.GetValueOrDefault("truncated") is true)
            {
                // A real but incomplete new selection is not the old source.
                // Keep explicit original recall, never implicit resurrection.
                lock (_browserPreRouteLock) _originalSelectionAllowsImplicitFollowup = false;
            }
        }
        if (!TryGetCapturedContextText(result, out var capturedText))
            return;

        var userTurn = RecentJarvisUserTurn();
        lock (_browserPreRouteLock)
        {
            _capturedBrowserContextText = capturedText;
            _capturedBrowserContextSource = toolName == "get_selected_text"
                ? "selected text"
                : "clipboard text";
            _capturedBrowserContextUserTurn = userTurn;
            _capturedBrowserContextUtc = DateTime.UtcNow;
            _capturedBrowserContextTruncated = CapturedContextIsPartial(result, capturedText);
            _capturedBrowserContextIsCurrentTurn = true;
            _capturedBrowserContextAuthorizedForLaterAction =
                BrowserAutomationTaskClassifier.IsDeferredInteractionRequest(userTurn);
        }
        AppLog.Info(
            $"Jarvis captured ephemeral {_capturedBrowserContextSource} for explicit same-conversation use; "
            + $"characters={_capturedBrowserContextText.Length} truncated={_capturedBrowserContextTruncated}");
    }

    internal static bool IsExplicitLocalNotePersistenceRequest(string userTurn)
    {
        var text = userTurn ?? "";
        if (Regex.IsMatch(
            text,
            @"\b(?:do\s+not|don't|dont|never|without)\s+(?:save|write|store|create|take|make)\b",
            RegexOptions.IgnoreCase))
            return false;
        // An unrelated "save this document" clause does not authorize
        // persisting the selected/clipboard source from a separate deferred
        // browser instruction. The lasting note must be about that source.
        const string source = @"\b(?:selected|highlighted|selection|copied|clipboard)\b";
        // save_note writes Jarvis Notes.md; a request for a separate file or
        // document is not permission to put the source into that note.
        const string destination = @"\b(?:note|notes)\b";
        const string persist = @"\b(?:save|write|store|create)\b";
        const string sameClause = @"(?:(?!\b(?:and|then)\b)[^,;.!?\r\n]){0,100}";
        return Regex.IsMatch(
            text,
            persist + sameClause + source + sameClause + destination
            + "|" + persist + sameClause + destination + sameClause + source
            + @"|\b(?:take|make)\s+(?:a\s+)?note\b" + sameClause + source,
            RegexOptions.IgnoreCase);
    }

    internal static bool ShouldRetainDeferredBrowserSourceInsteadOfSavingNote(
        string toolName,
        string userTurn) =>
        string.Equals(toolName, "save_note", StringComparison.Ordinal)
        && BrowserAutomationTaskClassifier.IsDeferredInteractionRequest(userTurn)
        && BrowserAutomationTaskClassifier.ReferencesCapturedUserContext(userTurn)
        && !IsExplicitLocalNotePersistenceRequest(userTurn);

    private async Task<Dictionary<string, object>> RetainDeferredBrowserSourceInsteadOfSavingNoteAsync(
        string userTurn,
        CancellationToken token)
    {
        // "Use this selection when I tell you to" authorizes an ephemeral
        // browser input, not an on-disk note. Reuse a capture from this same
        // turn, otherwise delegate to the existing selection/clipboard owner.
        lock (_browserPreRouteLock)
        {
            if (CapturedBrowserContextIsFreshLocked()
                && IsSameJarvisUserTurn(_capturedBrowserContextUserTurn, userTurn)
                && _capturedBrowserContextAuthorizedForLaterAction
                && CapturedContextSourceMatchesRequest(userTurn, _capturedBrowserContextSource))
            {
                return DeferredBrowserSourceRetainedResult(_capturedBrowserContextSource);
            }
        }

        var captureTool = BrowserAutomationTaskClassifier.FreshContextCaptureTool(userTurn);
        if (string.IsNullOrWhiteSpace(captureTool))
            return ToolResult(false,
                "No local note was saved. I need the selected or copied source before I can hold it temporarily for the later browser task.",
                supported: false);

        var capture = await _automation.ExecuteAsync(
            captureTool,
            JsonSerializer.SerializeToElement(new { include_text = true }),
            token);
        token.ThrowIfCancellationRequested();
        if (!TryGetCapturedContextText(capture, out _))
            return ToolResult(false,
                "No local note was saved, and I could not capture the selected or copied source. Please select or copy it again before the browser task.",
                supported: false);

        RecordCapturedBrowserContext(captureTool, capture);
        return DeferredBrowserSourceRetainedResult(
            captureTool == "get_selected_text" ? "selected text" : "clipboard text");
    }

    private static Dictionary<string, object> DeferredBrowserSourceRetainedResult(string source)
    {
        var result = ToolResult(
            true,
            $"Captured the {source} temporarily for the requested later browser task. No local note was saved.");
        result["retained_temporarily"] = true;
        result["durable_saved"] = false;
        result["source"] = source;
        return result;
    }

    private Dictionary<string, object> GetRecentSelectedText()
    {
        lock (_browserPreRouteLock)
        {
            var named = _selectionHistory.TryNamedReference(_latestJarvisUserTurn, out var recalled);
            if (named && recalled == null)
            {
                var unresolved = ToolResult(false,
                    "Which captured selection do you mean? That name does not identify one retained source.",
                    needsClarification: true);
                unresolved["selection_history"] = _selectionHistory.Index();
                return unresolved;
            }
            if (!named && Regex.IsMatch(_latestJarvisUserTurn,
                @"\b(?:earlier|previous|prior)\s+(?:selected\s+text|selection)\b", RegexOptions.IgnoreCase))
                recalled = _selectionHistory.Previous;
            if (recalled != null)
            {
                _originalSelectedText = recalled.Text;
                _originalSelectionContextId = recalled.ContextId;
                _originalSelectionCaptureId = recalled.CaptureId;
                _originalSelectionAllowsImplicitFollowup = true;
            }
            // Original-source follow-ups may recall this immutable snapshot.
            // Explicit current/fresh intent requires foreground capture;
            // the provider's use_recent hint cannot authorize an older source.
            var currentCapture = recalled == null && HasCurrentSelectedContextForTurn()
                && HasOriginalSelectionForCurrentTurn();
            if (!currentCapture && !RequestsOriginalSelectedContext(_latestJarvisUserTurn))
                return ToolResult(false,
                    "No earlier selection was requested. Read the current selection if the user selected text now.",
                    supported: false);
            if (string.IsNullOrWhiteSpace(_originalSelectedText))
                return ToolResult(false,
                    "The earlier selected text is no longer available in this conversation. Ask the user to select it again.",
                    supported: false);

            _originalSelectionReadTurn = _latestJarvisUserTurn;
            var result = ToolResult(true, "The complete original selected text was retrieved for this explicit follow-up, not an earlier generated answer.");
            result["text"] = _originalSelectedText;
            result["source"] = currentCapture ? "current_selection" : "recent_selection";
            result["content_role"] = "user_selected_source_content";
            result["truncated"] = false;
            result["source_context_id"] = _originalSelectionContextId;
            result["capture_id"] = _originalSelectionCaptureId;
            result["selection_history"] = _selectionHistory.Index();
            return result;
        }
    }

    private bool HasOriginalSelectionForCurrentTurn()
    {
        lock (_browserPreRouteLock)
            return !string.IsNullOrWhiteSpace(_originalSelectedText)
                && IsSameJarvisUserTurn(_originalSelectionReadTurn, _latestJarvisUserTurn);
    }

    internal async Task<Dictionary<string, object>> GetRecentSelectedTextOrRecaptureAsync(
        Func<CancellationToken, Task<Dictionary<string, object>>> captureCurrent,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var request = RecentJarvisUserTurn();
        if (UsesRecentVerifiedCopiedResult(request))
            return ReadRecentVerifiedCopiedResult();
        var recent = GetRecentSelectedText();
        // Explicit current-source intent outranks a provider's use_recent
        // hint, including when an older snapshot exists. An earlier source
        // or an unresolved "it" must never substitute today's text.
        var implicitReference = IsImplicitOriginalSelectionReference(request);
        if ((ToolSucceeded(recent) && recent.GetValueOrDefault("source")?.ToString() == "current_selection")
            || ReferencesEarlierSelection(request))
            return recent;
        var captured = await captureCurrent(token);
        token.ThrowIfCancellationRequested();
        // A pronoun can mean the newly selected foreground text, even in the
        // same document. Only an unavailable capture plus a genuine, still
        // compatible source context permits retained-source follow-up. An
        // oversized/incomplete new selection must never resurrect older text.
        if ((implicitReference || IsSimpleSelectionFollowup(request)) && !ToolSucceeded(captured)
            && captured.GetValueOrDefault("truncated") is not true
            && RequestsOriginalSelectedContext(request))
            return GetRecentSelectedText();
        RecordCapturedBrowserContext("get_selected_text", captured);
        if (ToolSucceeded(captured))
            lock (_browserPreRouteLock) captured["selection_history"] = _selectionHistory.Index();
        else if (IsSimpleSelectionFollowup(request) && captured.GetValueOrDefault("truncated") is not true)
            lock (_browserPreRouteLock)
                if (_selectionHistory.Count > 1)
                {
                    var ambiguous = ToolResult(false,
                        "No current selection was readable. Which earlier selection should I use?", needsClarification: true);
                    ambiguous["selection_history"] = _selectionHistory.Index();
                    return ambiguous;
                }
        return captured;
    }

    private async Task<Dictionary<string, object>> StartSelectedTranslationAsync(
        string request, string language, CancellationToken proposalToken)
    {
        if (!SelectedTextTranslationOperation.TryCanonicalLanguage(language, out language))
            return ToolResult(false, "Which language should I translate the selection into? No translation task was started.", needsClarification: true);
        if (_selectedTranslation?.IsRunning == true)
            return ToolResult(false, "A selected-text translation is already running. Cancel it explicitly before replacing it; no second job was started.");
        proposalToken.ThrowIfCancellationRequested();
        IDisposable captureWork = null;
        var updateWorkAdmission = _app?.UpdateWorkAdmission;
        if (updateWorkAdmission != null && !updateWorkAdmission.TryAcquire(out captureWork))
            return ToolResult(false, "An update is preparing installation. Please wait.");
        using var ownedCapture = captureWork;
        KeyboardWtfState.SetUi(VoiceUiPhase.Executing, KeyboardWtfState.AssistantName, "Reading selected text");
        var copiedResult = UsesRecentVerifiedCopiedResult(request);
        var earlier = !copiedResult && RequestsOriginalSelectedContext(request);
        // A capture already accepted for this still-open user turn remains
        // its source even if the final transcript adds the clipboard clause.
        // A later current-selection turn must capture anew; an unresolved
        // original/pronoun reference must never substitute unrelated text.
        var capture = await GetRecentSelectedTextOrRecaptureAsync(_captureSelectedText, proposalToken);
        proposalToken.ThrowIfCancellationRequested();
        if (!ToolSucceeded(capture) || !capture.TryGetValue("text", out var value)
            || value is not string source || string.IsNullOrWhiteSpace(source))
            return BrowserContextCaptureRequired(capture);
        earlier = capture.GetValueOrDefault("source")?.ToString() == "recent_selection";
        if (!earlier && !copiedResult) RecordCapturedBrowserContext("get_selected_text", capture);

        var operation = new SelectedTextTranslationOperation(request, language, _liveConversation.SessionCancellation,
            updateWorkAdmission);
        AppLog.Info($"Selected translation source: operation={operation.Id} capture={capture.GetValueOrDefault("capture_id")} "
            + $"context={capture.GetValueOrDefault("source_context_id")} retained={earlier} characters={source.Length}");
        Interlocked.Exchange(ref _selectedTranslation, operation)?.Dispose();
        KeyboardWtfState.BeginLongTaskUi(operation.Id, "Translating...", displayDelayMilliseconds: 500);
        var receipt = operation.Receipt();
        RecordAuthoritativePreRoute(request, "selected_text_translation", receipt);
        operation.Start(source, (text, targetLanguage, cancellation) =>
                _liveConversation.TranslateSelectedTextAsync(text, targetLanguage, operation.Id, cancellation),
            // Preserve the normal AlwaysAsk/confirmation boundary as well as
            // CopyText's Windows read-back verification. The operation lock
            // has already accepted or rejected cancellation before this call.
            output =>
            {
                var copied = _automation.ExecuteAsync("copy_text",
                    JsonSerializer.SerializeToElement(new { text = output }), CancellationToken.None).GetAwaiter().GetResult();
                _automation.AttributeVerifiedCopy(output, request, copied);
                return copied;
            },
            async completed =>
            {
                if (!ReferenceEquals(_selectedTranslation, completed) || completed.IsCancelled) return;
                var outcome = completed.Receipt();
                var detail = outcome["message"]?.ToString() ?? "Translation finished without a verified clipboard result.";
                var announced = await _liveConversation.TryAnnounceLifecycleAsync(new()
                {
                    Source = "selected_text_translation",
                    State = outcome.GetValueOrDefault("confirmation_required") is true ? "NeedsUserAction"
                        : ToolSucceeded(outcome) ? "Completed" : "Failed",
                    Detail = detail,
                    IsCurrent = () => ReferenceEquals(_selectedTranslation, completed) && !completed.IsCancelled,
                }, _liveConversation.SessionCancellation);
                if (!announced && ReferenceEquals(_selectedTranslation, completed) && !completed.IsCancelled)
                    _notifications?.Info(outcome.GetValueOrDefault("confirmation_required") is true ? "Translation ready"
                        : ToolSucceeded(outcome) ? "Translation copied" : "Translation failed", detail);
            }, status => KeyboardWtfState.TryUpdateLongTaskUi(operation.Id, status),
            sourceComplete: capture.GetValueOrDefault("truncated") is not true);
        return receipt;
    }

    private bool RequestsOriginalSelectedContext(string request)
    {
        if (UsesRecentVerifiedCopiedResult(request)) return false;
        if (ReferencesEarlierSelection(request)) return true;
        if (RequestsFreshSelectedContext(request) && !IsSimpleSelectionFollowup(request)) return false;
        lock (_browserPreRouteLock)
            return !string.IsNullOrWhiteSpace(_originalSelectedText)
                && _originalSelectionAllowsImplicitFollowup
                && (IsImplicitOriginalSelectionReference(request)
                    || IsSimpleSelectionFollowup(request) && _selectionHistory.Count == 1)
                && SelectionSourceContextMatches(_originalSelectionContextId, _currentSelectionContextId());
    }

    // A result just copied for the preceding user turn is a separate content
    // referent. It never overwrites immutable original selections. A late
    // background completion from another turn cannot take that referent.
    internal bool UsesRecentVerifiedCopiedResult(string request)
    {
        if (!IsImplicitOriginalSelectionReference(request)
            || BrowserAutomationTaskClassifier.ReferencesSelectedUserContext(request))
            return false;
        var copied = _automation?.LatestVerifiedClipboardCopy;
        lock (_browserPreRouteLock)
            return copied != null && IsSameJarvisUserTurn(copied.OriginatingUserTurn, _lastCompletedJarvisUserTurn);
    }

    private Dictionary<string, object> ReadRecentVerifiedCopiedResult()
    {
        var copied = _automation?.LatestVerifiedClipboardCopy;
        try
        {
            if (copied != null && string.Equals(_readClipboardText(), copied.Text, StringComparison.Ordinal))
                return new()
                {
                    ["ok"] = true, ["text"] = copied.Text, ["source"] = "verified_clipboard_result",
                    ["content_role"] = "user_copied_result_content", ["truncated"] = false,
                    ["capture_id"] = "verified-clipboard", ["source_context_id"] = "verified-clipboard",
                    ["message"] = "The current clipboard still contains the verified result the user just asked to copy. Use this result for the pronoun follow-up, not the original selection.",
                };
        }
        catch { /* A failed clipboard read cannot authorize a stale result. */ }
        var failed = ToolResult(false, "The clipboard no longer matches that copied result. Which text should I translate?", needsClarification: true);
        failed["clipboard_unchanged"] = true;
        return failed;
    }

    private static bool IsSimpleSelectionFollowup(string request) => Regex.IsMatch(
        SelectedTextTranslationOperation.NormalizeRequest(request),
        @"^\s*(?:(?:okay|ok|now|please|actually)[,\s]+)*(?:(?:can|could|would|will)\s+you\s+(?:please\s+)?)?"
        + @"(?:translate|rewrite|reword|polish|summarize|analyse|analyze)\s+(?:the\s+)?(?:entire\s+|whole\s+|full\s+|complete\s+)?selection\b",
        RegexOptions.IgnoreCase);

    internal static bool SelectionSourceContextMatches(string captured, string current) =>
        !string.IsNullOrWhiteSpace(captured)
        && string.Equals(captured, current, StringComparison.Ordinal);

    private static bool IsImplicitOriginalSelectionReference(string request) => Regex.IsMatch(SelectedTextTranslationOperation.NormalizeRequest(request),
        @"^\s*(?:(?:okay|ok|now|please|actually)[,\s]+)*(?:(?:can|could|would|will)\s+you\s+(?:please\s+)?)?"
        + @"(?:translate|rewrite|reword|polish|summarize|analyse|analyze)\s+"
        + @"(?:it|that(?:\s+(?:selection|selected\s+text))?|the whole thing)(?=\s*(?:$|[.!?,;]|\b(?:in|into|to|and|then|please)\b))",
        RegexOptions.IgnoreCase)
        || (SelectedTextTranslationOperation.TryResolveClipboardTranslationIntent(request, out _, out _, out var source)
            && (source.Equals("it", StringComparison.OrdinalIgnoreCase) || source.Equals("that", StringComparison.OrdinalIgnoreCase)));

    private static bool RequestsFreshSelectedContext(string request)
    {
        if (ReferencesEarlierSelection(request)) return false;
        if (SessionSelectionHistory.TryReferenceLabel(request, out _)) return true;
        const string source = @"\b(?:selected(?:\s+text)?|highlighted(?:\s+text)?|selection)\b";
        return Regex.IsMatch(SelectedTextTranslationOperation.NormalizeRequest(request),
            @"\b(?:fresh|new|newly|different|current|currently)\s+" + source
            + "|" + source + @"\s+now\b"
            + @"|\b(?:analyze|analyse)\s+this\s+(?:selected\s+text|highlighted\s+text|selection)\b"
            + @"|^\s*(?:(?:okay|ok|now|please|actually)[,\s]+)*(?:(?:can|could|would|will)\s+you\s+(?:please\s+)?)?"
            + @"(?:translate|rewrite|reword|polish|summarize|analyse|analyze)\s+(?:(?:the|this|my|a)\s+)?"
            + @"(?:(?:entire|whole|full|complete)\s+)*(?:selected\s+text|highlighted\s+text|selection)\b",
            RegexOptions.IgnoreCase);
    }

    internal static bool ReferencesEarlierSelection(string request) => Regex.IsMatch(
        request ?? "",
        @"\b(?:selected|highlighted)\b.{0,100}\b(?:earlier|previously|ago)\b"
        + @"|\b(?:earlier|previous|prior)\s+(?:selected\s+text|selection)\b"
        + @"|\b(?:earlier|previously)\b.{0,100}\b(?:selected|highlighted)\b"
        + @"|\b(?:original|entire original)\s+(?:selected\s+text|selection)\b",
        RegexOptions.IgnoreCase) || SessionSelectionHistory.IsHistoricalNamedReference(request);

    private bool TryRejectSelectedContextInstructionEcho(
        string toolName,
        string proposedText,
        out Dictionary<string, object> result)
    {
        result = null;
        string request;
        string selection;
        lock (_browserPreRouteLock)
        {
            if (!CapturedBrowserContextIsFreshLocked()
                || !string.Equals(
                    _capturedBrowserContextSource,
                    "selected text",
                    StringComparison.OrdinalIgnoreCase)
                || !IsSameJarvisUserTurn(
                    _capturedBrowserContextUserTurn,
                    _latestJarvisUserTurn))
            {
                return false;
            }

            request = _latestJarvisUserTurn;
            selection = _capturedBrowserContextText;
        }

        if (!IsSelectedContextInstructionEcho(proposedText, request, selection))
            return false;

        AppLog.Warning(
            $"Jarvis blocked a selected-context {toolName} payload that resembled the current instruction instead of the captured selection result; "
            + $"requestCharacters={request.Length} selectionCharacters={selection.Length} proposedCharacters={(proposedText ?? "").Length}");
        result = ToolResult(
            false,
            "The proposed output text restates the user's instruction instead of providing a result derived from the selected content. "
            + $"Leave the clipboard and selection unchanged, transform or analyze the selected text returned by get_selected_text, then call {toolName} with only that final result.",
            needsClarification: false);
        result["clipboard_unchanged"] = true;
        result["selection_unchanged"] = true;
        result["selected_context_result_required"] = true;
        return true;
    }

    private bool HasCurrentSelectedContextForTurn()
    {
        lock (_browserPreRouteLock)
        {
            return _capturedBrowserContextIsCurrentTurn
                && CapturedBrowserContextIsFreshLocked()
                && string.Equals(
                    _capturedBrowserContextSource,
                    "selected text",
                    StringComparison.OrdinalIgnoreCase)
                && IsSameJarvisUserTurn(
                    _capturedBrowserContextUserTurn,
                    _latestJarvisUserTurn);
        }
    }

    internal bool TryResolveCurrentSelectedWebDestination(
        string request, out string url, out Dictionary<string, object> rejection)
    {
        url = "";
        rejection = null;
        const string prefix = @"^\s*(?:(?:please|pls|can\s+you|could\s+you|would\s+you|now)\s+)*(?:open|visit|go\s+to|navigate\s+to)\s+";
        var explicitSelection = Regex.IsMatch(request ?? "", prefix
            + @"(?:(?:this|that|the|my)\s+)?(?:selected|highlighted)\s+(?:web\s+)?(?:site|website|link|url|domain|text)\s*[.!?]*\s*$",
            RegexOptions.IgnoreCase);
        var deictic = Regex.IsMatch(request ?? "", prefix + @"(?:this|that)\s*[.!?]*\s*$", RegexOptions.IgnoreCase);
        lock (_browserPreRouteLock)
        {
            var current = HasCurrentSelectedContextForTurn();
            // A normal referent such as "open that" must keep its existing
            // action context. A selection owns it only after a current read.
            if (!explicitSelection && !(deictic && current))
                return false;
            if (!current)
            {
                rejection = ToolResult(false,
                    "No destination was opened. This request refers to the current selection, not an earlier URL. Call get_selected_text with use_recent=false, then open only that selected web destination.");
                rejection["current_selection_required"] = true;
                return true;
            }
            if (_capturedBrowserContextTruncated
                || !BrowserLauncherService.TryNormalizeUrl(_capturedBrowserContextText, out var destination))
            {
                rejection = ToolResult(false,
                    "No destination was opened. The current selected text is not one complete valid web destination. Ask the user to select a URL or domain; do not reuse an earlier website.");
                rejection["invalid_selected_destination"] = true;
                return true;
            }
            url = destination.AbsoluteUri;
            return true;
        }
    }

    private bool HasPriorSelectedAssistantResultForCopy(string currentTurn)
    {
        DateTime capturedUtc;
        lock (_browserPreRouteLock)
        {
            if (!CapturedBrowserContextIsFreshLocked()
                || DateTime.UtcNow - _capturedBrowserContextUtc > TimeSpan.FromMinutes(5)
                || !string.Equals(_capturedBrowserContextSource, "selected text", StringComparison.OrdinalIgnoreCase)
                || IsSameJarvisUserTurn(_capturedBrowserContextUserTurn, currentTurn))
                return false;
            capturedUtc = _capturedBrowserContextUtc;
        }
        return _liveConversation?.HasRecentAssistantOutputAfter(
            capturedUtc, TimeSpan.FromMinutes(5)) == true;
    }

    internal static bool RequiresFreshSelectedContextBeforeMutation(
        string toolName,
        string request,
        bool hasCurrentSelectedContext,
        bool hasPriorSelectedAssistantResult = false) =>
        !hasCurrentSelectedContext
        && (toolName == "replace_selected_text"
            || (toolName == "copy_text"
                && BrowserAutomationTaskClassifier.ReferencesSelectedUserContext(request)
                && !(hasPriorSelectedAssistantResult && IsPureCopyOfRecentAssistantResult(request))));

    internal static bool IsPureCopyOfRecentAssistantResult(string request) =>
        Regex.IsMatch(request ?? "",
            @"^\s*(?:(?:okay|ok|alright|so|now|please|pls|hey\s+jarvis|can\s+you|could\s+you|would\s+you)\s+)*copy\b",
            RegexOptions.IgnoreCase)
        && ReferencesRecentAssistantResult(request)
        && !Regex.IsMatch(request ?? "",
            @"\b(?:translate|rewrite|reword|polish|summarize|analyse|analyze|improve|edit|correct|fix|convert|transform)\b",
            RegexOptions.IgnoreCase);

    internal static bool ReferencesRecentAssistantResult(string request) =>
        Regex.IsMatch(request ?? "",
            @"\b(?:what|whatever)\s+(?:you|jarvis)\s+(?:(?:just|earlier|previously)\s+)?(?:said|gave|translated|rewrote|polished|generated|answered)\b"
            + @"|\b(?:translation|rewrite|polished\s+text|result|answer|output|what)\b.{0,100}\b(?:you|your|jarvis)\b.{0,60}\b(?:just|earlier|previously)\b"
            + @"|\b(?:you|jarvis)\b.{0,60}\b(?:just|earlier|previously)\b.{0,100}\b(?:said|gave|translated|rewrote|polished|generated|answered)\b",
            RegexOptions.IgnoreCase);

    private static bool IsSameJarvisUserTurn(string capturedTurn, string currentTurn)
    {
        var captured = (capturedTurn ?? "").Trim();
        var current = (currentTurn ?? "").Trim();
        return captured.Length > 0
            && current.Length > 0
            && (string.Equals(captured, current, StringComparison.OrdinalIgnoreCase)
                || current.StartsWith(captured + " ", StringComparison.OrdinalIgnoreCase));
    }

    internal static bool IsSelectedContextInstructionEcho(
        string proposedCopy,
        string request,
        string selection)
    {
        var candidateTokens = SemanticTokens(proposedCopy);
        var requestTokens = SemanticTokens(request);
        var selectionTokens = SemanticTokens(selection);
        if (candidateTokens.Count < 5
            || requestTokens.Count < 5
            || selectionTokens.Count == 0)
        {
            return false;
        }

        var requestCoverage = candidateTokens.Count(requestTokens.Contains)
            / (double)candidateTokens.Count;
        var selectionCoverage = candidateTokens.Count(selectionTokens.Contains)
            / (double)candidateTokens.Count;
        return requestCoverage >= 0.55
            && selectionCoverage <= 0.35
            && requestCoverage - selectionCoverage >= 0.30;
    }

    private static HashSet<string> SemanticTokens(string value) =>
        Regex.Matches((value ?? "").ToLowerInvariant(), @"[\p{L}\p{N}]+")
            .Select(match => match.Value)
            .Where(token => token.Length >= 3 || token.All(char.IsDigit))
            .ToHashSet(StringComparer.Ordinal);

    private bool TryAttachCapturedBrowserContext(
        string request,
        bool currentRequestUsesCapturedContext,
        out string groundedRequest)
    {
        groundedRequest = request ?? "";
        lock (_browserPreRouteLock)
        {
            var fresh = CapturedBrowserContextIsFreshLocked();
            var referencesContext =
                BrowserAutomationTaskClassifier.ReferencesCapturedUserContext(request);
            var capturedForCurrentTurn = string.Equals(
                _capturedBrowserContextUserTurn,
                _latestJarvisUserTurn,
                StringComparison.OrdinalIgnoreCase);
            var pageMutation =
                BrowserAutomationTaskClassifier.RequiresPageMutation(request);
            var continuesDeferredAction =
                IsCompatibleDeferredBrowserAction(_capturedBrowserContextUserTurn, request);
            if (!CanAttachCapturedBrowserContext(
                    fresh,
                    referencesContext,
                    capturedForCurrentTurn,
                    _capturedBrowserContextAuthorizedForLaterAction,
                    pageMutation,
                    currentRequestUsesCapturedContext,
                    continuesDeferredAction)
                || !CapturedContextSourceMatchesRequest(
                    request,
                    _capturedBrowserContextSource))
            {
                return false;
            }

            groundedRequest = BuildBrowserTaskWithCapturedContext(
                groundedRequest,
                _capturedBrowserContextSource,
                _capturedBrowserContextText,
                _capturedBrowserContextTruncated);
            return true;
        }
    }

    internal static bool CanAttachCapturedBrowserContext(
        bool fresh,
        bool requestReferencesContext,
        bool capturedForCurrentTurn,
        bool explicitlyAuthorizedForLaterAction,
        bool requestRequiresPageMutation,
        bool currentRequestUsesCapturedContext = false,
        bool requestContinuesDeferredAction = false) =>
        fresh
        && requestRequiresPageMutation
        && ((requestReferencesContext && capturedForCurrentTurn)
            || currentRequestUsesCapturedContext
            || (explicitlyAuthorizedForLaterAction
                && (requestReferencesContext || requestContinuesDeferredAction)));

    // A deferred, explicitly user-authorized selection is one-use context for
    // that later page action. The later utterance may omit the source noun;
    // never attach it to a different action or an expanded target merely
    // because some browser work happens within the retention window.
    internal static bool IsCompatibleDeferredBrowserAction(
        string originalRequest,
        string currentRequest)
    {
        if (!BrowserAutomationTaskClassifier.IsDeferredInteractionRequest(originalRequest)
            || !BrowserAutomationTaskClassifier.RequiresPageMutation(currentRequest))
            return false;

        var original = SemanticTokens(originalRequest);
        var current = SemanticTokens(currentRequest);
        current.ExceptWith(DeferredBrowserContinuationFiller);
        return current.Count >= 2 && current.All(original.Contains);
    }

    private static readonly HashSet<string> DeferredBrowserContinuationFiller = new(
        ["all", "right", "alright", "okay", "please", "can", "could", "would", "will",
         "you", "now", "then", "go", "ahead", "the", "this", "that", "my", "our",
         "your", "current", "active", "here", "there", "out", "just"],
        StringComparer.Ordinal);

    internal static bool CapturedContextSourceMatchesRequest(
        string request,
        string capturedSource)
    {
        var requiredTool = BrowserAutomationTaskClassifier.FreshContextCaptureTool(request);
        if (string.IsNullOrWhiteSpace(requiredTool))
            return true;
        var requiredSource = requiredTool == "get_selected_text"
            ? "selected text"
            : "clipboard text";
        return string.Equals(
            requiredSource,
            (capturedSource ?? "").Trim(),
            StringComparison.OrdinalIgnoreCase);
    }

    internal static string BuildBrowserTaskWithCapturedContext(
        string request,
        string source,
        string context,
        bool sourceTruncated = false)
    {
        var task = (request ?? "").Trim();
        var sourceName = (source ?? "context").Trim();
        var captured = (context ?? "").Trim();
        var prefix = $"{task}\n\n"
            + $"AUTHORITATIVE USER-PROVIDED {sourceName.ToUpperInvariant()} "
            + "(ephemeral for this task; use only supported values and ask for genuinely missing required information):\n";

        var sourceLimit = string.Equals(sourceName, "clipboard text", StringComparison.OrdinalIgnoreCase)
            ? JarvisAutomationService.ClipboardContextCharacterLimit
            : string.Equals(sourceName, "selected text", StringComparison.OrdinalIgnoreCase)
                ? 6000
                : 0;
        if (sourceLimit == 0)
            return BrowserAutomationText.Truncate(prefix + captured, 10000);

        // The complete selected-text path stays unchanged. Only a genuinely
        // partial capture, or a task too long to carry its full source, gains
        // an explicit partial-source note instead of silently losing facts.
        const int taskLimit = 10000;
        var available = Math.Min(
            sourceLimit,
            Math.Max(0, taskLimit - prefix.Length));
        if (sourceTruncated || captured.Length > available)
        {
            prefix = $"{task}\n\n"
                + $"AUTHORITATIVE USER-PROVIDED {sourceName.ToUpperInvariant()} "
                + "(ephemeral for this task; source is partial. If an answer is not in this captured prefix, "
                + "do not claim it was absent from the original; ask for the omitted portion):\n";
            available = Math.Min(
                sourceLimit,
                Math.Max(0, taskLimit - prefix.Length));
        }

        return BrowserAutomationText.Truncate(
            prefix + BrowserAutomationText.Truncate(captured, available),
            taskLimit);
    }

    private bool HasFreshCapturedBrowserContext()
    {
        lock (_browserPreRouteLock)
            return CapturedBrowserContextIsFreshLocked();
    }

    private void ConsumeCapturedBrowserContext()
    {
        lock (_browserPreRouteLock)
            ClearCapturedBrowserContextLocked();
    }

    private bool CapturedBrowserContextIsFreshLocked() =>
        !string.IsNullOrWhiteSpace(_capturedBrowserContextText)
        && DateTime.UtcNow - _capturedBrowserContextUtc <= TimeSpan.FromMinutes(10);

    private void ClearCapturedBrowserContextLocked()
    {
        _capturedBrowserContextText = "";
        _capturedBrowserContextSource = "";
        _capturedBrowserContextUserTurn = "";
        _capturedBrowserContextUtc = default;
        _capturedBrowserContextTruncated = false;
        _capturedBrowserContextIsCurrentTurn = false;
        _capturedBrowserContextAuthorizedForLaterAction = false;
    }

    private bool TryGetPreRoutedToolResult(
        string toolName,
        out Dictionary<string, object> result)
    {
        lock (_browserPreRouteLock)
        {
            var receipt = _browserPreRouteReceipt;
            if (receipt?.AppliesTo(toolName, DateTime.UtcNow, _browserPreRouteOwnsCurrentTurn) != true)
            {
                result = null;
                return false;
            }

            if (!receipt.TryAcknowledgeOnce())
            {
                result = SuppressedPreRouteResult(receipt);
                return true;
            }

            if (receipt.Result != null)
            {
                result = new Dictionary<string, object>(receipt.Result);
                return true;
            }

            if (string.Equals(toolName, "browser_automation", StringComparison.Ordinal))
            {
                result = new Dictionary<string, object>
                {
                    ["ok"] = receipt.Ok,
                    ["started"] = receipt.Started,
                    ["state"] = receipt.Started ? "starting" : "idle",
                    ["supported"] = true,
                    ["needs_clarification"] = false,
                    ["task_id"] = receipt.TaskId,
                    ["message"] = receipt.Message,
                    ["routing_reason"] = "The local transcript router already started this exact browser task.",
                    ["lifecycle_note"] = "A browser task is started, not completed. Use browser_automation_status for live progress.",
                };
                return true;
            }

            result = ToolResult(
                receipt.Ok,
                receipt.Message,
                supported: true,
                detail: "The verified local browser command was already executed once for this user turn.");
            return true;
        }
    }

    private bool TryConsumePreRoutedAuthoritativeOutcome(
        out Dictionary<string, object> result)
    {
        lock (_browserPreRouteLock)
        {
            var receipt = _browserPreRouteReceipt;
            if (!(receipt?.Authoritative == true
                    || string.Equals(receipt?.ToolName, "run_workflow", StringComparison.Ordinal)
                    || IsDurableMutationTool(receipt?.ToolName))
                || (!_browserPreRouteOwnsCurrentTurn && receipt?.IsFresh(DateTime.UtcNow) != true)
                || receipt.Result == null)
            {
                result = null;
                return false;
            }
            if (!receipt.TryAcknowledgeOnce())
            {
                result = SuppressedPreRouteResult(receipt);
                return true;
            }
            result = new Dictionary<string, object>(receipt.Result)
            {
                ["routing_note"] = receipt.ToolName == "selected_text_translation"
                    ? "Use the actual operation state in this receipt. Started is not completed or copied; do not run an alternative transformation or clipboard action."
                    : string.Equals(receipt.ToolName, "run_workflow", StringComparison.Ordinal)
                    ? "The exact durable workflow trigger was resolved and executed locally; no alternate planner action was run."
                    : IsDurableMutationTool(receipt.ToolName)
                        ? "The explicit durable change was persisted and read back locally; no alternate planner action was run."
                        : "The authoritative local capability completed this turn; no alternate planner action was run.",
            };
            return true;
        }
    }

    private static Dictionary<string, object> SuppressedPreRouteResult(BrowserPreRouteReceipt receipt)
    {
        // The action has one execution owner, but every provider retry still
        // needs the verified fact. An empty response made a successful local
        // action look unknown and could send Live back into the same tool.
        var result = receipt?.Result == null
            ? new Dictionary<string, object>()
            : new Dictionary<string, object>(receipt.Result);
        result["ok"] = receipt?.Ok == true;
        result.TryAdd("supported", true);
        result["duplicate_suppressed"] = true;
        result["message"] = !string.IsNullOrWhiteSpace(receipt?.Message)
            ? receipt.Message
            : receipt?.Ok == true
                ? "The earlier local action succeeded; no new action ran."
                : "The earlier local action did not succeed; no new action ran.";
        if (receipt?.Started == true)
        {
            result["started"] = true;
            result["task_id"] = receipt.TaskId;
            result["lifecycle_note"] = "This is the earlier start receipt, not current task progress; use browser_automation_status for current state.";
        }
        return result;
    }

    private static bool IsSameResolvedOpenTurn(string previous, string current)
    {
        if (IsSameJarvisUserTurn(previous, current)) return true;
        // The same accumulated input can gain list punctuation after its first
        // action. Preserve only this effect ledger; selection/context ownership
        // continues to use its existing turn comparison unchanged.
        var prefix = (previous ?? "").Trim().TrimEnd('.', '!', '?');
        var accumulated = (current ?? "").Trim();
        return prefix.Length > 0 && accumulated.Length > prefix.Length
            && accumulated.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && (char.IsWhiteSpace(accumulated[prefix.Length])
                || accumulated[prefix.Length] is ',' or ';');
    }

    private bool TryGetResolvedOpenReceipt(string identity, out Dictionary<string, object> result)
    {
        lock (_browserPreRouteLock)
        {
            if (_settings.Current.JarvisPermissionMode == JarvisPermissionMode.AutoExecute
                && identity != null && _resolvedOpenReceipts.TryGetValue(identity, out var existing))
            {
                result = new(existing) { ["duplicate_suppressed"] = true };
                return true;
            }
        }
        result = null;
        return false;
    }

    private void RememberResolvedOpenReceipt(string identity, Dictionary<string, object> result)
    {
        if (identity == null || result == null
            || _settings.Current.JarvisPermissionMode != JarvisPermissionMode.AutoExecute
            || result.TryGetValue("confirmation_required", out var confirmation) && confirmation is true) return;
        lock (_browserPreRouteLock)
            _resolvedOpenReceipts[identity] = new(result);
    }

    private async Task<Dictionary<string, object>> ExecuteSavedOpenSetAsync(
        JarvisAutomationService.SavedOpenPlan plan, CancellationToken token)
    {
        var receipts = new List<object>();
        var targets = new List<object>();
        var successful = 0;
        var executed = 0;
        foreach (var action in plan.Actions)
        {
            token.ThrowIfCancellationRequested();
            if (!TryGetResolvedOpenReceipt(action.CanonicalKey, out var result))
            {
                executed++;
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(action.ToolName == "run_workflow" ? 20 : 12));
                    result = await _automation.ExecuteAsync(action.ToolName, action.Arguments, deadline.Token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    AppLog.Warning(ex, "Saved open set action failed");
                    result = ToolResult(false, ex is OperationCanceledException
                        ? "The requested destination did not finish in time. Check the browser before retrying."
                        : "The requested destination could not be verified.");
                }
                token.ThrowIfCancellationRequested();
                RememberResolvedOpenReceipt(action.CanonicalKey, result);
            }
            var ok = ToolSucceeded(result);
            if (ok) successful++;
            if (result.TryGetValue("referential_target", out var target)) targets.Add(target);
            if (result.TryGetValue("referential_targets", out var group) && group is IEnumerable<object> members)
                targets.AddRange(members);
            receipts.Add(new { requested_name = action.RequestedName, tool = action.ToolName, ok, result });
        }
        token.ThrowIfCancellationRequested();
        var complete = successful == plan.Actions.Count && plan.UnresolvedNames.Count == 0;
        var message = complete ? "Opened the requested destinations."
            : $"Completed {successful} requested action(s)."
                + (successful < plan.Actions.Count ? $" {plan.Actions.Count - successful} could not be verified." : "")
                + (plan.UnresolvedNames.Count > 0
                    ? $" Which destination do you mean by {string.Join(", ", plan.UnresolvedNames)}?" : "");
        var combined = ToolResult(complete, message, needsClarification: plan.UnresolvedNames.Count > 0);
        combined["requested_count"] = plan.RequestedCount;
        combined["resolved_count"] = plan.Actions.Count;
        combined["successful_count"] = successful;
        combined["executed_count"] = executed;
        combined["unresolved_names"] = plan.UnresolvedNames.ToArray();
        combined["action_results"] = receipts.ToArray();
        combined["referential_targets"] = targets.ToArray();
        if (executed == 0) combined["duplicate_suppressed"] = true;
        return combined;
    }

    private void RecordAuthoritativePreRoute(
        string userTurn,
        string toolName,
        Dictionary<string, object> result)
    {
        var ok = ToolSucceeded(result);
        var message = result?.TryGetValue("message", out var messageValue) == true
            ? messageValue?.ToString() ?? ""
            : "";
        lock (_browserPreRouteLock)
        {
            _browserPreRouteReceipt = new BrowserPreRouteReceipt
            {
                UserTurn = userTurn,
                ToolName = toolName,
                Ok = ok,
                Message = message,
                Result = result == null
                    ? ToolResult(false, "The durable change did not return a result.")
                    : new Dictionary<string, object>(result),
                Authoritative = true,
            };
            _browserPreRouteOwnsCurrentTurn = true;
        }
    }

    internal static bool IsDurableMutationTool(string toolName) =>
        (toolName ?? "").Trim() is
            "remember_routine"
            or "edit_workflow"
            or "remember_app_alias"
            or "remember_path_alias"
            or "remember_link_alias"
            or "forget_learned_mapping"
            or "set_browser_preference";

    private static bool ToolSucceeded(Dictionary<string, object> result) =>
        result?.TryGetValue("ok", out var value) == true
        && value is bool ok
        && ok;

    private Dictionary<string, object> BrowserAutomationStatus()
    {
        if (_browserAutomation == null)
            return ToolResult(false, "Browser Automation is unavailable.", supported: false);

        var terminalSnapshots = _browserAutomation.RecentTerminalOutcomes();
        var status = _browserAutomation.Snapshot();
        // The coordinator retains its last terminal snapshot for the UI, but
        // session shutdown clears the conversation's terminal outcomes. That
        // old task must not become the next conversation's current task.
        if (status?.State is BrowserTaskState.Completed or BrowserTaskState.CompletedWithNote
            or BrowserTaskState.Partial or BrowserTaskState.Blocked
            or BrowserTaskState.Cancelled or BrowserTaskState.Failed
            && !terminalSnapshots.Any(outcome => string.Equals(
                outcome.TaskId, status.TaskId, StringComparison.Ordinal)))
            status = new BrowserAutomationSnapshot();
        var recentOutcomes = terminalSnapshots
            .Select(outcome => new Dictionary<string, object>
            {
                ["task_id"] = outcome.TaskId,
                ["state"] = NormalizeBrowserTaskState(outcome),
                ["detail_state"] = outcome.State.ToString(),
                ["request"] = BrowserAutomationText.Truncate(outcome.Request, 500),
                ["result"] = BrowserAutomationText.Truncate(outcome.ResultSummary, 700),
                ["last_error"] = BrowserAutomationText.Truncate(outcome.LastError, 700),
                ["updated_utc"] = outcome.UpdatedUtc,
            })
            .ToArray();
        var helper = _browserAutomation.HelperStatus;
        return new Dictionary<string, object>
        {
            ["ok"] = true,
            ["task_id"] = status.TaskId,
            ["state"] = NormalizeBrowserTaskState(status),
            ["message"] = string.IsNullOrWhiteSpace(status.TaskId)
                ? "No Browser Automation task is active in this conversation. If a page interaction was requested, it has not started; do not poll unchanged status. Start only a user-requested remaining page-interaction subtask or report it incomplete."
                : "This is the current Browser Automation task state.",
            ["detail_state"] = status.State.ToString(),
            ["is_active"] = status.CanCancel,
            ["current_action"] = status.CurrentAction,
            ["current_domain"] = status.CurrentDomain,
            ["current_tab_title"] = status.CurrentTabTitle,
            ["elapsed_seconds"] = status.StartedUtc == default
                ? 0
                : Math.Max(0, (int)(DateTime.UtcNow - status.StartedUtc).TotalSeconds),
            ["can_pause"] = status.CanPause,
            ["can_resume"] = status.CanResume,
            ["can_cancel"] = status.CanCancel,
            ["vision_recovery"] = status.VisionRecovery,
            ["intervention_kind"] = status.InterventionKind,
            ["intervention_reason"] = status.InterventionReason,
            ["intervention_explanation"] = status.Intervention?.Explanation ?? "",
            ["intervention_required"] = status.Intervention?.Required ?? false,
            ["intervention_can_skip"] = status.Intervention?.CanSkip ?? false,
            ["result"] = status.ResultSummary,
            ["last_error"] = status.LastError,
            ["recent_outcomes"] = recentOutcomes,
            ["helper_connected"] = helper.CommandChannelReady,
        };
    }

    internal static string NormalizeBrowserTaskState(BrowserAutomationSnapshot snapshot)
    {
        if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.TaskId))
            return "idle";

        return snapshot.State switch
        {
            BrowserTaskState.Queued => "starting",
            BrowserTaskState.Paused or BrowserTaskState.NeedsUserAction => "paused",
            BrowserTaskState.Completed or BrowserTaskState.CompletedWithNote => "completed",
            BrowserTaskState.Cancelled => "cancelled",
            BrowserTaskState.Partial or BrowserTaskState.Blocked or BrowserTaskState.Failed => "failed",
            _ => "running",
        };
    }

    private async Task<Dictionary<string, object>> ControlBrowserAutomationAsync(JsonElement args)
    {
        if (_browserAutomation == null)
            return ToolResult(false, "Browser Automation is unavailable.", supported: false);

        var action = (GetString(args, "action") ?? "").Trim().ToLowerInvariant();
        var latestUserTurn = RecentJarvisUserTurn();
        if (!IsModelBrowserTaskControlAuthorized(action, latestUserTurn))
        {
            // The immediate user-owned cancel can finish before Live proposes
            // its tool call. A subsequent user turn may already have replaced
            // the short-lived transcript receipt. Report the coordinator's
            // recent terminal fact without granting a model-only cancel any
            // authority over an active or future task.
            var current = _browserAutomation.Snapshot();
            var terminal = current?.State == BrowserTaskState.Cancelled
                ? _browserAutomation.RecentTerminalOutcomes().FirstOrDefault(
                    outcome => string.Equals(outcome.TaskId, current.TaskId, StringComparison.Ordinal))
                : null;
            if (CanReportAlreadyCancelled(action, terminal, DateTime.UtcNow))
            {
                return new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["already_cancelled"] = true,
                    ["action_applied"] = false,
                    ["message"] = "The browser task is already cancelled. No new action was taken.",
                    ["status"] = new Dictionary<string, object>
                    {
                        ["task_id"] = terminal.TaskId,
                        ["state"] = "cancelled",
                        ["is_active"] = false,
                    },
                };
            }
            AppLog.Info(
                $"Browser task model control rejected because the latest user turn did not authorize it: action={action} " +
                $"request={BrowserAutomationText.Truncate(latestUserTurn, 120)}");
            return new Dictionary<string, object>
            {
                ["ok"] = false,
                ["message"] = "The latest user turn did not request that browser-task control.",
                ["status"] = BrowserAutomationStatus(),
            };
        }

        var ok = action switch
        {
            "pause" => _browserAutomation.Pause(),
            "cancel" => await _browserAutomation.CancelAsync(announceOutcome: false),
            // The user's actual turn is authoritative. A model-generated
            // paraphrase must never turn unrelated conversation into guidance
            // or bypass the coordinator's paused-task relevance checks.
            "resume" or "guide" => AddGuidance(latestUserTurn),
            _ => false,
        };
        return new Dictionary<string, object>
        {
            ["ok"] = ok,
            ["message"] = ok
                ? $"Browser automation {action} accepted."
                : "No active browser task accepted that control command.",
            ["status"] = BrowserAutomationStatus(),
        };
    }

    internal static bool CanReportAlreadyCancelled(
        string action,
        BrowserAutomationSnapshot terminal,
        DateTime utcNow) =>
        action == "cancel"
        && terminal?.State == BrowserTaskState.Cancelled
        && !terminal.CanCancel
        && !string.IsNullOrWhiteSpace(terminal.TaskId)
        && utcNow >= terminal.UpdatedUtc
        && utcNow - terminal.UpdatedUtc <= TimeSpan.FromSeconds(12);

    private static Dictionary<string, object> BrowserContextCaptureRequired(
        Dictionary<string, object> capture = null)
    {
        var message = capture != null
            && capture.TryGetValue("message", out var value)
            && !string.IsNullOrWhiteSpace(value?.ToString())
                ? value.ToString()
                : "I couldn't read the selected context for this browser task. Reselect it and try once more.";
        return new Dictionary<string, object>
        {
            ["ok"] = false,
            ["started"] = false,
            ["state"] = "idle",
            ["supported"] = true,
            ["needs_clarification"] = true,
            ["message"] = message,
            ["routing_reason"] = "The requested user-provided context was not available, so no browser task was started.",
        };
    }

    internal static bool CapturedContextIsPartial(Dictionary<string, object> capture, string retained) =>
        (capture.TryGetValue("truncated", out var truncated) && truncated is true)
        || (capture.TryGetValue("text", out var text) && (text?.ToString()?.Trim().Length ?? 0) > retained.Length);

    internal static bool TryGetCapturedContextText(
        Dictionary<string, object> result,
        out string text)
    {
        text = "";
        if (result == null
            || !result.TryGetValue("ok", out var ok)
            || ok is not true
            || !result.TryGetValue("text", out var value)
            || string.IsNullOrWhiteSpace(value?.ToString()))
        {
            return false;
        }

        var maximum = result.TryGetValue("source", out var source)
            && string.Equals(source?.ToString(), "clipboard_text", StringComparison.Ordinal)
                ? JarvisAutomationService.ClipboardContextCharacterLimit
                : 6000;
        text = BrowserAutomationText.Truncate(value.ToString().Trim(), maximum);
        return text.Length > 0;
    }

    internal static bool IsModelBrowserTaskControlAuthorized(
        string action,
        string latestUserTurn)
    {
        var normalizedAction = (action ?? "").Trim().ToLowerInvariant();
        if (normalizedAction is "pause" or "cancel")
        {
            return TryParseExplicitBrowserTaskStopOrPause(
                    latestUserTurn,
                    out var requestedAction,
                    out var explicitlyTargetsBrowserTask)
                && explicitlyTargetsBrowserTask
                && string.Equals(
                    requestedAction,
                    normalizedAction,
                    StringComparison.Ordinal);
        }

        // Resume and guidance remain subject to the coordinator's existing
        // relevance/intervention policy, but only a real current user turn may
        // be evaluated there. Unknown actions are never authorized.
        return normalizedAction is "resume" or "guide"
            && !string.IsNullOrWhiteSpace(latestUserTurn);
    }

    private bool AddGuidance(string guidance)
    {
        if (string.IsNullOrWhiteSpace(guidance))
            return false;
        var accepted = _browserAutomation.TryAddTaskGuidance(guidance, out var reason);
        if (!accepted)
            AppLog.Info($"Browser task guidance rejected: {reason}");
        return accepted;
    }

    private void ResetActionCancellation()
    {
        _actionCts?.Cancel();
        _actionCts?.Dispose();
        _actionCts = new CancellationTokenSource();
    }

    private static Dictionary<string, object> ToolResult(
        bool ok,
        string message,
        bool supported = true,
        bool needsClarification = false,
        string detail = null) => new()
        {
            ["ok"] = ok,
            ["supported"] = supported,
            ["needs_clarification"] = needsClarification,
            ["message"] = message ?? "",
            ["detail"] = detail ?? "",
        };

    internal static Dictionary<string, object> ExactTabSelectionToolResult(
        BrowserActionReceipt receipt,
        string selection,
        string description)
    {
        var verified = receipt.Ok && receipt.WindowId is > 0
            && receipt.TabId is > 0 && receipt.TabCount > 0;
        var result = ToolResult(
            verified,
            verified
                ? receipt.NoVisibleChange
                    ? $"{description} was already active in the selected Chrome window."
                    : $"Activated {description} in the selected Chrome window."
                : string.IsNullOrWhiteSpace(receipt.Error)
                    ? "Chrome did not confirm the requested tab and its exact ID."
                    : receipt.Error);
        result["tab_selection"] = selection;
        if (receipt.WindowId is int windowId)
            result["window_id"] = windowId;
        if (receipt.TabId is int tabId)
            result["tab_id"] = tabId;
        if (receipt.TabCount > 0)
            result["tab_count"] = receipt.TabCount;
        result["already_active"] = verified && receipt.NoVisibleChange;
        return result;
    }

    internal static bool TryGetStructuredTabSelection(
        JsonElement args,
        out string selection,
        out string description)
    {
        selection = "";
        description = "";
        if (args.ValueKind != JsonValueKind.Object
            || !string.Equals(GetString(args, "action"), "select tab", StringComparison.OrdinalIgnoreCase))
            return false;
        var hasIndex = args.TryGetProperty("tab_index", out var index);
        var hasFromEnd = args.TryGetProperty("tab_from_end", out var fromEnd);
        if (hasIndex == hasFromEnd)
            return false;
        var value = hasIndex ? index : fromEnd;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var position) || position < 1)
            return false;
        selection = hasIndex ? $"index:{position}" : $"from_end:{position}";
        description = hasIndex ? $"tab {position}" : position == 1 ? "the last tab" : $"tab {position} from the right";
        return true;
    }

    private static bool IsSafeHttpUrl(string url, out Uri uri)
    {
        uri = null;
        return !string.IsNullOrWhiteSpace(url)
            && Uri.TryCreate(url.Trim(), UriKind.Absolute, out uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private static string GetString(JsonElement args, string property)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(property, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()?.Trim(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToString(),
            _ => null,
        };
    }

    private static bool GetBoolean(JsonElement args, string property) =>
        args.ValueKind == JsonValueKind.Object
        && args.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.True;

    private static string NormalizeAppName(string appName)
    {
        var text = (appName ?? "").Trim().ToLowerInvariant();
        return text switch
        {
            "calc" or "calculator" => "calculator",
            "mspaint" or "paint" => "paint",
            "explorer" or "file explorer" or "files" or "file manager" => "file explorer",
            "keyboard settings" or "keyboard.wtf settings" or "settings" => "settings",
            "gmail" or "google mail" => "gmail",
            "whatsapp" or "whatsapp web" => "whatsapp",
            "notepad" => "notepad",
            _ => text,
        };
    }

    private static string NormalizePhoneNumber(string phoneNumber)
    {
        var raw = (phoneNumber ?? "").Trim();
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        if (digits.Length < 7)
            return "";
        return raw.StartsWith("+", StringComparison.Ordinal) ? "+" + digits : digits;
    }

    private static string HumanizeTool(string toolName) => toolName switch
    {
        "open_app" => "Opening app",
        "open_url" => "Opening website",
        "open_folder" => "Opening folder",
        "open_path" => "Opening file or folder",
        "window_action" => "Controlling window",
        "browser_action" => "Controlling browser",
        "browser_automation" => "Starting browser automation",
        "browser_automation_status" => "Checking browser automation",
        "browser_automation_control" => "Controlling browser automation",
        "web_search" => "Searching the web",
        "get_desktop_context" => "Reading desktop context",
        "get_clipboard_text" => "Reading clipboard",
        "get_selected_text" => "Reading selected text",
        "open_keyboard_wtf_settings" => "Opening keyboard.wtf Settings",
        "replace_selected_text" => "Replacing selected text",
        "type_text" => "Typing text",
        "press_key" => "Pressing key",
        "search_files" => "Searching files",
        "save_note" => "Saving note",
        "add_todo" => "Adding to-do",
        "list_todos" => "Reading to-do list",
        "complete_todo" => "Completing to-do",
        "set_timer" => "Setting timer",
        "cancel_timer" => "Cancelling timer",
        "list_timers" => "Checking timers",
        "system_control" => "Changing system setting",
        "system_status" => "Checking system status",
        "play_media" => "Opening media",
        "open_service_page" => "Opening service page",
        "open_camera" => "Opening camera",
        "take_photo" => "Opening camera for photo",
        "take_screenshot" => "Taking screenshot",
        "inspect_screen" => "Inspecting screen",
        "virtual_desktop_action" => "Controlling virtual desktop",
        "create_workflow" => "Saving workflow",
        "edit_workflow" => "Updating workflow",
        "list_workflows" => "Reading workflows",
        "run_workflow" => "Running workflow",
        "delete_workflow" => "Deleting workflow",
        "remember_app_alias" => "Remembering app mapping",
        "remember_path_alias" => "Remembering local path",
        "remember_link_alias" => "Remembering link",
        "remember_workflow_alias" => "Remembering workflow name",
        "list_learned_mappings" => "Reading learned mappings",
        "forget_learned_mapping" => "Forgetting learned mapping",
        "set_browser_preference" => "Saving browser preference",
        "request_sensitive_action" => "Requesting confirmation",
        "confirm_sensitive_action" => "Confirming action",
        "cancel_sensitive_action" => "Cancelling action",
        "open_gmail_draft" => "Preparing Gmail draft",
        "prepare_whatsapp_message" => "Preparing WhatsApp message",
        "copy_text" => "Copying text",
        _ => "Running safe action",
    };

    private void LogJarvisResult(string action, Dictionary<string, object> result)
    {
        if (_history == null)
            return;
        var success = result.TryGetValue("ok", out var ok) && ok is bool value && value;
        var message = result.TryGetValue("message", out var detail) ? detail?.ToString() : "";
        var confirmation = result.TryGetValue("confirmation_required", out var pending)
            && pending is bool required && required;
        _history.Add(action, message, success, confirmation);
    }

    private static string DescribePlan(SmartActionPlan plan) => plan.Action switch
    {
        "gmail_draft" => $"Preparing a Gmail draft for {plan.Recipient}",
        "open_notepad" => "Opening Notepad",
        "open_settings" => "Opening keyboard.wtf settings",
        "open_url" => "Opening a safe web address",
        "copy" => "Copying the prepared text",
        "ask" => "A detail is missing",
        _ => "Preparing text for the active app",
    };

    private static string Shorten(string text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "Ready";
        var singleLine = text.ReplaceLineEndings(" ").Trim();
        return singleLine.Length <= maxLength ? singleLine : singleLine[..(maxLength - 1)] + "...";
    }

    public void SaveCurrentNote()
    {
        var text = KeyboardWtfState.CurrentTranscript;
        if (string.IsNullOrWhiteSpace(text))
        {
            _notifications.Warning("No transcript", "Nothing to save.");
            return;
        }

        Directory.CreateDirectory(KeyboardWtfState.EffectiveVoiceNoteSavePath);
        var pattern = string.IsNullOrWhiteSpace(KeyboardWtfState.VoiceNoteFilenamePattern)
            ? KeyboardWtfState.DefaultVoiceNoteFilenamePattern
            : KeyboardWtfState.VoiceNoteFilenamePattern;
        var name = DateTime.Now.ToString(pattern);
        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '-');
        var path = Path.Combine(KeyboardWtfState.EffectiveVoiceNoteSavePath, name + ".txt");
        File.WriteAllText(path, text);
        _notifications.Info("Voice note saved", path);
    }

    public void ReadBack()
    {
        var text = KeyboardWtfState.CurrentTranscript;
        if (string.IsNullOrWhiteSpace(text))
        {
            _notifications.Warning("No transcript", "Nothing to read back.");
            return;
        }

        TtsService.Instance.Speak(text, KeyboardWtfState.SelectedLanguage, _app?.UpdateWorkAdmission);
        _notifications.Info("Reading back", "Speaking the current transcript.");
    }

    public void ReplayLast()
    {
        var last = KeyboardWtfState.TranscriptHistory.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(last))
        {
            _notifications.Warning("No history", "No previous transcript found.");
            return;
        }

        KeyboardWtfState.CurrentTranscript = last;
        ClipboardHelper.SetText(last);
        _notifications.Info("Replayed", "Last transcript copied to clipboard.");
    }

    public void OpenVoiceNotes()
    {
        Directory.CreateDirectory(KeyboardWtfState.EffectiveVoiceNoteSavePath);
        Process.Start(new ProcessStartInfo
        {
            FileName = KeyboardWtfState.EffectiveVoiceNoteSavePath,
            UseShellExecute = true,
        });
    }
}
