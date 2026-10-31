namespace KeyboardWtf.Services;

using System.Reflection;
using System.Windows.Forms;
using KeyboardWtf.Destinations;
using KeyboardWtf.Models;
using KeyboardWtf.Services.Ai;
using KeyboardWtf.Services.Ai.Backends;
using KeyboardWtf.Services.BrowserAutomationV2;
using KeyboardWtf.Services.PaidStaging;
using KeyboardWtf.Services.Updates;

public sealed class KeyboardWtfApp : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly SettingsService _settings = new();
    private readonly AudioRecorderService _audioRecorder = new();
    private readonly SpeechRecognitionService _speechRecognition = new();
    private readonly DestinationRouter _router = new();
    private readonly HotkeyService _hotkeys = new();
    private readonly IntentMemoryService _intentMemory = new();
    private readonly JarvisActionHistoryService _jarvisHistory = new();
    private readonly LearnedMappingService _learnedMappings = new();
    private readonly BrowserNativeHostInstaller _browserNativeHostInstaller = new();
    private readonly HashSet<string> _reportedHotkeyFailures = new(StringComparer.OrdinalIgnoreCase);

    private NotificationService _notifications;
    private VoiceCaptureService _capture;
    private CommandRegistry _commands;
    private VoiceOverlayForm _overlay;
    private GeminiLiveConversationService _liveConversation;
    private JarvisAutomationService _jarvisAutomation;
    private BrowserHelperBridge _browserHelper;
    private BrowserAutomationPlanner _browserPlanner;
    private BrowserAutomationCoordinator _browserAutomation;
    private PaidAccountService _paidAccount;
    private AiBackendGateway _aiBackend;
    private PersonalGeminiKeyValidator _personalKeyValidator;
    private PersonalApiSettingsController _personalApiSettings;
    private JarvisTimerService _timers;
    private System.Windows.Forms.Timer _startupHotkeyRetryTimer;
    private System.Windows.Forms.Timer _publicUpdateTimer;
    private PublicUpdateNotificationMonitor _publicUpdateNotifications;
    private int _startupHotkeyRetryCount;
    private bool _disposed;
    private int _restartRequested;

    public KeyboardWtfApp(NotifyIcon notifyIcon)
    {
        _notifyIcon = notifyIcon;
    }

    public SettingsService Settings => _settings;
    internal UpdateWorkAdmissionGate UpdateWorkAdmission { get; } = new();
    internal Task TrackFiniteUpdateWorkAsync(Func<Task> work) => UpdateWorkAdmission.TrackAsync(work);
#if KEYBOARD_WTF_PERSONAL_API_CANDIDATE || KEYBOARD_WTF_CONNECTOR_STAGING_CANDIDATE
    // Private Settings audit adapter only; absent from normal release builds.
    internal GeminiLiveConversationService PrivateAuditConversation => _liveConversation;
