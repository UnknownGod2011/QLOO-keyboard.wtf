namespace KeyboardWtf.Services.Ai.Backends;

using System.Net.WebSockets;
using KeyboardWtf.Services.PaidStaging;

internal sealed record AiDispatchEvidence(Guid OperationId, Guid RequestId, AiBackendOwner Owner,
    AiCapability Capability, string EndpointHost, int Attempt);

/// <summary>Route boundary shared by Live and every existing paid-provider secondary call.</summary>
internal sealed class AiBackendGateway : IDisposable
{
    private readonly PersonalApiVault _vault;
    private readonly AiBackendRouter _router;
    private readonly PersonalGeminiHttpTransport _text;
    private readonly PersonalGeminiLiveTransport _live;
    private readonly PersonalGeminiKeyValidator _validator;
    private readonly Func<CancellationToken, Task> _authorizePersonal;
    private readonly Action<AiDispatchEvidence> _evidence;
    private readonly AsyncLocal<AiBackendOperation> _current = new();

    internal AiBackendGateway(PersonalApiVault vault, Func<CancellationToken, Task> authorizePersonal,
        PersonalGeminiHttpTransport text = null, PersonalGeminiLiveTransport live = null,
        Action<AiDispatchEvidence> evidence = null, PersonalGeminiKeyValidator validator = null)
    {
        _vault = vault;
        _router = new(vault, vault);
        _text = text ?? new(vault);
        _live = live ?? new(vault);
        _validator = validator ?? new();
        _authorizePersonal = authorizePersonal ?? throw new ArgumentNullException(nameof(authorizePersonal));
        _evidence = evidence ?? (entry => AppLog.Info(
            $"AI backend dispatch: operation={entry.OperationId:N} request={entry.RequestId:N} " +
            $"owner={entry.Owner} capability={entry.Capability} host={entry.EndpointHost} attempt={entry.Attempt}"));
    }

    internal AiBackendOperation Current => _current.Value;
    internal bool MayAcceptAllowanceFallback => Current is { Owner: null } operation
        && operation.Configuration.Policy == AiBackendPolicy.ManagedThenPersonal;
    internal bool RequiresManagedCredits
    {
        get
        {
            if (Current is { } operation)
                return operation.Owner == AiBackendOwner.Managed
                    || (operation.Owner is null && operation.Configuration.Policy == AiBackendPolicy.Managed);
            try { return _vault.ReadConfiguration().Policy == AiBackendPolicy.Managed; }
            catch (PersonalApiConfigurationException)
            {
                // A UI credit precheck is not backend authorization. Let BeginOperation
                // report the damaged store through the feature's existing error path;
                // it still fails before either provider can be dispatched.
                return false;
            }
        }
    }
    internal AiBackendOperation BeginOperation(CancellationToken token = default, bool acquireCredential = true)
    {
        var operation = Current?.CreateChild(token) ?? _router.BeginOperation(token, acquireCredential);
#if KEYBOARD_WTF_PERSONAL_API_CANDIDATE
        if (PrivateTextAuditEnabled && operation.Configuration.Policy != AiBackendPolicy.PersonalOnly)
        {
            operation.Dispose();
            throw new InvalidOperationException("Private runtime audit refuses a non-PersonalOnly operation before dispatch.");
        }
#endif
        return operation;
    }

#if KEYBOARD_WTF_PERSONAL_API_CANDIDATE
    private static bool PrivateTextAuditEnabled => string.Equals(
        Environment.GetEnvironmentVariable("KEYBOARD_WTF_PRIVATE_AUDIT_TEXT_ONLY"), "1", StringComparison.Ordinal);

    private static void RefuseManagedDuringPrivateAudit()
    {
        if (PrivateTextAuditEnabled)
            throw new InvalidOperationException("Private runtime audit refuses Managed inference before reservation/dispatch.");
    }
#endif

    // Synchronous entry preserves AsyncLocal ownership through Task.Run/await without global mutable policy.
    internal IDisposable Enter(AiBackendOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        operation.ThrowIfUnavailable();
        var previous = Current;
        _current.Value = operation;
        return new Scope(() => _current.Value = previous);
    }

    internal IDisposable BeginScope(CancellationToken token = default)
    {
        var operation = BeginOperation(token, acquireCredential: false);
        var entered = Enter(operation);
        return new Scope(() => { entered.Dispose(); operation.Dispose(); });
    }

