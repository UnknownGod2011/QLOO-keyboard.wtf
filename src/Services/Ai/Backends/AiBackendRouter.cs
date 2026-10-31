namespace KeyboardWtf.Services.Ai.Backends;

/// <summary>Single policy decision owner. No provider calls, globals or billing mutation here.</summary>
internal sealed class AiBackendRouter(
    IAiBackendConfigurationSource configuration,
    IPersonalCredentialPool credentials,
    Func<TimeSpan, CancellationToken, Task> retryDelay = null,
    TimeSpan? personalAttemptBudget = null)
{
    internal AiBackendOperation BeginOperation(CancellationToken cancellationToken = default, bool acquireCredential = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = configuration.ReadConfiguration();
        if (snapshot is null || snapshot.Revision < 0 || !Enum.IsDefined(snapshot.Policy)
            || snapshot.Provider != PersonalAiProvider.Gemini)
            throw new InvalidOperationException("Personal API configuration is unavailable or unsupported.");
        var binding = new AiBackendBinding(snapshot, credentials);
        try
        {
            // PersonalOnly cannot even enter a managed delegate if no key is eligible.
            if (snapshot.Policy == AiBackendPolicy.PersonalOnly && acquireCredential)
                binding.BindPersonal();
            return new AiBackendOperation(Guid.NewGuid(), snapshot, binding, cancellationToken);
        }
        catch
        {
            binding.Release();
            throw;
        }
    }

    internal async Task<T> ExecuteAsync<T>(
        AiBackendOperation operation,
        AiCapability capability,
        Func<CancellationToken, Task<T>> managed,
        Func<IPersonalCredentialLease, string, CancellationToken, Task<T>> personal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(managed);
        ArgumentNullException.ThrowIfNull(personal);
        var model = PersonalGeminiModels.For(capability);
        operation.ThrowIfUnavailable();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(
            operation.CancellationToken, cancellationToken);
        var token = request.Token;
        var binding = operation.Binding;
        binding.AddReference(); // Keep the key in-use until even cancelled transport work exits.
        try
        {
            if (binding.InitialDispatchCompleted)
                return await DispatchAsync();

            // Only initial dispatch selection is serialized. Subsequent independent
            // secondary requests share the frozen owner without a global send queue.
            await binding.InitialDispatch.WaitAsync(token);
            try
            {
                if (binding.InitialDispatchCompleted) return await DispatchAsync();
                try
                {
                    // Local portions of a mixed operation may freeze policy without
                    // acquiring a key; first actual AI dispatch is still fail-closed.
                    if (!binding.IsBound && operation.Configuration.Policy == AiBackendPolicy.PersonalOnly)
                        binding.BindPersonal();
                    if (!binding.IsBound)
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            var result = await managed(token);
                            binding.BindManaged();
                            token.ThrowIfCancellationRequested();
                            return result;
                        }
                        catch (ManagedAllowanceRefusedException)
                            when (operation.Configuration.Policy == AiBackendPolicy.ManagedThenPersonal)
                        {
                            token.ThrowIfCancellationRequested();
                            binding.BindPersonal();
                        }
                    }
                    // At most three total attempts before the first accepted operation.
                    // Never change a credential during Live resumption or progressed work.
                    request.CancelAfter(personalAttemptBudget ?? TimeSpan.FromSeconds(75));
                    for (var attempt = 1; ; attempt++)
                    {
                        try { return await DispatchAsync(); }
                        catch (PersonalAiException ex) when (attempt < 3
                            && ex.Failure == PersonalAiFailure.PreDispatchConnection)
                        {
                            await (retryDelay ?? Task.Delay)(TimeSpan.FromMilliseconds(250 * attempt), token);
                        }
                        catch (PersonalAiException ex) when (attempt < 3
                            && ex.Failure == PersonalAiFailure.Credential)
                        {
                            token.ThrowIfCancellationRequested();
                            if (!binding.TryNextCredential()) throw;
                        }
                    }
                }
                finally
                {
                    // Unknown/ambiguous failures freeze ownership too. A later feature
                    // retry must never multiply credential retries or migrate a session.
                    if (!binding.IsBound && operation.Configuration.Policy != AiBackendPolicy.PersonalOnly)
                        binding.BindManaged();
                    binding.InitialDispatchCompleted = binding.IsBound;
                }
            }
            finally { binding.InitialDispatch.Release(); }

        }
        finally { binding.Release(); }

        async Task<T> DispatchAsync()
        {
            token.ThrowIfCancellationRequested();
            var result = binding.Owner == AiBackendOwner.Personal
                ? await personal(binding.Credential, model, token)
                : await managed(token);
            token.ThrowIfCancellationRequested();
            return result;
        }
    }
}

