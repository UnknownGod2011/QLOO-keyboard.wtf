namespace KeyboardWtf.Services;

using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using KeyboardWtf.Services.Ai.Backends;

/// <summary>
/// A separate Personal credential owner for opt-in Settings pet voice. Never calls the Jarvis
/// gateway, changes its policy/key health, rotates keys, or reserves/settles Managed credits.
/// The single-use token grant remains leased until expiry, even if a browser says it stopped.
/// Model eligibility is checked using read-only metadata before minting on an explicit voice click.
/// </summary>
internal sealed class LumaLocalTokenService : IDisposable
{
    private readonly PersonalApiVault _vault;
    private readonly Func<bool> _nativeMicrophoneBusy;
    private readonly Func<DateTimeOffset> _now;
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly bool _eligibilityVerified;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private IPersonalCredentialLease _credential;
    private DateTimeOffset _leaseUntil;
    private DateTimeOffset _nextAttemptAt;
    private Guid _preferredSlot;
    private int _disposed;

    internal LumaLocalTokenService(PersonalApiVault vault, Func<bool> nativeMicrophoneBusy,
        string model = LumaLiveProtocol.PreferredModel, bool eligibilityVerified = false,
        HttpMessageHandler handler = null, Func<DateTimeOffset> now = null)
    {
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        _nativeMicrophoneBusy = nativeMicrophoneBusy ?? throw new ArgumentNullException(nameof(nativeMicrophoneBusy));
        _model = LumaLiveProtocol.ValidateModel(model);
        _eligibilityVerified = eligibilityVerified;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _http = handler is null
            ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(10);
    }

