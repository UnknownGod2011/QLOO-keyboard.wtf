namespace KeyboardWtf.Services.Ai.Backends;

using System.Text.Json;
using System.Text.Json.Serialization;

// Deliberately no key/secret/label property: browser requests only identify actions.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class PersonalSettingsAction
{
    public string Action { get; set; }
    public long Revision { get; set; }
    public Guid Id { get; set; }
    public long KeyRevision { get; set; }
    public bool Enabled { get; set; }
    public AiBackendPolicy Policy { get; set; }
}

internal sealed record PersonalSettingsResult(bool Ok, string Message, PersonalApiView State = null);

internal sealed class PersonalKeyEntry(string label, string secret) : IDisposable
{
    internal string Label { get; } = label;
    internal string Secret { get; private set; } = secret;
    public void Dispose() => Secret = null;
    public override string ToString() => "Personal key entry (redacted)";
}

internal interface IPersonalKeyEntryDialog
{
    Task<PersonalKeyEntry> ShowAsync(bool replacing, CancellationToken token);
    Task<bool> ConfirmResetAsync(CancellationToken token);
    bool RevealPending();
}

/// <summary>Protected Settings adapter. Only native entry and the credential owner see secrets.</summary>
internal sealed class PersonalApiSettingsController(PersonalApiVault vault,
    PersonalGeminiKeyValidator validator, IPersonalKeyEntryDialog dialog, bool forcePersonalOnly = false)
{
    private readonly SemaphoreSlim _mutation = new(1, 1);

    internal PersonalSettingsResult GetState()
    {
        try { return new(true, "", vault.GetView()); }
        catch (PersonalApiConfigurationException) { return new(false, ConfigurationError); }
    }

    internal async Task<PersonalSettingsResult> ExecuteAsync(string json, CancellationToken token)
    {
        if (json is null || json.Length > 2048) return new(false, "Invalid Personal API Settings request.");
        PersonalSettingsAction request;
        try
        {
            request = JsonSerializer.Deserialize<PersonalSettingsAction>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return new(false, "Invalid Personal API Settings request. Keys must be entered in the native dialog.");
        }
        if (request is null) return new(false, "Invalid Personal API Settings request.");
        if (request.Action == "reveal")
            return dialog.RevealPending()
                ? new(true, "The existing native Personal API dialog was brought to the front.")
                : new(false, "No native Personal API dialog is currently open.");
        if (!await _mutation.WaitAsync(0, token))
            return dialog.RevealPending()
                ? new(false, "A native Personal API dialog is already in progress; it was brought to the front.")
                : new(false, "Another Personal API Settings action is in progress.");
        try
        {
            token.ThrowIfCancellationRequested();
            if (request.Action == "reset")
            {
                if (!await dialog.ConfirmResetAsync(token)) return new(false, "Reset cancelled.");
                token.ThrowIfCancellationRequested();
                var reset = vault.ResetExplicitly();
                if (forcePersonalOnly && reset.Policy != AiBackendPolicy.PersonalOnly)
                    reset = vault.SetPolicy(reset.Revision, AiBackendPolicy.PersonalOnly);
                return new(true, forcePersonalOnly
                    ? "Personal API keys were reset. QLOO continues to use the Personal API backend."
                    : "Personal configuration reset to Managed; saved Personal keys were removed.", reset);
            }

            var current = vault.GetView();
            if (current.Revision != request.Revision) return new(false, "Personal API settings changed; reload this section.", current);
            switch (request.Action)
            {
                case "policy":
                    if (forcePersonalOnly && request.Policy != AiBackendPolicy.PersonalOnly)
                        return new(false, "QLOO always uses the Gemini Personal API backend.", current);
                    return new(true, "Backend policy saved for the next operation or Jarvis session.", vault.SetPolicy(request.Revision, request.Policy));
                case "remove":
                    return new(true, "Personal key removed.", vault.Remove(request.Revision, request.Id));
                case "enabled":
                    return new(true, "Personal key state saved.", vault.SetEnabled(request.Revision, request.Id, request.Enabled));
                case "add":
                case "replace":
                {
                    var replacing = request.Action == "replace";
                    if (!replacing && current.Keys.Count >= PersonalApiVault.MaximumKeys)
                        return new(false, "At most ten Personal API keys are allowed.", current);
                    if (replacing && !current.Keys.Any(row => row.Id == request.Id && row.Revision == request.KeyRevision))
                        return new(false, "This key changed; reload this section.", current);
                    using var entry = await dialog.ShowAsync(replacing, token);
                    if (entry is null) return new(false, "Key entry cancelled.", current);
                    var validation = await validator.ValidateAsync(entry.Secret, token);
                    token.ThrowIfCancellationRequested();
                    var state = replacing
                        ? vault.Replace(request.Revision, request.Id, request.KeyRevision, entry.Secret, validation.Status)
                        : vault.Add(request.Revision, entry.Label, entry.Secret, validation.Status);
                    return new(true, validation.Message + (validation.Status == PersonalKeyValidation.AuthenticationValidated
                        ? "" : " The new key is saved inactive."), state);
                }
                case "validate":
                {
                    using var lease = vault.AcquireForValidation(request.Revision, request.Id, request.KeyRevision);
                    var validation = await validator.ValidateAsync(vault.ReadSecretForTransport(lease), token);
                    token.ThrowIfCancellationRequested();
                    if (!vault.ApplyValidation(request.Revision, request.Id, request.KeyRevision, validation.Status))
                        return new(false, "Settings changed; the stale validation result was discarded.", vault.GetView());
                    return new(true, validation.Message, vault.GetView());
                }
                default: return new(false, "Unsupported Personal API Settings action.");
            }
        }
        catch (PersonalApiConfigurationException) { return new(false, ConfigurationError); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return new(false, "Personal API Settings action cancelled."); }
        catch (JsonException) { return new(false, "Invalid Personal API Settings request. Keys must be entered in the native dialog."); }
        catch (ArgumentException) { return new(false, "Invalid Personal API input. Use a valid policy, a short label and a key without spaces or control characters."); }
        catch (InvalidOperationException)
        {
            // Never forward arbitrary exceptions from UI, storage or transport.
            return new(false, "The change was not saved. End active Personal work, check validation and reload this section. Existing data was retained.");
        }
        finally { _mutation.Release(); }
    }

    private const string ConfigurationError = "Personal API configuration is unavailable or damaged. AI routing is blocked until repaired or explicitly reset; no managed fallback occurred.";
}
