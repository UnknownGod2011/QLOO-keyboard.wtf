namespace KeyboardWtf.Services;

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using KeyboardWtf.Helpers;
using KeyboardWtf.Models;

public enum VoiceTextDeliveryMode
{
    TypeIntoActiveApp,
    ClipboardOnly,
    AskEveryTime
}

public enum JarvisPermissionMode
{
    AlwaysAsk,
    AutoExecute
}

public sealed class JarvisWorkflowSettings
{
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;
    public string Name { get; set; } = "";
    public List<string> Aliases { get; set; } = new();
    public List<JarvisWorkflowActionSettings> Actions { get; set; } = new();

    // Retained for Settings UI and backward compatibility with workflows
    // saved before the ordered-action representation was introduced.
    public string Apps { get; set; } = "";
    public string Urls { get; set; } = "";
    public string Folder { get; set; } = "";
}

public sealed class JarvisWorkflowActionSettings
{
    public string Kind { get; set; } = "";
    public string Target { get; set; } = "";
}

public sealed class HotkeySettings
{
    // Ctrl+Alt+Q (Jarvis), Ctrl+Alt+J (its fallback) and Ctrl+Alt+Space (legacy push-to-talk) belong to
    // keyboard.wtf. QLOO must run beside it, so its Jarvis default keeps the Q mnemonic with Shift.
    public string Jarvis { get; set; } = "Ctrl+Alt+Shift+Q";
    public string Cancel { get; set; } = "Ctrl+Alt+X";

    // The loopback settings transport is shared with the original app.  Keep
    // these deserialization-only members so an old browser settings page
    // cannot crash QLOO during migration; they are neither persisted nor
    // registered by QLOO's HotkeyService.
    [JsonIgnore] public string PushToTalk { get; set; } = "";
    [JsonIgnore] public string Dictation { get; set; } = "";
    [JsonIgnore] public string CommandMode { get; set; } = "";
    [JsonIgnore] public string QuickSend { get; set; } = "";
    // Second global shortcut: opens QLOO Settings. Ctrl+Alt+P avoids Windows-reserved combos,
    // keyboard.wtf's own defaults (K, D, Q, X, comma) and the Jarvis/Cancel bindings.
    public string OpenSettings { get; set; } = "Ctrl+Alt+P";

    public Dictionary<string, string> ToDictionary() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Jarvis"] = Jarvis,
        ["Cancel"] = Cancel,
        ["OpenSettings"] = OpenSettings,
    };

    public static HotkeySettings Defaults() => new();
}

public sealed class AppSettings
{
    internal AppSettings CopyForSave() => (AppSettings)MemberwiseClone();
    public SpeechEngine SpeechEngine { get; set; } = SpeechEngine.Whisper;
    public string Language { get; set; } = "auto";
    public int MicrophoneDevice { get; set; } = -1;
    public int MaxRecordingSeconds { get; set; } = 30;
    public WhisperModelSize WhisperModelSize { get; set; } = WhisperModelSize.Small;
    public AiProvider AiProvider { get; set; } = AiProvider.Gemini;
    // Compatibility-only fields for shared settings transport. QLOO never
    // persists or exposes alternate AI providers.
    [JsonIgnore] public string ClaudeApiKey { get; set; }
    [JsonIgnore] public string OpenAiApiKey { get; set; }
    [JsonIgnore] public string GeminiApiKey { get; set; }
    [JsonIgnore] public string DeepSeekApiKey { get; set; }
    [JsonIgnore] public string PerplexityApiKey { get; set; }
    public string SlackWebhookUrl { get; set; }
    public string DiscordWebhookUrl { get; set; }
    public string VoiceNoteSavePath { get; set; }
    public string VoiceNoteFilenamePattern { get; set; }
    public bool UseNoiseGate { get; set; } = true;
    public bool UseAi { get; set; } = true;
    public string TranslateTargetLanguage { get; set; } = "";
    public bool UseFillerWordCleaner { get; set; }
    public CaseTransform CaseTransform { get; set; } = CaseTransform.None;
    public Dictionary<string, string> CustomPrompts { get; set; } = new();
    public HotkeySettings Hotkeys { get; set; } = HotkeySettings.Defaults();
    public string DefaultDestination { get; set; } = "Clipboard";
    public VoiceTextDeliveryMode TextDeliveryMode { get; set; } = VoiceTextDeliveryMode.TypeIntoActiveApp;
    public string AssistantName { get; set; } = "Jarvis";
    public AssistantTone AssistantTone { get; set; } = AssistantTone.Balanced;
    public string GeminiVoice { get; set; } = "Kore";
    public List<JarvisWorkflowSettings> JarvisWorkflows { get; set; } = new();
    public List<string> DeletedJarvisWorkflowNames { get; set; } = new();
    public bool LargeSavedSetupWarningDismissed { get; set; }
    public bool StartWithWindows { get; set; } = true;
    public bool ShowQuickActionButton { get; set; } = true;
    public JarvisPermissionMode JarvisPermissionMode { get; set; } = JarvisPermissionMode.AlwaysAsk;
    public string PreferredBrowser { get; set; } = "";
    public bool BrowserAutomationEnabled { get; set; }
    public int BrowserAutomationConsentVersion { get; set; }
    public string BrowserAutomationModel { get; set; } = "gemini-3.1-flash-lite-preview";
    public bool BrowserAutomationMemoryEnabled { get; set; }
    public string UpdateChannel { get; set; } = "stable";
    // Qloo cultural intelligence + adaptive taste memory. On by default; Qloo calls also need a key.
    // Off restores the original Jarvis tool list and system instruction exactly.
    public bool QlooEnabled { get; set; } = true;
    public int QlooDailyBudget { get; set; } = 150;
    public int HotkeysRevision { get; set; }
}