#endif
    public SpeechRecognitionService SpeechRecognition => _speechRecognition;
    public CommandRegistry Commands => _commands;
    public BrowserAutomationCoordinator BrowserAutomation => _browserAutomation;

    public void Start()
    {
        AppLog.Init();
        AppResources.Init(Assembly.GetExecutingAssembly());

        _notifications = new NotificationService(_notifyIcon);
        _settings.Load();
        _intentMemory.Load();
        _jarvisHistory.Load();
        _learnedMappings.Load();
        _paidAccount = new PaidAccountService(updateWorkAdmission: UpdateWorkAdmission);
        PersonalApiSettingsController personalApiSettings = null;
#if QLOO_JARVIS
        var vault = new PersonalApiVault(
            Path.Combine(SettingsService.AppDataDir, "personal-api-v1.dat"),
            defaultPolicy: AiBackendPolicy.PersonalOnly);
        var vaultState = vault.GetView();
        if (vaultState.Policy != AiBackendPolicy.PersonalOnly)
            vaultState = vault.SetPolicy(vaultState.Revision, AiBackendPolicy.PersonalOnly);
        _aiBackend = new(vault, AuthorizeQlooPersonalApiAsync);
        _paidAccount.AttachAiBackend(_aiBackend);
        _personalKeyValidator = new();
        personalApiSettings = new(vault, _personalKeyValidator,
            new NativePersonalKeyEntryDialog(), forcePersonalOnly: true);
        _personalApiSettings = personalApiSettings;
#else
        if (_paidAccount.Enabled)
        {
            var vault = new PersonalApiVault(Path.Combine(SettingsService.AppDataDir, "personal-api-v1.dat"));
            _aiBackend = new(vault, _paidAccount.AuthorizePersonalAsync);
            _paidAccount.AttachAiBackend(_aiBackend);
            _personalKeyValidator = new();
            personalApiSettings = new(vault, _personalKeyValidator, new NativePersonalKeyEntryDialog());
        }
#endif
        if (!_paidAccount.PreserveExistingRegistrations)
        {
            SyncStartupRegistration();
            RegisterBrowserNativeHost();
        }
        else
        {
            AppLog.Info("Isolated paid candidate: startup and native-host registrations were left unchanged");
        }
        AiProviderRegistry.Initialize();
        RegisterDestinations();

        _audioRecorder.AttachUpdateWorkAdmission(UpdateWorkAdmission);
        _capture = new VoiceCaptureService(_audioRecorder, _speechRecognition, _notifications, _router, UpdateWorkAdmission);
        _liveConversation = new GeminiLiveConversationService(
            _notifications,
            _settings,
            _intentMemory,
            _paidAccount,
            _learnedMappings,
            UpdateWorkAdmission);
        _browserHelper = new BrowserHelperBridge();
        _browserHelper.Start();
        _browserPlanner = new BrowserAutomationPlanner(_paidAccount);
        _browserAutomation = new BrowserAutomationCoordinator(
            _settings,
            _notifications,
            _browserHelper,
            _browserPlanner,
            _paidAccount,
            UpdateWorkAdmission);
        _timers = new JarvisTimerService(_notifications, _jarvisHistory, UpdateWorkAdmission);
        _browserAutomation.AnnouncementRequested += HandleLifecycleAnnouncement;
        _timers.AnnouncementRequested += HandleLifecycleAnnouncement;
        _jarvisAutomation = new JarvisAutomationService(
            _notifications,
            _settings,
            _jarvisHistory,
            _learnedMappings,
            () => OpenSettings(),
            _timers,
            _paidAccount,
            _browserHelper);
        _commands = new CommandRegistry(
            this,
            _capture,
            _router,
            _settings,
            _notifications,
            _liveConversation,
            _jarvisAutomation,
            _jarvisHistory,
            _browserAutomation);
        _jarvisAutomation.Connectors = new Connectors.JarvisConnectorController(
            _paidAccount.ConnectorRequestAsync,
            () => _paidAccount.ConnectorSessionOwner,
            () => _liveConversation.SessionCancellation,
            () => _commands.ConnectorUserRequest,
            (announcement, token) => _liveConversation.TryAnnounceLifecycleAsync(announcement, token),
            UpdateWorkAdmission,
            notify: (title, detail) => _notifications.Info(title, detail),
            userRequestIdentity: () => _commands.ConnectorUserRequestIdentity,
            logicalLiveSessionId: () => _liveConversation.ConnectorLogicalLiveSessionId,
            interpretCount: _liveConversation.InterpretConnectorMailCountAsync,
            reasonRead: _liveConversation.ReasonConnectorReadAsync,
            userRequestSnapshot: () => _commands.ConnectorUserRequestSnapshot,
            preferDirectReadReceipt: true);
        _paidAccount.ConnectorAccountInvalidated += _jarvisAutomation.Connectors.InvalidateAccount;
        _liveConversation.SetToolExecutor(_commands.ExecuteJarvisToolAsync);
        _liveConversation.SetUserTranscriptObserver(
            _commands.ObserveJarvisTranscript,
            _commands.ObserveImmediateJarvisControl,
            _commands.CompleteJarvisUserTurn);
        _liveConversation.SetSessionEndingObserver(_commands.HandleJarvisSessionEndingAsync);
        _liveConversation.SetConnectorFinalInputObserver(_commands.ReserveFinalConnectorRead);
        _liveConversation.SetConnectorReplyOwnership(
            () => _jarvisAutomation.Connectors.HoldsPendingReply(_commands.ConnectorUserRequestSnapshot),
            answer => _jarvisAutomation.Connectors.ObserveCompletedReply(answer, _commands.ConnectorUserRequestIdentity));
        _overlay = new VoiceOverlayForm(_audioRecorder, GetPopupQuickAction,
            ExecutePopupQuickActionAsync, () => _settings.Current.ShowQuickActionButton);
        _overlay.InitializeUiThreadHandle();

        WebSettingsService.Instance.Initialize(
            this,
            _settings,
            _hotkeys,
            _intentMemory,
            _jarvisHistory,
            _learnedMappings,
            _jarvisAutomation,
            _browserAutomation,
            _paidAccount,
            personalApiSettings);
        WebSettingsService.Instance.Start();
        if (_paidAccount.Enabled)
            _ = TrackFiniteUpdateWorkAsync(InitializePaidAccountAsync);

        _hotkeys.RegistrationFailed += (name, reason) =>
        {
            if (MarkHotkeyFailureNotified(_reportedHotkeyFailures, name, reason))
                _notifications.Warning("Hotkey unavailable", $"{name}: {reason}");
            else
                AppLog.Info($"Hotkey remains unavailable during bounded startup retry: {name}");
        };
        _hotkeys.RegistrationFallbackUsed += (name, requested, fallback) =>
            _notifications.Warning(
                "Hotkey conflict handled",
                $"{name}: {requested} is in use, so {QlooProduct.Name} is using {fallback}. Change it in Settings if preferred.");
        if (!RegisterHotkeys()
            && !HotkeyService.HasDuplicates(_settings.Current.Hotkeys, out _))
            ScheduleStartupHotkeyRetry();
#if !QLOO_JARVIS
        _ = TrackFiniteUpdateWorkAsync(LoadModelsAsync);
#endif
        StartPiperInstallInBackground();
        OpenFirstRunSetup();

        _notifications.Info(QlooProduct.Name, "Jarvis is ready.");
    }

    private void StartPublicUpdateNotifications()
    {
        // Candidate/staging builds create no periodic client, token, UI timer
        // or profile work. Existing feature startup finishes before this call.
        if (!PublicUpdateEligibility.IsAllowed() || _disposed || _publicUpdateNotifications is not null) return;
        try
        {
            _publicUpdateNotifications = new(UpdateWorkAdmission,
                PublicUpdateEligibility.IsAllowed,
                () => _settings.Current.UpdateChannel,
                CheckPublicUpdateAsync,
                callback => _overlay.BeginInvoke(callback),
                update =>
                {
                    _notifications.Attention("Update available",
                        $"keyboard.wtf {update.Version} ({update.Channel}) is available. Open Settings to review it.");
                    return true; // A guarded attempt, not proof Windows displayed it.
                });
            _publicUpdateTimer = new() { Interval = 60_000 };
            _publicUpdateTimer.Tick += (_, _) =>
            {
                if (_disposed) return;
                _publicUpdateTimer.Interval = 86_400_000; // Daily; no hidden retry loop.
                _ = _publicUpdateNotifications.CheckOnceAsync(); // Retains the same held run on overlap.
            };
            _publicUpdateTimer.Start();
        }
        catch
        {
            StopPublicUpdateNotifications();
            AppLog.Info("Public update notification setup was unavailable; manual checking remains available.");
        }
    }

    private static Task<VerifiedUpdate> CheckPublicUpdateAsync(string channel, CancellationToken token) =>
        Task.Run(async () =>
        {
            // Existing CheckAsync awaits may capture their caller. Give this
            // periodic worker NO UI context, and own its independent client
            // through ORIGINAL completion/disposal. Never borrow manual consent.
            using var checker = new SecureUpdateService();
            try { return await checker.CheckAsync(channel, token).ConfigureAwait(false); }
            catch
            {
                if (!token.IsCancellationRequested)
                    AppLog.Info("Public update metadata check failed; no download or installation was attempted.");
                throw;
            }
        }, token);

    private void StopPublicUpdateNotifications()
    {
        _publicUpdateTimer?.Stop();
        _publicUpdateTimer?.Dispose();
        // Pending callbacks become inert; running/check workers retain original
        // ownership and token resources. Never synchronously join this UI.
        _publicUpdateNotifications?.Dispose();
    }

    public bool RegisterHotkeys()
    {
        if (HotkeyService.HasDuplicates(_settings.Current.Hotkeys, out var duplicate))
        {
            _notifications.Warning("Duplicate hotkey", $"{duplicate} is assigned more than once. Reset or change it in settings.");
            return false;
        }

        return _hotkeys.RegisterDefaults(_settings.Current.Hotkeys, _commands);
    }

    public bool TryUpdateHotkeys(HotkeySettings requested, out string error)
    {
        if (!_hotkeys.TryReplaceAll(
                requested,
                _settings.Current.Hotkeys,
                _commands,
                requireAllRequested: false,
                out error))
            return false;
        _settings.SaveHotkeys(requested);
        return true;
    }

    public bool TryResetHotkeys(out string error)
    {
        var defaults = HotkeySettings.Defaults();
        if (!_hotkeys.TryReplaceAll(
                defaults,
                _settings.Current.Hotkeys,
                _commands,
                requireAllRequested: true,
                out error))
            return false;
        _settings.SaveHotkeys(defaults);
        return true;
    }

    private void ScheduleStartupHotkeyRetry()
    {
        _startupHotkeyRetryTimer?.Dispose();
        _startupHotkeyRetryCount = 0;
        _startupHotkeyRetryTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        _startupHotkeyRetryTimer.Tick += (_, _) =>
        {
            _startupHotkeyRetryCount++;
            AppLog.Info($"Retrying hotkey registration after startup ({_startupHotkeyRetryCount}/3)");
            if (RegisterHotkeys() || _startupHotkeyRetryCount >= 3)
            {
                _startupHotkeyRetryTimer.Stop();
                _startupHotkeyRetryTimer.Dispose();
                _startupHotkeyRetryTimer = null;
            }
        };
        _startupHotkeyRetryTimer.Start();
    }

    internal static bool MarkHotkeyFailureNotified(
        ISet<string> reportedFailures,
        string name,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(reportedFailures);
        return reportedFailures.Add($"{name ?? ""}\u001f{reason ?? ""}");
    }

    public void OpenSettings(string section = "") =>
        WebSettingsService.Instance.OpenInBrowser(SettingsSectionRegistry.Resolve(section));

    private async Task InitializePaidAccountAsync()
    {
        try
        {
            await _paidAccount.InitializeAsync();
            // The earlier constructor-time warmup runs before the persisted
            // paid session has loaded and therefore normally has nothing to
            // authenticate with. Warm only after initialization; this is a
            // read-only backend readiness request, never a provider/Live
            // session and never a credit reservation.
            await _paidAccount.WarmBackendAsync();
        }
        catch (Exception exception)
        {
            AppLog.Warning(exception, "Paid account initialization failed");
        }
    }

    public async Task SendJarvisTextTurnAsync(string text, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("A Jarvis command is required.", nameof(text));
        if (_liveConversation == null)
            throw new InvalidOperationException("Jarvis is not initialized.");

        if (!_liveConversation.IsActive)
            await _liveConversation.StartAsync();
        if (!_liveConversation.IsActive)
            throw new InvalidOperationException("Jarvis could not start. Check the Gemini key, microphone, and app logs.");

        // A text turn is already an authoritative final user utterance. Keep
        // conversation termination ahead of direct actions, selected-context
        // routing, browser guidance, and model interpretation just as the
        // recognized-speech path does.
        if (_liveConversation.TryEndFromAuthoritativeUserText(text))
            return;

        // Ordinary actions belong to tool execution so the model receives
        // their actual result. Only explicit cancellation is immediate.
        using var turnCancellation = CancellationTokenSource.CreateLinkedTokenSource(token, _liveConversation.SessionCancellation);
        _commands?.ObserveSubmittedJarvisText(text, turnCancellation.Token);

        await _liveConversation.SendTextTurnAsync(
            BrowserAutomationText.Truncate(text.Trim(), 4000),
            turnCancellation.Token);
    }

    public string StartupExecutablePath => ResolveStartupExecutablePath();

    public bool RequestRestart(out string error)
    {
        error = "";
        if (Interlocked.Exchange(ref _restartRequested, 1) != 0)
            return true;

        _commands?.CancelCurrent();
        if (!WindowsAppLifecycleService.StartRestartHelper(out error))
        {
            Interlocked.Exchange(ref _restartRequested, 0);
            return false;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(350);
            Application.Exit();
        });
        return true;
    }

    private PopupQuickAction GetPopupQuickAction()
    {
        if (_capture?.SmartWritingCompletionOperationId is int operationId)
            return new(PopupQuickActionKind.Done, "smart-writing", operationId);
        if (_liveConversation?.GetMicrophoneActionState() is not { } microphone)
            return PopupQuickAction.Hidden;
        return new(microphone.Muted ? PopupQuickActionKind.Unmute : PopupQuickActionKind.Mute,
            microphone.SessionId, microphone.Revision);
    }

    private Task<bool> ExecutePopupQuickActionAsync(PopupQuickAction action)
    {
        if (!_settings.Current.ShowQuickActionButton || action is null)
            return Task.FromResult(false);
        return action.Kind switch
        {
            PopupQuickActionKind.Done when action.Owner == "smart-writing"
                && action.Revision is >= int.MinValue and <= int.MaxValue
                => _capture?.TryCompleteSmartWritingAsync((int)action.Revision) ?? Task.FromResult(false),
            PopupQuickActionKind.Mute or PopupQuickActionKind.Unmute
                => _liveConversation?.TrySetMicrophoneMutedAsync(action.Owner, action.Revision,
                    action.Kind == PopupQuickActionKind.Mute) ?? Task.FromResult(false),
            _ => Task.FromResult(false),
        };
    }

    public void RequestExitForUpdate()
    {
        _commands?.CancelCurrent();
        _ = Task.Run(async () =>
        {
            await Task.Delay(350);
            Application.Exit();
        });
    }

    private void RegisterBrowserNativeHost()
    {
        try
        {
            var executable = Environment.ProcessPath ?? Application.ExecutablePath;
            _browserNativeHostInstaller.Register(executable);
            AppLog.Info("Chrome Helper native host registration is ready");
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "Chrome Helper native host registration failed");
        }
    }

    private void HandleLifecycleAnnouncement(JarvisLifecycleAnnouncement announcement)
    {
        if (_disposed || announcement == null)
            return;
        _ = TrackFiniteUpdateWorkAsync(() => DeliverLifecycleAnnouncementAsync(announcement));
    }

    private async Task DeliverLifecycleAnnouncementAsync(
        JarvisLifecycleAnnouncement announcement)
    {
        var deliveredByJarvis = false;
        var liveOwnedDelivery = _liveConversation?.IsActive == true
            || _liveConversation?.IsStarting == true;
        try
        {
            deliveredByJarvis = _liveConversation != null
                && await _liveConversation.TryAnnounceLifecycleAsync(announcement);
            if (deliveredByJarvis)
            {
                AppLog.Info(
                    $"Lifecycle announcement delivered through Jarvis Live: source={announcement.Source} state={announcement.State}");
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning(
                ex,
                $"Lifecycle delivery failed: source={announcement.Source} state={announcement.State}");
        }

        if (!deliveredByJarvis
            && !liveOwnedDelivery
            && !_disposed
            && announcement.CanPublish
            && !string.IsNullOrWhiteSpace(announcement.FallbackSpeech))
        {
            // A failed/cancelled Live delivery must never become a second TTS
            // owner after goodbye. Background-only tasks retain their fallback.
            AppLog.Info(
                $"Lifecycle announcement used local speech fallback: source={announcement.Source} state={announcement.State}");
            TtsService.Instance.Speak(announcement.FallbackSpeech, "en", UpdateWorkAdmission);
        }
    }

#if KEYBOARD_WTF_CONNECTOR_STAGING_CANDIDATE
    public void SyncStartupRegistration()
    {
        AppLog.Info("This test app does not manage Windows startup. The public app's startup registration is unchanged.");
    }
#else
    public void SyncStartupRegistration() =>
        WindowsStartupService.Sync(_settings.Current.StartWithWindows, StartupExecutablePath);
#endif

    private void OpenFirstRunSetup()
    {
#if QLOO_JARVIS
        var personalKeyReady = _personalApiSettings?.GetState().State?.Keys
            .Any(key => key.Enabled && key.Validated) == true;
        if (personalKeyReady) return;
        _ = TrackFiniteUpdateWorkAsync(() => Task.Run(async () =>
        {
            await Task.Delay(900);
            WebSettingsService.Instance.OpenInBrowser("#personal-api");
        }));
#else
        if (_paidAccount?.Enabled == true)
        {
            if (_settings.IsFirstRun)
                _ = TrackFiniteUpdateWorkAsync(() => Task.Run(async () =>
                {
                    await Task.Delay(900);
                    WebSettingsService.Instance.OpenInBrowser("#getting-started");
                }));
            return;
        }
        if (!string.IsNullOrWhiteSpace(KeyboardWtfState.GeminiApiKey))
            return;

        _ = TrackFiniteUpdateWorkAsync(() => Task.Run(async () =>
        {
            await Task.Delay(900);
            WebSettingsService.Instance.OpenInBrowser("#set-api-key");
        }));
#endif
    }

    public void ToggleAi()
    {
        try { _settings.SaveUseAi(!KeyboardWtfState.UseAi); }
        catch (Helpers.SecretPersistenceException)
        {
            _notifications.Error("Settings not saved", Helpers.SecretPersistenceException.SafeMessage);
            return;
        }
        _notifications.Info("AI formatting", KeyboardWtfState.UseAi ? "Enabled." : "Disabled.");
    }

    public void SetDefaultDestination(string destination)
    {
        try { _settings.SaveDefaultDestination(destination); }
        catch (Helpers.SecretPersistenceException)
        {
            _notifications.Error("Settings not saved", Helpers.SecretPersistenceException.SafeMessage);
            return;
        }
        _notifications.Info("Default destination", destination);
    }

    private static void RegisterDestinations()
    {
        if (DestinationRegistry.All.Count > 0)
            return;

        if (OperatingSystem.IsWindows())
        {
            DestinationRegistry.Register(new ClipboardDestination());
            DestinationRegistry.Register(new TypeOutDestination());
        }

        DestinationRegistry.Register(new EmailDestination());
        DestinationRegistry.Register(new SlackWebhookDestination());
        DestinationRegistry.Register(new DiscordWebhookDestination());
        DestinationRegistry.Register(new TeamsDestination());
        DestinationRegistry.Register(new CalendarDestination());
        DestinationRegistry.Register(new WhatsAppDestination());
        DestinationRegistry.Register(new NotionDestination());
        DestinationRegistry.Register(new TelegramDestination());
    }

#if QLOO_JARVIS
    private static Task AuthorizeQlooPersonalApiAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
#endif

    private async Task LoadModelsAsync()
    {
        try
        {
            AppLog.Info($"Models directory: {ModelManager.ModelsBaseDir}");
            if (OperatingSystem.IsWindows())
            {
                var runtimeWhisperDll = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "whisper.dll");
                var whisperDll = File.Exists(runtimeWhisperDll)
                    ? runtimeWhisperDll
                    : Path.Combine(AppContext.BaseDirectory, "whisper.dll");
                if (File.Exists(whisperDll))
                    Whisper.net.LibraryLoader.RuntimeOptions.LibraryPath = whisperDll;
            }

            if (ModelManager.IsVoskPresent)
                _speechRecognition.LoadVoskModel(ModelManager.VoskModelPath);
            if (ModelManager.IsWhisperPresent)
                _speechRecognition.LoadWhisperModel(ModelManager.WhisperModelPath);

            KeyboardWtfState.IsDownloadingModels = true;
            void ReportModelStatus(string status)
            {
                KeyboardWtfState.ModelDownloadStatus = status;
                AppLog.Info($"Model download progress: statusCharacters={status?.Length ?? 0}");
            }

            var voskReady = ModelManager.IsVoskPresent
                || await ModelManager.DownloadVoskModelAsync(ReportModelStatus);

            if (voskReady && ModelManager.IsVoskPresent && !_speechRecognition.IsVoskLoaded)
                _speechRecognition.LoadVoskModel(ModelManager.VoskModelPath);
            else if (!voskReady)
                _notifications?.Warning(
                    "Dictation model unavailable",
                    "The local Vosk model could not be downloaded. Check your connection and retry in Settings.");

            // Vosk is available after the small first download, so Dictation
            // can start while the more accurate Whisper model continues.
            var whisperReady = ModelManager.IsWhisperPresent
                || await ModelManager.DownloadWhisperModelAsync(ReportModelStatus);

            if (whisperReady && ModelManager.IsWhisperPresent && !_speechRecognition.IsWhisperLoaded)
                _speechRecognition.LoadWhisperModel(ModelManager.WhisperModelPath);
            else if (!whisperReady)
                _notifications?.Warning(
                    "Accurate speech model unavailable",
                    "Whisper could not be downloaded. Fast local Dictation remains available when Vosk is ready.");
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "Model loading failed");
            _notifications?.Warning("Speech models", "Model loading failed. Vosk/Whisper can be retried in settings.");
        }
        finally
        {
            KeyboardWtfState.IsDownloadingModels = false;
            KeyboardWtfState.ModelDownloadStatus = null;
        }
    }

    private void StartPiperInstallInBackground()
    {
        if (PiperInstaller.IsFullyInstalled)
            return;

        _ = TrackFiniteUpdateWorkAsync(() => Task.Run(async () =>
        {
            KeyboardWtfState.IsInstallingPiper = true;
            try
            {
                await PiperInstaller.EnsureInstalledAsync(status =>
                {
                    KeyboardWtfState.PiperInstallStatus = status;
                    AppLog.Info($"Piper install progress: statusCharacters={status?.Length ?? 0}");
                });
            }
            catch (Exception ex)
            {
                AppLog.Warning(ex, "Piper install failed");
            }
            finally
            {
                KeyboardWtfState.IsInstallingPiper = false;
                KeyboardWtfState.PiperInstallStatus = null;
            }
        }));
    }

    private static string ResolveStartupExecutablePath()
    {
        var current = Environment.ProcessPath ?? Application.ExecutablePath;
        var installed = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "QLOO",
            "QLOO.exe");
        return ResolveStartupExecutablePath(current, installed);
    }

    internal static string ResolveStartupExecutablePath(
        string current,
        string installed)
    {
        if (string.IsNullOrWhiteSpace(current))
            return installed ?? "";

        var directory = new DirectoryInfo(Path.GetDirectoryName(current) ?? "");
        for (var depth = 0; depth < 12 && directory != null; depth++, directory = directory.Parent)
        {
            var gitMarker = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(gitMarker) || File.Exists(gitMarker))
            {
                // Validation binaries run directly from the source tree. They
                // must never replace the user's installed/startup executable.
                return !string.IsNullOrWhiteSpace(installed) && File.Exists(installed)
                    ? installed
                    : current;
            }
        }

        return current;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopPublicUpdateNotifications();
        WebSettingsService.Instance?.Dispose();
        _hotkeys.Dispose();
        _startupHotkeyRetryTimer?.Dispose();
        _audioRecorder.Dispose();
        _speechRecognition.Dispose();
        _liveConversation?.Dispose();
        _timers?.Dispose();
        _browserAutomation?.Dispose();
        _aiBackend?.Dispose();
        _personalKeyValidator?.Dispose();
        _paidAccount?.Dispose();
        _jarvisAutomation?.Dispose();
        _overlay?.Dispose();
    }
}