    internal async Task<string> ProbeEligibilityAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        cancellationToken = deadline.Token;
        if (!await _gate.WaitAsync(0, cancellationToken))
            throw new LumaLiveUnavailableException("Luma voice is already connecting.");
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_now() < _leaseUntil)
                throw new LumaLiveUnavailableException("An existing Luma token grant is still active.");
            return await AcquireEligibleOwnerAsync(cancellationToken);
        }
        finally
        {
            // A rejected diagnostic call must not release an already-issued live grant.
            if (_leaseUntil <= _now())
            {
                _credential?.Dispose();
                _credential = null;
            }
            _gate.Release();
        }
    }

    // Read-only admission state for the private preview. No provider/credential
    // access, release, rotation or grant is performed by this check.
    internal async Task<LumaVoiceAvailability> GetAvailabilityAsync(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_nativeMicrophoneBusy()) return new(false, 0, "Finish the active voice task before talking with Luma.");
        if (!await _gate.WaitAsync(0, token)) return new(false, 1, "Luma voice is already connecting.");
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var now = _now();
            var until = _leaseUntil > _nextAttemptAt ? _leaseUntil : _nextAttemptAt;
            var remaining = (int)Math.Clamp(Math.Ceiling((until - now).TotalSeconds), 0, LumaLiveProtocol.SessionSeconds);
            return remaining > 0
                ? new(false, remaining, $"Luma's earlier short Voice connection is still reserved. Try again in {remaining} seconds.")
                : new(true, 0, "Ready to connect.");
        }
        finally { _gate.Release(); }
    }

    internal async Task<LumaLiveGrant> IssueAsync(string section, CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string> activeHotkeys = null)
    {
        LumaLiveProtocol.ValidateSection(section);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var callerToken = cancellationToken;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        cancellationToken = deadline.Token;
        // Fail safely; never stop Jarvis/dictation or take over their microphone owner.
        if (_nativeMicrophoneBusy())
            throw new LumaLiveUnavailableException("Finish the active voice task before talking with Luma.");
        if (!await _gate.WaitAsync(0, cancellationToken))
            throw new LumaLiveUnavailableException("Luma voice is already connecting.");
        byte[] payload = null;
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var now = _now();
            if (now < _leaseUntil)
                throw new LumaLiveUnavailableException("Luma voice already has an active short session. Try again after it expires.");
            if (now < _nextAttemptAt)
                throw new LumaLiveUnavailableException("Luma voice needs a brief pause before another connection attempt.");
            _credential?.Dispose();
            _credential = null;
            if (_nativeMicrophoneBusy())
                throw new LumaLiveUnavailableException("Finish the active voice task before talking with Luma.");
            // Metadata failure is not a reason for rapid pool cycling on repeated clicks.
            _nextAttemptAt = now.AddSeconds(30);
            var model = await AcquireEligibleOwnerAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (_nativeMicrophoneBusy())
                throw new LumaLiveUnavailableException("Another voice task started. Luma did not open the microphone.");
            // Freeze one eligible owner/model before minting. No replacement after this point,
            // including 401/429/503/network errors or a later provider 1011.
            now = _now();
            // Retain ownership on ambiguous network/provider responses, not only successful grants.
            _leaseUntil = now.AddSeconds(LumaLiveProtocol.SessionSeconds);
            _ = ReleaseExpiredCredentialAsync(_credential, _leaseUntil);
            payload = LumaLiveProtocol.CreateTokenRequest(model, section, now, activeHotkeys);
            using var request = new HttpRequestMessage(HttpMethod.Post,
                "https://generativelanguage.googleapis.com/v1beta/auth_tokens");
            request.Headers.Add("x-goog-api-key", _vault.ReadSecretForTransport(_credential));
            request.Content = new ByteArrayContent(payload);
            request.Content.Headers.ContentType = new("application/json");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new LumaLiveUnavailableException(response.StatusCode == HttpStatusCode.TooManyRequests
                    ? "Luma voice has reached the provider's current limit. Please try later."
                    : "Luma voice is temporarily unavailable. The tour and text help still work.");
            var bytes = await ReadBoundedAsync(response.Content, cancellationToken);
            try
            {
                using var document = JsonDocument.Parse(bytes);
                var token = LumaLiveProtocol.ReadToken(document.RootElement, _leaseUntil);
                cancellationToken.ThrowIfCancellationRequested();
                if (_nativeMicrophoneBusy())
                    throw new LumaLiveUnavailableException("Another voice task started. Luma did not open the microphone.");
                if (_now() >= now.AddSeconds(40))
                    throw new LumaLiveUnavailableException("Luma's connection window expired. Please try later.");
                return new(token, model, _leaseUntil, now.AddSeconds(45),
                    LumaLiveProtocol.SessionSeconds, LumaLiveProtocol.WebSocketUrl);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (LumaLiveUnavailableException) { throw; }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException or ObjectDisposedException)
        { throw new LumaLiveUnavailableException("Luma voice is temporarily unavailable. The tour and text help still work."); }
        finally
        {
            if (payload is not null) CryptographicOperations.ZeroMemory(payload);
            if (_credential is not null && _leaseUntil <= _now())
            {
                _credential.Dispose();
                _credential = null;
            }
            _gate.Release();
        }
    }

    // Read-only bounded pool admission, before any token or Live session exists. This service
    // owns a separate Luma DPAPI document and never changes the global pool selector/key health.
    private async Task<string> AcquireEligibleOwnerAsync(CancellationToken cancellationToken)
    {
        PersonalApiView view;
        try { view = _vault.GetView(); }
        catch (PersonalApiConfigurationException)
        { throw new LumaLiveUnavailableException("Luma voice needs an eligible Personal API key configured in Settings."); }
        var eligible = view.Keys.Where(value => value.Enabled && value.AuthenticationValidated)
            .OrderByDescending(value => value.Id == _preferredSlot).Take(2).ToArray();
        foreach (var key in eligible)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _credential?.Dispose();
            _credential = null;
            try
            {
                _credential = _vault.AcquireForValidation(view.Revision, key.Id, key.Revision);
                _vault.EnsureDispatchAllowed(_credential);
                var model = _eligibilityVerified ? _model : await DiscoverEligibleModelAsync(cancellationToken);
                _preferredSlot = key.Id;
                return model;
            }
            catch (LumaSlotUnavailableException)
            {
                // Only definitive pre-session authentication/model absence. Never quota/network.
                _credential?.Dispose();
                _credential = null;
            }
            catch (Exception ex) when (ex is PersonalApiConfigurationException or InvalidOperationException)
            { throw new LumaLiveUnavailableException("Luma voice needs an eligible Personal API key configured in Settings."); }
        }
        throw new LumaLiveUnavailableException("Luma voice needs an eligible Personal API key with an available Live model.");
    }

    // Metadata-only eligibility check: no inference, microphone, Live connection or Managed billing.
    // Each discovery uses the currently selected credential. Slot replacement is allowed only
    // before token/session creation, after a definitive authentication/model-availability result.
    private async Task<string> DiscoverEligibleModelAsync(CancellationToken cancellationToken)
    {
        foreach (var model in new[] { _model, LumaLiveProtocol.AlternativeModel }.Distinct())
        {
            _vault.EnsureDispatchAllowed(_credential);
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://generativelanguage.googleapis.com/v1beta/models/" + model);
            request.Headers.Add("x-goog-api-key", _vault.ReadSecretForTransport(_credential));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            // A model-specific absence may select the configured alternative, never another key.
            if (response.StatusCode == HttpStatusCode.NotFound) continue;
            if (response.StatusCode == HttpStatusCode.Unauthorized) throw new LumaSlotUnavailableException();
            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                var rejection = await ReadBoundedAsync(response.Content, cancellationToken);
                try
                {
                    using var json = JsonDocument.Parse(rejection);
                    if (IsDefinitivelyInvalidKey(json.RootElement)) throw new LumaSlotUnavailableException();
                }
                finally { CryptographicOperations.ZeroMemory(rejection); }
            }
            if (!response.IsSuccessStatusCode)
                throw new LumaLiveUnavailableException("Luma voice model eligibility could not be verified. The tour and text help still work.");
            var bytes = await ReadBoundedAsync(response.Content, cancellationToken);
            try
            {
                using var document = JsonDocument.Parse(bytes);
                if (ModelMetadataSupportsLive(document.RootElement, model)) return model;
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        throw new LumaSlotUnavailableException();
    }

    private sealed class LumaSlotUnavailableException : Exception { }

    private static bool IsDefinitivelyInvalidKey(JsonElement root) => root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
        && error.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array
        && details.EnumerateArray().Any(detail => detail.ValueKind == JsonValueKind.Object
            && detail.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String
            && reason.GetString() == "API_KEY_INVALID");

    internal static bool ModelMetadataSupportsLive(JsonElement root, string model) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
        && name.GetString() == "models/" + LumaLiveProtocol.ValidateModel(model)
        && root.TryGetProperty("supportedGenerationMethods", out var methods) && methods.ValueKind == JsonValueKind.Array
        && methods.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String
            && value.GetString() is "bidiGenerateContent" or "bidiGenerateContentConstrained");

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > 8192) throw new InvalidOperationException("Luma token response is too large.");
        using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var result = new MemoryStream();
        var buffer = new byte[1024];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (result.Length + count > 8192) throw new InvalidOperationException("Luma token response is too large.");
            result.Write(buffer, 0, count);
        }
        return result.ToArray();
    }

    public void Dispose()
    {
        // No outbound request or settings mutation; caller closes its own browser audio/socket.
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        _http.Dispose();
        // Serialize with acquisition/issue so Dispose cannot miss a newly acquired credential.
        _ = DisposeOwnedCredentialAsync();
    }

    private async Task DisposeOwnedCredentialAsync()
    {
        await _gate.WaitAsync();
        IPersonalCredentialLease credential;
        DateTimeOffset until;
        try { credential = _credential; _credential = null; until = _leaseUntil; }
        finally { _gate.Release(); }
        _lifetime.Dispose();
        if (credential is not null)
        {
            var delay = until - _now();
            if (delay > TimeSpan.Zero) await Task.Delay(delay);
            credential.Dispose();
        }
    }

    private async Task ReleaseExpiredCredentialAsync(IPersonalCredentialLease credential, DateTimeOffset until)
    {
        var delay = until - _now();
        if (delay > TimeSpan.Zero) await Task.Delay(delay);
        await _gate.WaitAsync();
        try
        {
            if (_now() >= until && ReferenceEquals(_credential, credential))
            {
                _credential = null;
                credential.Dispose();
            }
        }
        finally { _gate.Release(); }
    }
}

internal sealed record LumaVoiceAvailability(bool Available, int RetryAfterSeconds, string Message);