public sealed class SettingsService
{
    public const int CurrentBrowserAutomationConsentVersion = 1;
    private readonly object _saveLock = new();
    private readonly Func<string, string> _protectSecret;
    private sealed record SecretField(string Name, Func<AppSettings, string> Read,
        Action<AppSettings, string> Write, Action<string> Activate);
    private static readonly SecretField[] SecretFields =
    [
        new("slackWebhookUrl", s => s.SlackWebhookUrl, (s, v) => s.SlackWebhookUrl = v, v => KeyboardWtfState.SlackWebhookUrl = v),
        new("discordWebhookUrl", s => s.DiscordWebhookUrl, (s, v) => s.DiscordWebhookUrl = v, v => KeyboardWtfState.DiscordWebhookUrl = v),
    ];

    internal static IEnumerable<string> SecretSettingNames => SecretFields.Select(field => field.Name);

    public static readonly IReadOnlyDictionary<string, string> GeminiVoices =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Kore"] = "Firm",
            ["Orus"] = "Firm",
            ["Puck"] = "Upbeat",
            ["Charon"] = "Informative",
            ["Aoede"] = "Breezy",
            ["Zephyr"] = "Bright",
            ["Leda"] = "Youthful",
            ["Fenrir"] = "Excitable",
            ["Achird"] = "Friendly",
            ["Gacrux"] = "Mature",
            ["Sulafat"] = "Warm",
            ["Iapetus"] = "Clear",
        };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string AppDataDir { get; } = ResolveAppDataDir(
        ConfiguredAppDataOverride(),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    private static string ConfiguredAppDataOverride()
    {
        return Environment.GetEnvironmentVariable(QlooProduct.AppDataEnvironmentVariable)
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                QlooProduct.AppDataFolder);
    }

    internal static string ResolveAppDataDir(string overridePath, string localAppData)
    {
        var root = Path.GetFullPath(localAppData);
        var defaultPath = Path.Combine(root, QlooProduct.AppDataFolder);
        if (string.IsNullOrWhiteSpace(overridePath))
            return defaultPath;

        // QLOO_APPDATA_DIR may select only the QLOO profile itself or a
        // subdirectory of it (as documented in README.md), so an override can
        // never point QLOO at keyboard.wtf's or another app's LocalAppData profile.
        var requested = Path.GetFullPath(overridePath.Trim());
        var relative = Path.GetRelativePath(defaultPath, requested);
        if (relative is ".")
            return defaultPath;
        if (Path.IsPathRooted(relative)
            || relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "QLOO_APPDATA_DIR must be the QLOO profile (%LOCALAPPDATA%\\QLOO) or a subdirectory of it.");
        }

        return requested;
    }

    public SettingsService(string settingsPath = null) : this(settingsPath, SecureStore.Protect)
    {
    }

    internal SettingsService(string settingsPath, Func<string, string> protectSecret)
    {
        _protectSecret = protectSecret ?? throw new ArgumentNullException(nameof(protectSecret));
        SettingsPath = string.IsNullOrWhiteSpace(settingsPath)
            ? Path.Combine(AppDataDir, "settings.json")
            : settingsPath;
    }

    public string SettingsPath { get; }

    public event Action WorkflowsChanged;

    public bool IsFirstRun { get; private set; }
    public string SettingsStorageWarning { get; private set; } = "";

    public AppSettings Current { get; private set; } = new();

    public bool HasBrowserAutomationConsent =>
        Current.BrowserAutomationConsentVersion
        >= CurrentBrowserAutomationConsentVersion;

    public void Load()
    {
        var hadSettingsFile = File.Exists(SettingsPath);
        IsFirstRun = !hadSettingsFile;
        var settingsJson = "";
        var readableSettings = true;
        if (File.Exists(SettingsPath))
        {
            try
            {
                settingsJson = File.ReadAllText(SettingsPath);
                Current = JsonSerializer.Deserialize<AppSettings>(settingsJson, JsonOptions) ?? new AppSettings();
            }
            catch (Exception ex)
            {
                AppLog.Warning(ex, "Settings load failed; using defaults");
                Current = new AppSettings();
                readableSettings = false;
                SettingsStorageWarning = "The settings file could not be read. Its original contents have been preserved.";
            }
        }

        Current.Hotkeys ??= HotkeySettings.Defaults();
        MigrateLegacyHotkeys();
        if (string.Equals(Current.AssistantName, "Cursivis", StringComparison.OrdinalIgnoreCase))
            Current.AssistantName = "Jarvis";
        Current.CustomPrompts ??= new Dictionary<string, string>();
        Current.JarvisWorkflows ??= new List<JarvisWorkflowSettings>();
        Current.DeletedJarvisWorkflowNames ??= new List<string>();
        MigrateLegacyWorkflows();
        Current.UpdateChannel = string.Equals(Current.UpdateChannel, "beta", StringComparison.OrdinalIgnoreCase)
            ? "beta"
            : "stable";
        if (!hadSettingsFile || !settingsJson.Contains("\"StartWithWindows\"", StringComparison.OrdinalIgnoreCase))
            Current.StartWithWindows = true;
        Current.GeminiVoice = SanitizeGeminiVoice(Current.GeminiVoice);
        if (string.IsNullOrWhiteSpace(Current.BrowserAutomationModel))
            Current.BrowserAutomationModel = "gemini-3.1-flash-lite-preview";
        ApplyToState();
        if (readableSettings)
        {
            try
            {
                lock (_saveLock) SaveSnapshot(Current.CopyForSave(), migrateEncrypted: true);
            }
            catch (SecretPersistenceException)
            {
                // A readable legacy key remains available in this session, but
                // its original file must not be rewritten when protection fails.
                AppLog.Warning("Settings migration was not committed; the original file was preserved.");
            }
        }
    }

    private void MigrateLegacyHotkeys()
    {
        if (Current.Hotkeys == null)
        {
            Current.Hotkeys = HotkeySettings.Defaults();
            return;
        }

        if (string.IsNullOrWhiteSpace(Current.Hotkeys.Jarvis))
            Current.Hotkeys.Jarvis = HotkeySettings.Defaults().Jarvis;
        if (string.Equals(Current.Hotkeys.Jarvis, "Ctrl+Alt+S", StringComparison.OrdinalIgnoreCase))
            Current.Hotkeys.Jarvis = HotkeySettings.Defaults().Jarvis;
        if (string.IsNullOrWhiteSpace(Current.Hotkeys.Cancel))
            Current.Hotkeys.Cancel = HotkeySettings.Defaults().Cancel;
        if (string.IsNullOrWhiteSpace(Current.Hotkeys.OpenSettings)
            || IsInaccessibleFunctionKey(Current.Hotkeys.OpenSettings))
            Current.Hotkeys.OpenSettings = HotkeySettings.Defaults().OpenSettings;
        // One-time move off the inherited keyboard.wtf Jarvis default, which the two apps would share.
        if (Current.HotkeysRevision < 2)
        {
            if (Current.Hotkeys.Jarvis is { } jarvis
                && (jarvis.Equals("Ctrl+Alt+Q", StringComparison.OrdinalIgnoreCase)
                    || jarvis.Equals("Ctrl+Alt+Space", StringComparison.OrdinalIgnoreCase)))
                Current.Hotkeys.Jarvis = HotkeySettings.Defaults().Jarvis;
            Current.HotkeysRevision = 2;
        }

        // A side-by-side production candidate briefly used F13-F17 to avoid
        // colliding with an already-running legacy build. Most customer
        // keyboards cannot produce F13-F24, so those implementation-only
        // values must never survive into normal user settings.
        var defaults = HotkeySettings.Defaults();
        if (IsInaccessibleFunctionKey(Current.Hotkeys.Jarvis))
            Current.Hotkeys.Jarvis = defaults.Jarvis;
        if (IsInaccessibleFunctionKey(Current.Hotkeys.Cancel))
            Current.Hotkeys.Cancel = defaults.Cancel;
        Current.AiProvider = AiProvider.Gemini;
    }

    internal static bool IsInaccessibleFunctionKey(string hotkey) =>
        (hotkey ?? string.Empty)
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(part => part.StartsWith("F", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(part.AsSpan(1), out var number)
                && number is >= 13 and <= 24);

    public void Save()
    {
        lock (_saveLock)
            SaveSnapshot(Current.CopyForSave());
    }

    private string ProtectSecretValue(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return string.Empty;
        try
        {
            var protectedValue = _protectSecret(plaintext);
            if (protectedValue?.StartsWith(SecureStore.EncryptedPrefix, StringComparison.Ordinal) != true)
                throw new SecretPersistenceException();
            return protectedValue;
        }
        catch (Exception)
        {
            SettingsStorageWarning = SecretPersistenceException.SafeMessage;
            throw new SecretPersistenceException();
        }
    }

    // Stage all protection before creating/writing any file. Ciphertext is not
    // reprocessed on ordinary saves; only Load performs old-entropy migration.
    private void SaveSnapshot(AppSettings snapshot, bool migrateEncrypted = false)
    {
        var temporaryPath = SettingsPath + ".tmp";
        var writeAttempted = false;
        try
        {
            foreach (var field in SecretFields)
            {
                var stored = field.Read(snapshot);
                if (string.IsNullOrEmpty(stored)) continue;
                if (stored.StartsWith(SecureStore.EncryptedPrefix, StringComparison.Ordinal))
                {
                    if (!migrateEncrypted) continue;
                    var plaintext = SecureStore.Unprotect(stored, out var requiresResave);
                    if (!requiresResave || string.IsNullOrEmpty(plaintext)) continue;
                    stored = plaintext;
                }
                field.Write(snapshot, ProtectSecretValue(stored));
            }
            var directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            writeAttempted = true;
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, SettingsPath, overwrite: true);
            foreach (var field in SecretFields) field.Write(Current, field.Read(snapshot));
            SettingsStorageWarning = "";
        }
        catch (Exception)
        {
            if (writeAttempted)
            {
                try { File.Delete(temporaryPath); } catch { }
            }
            SettingsStorageWarning = SecretPersistenceException.SafeMessage;
            AppLog.Warning("Settings persistence failed; no success was acknowledged.");
            throw new SecretPersistenceException();
        }
    }

    internal void SaveSecretSettings(IReadOnlyDictionary<string, string> updates, AiProvider? provider = null)
    {
        if (updates.Count == 0 && provider == null) return;
        if (provider.HasValue && provider.Value != AiProvider.Gemini)
            throw new NotSupportedException("QLOO uses Gemini Personal API only.");
        lock (_saveLock)
        {
            var snapshot = Current.CopyForSave();
            foreach (var name in updates.Keys)
                if (!SecretFields.Any(field => field.Name == name))
                    throw new ArgumentException("Unsupported secret setting.", nameof(updates));
            // Raw user input is always protected, even if it starts with enc:v1:.
            foreach (var field in SecretFields)
                if (updates.TryGetValue(field.Name, out var value))
                    field.Write(snapshot, ProtectSecretValue(value));
            snapshot.AiProvider = AiProvider.Gemini;
            SaveSnapshot(snapshot);
            foreach (var field in SecretFields)
                if (updates.TryGetValue(field.Name, out var value)) field.Activate(value ?? "");
            Current.AiProvider = AiProvider.Gemini;
            KeyboardWtfState.SelectedAiProvider = AiProvider.Gemini;
        }
    }

    private void SaveSecretSetting(string name, string value) => SaveSecretSettings(
        new Dictionary<string, string> { [name] = value });

    public void ApplyToState()
    {
        KeyboardWtfState.SelectedEngine = Current.SpeechEngine;
        KeyboardWtfState.SelectedLanguage = Current.Language ?? "auto";
        KeyboardWtfState.SelectedMicrophoneIndex = Current.MicrophoneDevice;
        KeyboardWtfState.MaxRecordingSeconds = NormalizeMaxRecordingSeconds(Current.MaxRecordingSeconds);
        KeyboardWtfState.SelectedWhisperModel = Current.WhisperModelSize;
        Current.AiProvider = AiProvider.Gemini;
        KeyboardWtfState.SelectedAiProvider = AiProvider.Gemini;
        foreach (var field in SecretFields) field.Activate(SecureStore.Unprotect(field.Read(Current)));
        KeyboardWtfState.VoiceNoteSavePath = string.IsNullOrWhiteSpace(Current.VoiceNoteSavePath) ? null : Current.VoiceNoteSavePath;
        KeyboardWtfState.VoiceNoteFilenamePattern = string.IsNullOrWhiteSpace(Current.VoiceNoteFilenamePattern) ? null : Current.VoiceNoteFilenamePattern;
        KeyboardWtfState.UseNoiseGate = Current.UseNoiseGate;
        KeyboardWtfState.UseAi = Current.UseAi;
        KeyboardWtfState.TranslateTargetLanguage = Current.TranslateTargetLanguage ?? "";
        KeyboardWtfState.UseFillerWordCleaner = Current.UseFillerWordCleaner;
        KeyboardWtfState.SelectedCaseTransform = Current.CaseTransform;
        KeyboardWtfState.AssistantName = SanitizeAssistantName(Current.AssistantName);
        KeyboardWtfState.AssistantTone = Current.AssistantTone;
        KeyboardWtfState.GeminiVoice = SanitizeGeminiVoice(Current.GeminiVoice);
        KeyboardWtfState.CustomPrompts.Clear();
        foreach (var pair in Current.CustomPrompts)
        {
            if (!string.IsNullOrWhiteSpace(pair.Value))
                KeyboardWtfState.CustomPrompts[pair.Key] = pair.Value;
        }
    }

    public void SaveSpeechEngine(SpeechEngine value) { Current.SpeechEngine = value; KeyboardWtfState.SelectedEngine = value; Save(); }
    public void SaveLanguage(string value) { Current.Language = value ?? "auto"; KeyboardWtfState.SelectedLanguage = Current.Language; Save(); }
    public void SaveMicrophoneDevice(int value) { Current.MicrophoneDevice = value; KeyboardWtfState.SelectedMicrophoneIndex = value; Save(); }
    public void SaveMaxRecordingSeconds(int value) { Current.MaxRecordingSeconds = NormalizeMaxRecordingSeconds(value); KeyboardWtfState.MaxRecordingSeconds = Current.MaxRecordingSeconds; Save(); }

    internal static int NormalizeMaxRecordingSeconds(int value) =>
        value == 0 ? 0 : Math.Clamp(value, 5, 3600);
    public void SaveWhisperModelSize(WhisperModelSize value) { Current.WhisperModelSize = value; KeyboardWtfState.SelectedWhisperModel = value; Save(); }
    public void SaveAiProvider(AiProvider value) => SaveSecretSettings(new Dictionary<string, string>(), AiProvider.Gemini);
    public void SaveClaudeApiKey(string value) => throw new NotSupportedException("QLOO uses Gemini Personal API only.");
    public void SaveOpenAiApiKey(string value) => throw new NotSupportedException("QLOO uses Gemini Personal API only.");
    public void SaveGeminiApiKey(string value) => throw new NotSupportedException("Configure Gemini keys in the Personal API vault.");
    public void SaveDeepSeekApiKey(string value) => throw new NotSupportedException("QLOO uses Gemini Personal API only.");
    public void SavePerplexityApiKey(string value) => throw new NotSupportedException("QLOO uses Gemini Personal API only.");
    public void SaveSlackWebhookUrl(string value) => SaveSecretSetting("slackWebhookUrl", value);
    public void SaveDiscordWebhookUrl(string value) => SaveSecretSetting("discordWebhookUrl", value);
    public void SaveVoiceNoteSavePath(string value) { Current.VoiceNoteSavePath = string.IsNullOrWhiteSpace(value) ? null : value.Trim(); KeyboardWtfState.VoiceNoteSavePath = Current.VoiceNoteSavePath; Save(); }
    public void SaveVoiceNoteFilenamePattern(string value) { Current.VoiceNoteFilenamePattern = string.IsNullOrWhiteSpace(value) ? null : value.Trim(); KeyboardWtfState.VoiceNoteFilenamePattern = Current.VoiceNoteFilenamePattern; Save(); }
    public void SaveUseNoiseGate(bool value) { Current.UseNoiseGate = value; KeyboardWtfState.UseNoiseGate = value; Save(); }
    public void SaveUseAi(bool value)
    {
        var previous = Current.UseAi;
        Current.UseAi = value;
        try { Save(); }
        catch { Current.UseAi = previous; throw; }
        KeyboardWtfState.UseAi = value;
    }
    public void SaveTranslateTargetLanguage(string value) { Current.TranslateTargetLanguage = value ?? ""; KeyboardWtfState.TranslateTargetLanguage = Current.TranslateTargetLanguage; Save(); }
    public void SaveUseFillerWordCleaner(bool value) { Current.UseFillerWordCleaner = value; KeyboardWtfState.UseFillerWordCleaner = value; Save(); }
    public void SaveCaseTransform(CaseTransform value) { Current.CaseTransform = value; KeyboardWtfState.SelectedCaseTransform = value; Save(); }
    public void SaveAssistantName(string value) { Current.AssistantName = SanitizeAssistantName(value); KeyboardWtfState.AssistantName = Current.AssistantName; Save(); }
    public void SaveAssistantTone(AssistantTone value) { Current.AssistantTone = value; KeyboardWtfState.AssistantTone = value; Save(); }
    public void SaveGeminiVoice(string value) { Current.GeminiVoice = SanitizeGeminiVoice(value); KeyboardWtfState.GeminiVoice = Current.GeminiVoice; Save(); }
    public void SaveStartWithWindows(bool value) { Current.StartWithWindows = value; Save(); }
    public void SaveShowQuickActionButton(bool value)
    {
        lock (_saveLock)
        {
            var snapshot = Current.CopyForSave();
            snapshot.ShowQuickActionButton = value;
            // Publish only after the existing atomic file commit succeeds.
            // A rejected save leaves both the preference and old file intact.
            SaveSnapshot(snapshot);
            Current.ShowQuickActionButton = value;
        }
    }
    public void SaveQlooEnabled(bool value)
    {
        lock (_saveLock)
        {
            var snapshot = Current.CopyForSave();
            snapshot.QlooEnabled = value;
            SaveSnapshot(snapshot);
            Current.QlooEnabled = value;
        }
    }

    public void DismissLargeSavedSetupWarning()
    {
        var previous = Current.LargeSavedSetupWarningDismissed;
        Current.LargeSavedSetupWarningDismissed = true;
        try { Save(); }
        catch { Current.LargeSavedSetupWarningDismissed = previous; throw; }
    }
    public void SaveJarvisPermissionMode(JarvisPermissionMode value) { Current.JarvisPermissionMode = value; Save(); }
    public void SaveBrowserAutomationEnabled(bool value)
    {
        Current.BrowserAutomationEnabled = value && HasBrowserAutomationConsent;
        Save();
    }
    public void SaveBrowserAutomationConsent(bool accepted)
    {
        Current.BrowserAutomationConsentVersion = accepted
            ? CurrentBrowserAutomationConsentVersion
            : 0;
        if (!accepted)
            Current.BrowserAutomationEnabled = false;
        Save();
    }
    public void SaveBrowserAutomationModel(string value)
    {
        var model = (value ?? "").Trim();
        Current.BrowserAutomationModel = model.Length is > 0 and <= 80
            ? model
            : "gemini-3.1-flash-lite-preview";
        Save();
    }
    public void SaveBrowserAutomationMemoryEnabled(bool value)
    {
        Current.BrowserAutomationMemoryEnabled = value;
        Save();
    }
    public void SaveUpdateChannel(string channel)
    {
        Current.UpdateChannel = string.Equals(channel, "beta", StringComparison.OrdinalIgnoreCase)
            ? "beta"
            : "stable";
        Save();
    }
    public void SavePreferredBrowser(string value)
    {
        Current.PreferredBrowser = BrowserLauncherService.NormalizeBrowser(value);
        Save();
    }

    public JarvisWorkflowSettings SaveWorkflow(string name, string apps, string urls, string folder)
    {
        var actions = SplitWorkflowItems(apps)
            .Select(target => new JarvisWorkflowActionSettings { Kind = "auto", Target = target })
            .Concat(SplitWorkflowItems(urls)
                .Select(target => new JarvisWorkflowActionSettings { Kind = "url", Target = target }))
            .Concat(string.IsNullOrWhiteSpace(folder)
                ? Array.Empty<JarvisWorkflowActionSettings>()
                : [new JarvisWorkflowActionSettings { Kind = "folder", Target = folder }])
            .ToArray();
        return SaveWorkflow(name, [name], actions);
    }

    public JarvisWorkflowSettings SaveWorkflow(
        string name,
        IEnumerable<string> aliases,
        IEnumerable<JarvisWorkflowActionSettings> actions)
    {
        var cleanName = CleanInline(name, 48);
        if (string.IsNullOrWhiteSpace(cleanName))
            throw new InvalidOperationException("Workflow name is required.");

        var cleanAliases = (aliases ?? Array.Empty<string>())
            .Prepend(cleanName)
            .Select(value => CleanInline(value, 48))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();
        var cleanActions = CleanNewWorkflowActions(actions);
        if (cleanActions.Count == 0)
            throw new InvalidOperationException("A workflow needs at least one app, website, or folder.");

        Current.JarvisWorkflows ??= new List<JarvisWorkflowSettings>();
        Current.DeletedJarvisWorkflowNames ??= new List<string>();
        Current.DeletedJarvisWorkflowNames.RemoveAll(item =>
            cleanAliases.Any(alias => string.Equals(
                FuzzyMatcher.Normalize(item),
                FuzzyMatcher.Normalize(alias),
                StringComparison.Ordinal)));
        var workflow = Current.JarvisWorkflows.FirstOrDefault(w =>
            string.Equals(FuzzyMatcher.Normalize(w.Name), FuzzyMatcher.Normalize(cleanName), StringComparison.Ordinal)
            || (w.Aliases ?? []).Any(alias => string.Equals(
                FuzzyMatcher.Normalize(alias),
                FuzzyMatcher.Normalize(cleanName),
                StringComparison.Ordinal)));
        if (workflow == null)
        {
            workflow = new JarvisWorkflowSettings { Name = cleanName };
            Current.JarvisWorkflows.Add(workflow);
        }

        workflow.Version = JarvisWorkflowSettings.CurrentVersion;
        workflow.Name = cleanName;
        workflow.Aliases = cleanAliases;
        workflow.Actions = cleanActions;
        SynchronizeLegacyWorkflowFields(workflow);
        Save();
        SavedStateNotifications.Notify(WorkflowsChanged);
        return workflow;
    }

    public JarvisWorkflowSettings UpdateWorkflow(
        string currentName,
        string newName,
        IEnumerable<string> aliases,
        IEnumerable<JarvisWorkflowActionSettings> actions)
    {
        var currentNormalized = FuzzyMatcher.Normalize(currentName);
        if (string.IsNullOrWhiteSpace(currentNormalized))
            throw new InvalidOperationException("Which workflow should I update?");

        lock (_saveLock)
        {
            Current.JarvisWorkflows ??= new List<JarvisWorkflowSettings>();
            Current.DeletedJarvisWorkflowNames ??= new List<string>();
            var workflow = Current.JarvisWorkflows.FirstOrDefault(candidate =>
                string.Equals(
                    FuzzyMatcher.Normalize(candidate.Name),
                    currentNormalized,
                    StringComparison.Ordinal)
                || (candidate.Aliases ?? []).Any(alias => string.Equals(
                    FuzzyMatcher.Normalize(alias),
                    currentNormalized,
                    StringComparison.Ordinal)));
            if (workflow == null)
                throw new InvalidOperationException($"Workflow {currentName} was not found.");

            var cleanName = CleanInline(newName, 48);
            if (string.IsNullOrWhiteSpace(cleanName))
                throw new InvalidOperationException("Workflow name is required.");
            var cleanAliases = (aliases ?? Array.Empty<string>())
                .Prepend(cleanName)
                .Select(value => CleanInline(value, 48))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList();
            var cleanActions = CleanNewWorkflowActions(actions);
            if (cleanActions.Count == 0)
                throw new InvalidOperationException("A workflow needs at least one app, website, or folder.");

            var newIdentities = cleanAliases
                .Select(FuzzyMatcher.Normalize)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToHashSet(StringComparer.Ordinal);
            var collision = Current.JarvisWorkflows
                .Where(candidate => !ReferenceEquals(candidate, workflow))
                .FirstOrDefault(candidate => (candidate.Aliases ?? [])
                    .Prepend(candidate.Name)
                    .Select(FuzzyMatcher.Normalize)
                    .Any(newIdentities.Contains));
            if (collision != null)
                throw new InvalidOperationException($"That name or alias already belongs to workflow {collision.Name}.");

            var previousIdentities = (workflow.Aliases ?? [])
                .Prepend(workflow.Name)
                .Select(value => CleanInline(value, 48))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (var previous in previousIdentities)
            {
                var normalized = FuzzyMatcher.Normalize(previous);
                if (newIdentities.Contains(normalized)
                    || Current.DeletedJarvisWorkflowNames.Any(item => string.Equals(
                        FuzzyMatcher.Normalize(item),
                        normalized,
                        StringComparison.Ordinal)))
                    continue;
                Current.DeletedJarvisWorkflowNames.Add(previous);
            }
            Current.DeletedJarvisWorkflowNames.RemoveAll(item =>
                newIdentities.Contains(FuzzyMatcher.Normalize(item)));

            workflow.Version = JarvisWorkflowSettings.CurrentVersion;
            workflow.Name = cleanName;
            workflow.Aliases = cleanAliases;
            workflow.Actions = cleanActions;
            SynchronizeLegacyWorkflowFields(workflow);
            Save();
            SavedStateNotifications.Notify(WorkflowsChanged);
            return workflow;
        }
    }

    public bool DeleteWorkflow(string name)
    {
        var normalized = FuzzyMatcher.Normalize(name);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;
        var matched = Current.JarvisWorkflows?.Where(w =>
            string.Equals(FuzzyMatcher.Normalize(w.Name), normalized, StringComparison.Ordinal)
            || (w.Aliases ?? []).Any(alias => string.Equals(
                FuzzyMatcher.Normalize(alias), normalized, StringComparison.Ordinal)))
            .ToArray() ?? [];
        var removed = matched.Length > 0;
        if (removed)
            Current.JarvisWorkflows.RemoveAll(workflow => matched.Contains(workflow));
        Current.DeletedJarvisWorkflowNames ??= new List<string>();
        var tombstones = matched
            .SelectMany(workflow => (workflow.Aliases ?? []).Prepend(workflow.Name))
            .Append(name)
            .Select(value => CleanInline(value, 48))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var tombstoneAdded = false;
        foreach (var tombstone in tombstones)
        {
            var tombstoneNormalized = FuzzyMatcher.Normalize(tombstone);
            if (Current.DeletedJarvisWorkflowNames.Any(item =>
                    string.Equals(FuzzyMatcher.Normalize(item), tombstoneNormalized, StringComparison.Ordinal)))
                continue;
            Current.DeletedJarvisWorkflowNames.Add(tombstone);
            tombstoneAdded = true;
        }
        if (removed || tombstoneAdded)
        {
            Save();
            SavedStateNotifications.Notify(WorkflowsChanged);
        }
        return removed || tombstoneAdded;
    }

    public bool IsWorkflowDeleted(string name)
    {
        var normalized = FuzzyMatcher.Normalize(name);
        return !string.IsNullOrWhiteSpace(normalized)
            && (Current.DeletedJarvisWorkflowNames ?? []).Any(item =>
                string.Equals(FuzzyMatcher.Normalize(item), normalized, StringComparison.Ordinal));
    }

    internal JarvisWorkflowSettings ReadPersistedWorkflow(string trigger)
    {
        var normalized = FuzzyMatcher.Normalize(trigger);
        if (string.IsNullOrWhiteSpace(normalized) || !File.Exists(SettingsPath))
            return null;
        lock (_saveLock)
        {
            var persisted = JsonSerializer.Deserialize<AppSettings>(
                File.ReadAllText(SettingsPath),
                JsonOptions);
            return persisted?.JarvisWorkflows?.FirstOrDefault(workflow =>
                string.Equals(FuzzyMatcher.Normalize(workflow.Name), normalized, StringComparison.Ordinal)
                || (workflow.Aliases ?? []).Any(alias => string.Equals(
                    FuzzyMatcher.Normalize(alias), normalized, StringComparison.Ordinal)));
        }
    }

    internal string ReadPersistedPreferredBrowser()
    {
        if (!File.Exists(SettingsPath))
            return "";
        lock (_saveLock)
        {
            var persisted = JsonSerializer.Deserialize<AppSettings>(
                File.ReadAllText(SettingsPath),
                JsonOptions);
            return BrowserLauncherService.NormalizeBrowser(persisted?.PreferredBrowser);
        }
    }

    public void SaveCustomPrompt(string destinationName, string prompt)
    {
        var trimmed = string.IsNullOrWhiteSpace(prompt) ? string.Empty : prompt.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            Current.CustomPrompts.Remove(destinationName);
            KeyboardWtfState.CustomPrompts.Remove(destinationName);
        }
        else
        {
            Current.CustomPrompts[destinationName] = trimmed;
            KeyboardWtfState.CustomPrompts[destinationName] = trimmed;
        }
        Save();
    }

    public void SaveHotkeys(HotkeySettings hotkeys)
    {
        Current.Hotkeys = hotkeys ?? HotkeySettings.Defaults();
        Save();
    }

    public void ResetHotkeys()
    {
        Current.Hotkeys = HotkeySettings.Defaults();
        Save();
    }

    public void SaveDefaultDestination(string destination)
    {
        var previous = Current.DefaultDestination;
        Current.DefaultDestination = string.IsNullOrWhiteSpace(destination) ? "Clipboard" : destination;
        try { Save(); }
        catch { Current.DefaultDestination = previous; throw; }
    }

    public void SaveTextDeliveryMode(VoiceTextDeliveryMode mode)
    {
        Current.TextDeliveryMode = mode;
        Save();
    }

    private static string SanitizeAssistantName(string value)
    {
        var name = string.IsNullOrWhiteSpace(value) ? "Jarvis" : value.Trim();
        name = new string(name.Where(c => !char.IsControl(c)).ToArray());
        if (string.IsNullOrWhiteSpace(name))
            return "Jarvis";
        return name.Length <= 32 ? name : name[..32];
    }

    private static string SanitizeGeminiVoice(string value) =>
        GeminiVoices.ContainsKey(value ?? "") ? value.Trim() : "Kore";

    private static string CleanInline(string value, int maxLength)
    {
        var clean = string.Join(" ", (value ?? "")
            .ReplaceLineEndings(" ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return clean.Length <= maxLength ? clean : clean[..maxLength].Trim();
    }

    private void MigrateLegacyWorkflows()
    {
        foreach (var workflow in Current.JarvisWorkflows ?? [])
        {
            workflow.Aliases ??= new List<string>();
            workflow.Actions ??= new List<JarvisWorkflowActionSettings>();
            if (!workflow.Aliases.Any(alias => string.Equals(
                    FuzzyMatcher.Normalize(alias),
                    FuzzyMatcher.Normalize(workflow.Name),
                    StringComparison.Ordinal)))
            {
                workflow.Aliases.Insert(0, CleanInline(workflow.Name, 48));
            }
            if (workflow.Actions.Count == 0)
            {
                workflow.Actions.AddRange(SplitWorkflowItems(workflow.Apps)
                    .Select(target => new JarvisWorkflowActionSettings { Kind = "auto", Target = target }));
                workflow.Actions.AddRange(SplitWorkflowItems(workflow.Urls)
                    .Select(target => new JarvisWorkflowActionSettings { Kind = "url", Target = target }));
                if (!string.IsNullOrWhiteSpace(workflow.Folder))
                {
                    workflow.Actions.Add(new JarvisWorkflowActionSettings
                    {
                        Kind = "folder",
                        Target = CleanInline(workflow.Folder, 260),
                    });
                }
            }
            workflow.Actions = workflow.Actions
                .Select(CleanWorkflowAction)
                .Where(action => action != null)
                .Take(24)
                .ToList();
            workflow.Aliases = workflow.Aliases
                .Select(value => CleanInline(value, 48))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList();
            workflow.Version = JarvisWorkflowSettings.CurrentVersion;
            SynchronizeLegacyWorkflowFields(workflow);
        }
    }

    private static List<JarvisWorkflowActionSettings> CleanNewWorkflowActions(
        IEnumerable<JarvisWorkflowActionSettings> actions)
    {
        var requested = (actions ?? Array.Empty<JarvisWorkflowActionSettings>())
            .Take(24)
            .ToList();
        // A live selection/referent is not a durable app or folder identity.
        // Validate only new saves/edits; loading legacy settings stays non-destructive.
        if (requested.Any(action => IsUnresolvedWorkflowTarget(action?.Target)))
            throw new InvalidOperationException("A workflow target still refers to a live selection. Resolve its actual destination before saving; nothing was saved.");
        // Keep legacy load/migration non-destructive; reject invalid new URL
        // actions atomically instead of persisting a partial routine.
        if (requested.Any(action => (action?.Kind?.Trim().ToLowerInvariant() is "url" or "website" or "link")
            && !BrowserLauncherService.TryNormalizeUrl(action.Target, out _)))
            throw new InvalidOperationException("A workflow website is not a valid web link; nothing was saved.");
        var cleaned = requested
            .Select(CleanWorkflowAction)
            .ToList();
        if (cleaned.Any(action => action == null))
            throw new InvalidOperationException("A workflow action is invalid; nothing was saved.");
        return cleaned;
    }

    internal static bool IsUnresolvedWorkflowTarget(string target) => Regex.IsMatch(
        (target ?? "").Trim(),
        @"^(?:use_selected|(?:the\s+)?(?:selected|highlighted)\s+(?:link|url|website|site|destination|text|file|folder|path)|(?:this|that)\s+(?:(?:selected|highlighted)\s+)?(?:link|url|website|site|destination|file|folder|path))$",
        RegexOptions.IgnoreCase);

    private static JarvisWorkflowActionSettings CleanWorkflowAction(
        JarvisWorkflowActionSettings action)
    {
        var kind = (action?.Kind ?? "").Trim().ToLowerInvariant() switch
        {
            "application" or "native" or "native_app" => "app",
            "website" or "link" => "url",
            "directory" => "folder",
            "automatic" => "auto",
            var value => value,
        };
        if (kind is not ("app" or "auto" or "url" or "folder"))
            return null;
        var maxLength = kind == "url" ? 800 : kind == "folder" ? 260 : 120;
        var target = CleanInline(action.Target, maxLength);
        if (string.IsNullOrWhiteSpace(target))
            return null;
        if (kind == "url")
        {
            var candidate = target.Contains("://", StringComparison.Ordinal)
                ? target
                : "https://" + target;
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return null;
            target = uri.AbsoluteUri;
        }
        return new JarvisWorkflowActionSettings { Kind = kind, Target = target };
    }

    private static void SynchronizeLegacyWorkflowFields(JarvisWorkflowSettings workflow)
    {
        workflow.Apps = CleanInline(string.Join(", ", workflow.Actions
            .Where(action => action.Kind is "app" or "auto")
            .Select(action => action.Target)), 300);
        workflow.Urls = CleanInline(string.Join(", ", workflow.Actions
            .Where(action => action.Kind == "url")
            .Select(action => action.Target)), 800);
        workflow.Folder = CleanInline(workflow.Actions
            .FirstOrDefault(action => action.Kind == "folder")?.Target, 260);
    }

    private static IEnumerable<string> SplitWorkflowItems(string value) =>
        (value ?? "").Split(
            [',', ';', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