internal sealed class AiBackendOperation : IDisposable
{
    private readonly CancellationTokenSource _lifetime;
    private int _disposed;

    internal AiBackendOperation(Guid logicalOperationId, AiBackendConfiguration configuration,
        AiBackendBinding binding, CancellationToken cancellationToken)
    {
        LogicalOperationId = logicalOperationId;
        Configuration = configuration;
        Binding = binding;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken = _lifetime.Token;
    }

    internal Guid LogicalOperationId { get; }
    internal AiBackendConfiguration Configuration { get; }
    internal CancellationToken CancellationToken { get; }
    internal AiBackendOwner? Owner => Binding.IsBound ? Binding.Owner : null;
    internal AiBackendBinding Binding { get; }

    internal AiBackendOperation CreateChild(CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        Binding.AddReference();
        CancellationTokenSource linked = null;
        try
        {
            linked = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken, cancellationToken);
            return new AiBackendOperation(LogicalOperationId, Configuration, Binding, linked);
        }
        catch
        {
            linked?.Dispose();
            Binding.Release();
            throw;
        }
    }

    private AiBackendOperation(Guid logicalOperationId, AiBackendConfiguration configuration,
        AiBackendBinding binding, CancellationTokenSource lifetime)
    {
        LogicalOperationId = logicalOperationId;
        Configuration = configuration;
        Binding = binding;
        _lifetime = lifetime;
        CancellationToken = lifetime.Token;
    }

    internal void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        CancellationToken.ThrowIfCancellationRequested();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _lifetime.Cancel(); }
        finally
        {
            _lifetime.Dispose();
            Binding.Release();
        }
    }
}

internal sealed class AiBackendBinding(AiBackendConfiguration configuration, IPersonalCredentialPool pool)
{
    private readonly object _referencesLock = new();
    private int _references = 1;
    private int _bound;
    private int _initialDispatchCompleted;
    internal bool InitialDispatchCompleted
    {
        get => Volatile.Read(ref _initialDispatchCompleted) != 0;
        set => Volatile.Write(ref _initialDispatchCompleted, value ? 1 : 0);
    }
    internal SemaphoreSlim InitialDispatch { get; } = new(1, 1);
    internal bool IsBound => Volatile.Read(ref _bound) != 0;
    internal AiBackendOwner Owner { get; private set; }
    internal IPersonalCredentialLease Credential { get; private set; }

    internal void BindPersonal()
    {
        Credential = pool.Acquire(configuration)
            ?? throw new InvalidOperationException("No eligible Personal API credential is available.");
        Owner = AiBackendOwner.Personal;
        Volatile.Write(ref _bound, 1);
    }

    internal void BindManaged()
    {
        Owner = AiBackendOwner.Managed;
        Volatile.Write(ref _bound, 1);
    }

    internal bool TryNextCredential()
    {
        if (Owner != AiBackendOwner.Personal || pool is not IPersonalCredentialRotation rotation) return false;
        var next = rotation.AcquireNext(configuration, Credential.SlotId);
        if (next is null) return false;
        var previous = Credential;
        Credential = next;
        previous.Dispose();
        return true;
    }

    internal void AddReference()
    {
        lock (_referencesLock)
        {
            ObjectDisposedException.ThrowIf(_references == 0, this);
            _references++;
        }
    }

    internal void Release()
    {
        lock (_referencesLock)
        {
            if (--_references != 0) return;
            Credential?.Dispose();
            InitialDispatch.Dispose();
        }
    }
}