    internal async Task<PaidGenerateResponse> GenerateAsync(AiGenerationRequest request, string taskKind,
        long knownManagedBalance, Func<CancellationToken, Task<PaidGenerateResponse>> managed, CancellationToken token)
    {
        using var scope = BeginScope(token);
        var operation = Current;
        var capability = CapabilityFor(taskKind, request);
        var requestId = Guid.NewGuid();
        var attempt = 0;
        return await _router.ExecuteAsync(operation, capability,
            cancellation =>
            {
#if KEYBOARD_WTF_PERSONAL_API_CANDIDATE
                RefuseManagedDuringPrivateAudit();
#endif
                Record(AiBackendOwner.Managed, "managed-backend");
                return managed(cancellation);
            }, async (key, model, cancellation) =>
            {
                await _authorizePersonal(cancellation); // Account auth only; never reservation/AI/credit checks.
                Record(AiBackendOwner.Personal, "generativelanguage.googleapis.com");
                PersonalGenerationResult result;
                try
                {
                    result = await _text.GenerateAsync(key, model, request, cancellation);
                }
                catch (PersonalAiException ex) when (ex.ModelScoped && PersonalModelCooldowns.FallbackFor(model) is { } fallback)
                {
                    AppLog.Info($"Personal AI model quota exhausted: operation={operation.LogicalOperationId:N} request={requestId:N} "
                        + $"capability={capability} model={model} fallback={fallback} failure={ex.Failure} httpStatus={ex.HttpStatusCode?.ToString() ?? "none"}");
                    cancellation.ThrowIfCancellationRequested();
                    result = await _text.GenerateAsync(key, fallback, request, cancellation);
                }
                catch (PersonalAiException ex)
                {
                    AppLog.Info($"Personal AI request failed: operation={operation.LogicalOperationId:N} request={requestId:N} "
                        + $"capability={capability} failure={ex.Failure} httpStatus={ex.HttpStatusCode?.ToString() ?? "none"}");
                    throw;
                }
                // Existing consumers read Text/Verified; no account counters are mutated here.
                return new("personal_" + requestId.ToString("N"), result.Text, result.Model,
                    0, knownManagedBalance, result.Usage, true,
                    ContentComplete: request.CompleteSelectedText ? true : null);
            }, token);

        void Record(AiBackendOwner owner, string host) =>
            _evidence(new(operation.LogicalOperationId, requestId, owner, capability, host, ++attempt));
    }

    internal Task<ClientWebSocket> ConnectLiveAsync(Func<CancellationToken, Task<ClientWebSocket>> managed, CancellationToken token)
    {
        var operation = Current ?? throw new InvalidOperationException("Live requires a logical backend owner.");
        var requestId = Guid.NewGuid();
        var attempt = 0;
        return _router.ExecuteAsync(operation, AiCapability.Live, cancellation =>
        {
#if KEYBOARD_WTF_PERSONAL_API_CANDIDATE
            RefuseManagedDuringPrivateAudit();
#endif
            Record(AiBackendOwner.Managed, "managed-backend");
            return managed(cancellation);
        }, async (key, model, cancellation) =>
        {
            await _authorizePersonal(cancellation);
            Record(AiBackendOwner.Personal, PersonalGeminiLiveTransport.Endpoint.Host);
            return await _live.ConnectAsync(key, model, cancellation);
        }, token);

        void Record(AiBackendOwner owner, string host) =>
            _evidence(new(operation.LogicalOperationId, requestId, owner, AiCapability.Live, host, ++attempt));
    }

    internal object PrepareLiveSetup(object setup) => Current?.Owner == AiBackendOwner.Personal
        ? PersonalGeminiLiveTransport.PrepareSetup(setup) : setup;

    internal async Task<(bool Success, string Message)?> TestPersonalMetadataAsync(CancellationToken token = default)
    {
        // Preserve the old Managed health route; Personal health is metadata-only.
        if (Current?.Owner == AiBackendOwner.Managed || (Current?.Owner != AiBackendOwner.Personal
            && (Current?.Configuration ?? _vault.ReadConfiguration()).Policy != AiBackendPolicy.PersonalOnly)) return null;
        using var scope = BeginScope(token);
        await _authorizePersonal(token);
        using var ownLease = Current.Binding.Credential is null ? _vault.Acquire(Current.Configuration) : null;
        var lease = Current.Binding.Credential ?? ownLease;
        var result = await _validator.ValidateAsync(_vault.ReadSecretForTransport(lease), token);
        return (result.Status == PersonalKeyValidation.AuthenticationValidated, result.Message);
    }

    internal static AiCapability CapabilityFor(string taskKind, AiGenerationRequest request) => taskKind switch
    {
        "browser-plan" or "browser-vision-recovery" => AiCapability.BrowserPlanning,
        "screen-guidance" => AiCapability.Images,
        "desktop-structured" => AiCapability.StructuredPlanning,
        "smart-writing-audio" => AiCapability.AudioWriting,
        _ => request.Media?.Any(part => part.MimeType.StartsWith("image/", StringComparison.Ordinal)) == true
            ? AiCapability.Images : AiCapability.Text,
    };

    public void Dispose() { _text.Dispose(); _live.Dispose(); _validator.Dispose(); }
    private sealed class Scope(Action release) : IDisposable
    {
        private Action _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
