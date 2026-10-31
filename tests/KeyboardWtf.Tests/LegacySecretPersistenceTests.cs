using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KeyboardWtf.Helpers;
using KeyboardWtf.Models;
using KeyboardWtf.Services;
using Xunit;

namespace KeyboardWtf.Tests;

// SettingsService.Load publishes process-wide runtime settings. These fixtures
// restore every field it publishes and may not run beside another collection.
[CollectionDefinition("Legacy secret persistence", DisableParallelization = true)]
public sealed class LegacySecretPersistenceCollection { }

[Collection("Legacy secret persistence")]
public sealed class LegacySecretPersistenceTests
{
    [Fact]
    public void Popup_preference_defaults_on_for_legacy_settings_and_persists_off_on_and_reload()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.SettingsPath, "{\"AssistantName\":\"Fixture owner\",\"Language\":\"preserve-language\"}");
        var settings = fixture.CreateSettings(SecureStore.Protect);
        settings.Load();
        Assert.True(settings.Current.ShowQuickActionButton);
        settings.SaveShowQuickActionButton(false);
        Assert.False(settings.Current.ShowQuickActionButton);
        var reloaded = fixture.CreateSettings(SecureStore.Protect);
        reloaded.Load();
        Assert.False(reloaded.Current.ShowQuickActionButton);
        Assert.Equal("Fixture owner", reloaded.Current.AssistantName);
        Assert.Equal("preserve-language", reloaded.Current.Language);
        reloaded.SaveShowQuickActionButton(true);
        var enabled = fixture.CreateSettings(SecureStore.Protect);
        enabled.Load();
        Assert.True(enabled.Current.ShowQuickActionButton);
    }

    [Fact]
    public void Popup_preference_disk_failure_preserves_current_and_original_file_then_recovers()
    {
        using var fixture = new Fixture();
        var settings = fixture.CreateSettings(SecureStore.Protect);
        settings.Save();
        var original = File.ReadAllBytes(fixture.SettingsPath);
        using (var held = new FileStream(fixture.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Throws<SecretPersistenceException>(() => settings.SaveShowQuickActionButton(false));
            Assert.True(settings.Current.ShowQuickActionButton);
            Assert.Equal(original, File.ReadAllBytes(fixture.SettingsPath));
            Assert.False(File.Exists(fixture.SettingsPath + ".tmp"));
        }
        settings.SaveShowQuickActionButton(false);
        Assert.False(settings.Current.ShowQuickActionButton);
        using var saved = JsonDocument.Parse(File.ReadAllText(fixture.SettingsPath));
        Assert.False(saved.RootElement.GetProperty("ShowQuickActionButton").GetBoolean());
    }

    private static readonly string[] SecretFields =
    [
        nameof(AppSettings.ClaudeApiKey), nameof(AppSettings.OpenAiApiKey),
        nameof(AppSettings.GeminiApiKey), nameof(AppSettings.DeepSeekApiKey),
        nameof(AppSettings.PerplexityApiKey), nameof(AppSettings.SlackWebhookUrl),
        nameof(AppSettings.DiscordWebhookUrl),
    ];

    public static IEnumerable<object[]> Secrets() => SecretFields.Select(field => new object[] { field });

    public static IEnumerable<object[]> EmptySecrets() => SecretFields.SelectMany(field =>
        new[] { new object[] { field, "" }, new object[] { field, null } });

    public static IEnumerable<object[]> CorruptSecrets() => SecretFields.SelectMany(field =>
        new[]
        {
            new object[] { field, "enc:v1:not valid base64!" },
            new object[] { field, "enc:v1:" + Convert.ToBase64String(new byte[] { 1, 2, 3, 4, 5 }) },
        });

    [Theory]
    [MemberData(nameof(Secrets))]
    public void Protection_failure_does_not_change_file_current_or_runtime_secret(string field)
    {
        using var fixture = new Fixture();
        var original = fixture.CreateBaseline(field);
        var replacement = "synthetic-new-" + field;
        var calls = new List<string>();
        var settings = fixture.CreateSettings(value =>
        {
            calls.Add(value);
            throw new CryptographicException("Synthetic encryption failure contains " + value);
        });
        fixture.SetCurrentSecret(settings, field, original.Stored);
        SetRuntimeSecret(field, original.Plaintext);

        var failure = Record.Exception(() => SaveSecret(settings, field, replacement));

        Assert.NotNull(failure);
        Assert.Equal(new[] { replacement }, calls);
        Assert.DoesNotContain(replacement, failure.ToString());
        Assert.Equal(original.Bytes, File.ReadAllBytes(fixture.SettingsPath));
        Assert.Equal(original.Stored, GetCurrentSecret(settings, field));
        Assert.Equal(original.Plaintext, GetRuntimeSecret(field));
        Assert.False(File.Exists(fixture.SettingsPath + ".tmp"));
    }

    [Theory]
    [MemberData(nameof(Secrets))]
    public void Disk_failure_does_not_activate_or_retain_a_replacement_secret(string field)
    {
        using var fixture = new Fixture();
        var original = fixture.CreateBaseline(field);
        var replacement = "synthetic-disk-new-" + field;
        var protectionWasCalled = false;
        var settings = fixture.CreateSettings(value =>
        {
            protectionWasCalled = true;
            return SecureStore.Protect(value);
        });
        fixture.SetCurrentSecret(settings, field, original.Stored);
        SetRuntimeSecret(field, original.Plaintext);

        // Windows denies atomic replacement while this existing destination is
        // open without FileShare.Delete. The original remains readable.
        using (var locked = new FileStream(fixture.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var failure = Record.Exception(() => SaveSecret(settings, field, replacement));
            Assert.NotNull(failure);
            Assert.True(protectionWasCalled);
            Assert.DoesNotContain(replacement, failure.ToString());
            Assert.Equal(original.Bytes, File.ReadAllBytes(fixture.SettingsPath));
            Assert.Equal(original.Stored, GetCurrentSecret(settings, field));
            Assert.Equal(original.Plaintext, GetRuntimeSecret(field));
            Assert.False(File.Exists(fixture.SettingsPath + ".tmp"));
        }

        // A failed attempt must not leave a poisoned future save or temporary
        // file. Once storage is available, the same new value may be committed.
        SaveSecret(settings, field, replacement);
        Assert.Equal(replacement, GetRuntimeSecret(field));
        Assert.Equal(replacement, SecureStore.Unprotect(fixture.ReadStoredSecret(field)));
    }

    [Theory]
    [MemberData(nameof(Secrets))]
    public void User_input_starting_with_ciphertext_prefix_is_still_protected(string field)
    {
        using var fixture = new Fixture();
        var input = "enc:v1:synthetic-user-input-" + field;
        var calls = new List<string>();
        var settings = fixture.CreateSettings(value =>
        {
            calls.Add(value);
            return SecureStore.Protect(value);
        });

        SaveSecret(settings, field, input);

        Assert.Equal(new[] { input }, calls);
        var persisted = fixture.ReadStoredSecret(field);
        Assert.NotEqual(input, persisted);
        Assert.StartsWith(SecureStore.EncryptedPrefix, persisted);
        Assert.Equal(input, SecureStore.Unprotect(persisted));
        Assert.Equal(input, GetRuntimeSecret(field));
    }

    [Theory]
    [MemberData(nameof(Secrets))]
    public void A_protector_returning_plaintext_is_rejected_without_saving(string field)
    {
        using var fixture = new Fixture();
        var original = fixture.CreateBaseline(field);
        var replacement = "synthetic-plaintext-replacement-" + field;
        var settings = fixture.CreateSettings(value => value);
        fixture.SetCurrentSecret(settings, field, original.Stored);
        SetRuntimeSecret(field, original.Plaintext);

        var failure = Record.Exception(() => SaveSecret(settings, field, replacement));

        Assert.NotNull(failure);
        Assert.DoesNotContain(replacement, failure.ToString());
        Assert.Equal(original.Bytes, File.ReadAllBytes(fixture.SettingsPath));
        Assert.Equal(original.Stored, GetCurrentSecret(settings, field));
        Assert.Equal(original.Plaintext, GetRuntimeSecret(field));
        Assert.False(File.Exists(fixture.SettingsPath + ".tmp"));
    }

    [Fact]
    public void Save_protects_all_legacy_plaintext_before_writing_and_does_not_partially_mutate_current()
    {
        using var fixture = new Fixture();
        var originalBytes = fixture.WriteSettings(new AppSettings { Language = "original-file-sentinel" });
        var inputs = SecretFields.Select(field => "synthetic-legacy-" + field).ToArray();
        var calls = new List<string>();
        var settings = fixture.CreateSettings(value =>
        {
            calls.Add(value);
            if (value == inputs[^1])
                throw new CryptographicException("Synthetic last-field failure " + value);
            return SecureStore.Protect(value);
        });
        for (var index = 0; index < SecretFields.Length; index++)
            fixture.SetCurrentSecret(settings, SecretFields[index], inputs[index]);

        var failure = Record.Exception(settings.Save);

        Assert.NotNull(failure);
        Assert.Equal(inputs, calls);
        Assert.DoesNotContain(inputs[^1], failure.ToString());
        Assert.Equal(originalBytes, File.ReadAllBytes(fixture.SettingsPath));
        Assert.False(File.Exists(fixture.SettingsPath + ".tmp"));
        for (var index = 0; index < SecretFields.Length; index++)
            Assert.Equal(inputs[index], GetCurrentSecret(settings, SecretFields[index]));
    }

    [Fact]
    public void Unrelated_save_cannot_write_a_legacy_plaintext_secret_when_protection_fails()
    {
        using var fixture = new Fixture();
        var originalBytes = fixture.WriteSettings(new AppSettings { Language = "original-sentinel" });
        var legacy = "synthetic-legacy-discord-webhook";
        var settings = fixture.CreateSettings(value => throw new CryptographicException("Synthetic unavailable DPAPI"));
        fixture.SetCurrentSecret(settings, nameof(AppSettings.DiscordWebhookUrl), legacy);
        settings.Current.Language = "unrelated-change";

        Assert.NotNull(Record.Exception(settings.Save));

        Assert.Equal(originalBytes, File.ReadAllBytes(fixture.SettingsPath));
        Assert.Equal(legacy, settings.Current.DiscordWebhookUrl);
        Assert.False(File.Exists(fixture.SettingsPath + ".tmp"));
    }

    [Fact]
    public void Disk_failure_rolls_back_all_staged_legacy_ciphertext_and_preserves_file()
    {
        using var fixture = new Fixture();
        var originalBytes = fixture.WriteSettings(new AppSettings());
        var settings = fixture.CreateSettings(SecureStore.Protect);
        foreach (var field in SecretFields)
            fixture.SetCurrentSecret(settings, field, "synthetic-staged-" + field);

        using var locked = new FileStream(fixture.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.NotNull(Record.Exception(settings.Save));

        Assert.Equal(originalBytes, File.ReadAllBytes(fixture.SettingsPath));
        foreach (var field in SecretFields)
            Assert.Equal("synthetic-staged-" + field, GetCurrentSecret(settings, field));
        Assert.False(File.Exists(fixture.SettingsPath + ".tmp"));
    }

    [Fact]
    public void Successful_save_commits_all_legacy_secrets_encrypted_once()
    {
        using var fixture = new Fixture();
        var calls = new List<string>();
        var settings = fixture.CreateSettings(value =>
        {
            calls.Add(value);
            return SecureStore.Protect(value);
        });
        foreach (var field in SecretFields)
            fixture.SetCurrentSecret(settings, field, "synthetic-save-" + field);

        settings.Save();

        Assert.Equal(SecretFields.Length, calls.Count);
        foreach (var field in SecretFields)
        {
            var stored = fixture.ReadStoredSecret(field);
            Assert.StartsWith(SecureStore.EncryptedPrefix, stored);
            Assert.Equal("synthetic-save-" + field, SecureStore.Unprotect(stored));
            Assert.Equal(stored, GetCurrentSecret(settings, field));
        }
        Assert.False(File.Exists(fixture.SettingsPath + ".tmp"));

        settings.Save();
        Assert.Equal(SecretFields.Length, calls.Count); // Existing ciphertext is not encrypted a second time.
    }

    [Theory]
    [MemberData(nameof(Secrets))]
    public void A_successful_secret_setter_commits_encrypted_value_before_runtime_success(string field)
    {
        using var fixture = new Fixture();
        var original = fixture.CreateBaseline(field);
        var replacement = "synthetic-success-" + field;
        SetRuntimeSecret(field, original.Plaintext);
        var settings = fixture.CreateSettings(value =>
        {
            Assert.Equal(original.Plaintext, GetRuntimeSecret(field));
            return SecureStore.Protect(value);
        });
        fixture.SetCurrentSecret(settings, field, original.Stored);

        SaveSecret(settings, field, replacement);

        var stored = fixture.ReadStoredSecret(field);
        Assert.StartsWith(SecureStore.EncryptedPrefix, stored);
        Assert.Equal(replacement, SecureStore.Unprotect(stored));
        Assert.Equal(stored, GetCurrentSecret(settings, field));
        Assert.Equal(replacement, GetRuntimeSecret(field));
        Assert.DoesNotContain(replacement, File.ReadAllText(fixture.SettingsPath));
        Assert.False(File.Exists(fixture.SettingsPath + ".tmp"));
    }

    [Theory]
    [MemberData(nameof(EmptySecrets))]
    public void Empty_secret_clearing_does_not_require_encryption_and_is_committed(string field, string empty)
    {
        using var fixture = new Fixture();
        var original = fixture.CreateBaseline(field);
        var settings = fixture.CreateSettings(value => throw new InvalidOperationException("Empty values must not need DPAPI"));
        fixture.SetCurrentSecret(settings, field, original.Stored);
        SetRuntimeSecret(field, original.Plaintext);

        SaveSecret(settings, field, empty);

        Assert.True(string.IsNullOrEmpty(fixture.ReadStoredSecret(field)));
        Assert.True(string.IsNullOrEmpty(GetCurrentSecret(settings, field)));
        Assert.True(string.IsNullOrEmpty(GetRuntimeSecret(field)));
        Assert.False(File.Exists(fixture.SettingsPath + ".tmp"));
    }

    [Fact]
    public void Startup_with_unavailable_protection_preserves_original_legacy_file_and_readable_runtime_values()
    {
        using var fixture = new Fixture();
        var legacySettings = new AppSettings();
        foreach (var field in SecretFields)
            SetSecret(legacySettings, field, "synthetic-startup-" + field);
        var originalBytes = fixture.WriteSettings(legacySettings);
        var settings = fixture.CreateSettings(value =>
        {
            if (value.EndsWith(nameof(AppSettings.DiscordWebhookUrl), StringComparison.Ordinal))
                throw new CryptographicException("Synthetic startup migration failure");
            return SecureStore.Protect(value);
        });

        settings.Load();

        Assert.False(settings.IsFirstRun);
        Assert.Equal(originalBytes, File.ReadAllBytes(fixture.SettingsPath));
        Assert.False(File.Exists(fixture.SettingsPath + ".tmp"));
        foreach (var field in SecretFields)
        {
            Assert.Equal("synthetic-startup-" + field, GetCurrentSecret(settings, field));
            Assert.Equal("synthetic-startup-" + field, GetRuntimeSecret(field));
        }
    }

    [Theory]
    [MemberData(nameof(Secrets))]
    public void Retired_entropy_ciphertext_is_not_destroyed_when_its_upgrade_cannot_be_protected(string field)
    {
        using var fixture = new Fixture();
        var plaintext = "synthetic-retired-" + field;
        var previousEntropy = Convert.FromBase64String("TG91cGVkZWNrLlZveFJpbmdQbHVnaW4uU2VjcmV0LnYx");
        var ciphertext = SecureStore.EncryptedPrefix + Convert.ToBase64String(
            ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), previousEntropy, DataProtectionScope.CurrentUser));
        var baseline = new AppSettings();
        SetSecret(baseline, field, ciphertext);
        var originalBytes = fixture.WriteSettings(baseline);
        Assert.Equal(plaintext, SecureStore.Unprotect(ciphertext, out var requiresResave));
        Assert.True(requiresResave);
        var settings = fixture.CreateSettings(value => throw new CryptographicException("Synthetic retired-entropy migration failure"));

        settings.Load();

        Assert.Equal(originalBytes, File.ReadAllBytes(fixture.SettingsPath));
        Assert.Equal(ciphertext, GetCurrentSecret(settings, field));
        Assert.Equal(plaintext, GetRuntimeSecret(field));
        Assert.False(File.Exists(fixture.SettingsPath + ".tmp"));
    }

    [Theory]
    [MemberData(nameof(CorruptSecrets))]
    public void Unreadable_ciphertext_is_preserved_and_never_replaced_with_an_empty_value(string field, string ciphertext)
    {
        using var fixture = new Fixture();
        var baseline = new AppSettings();
        SetSecret(baseline, field, ciphertext);
        fixture.WriteSettings(baseline);
        var protectorCalls = new List<string>();
        var settings = fixture.CreateSettings(value =>
        {
            protectorCalls.Add(value);
            return SecureStore.Protect(value);
        });

        settings.Load();

        Assert.Empty(GetRuntimeSecret(field));
        Assert.Equal(ciphertext, GetCurrentSecret(settings, field));
        Assert.Equal(ciphertext, fixture.ReadStoredSecret(field));
        Assert.Empty(protectorCalls);
        Assert.False(File.Exists(fixture.SettingsPath + ".tmp"));
    }

    [Fact]
    public void Protector_failure_is_scoped_to_one_instance_not_a_global_hook()
    {
        using var fixture = new Fixture();
        var firstPath = Path.Combine(fixture.Root, "first.json");
        var secondPath = Path.Combine(fixture.Root, "second.json");
        var first = new SettingsService(firstPath, value => throw new CryptographicException("Synthetic instance-only failure"));
        var second = new SettingsService(secondPath, SecureStore.Protect);

        Assert.NotNull(Record.Exception(() => first.SaveGeminiApiKey("synthetic-first")));
        second.SaveGeminiApiKey("synthetic-second");

        Assert.False(File.Exists(firstPath));
        using var committed = JsonDocument.Parse(File.ReadAllText(secondPath));
        var stored = committed.RootElement.GetProperty(nameof(AppSettings.GeminiApiKey)).GetString();
        Assert.Equal("synthetic-second", SecureStore.Unprotect(stored));
        Assert.Equal("synthetic-second", KeyboardWtfState.GeminiApiKey);
        Assert.False(File.Exists(firstPath + ".tmp"));
        Assert.False(File.Exists(secondPath + ".tmp"));
    }

    [Fact]
    public void Malformed_settings_load_preserves_original_bytes_and_reports_storage_warning()
    {
        using var fixture = new Fixture();
        var original = Encoding.UTF8.GetBytes("{\"GeminiApiKey\":\"synthetic-unfinished-json");
        File.WriteAllBytes(fixture.SettingsPath, original);
        var settings = fixture.CreateSettings(SecureStore.Protect);

        settings.Load();

        Assert.False(settings.IsFirstRun);
        Assert.Equal(original, File.ReadAllBytes(fixture.SettingsPath));
        Assert.False(File.Exists(fixture.SettingsPath + ".tmp"));
        Assert.False(string.IsNullOrWhiteSpace(settings.SettingsStorageWarning));
        Assert.DoesNotContain("synthetic-unfinished-json", settings.SettingsStorageWarning);
        Assert.True(string.IsNullOrEmpty(KeyboardWtfState.GeminiApiKey));
    }

    [Fact]
    public void Successful_load_migrates_all_retired_entropy_secrets_once_and_ordinary_save_does_not_reprotect()
    {
        using var fixture = new Fixture();
        var previousEntropy = Convert.FromBase64String("TG91cGVkZWNrLlZveFJpbmdQbHVnaW4uU2VjcmV0LnYx");
        var baseline = new AppSettings();
        var retired = new Dictionary<string, string>();
        foreach (var field in SecretFields)
        {
            var plaintext = "synthetic-migrate-success-" + field;
            var ciphertext = SecureStore.EncryptedPrefix + Convert.ToBase64String(
                ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), previousEntropy, DataProtectionScope.CurrentUser));
            retired[field] = ciphertext;
            SetSecret(baseline, field, ciphertext);
        }
        fixture.WriteSettings(baseline);
        var protectionCalls = new List<string>();
        var settings = fixture.CreateSettings(value =>
        {
            protectionCalls.Add(value);
            return SecureStore.Protect(value);
        });

        settings.Load();

        Assert.Equal(SecretFields.Select(field => "synthetic-migrate-success-" + field), protectionCalls);
        Assert.Empty(settings.SettingsStorageWarning);
        var migrated = new Dictionary<string, string>();
        foreach (var field in SecretFields)
        {
            var stored = fixture.ReadStoredSecret(field);
            migrated[field] = stored;
            Assert.NotEqual(retired[field], stored);
            Assert.Equal("synthetic-migrate-success-" + field, SecureStore.Unprotect(stored, out var requiresResave));
            Assert.False(requiresResave);
            Assert.Equal(stored, GetCurrentSecret(settings, field));
            Assert.Equal("synthetic-migrate-success-" + field, GetRuntimeSecret(field));
        }

        settings.Save();

        Assert.Equal(SecretFields.Length, protectionCalls.Count);
        foreach (var field in SecretFields)
            Assert.Equal(migrated[field], fixture.ReadStoredSecret(field));
        Assert.False(File.Exists(fixture.SettingsPath + ".tmp"));
    }

    [Fact]
    public void Storage_warning_survives_failed_attempts_and_clears_only_after_successful_commit()
    {
        using var fixture = new Fixture();
        var original = fixture.CreateBaseline(nameof(AppSettings.GeminiApiKey));
        var protectionAvailable = false;
        var settings = fixture.CreateSettings(value => protectionAvailable
            ? SecureStore.Protect(value)
            : throw new CryptographicException("Synthetic warning-path failure"));
        fixture.SetCurrentSecret(settings, nameof(AppSettings.GeminiApiKey), original.Stored);
        SetRuntimeSecret(nameof(KeyboardWtfState.GeminiApiKey), original.Plaintext);

        Assert.NotNull(Record.Exception(() => settings.SaveGeminiApiKey("synthetic-warning-new")));
        Assert.Equal(SecretPersistenceException.SafeMessage, settings.SettingsStorageWarning);
        Assert.Equal(original.Bytes, File.ReadAllBytes(fixture.SettingsPath));

        Assert.NotNull(Record.Exception(() => settings.SaveGeminiApiKey("synthetic-warning-new")));
        Assert.Equal(SecretPersistenceException.SafeMessage, settings.SettingsStorageWarning);
        Assert.Equal(original.Bytes, File.ReadAllBytes(fixture.SettingsPath));

        protectionAvailable = true;
        settings.SaveGeminiApiKey("synthetic-warning-new");

        Assert.Empty(settings.SettingsStorageWarning);
        Assert.Equal("synthetic-warning-new", SecureStore.Unprotect(fixture.ReadStoredSecret(nameof(AppSettings.GeminiApiKey))));
        Assert.Equal("synthetic-warning-new", KeyboardWtfState.GeminiApiKey);
    }

    [Fact]
    public void Optional_environment_key_import_failure_does_not_abort_startup_or_activate_an_unsaved_key()
    {
        using var fixture = new Fixture();
        const string environmentName = "KEYBOARD_WTF_GEMINI_API_KEY";
        const string incomingKey = "synthetic-environment-key-never-persisted";
        var originalEnvironmentValue = Environment.GetEnvironmentVariable(environmentName);
        var protectionCalls = new List<string>();
        var settings = fixture.CreateSettings(value =>
        {
            protectionCalls.Add(value);
            throw new CryptographicException("Synthetic environment-import failure contains " + value);
        });
        settings.Current.AiProvider = AiProvider.Claude;
        KeyboardWtfState.SelectedAiProvider = AiProvider.Claude;
        KeyboardWtfState.GeminiApiKey = "";
        var notices = new List<(string Title, string Message, System.Windows.Forms.ToolTipIcon Icon)>();

        // Skip every app constructor/field initializer: no native microphone,
        // hotkeys, window, real settings path, paid account or helper is created.
        var app = (KeyboardWtfApp)RuntimeHelpers.GetUninitializedObject(typeof(KeyboardWtfApp));
        typeof(KeyboardWtfApp).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(app, settings);
        typeof(KeyboardWtfApp).GetField("_notifications", BindingFlags.Instance | BindingFlags.NonPublic)
            .SetValue(app, NotificationService.CreateForTesting((title, message, icon) => notices.Add((title, message, icon))));
        var import = typeof(KeyboardWtfApp).GetMethod("ApplySecretEnvironmentOverrides", BindingFlags.Instance | BindingFlags.NonPublic);
        try
        {
            Environment.SetEnvironmentVariable(environmentName, incomingKey);

            var failure = Record.Exception(() => import.Invoke(app, null));

            Assert.Null(failure);
            Assert.Equal(new[] { incomingKey }, protectionCalls);
            Assert.False(File.Exists(fixture.SettingsPath));
            Assert.False(File.Exists(fixture.SettingsPath + ".tmp"));
            Assert.True(string.IsNullOrEmpty(settings.Current.GeminiApiKey));
            Assert.Empty(KeyboardWtfState.GeminiApiKey);
            Assert.Equal(AiProvider.Claude, settings.Current.AiProvider);
            Assert.Equal(AiProvider.Claude, KeyboardWtfState.SelectedAiProvider);
            var notice = Assert.Single(notices);
            Assert.False(string.IsNullOrWhiteSpace(notice.Title));
            Assert.Equal(SecretPersistenceException.SafeMessage, notice.Message);
            Assert.DoesNotContain(incomingKey, notice.Message);
            Assert.True(notice.Icon is System.Windows.Forms.ToolTipIcon.Error or System.Windows.Forms.ToolTipIcon.Warning);
        }
        finally
        {
            Environment.SetEnvironmentVariable(environmentName, originalEnvironmentValue);
            // Do not Dispose an uninitialized app; it owns no native objects.
        }
    }

    private static string GetCurrentSecret(SettingsService settings, string field) =>
        (string)typeof(AppSettings).GetProperty(field).GetValue(settings.Current);

    private static void SetSecret(AppSettings settings, string field, string value) =>
        typeof(AppSettings).GetProperty(field).SetValue(settings, value);

    private static string GetRuntimeSecret(string field) =>
        (string)typeof(KeyboardWtfState).GetProperty(field).GetValue(null);

    private static void SetRuntimeSecret(string field, string value) =>
        typeof(KeyboardWtfState).GetProperty(field).SetValue(null, value);

    private static void SaveSecret(SettingsService settings, string field, string value)
    {
        switch (field)
        {
            case nameof(AppSettings.ClaudeApiKey): settings.SaveClaudeApiKey(value); break;
            case nameof(AppSettings.OpenAiApiKey): settings.SaveOpenAiApiKey(value); break;
            case nameof(AppSettings.GeminiApiKey): settings.SaveGeminiApiKey(value); break;
            case nameof(AppSettings.DeepSeekApiKey): settings.SaveDeepSeekApiKey(value); break;
            case nameof(AppSettings.PerplexityApiKey): settings.SavePerplexityApiKey(value); break;
            case nameof(AppSettings.SlackWebhookUrl): settings.SaveSlackWebhookUrl(value); break;
            case nameof(AppSettings.DiscordWebhookUrl): settings.SaveDiscordWebhookUrl(value); break;
            default: throw new ArgumentOutOfRangeException(nameof(field));
        }
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };

        private static readonly string[] RuntimeFields =
        [
            nameof(KeyboardWtfState.SelectedEngine), nameof(KeyboardWtfState.SelectedLanguage),
            nameof(KeyboardWtfState.SelectedMicrophoneIndex), nameof(KeyboardWtfState.MaxRecordingSeconds),
            nameof(KeyboardWtfState.SelectedWhisperModel), nameof(KeyboardWtfState.SelectedAiProvider),
            nameof(KeyboardWtfState.ClaudeApiKey), nameof(KeyboardWtfState.OpenAiApiKey),
            nameof(KeyboardWtfState.GeminiApiKey), nameof(KeyboardWtfState.DeepSeekApiKey),
            nameof(KeyboardWtfState.PerplexityApiKey), nameof(KeyboardWtfState.SlackWebhookUrl),
            nameof(KeyboardWtfState.DiscordWebhookUrl), nameof(KeyboardWtfState.VoiceNoteSavePath),
            nameof(KeyboardWtfState.VoiceNoteFilenamePattern), nameof(KeyboardWtfState.UseNoiseGate),
            nameof(KeyboardWtfState.UseAi), nameof(KeyboardWtfState.TranslateTargetLanguage),
            nameof(KeyboardWtfState.UseFillerWordCleaner), nameof(KeyboardWtfState.SelectedCaseTransform),
            nameof(KeyboardWtfState.AssistantName), nameof(KeyboardWtfState.AssistantTone),
            nameof(KeyboardWtfState.GeminiVoice),
        ];

        private readonly Dictionary<PropertyInfo, object> _runtime = RuntimeFields.ToDictionary(
            name => typeof(KeyboardWtfState).GetProperty(name),
            name => typeof(KeyboardWtfState).GetProperty(name).GetValue(null));
        private readonly Dictionary<string, string> _customPrompts = new(KeyboardWtfState.CustomPrompts);

        public Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "keyboard-wtf-legacy-secrets-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            SettingsPath = Path.Combine(Root, "settings.json");
        }

        public string Root { get; }
        public string SettingsPath { get; }

        public SettingsService CreateSettings(Func<string, string> protector) => new(SettingsPath, protector);

        public void SetCurrentSecret(SettingsService settings, string field, string value) => SetSecret(settings.Current, field, value);

        public (string Plaintext, string Stored, byte[] Bytes) CreateBaseline(string field)
        {
            var plaintext = "synthetic-old-" + field;
            var stored = SecureStore.Protect(plaintext);
            var settings = new AppSettings { Language = "preserve-unrelated-sentinel" };
            SetSecret(settings, field, stored);
            return (plaintext, stored, WriteSettings(settings));
        }

        public byte[] WriteSettings(AppSettings settings)
        {
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
            return File.ReadAllBytes(SettingsPath);
        }

        public string ReadStoredSecret(string field)
        {
            using var committed = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            return committed.RootElement.GetProperty(field).GetString();
        }

        public void Dispose()
        {
            foreach (var (property, value) in _runtime)
                property.SetValue(null, value);
            KeyboardWtfState.CustomPrompts.Clear();
            foreach (var (key, value) in _customPrompts)
                KeyboardWtfState.CustomPrompts[key] = value;

            // The only recursive target is this fixture's explicit GUID-named
            // direct child of Temp; never an environment or profile root.
            var resolved = Path.GetFullPath(Root);
            if (Path.GetDirectoryName(resolved) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
                || !Path.GetFileName(resolved).StartsWith("keyboard-wtf-legacy-secrets-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside the owned fixture directory.");
            Directory.Delete(resolved, recursive: true);
        }
    }
}
