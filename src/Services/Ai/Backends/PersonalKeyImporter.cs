namespace KeyboardWtf.Services.Ai.Backends;

using System.Net;
using System.Text;

/// <summary>
/// One-shot bulk import into the Personal API key pool: <c>QLOO.exe --import-gemini-keys "&lt;file&gt;"</c>
/// (one key per line; AQ. auth keys and legacy AIza keys are both accepted). Each key is probed with a
/// real generateContent call using the documented <c>x-goog-api-key</c> header, so a key whose project
/// cannot generate (for example "free tier is not available for this project") is stored inactive
/// instead of entering the active rotation. Key material is never logged.
/// </summary>
internal static class PersonalKeyImporter
{
    internal sealed record ImportSummary(int Added, int Active, int Inactive, int Skipped);

    internal static async Task<ImportSummary> ImportAsync(
        PersonalApiVault vault,
        IEnumerable<string> keys,
        Func<string, CancellationToken, Task<PersonalKeyValidation>> probe,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(vault);
        ArgumentNullException.ThrowIfNull(probe);
        var view = vault.GetView();
        if (view.Policy != AiBackendPolicy.PersonalOnly)
            view = vault.SetPolicy(view.Revision, AiBackendPolicy.PersonalOnly);
        int added = 0, active = 0, inactive = 0, skipped = 0;
        var number = view.Keys.Count;
        foreach (var key in (keys ?? Array.Empty<string>())
                     .Select(k => (k ?? "").Trim())
                     .Where(k => k.Length > 0 && !k.StartsWith('#'))
                     .Distinct(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            if (view.Keys.Count >= PersonalApiVault.MaximumKeys) { skipped++; continue; }
            PersonalKeyValidation status;
            try { status = await probe(key, token); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            { status = PersonalKeyValidation.Unavailable; }
            try
            {
                view = vault.Add(view.Revision, $"Gemini key {++number}", key, status);
                added++;
                if (status == PersonalKeyValidation.AuthenticationValidated) active++; else inactive++;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                number--;
                skipped++; // already in the pool, pool full, or not a usable key string
            }
        }
        return new ImportSummary(added, active, inactive, skipped);
    }

    internal static async Task<PersonalKeyValidation> ProbeGenerationAsync(HttpClient http, string key, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "https://generativelanguage.googleapis.com/v1beta/models/" + PersonalGeminiModels.Text + ":generateContent");
        request.Headers.Add("x-goog-api-key", key);
        request.Content = new StringContent(
            "{\"contents\":[{\"role\":\"user\",\"parts\":[{\"text\":\"Reply with OK\"}]}],\"generationConfig\":{\"maxOutputTokens\":8}}",
            Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        return response.StatusCode switch
        {
            HttpStatusCode.OK => PersonalKeyValidation.AuthenticationValidated,
            // Authenticated but currently over quota: keep it in rotation; the pool cools it down.
            HttpStatusCode.TooManyRequests => PersonalKeyValidation.AuthenticationValidated,
            HttpStatusCode.Unauthorized => PersonalKeyValidation.InvalidCredential,
            HttpStatusCode.Forbidden or HttpStatusCode.NotFound => PersonalKeyValidation.CapabilityUnavailable,
            _ => PersonalKeyValidation.Unavailable,
        };
    }

    internal static int Run(string path)
    {
        AppLog.Init();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            AppLog.Warning("Personal API key import: the key file was not found.");
            return 2;
        }
        var vault = new PersonalApiVault(
            Path.Combine(SettingsService.AppDataDir, "personal-api-v1.dat"),
            defaultPolicy: AiBackendPolicy.PersonalOnly);
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        var summary = ImportAsync(vault, File.ReadAllLines(path),
                (key, token) => ProbeGenerationAsync(http, key, token))
            .GetAwaiter().GetResult();
        AppLog.Info($"Personal API key import: added={summary.Added} active={summary.Active} inactive={summary.Inactive} skipped={summary.Skipped}");
        return summary.Active > 0 || vault.GetView().Keys.Any(k => k.Enabled && k.AuthenticationValidated) ? 0 : 1;
    }
}
