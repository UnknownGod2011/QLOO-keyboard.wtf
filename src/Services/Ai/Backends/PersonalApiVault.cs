namespace KeyboardWtf.Services.Ai.Backends;

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

internal enum PersonalKeyValidation { Unknown, AuthenticationValidated, InvalidCredential, CapabilityUnavailable, Unavailable }
internal sealed record PersonalKeyView(Guid Id, long Revision, string Label, string Mask,
    bool Enabled, [property: JsonPropertyName("validated")] bool AuthenticationValidated,
    PersonalKeyValidation LastValidation)
{
    [JsonIgnore]
    public bool Validated => AuthenticationValidated;
}
internal sealed record PersonalApiView(long Revision, AiBackendPolicy Policy, PersonalAiProvider Provider,
    bool Configured, IReadOnlyList<PersonalKeyView> Keys, DateTimeOffset? CooldownUntil);

internal sealed class PersonalApiConfigurationException : Exception
{
    internal PersonalApiConfigurationException()
        : base("Personal API configuration is missing, damaged or unavailable. Repair or explicitly reset it; no backend fallback was performed.") { }
}

internal sealed class PersonalApiCooldownException : InvalidOperationException
{
    internal PersonalApiCooldownException()
        : base("The Personal Gemini key pool is temporarily cooling down.") { }
}

internal interface IPersonalDataProtection
{
    byte[] Protect(byte[] plaintext, byte[] entropy);
    byte[] Unprotect(byte[] ciphertext, byte[] entropy);
}

internal sealed class PersonalCurrentUserProtection : IPersonalDataProtection
{
    public byte[] Protect(byte[] plaintext, byte[] entropy)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows DPAPI is required.");
        return ProtectedData.Protect(plaintext, entropy, DataProtectionScope.CurrentUser);
    }
    public byte[] Unprotect(byte[] ciphertext, byte[] entropy)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows DPAPI is required.");
        return ProtectedData.Unprotect(ciphertext, entropy, DataProtectionScope.CurrentUser);
    }
}

/// <summary>Only owner of the encrypted policy/key document; never uses legacy keys or SecureStore.</summary>
internal sealed class PersonalApiVault : IAiBackendConfigurationSource, IPersonalCredentialPool, IPersonalCredentialRotation
{
    internal const int MaximumKeys = 10;
    private const int MaximumDocumentBytes = 131_072;
    private const string MarkerValue = "keyboard.wtf-personal-api-configured-v1";
    private static readonly ConcurrentDictionary<string, PoolState> Pools = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _path;
    private readonly byte[] _entropy;
    private readonly string _mutexName;
    private readonly PoolState _pool;
    private readonly IPersonalDataProtection _protection;
    private readonly Action<string, byte[]> _atomicWrite;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly AiBackendPolicy _defaultPolicy;

    internal PersonalApiVault(string path, IPersonalDataProtection protection = null,
        Action<string, byte[]> atomicWrite = null, Func<DateTimeOffset> utcNow = null,
        AiBackendPolicy defaultPolicy = AiBackendPolicy.Managed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Enum.IsDefined(defaultPolicy)) throw new ArgumentOutOfRangeException(nameof(defaultPolicy));
        _path = Path.GetFullPath(path);
        _entropy = SHA256.HashData(Encoding.UTF8.GetBytes("keyboard.wtf/personal-api/v1/" + _path.ToUpperInvariant()));
        _mutexName = "Local\\keyboard.wtf-personal-api-" + Convert.ToHexString(_entropy);
        _pool = Pools.GetOrAdd(_path, _ => new PoolState());
        _protection = protection ?? new PersonalCurrentUserProtection();
        _atomicWrite = atomicWrite ?? WriteAtomic;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _defaultPolicy = defaultPolicy;
    }

    public AiBackendConfiguration ReadConfiguration() => Locked(() =>
    {
        var document = ReadDocument();
        return new AiBackendConfiguration(document.Revision, document.Policy, document.Provider, document.StoreId);
    });

    internal PersonalApiView GetView() => Locked(() => View(ReadDocument()));

    internal PersonalApiView SetPolicy(long expectedRevision, AiBackendPolicy policy)
    {
        if (!Enum.IsDefined(policy)) throw new ArgumentException("Unsupported backend policy.");
        return Update(expectedRevision, document => document.Policy = policy);
    }

    internal PersonalApiView Add(long expectedRevision, string label, string key, PersonalKeyValidation validation)
    {
        label = ValidateLabel(label);
        key = ValidateKey(key);
        ValidateResult(validation);
        return Update(expectedRevision, document =>
        {
            if (document.Keys.Count >= MaximumKeys) throw new InvalidOperationException("At most ten Personal API keys are allowed.");
            if (document.Keys.Any(row => row.Secret == key)) throw new InvalidOperationException("This Personal API key is already stored.");
            var validated = validation == PersonalKeyValidation.AuthenticationValidated;
            document.Keys.Add(new Credential
            {
                Id = Guid.NewGuid(), Revision = 1, Label = label, Secret = key,
                Enabled = validated, Validated = validated, LastValidation = validation,
            });
        });
    }

    internal PersonalApiView Replace(long expectedRevision, Guid id, long credentialRevision,
        string key, PersonalKeyValidation validation)
    {
        key = ValidateKey(key);
        if (validation != PersonalKeyValidation.AuthenticationValidated)
            throw new InvalidOperationException("Replacement was not authenticated; the existing key was retained.");
        return Update(expectedRevision, document =>
        {
            EnsureNotInUse(id);
            var row = Find(document, id);
            if (row.Revision != credentialRevision) throw new InvalidOperationException("Personal API key changed; reload Settings.");
            if (document.Keys.Any(other => other.Id != id && other.Secret == key))
                throw new InvalidOperationException("This Personal API key is already stored.");
            row.Secret = key;
            row.Revision = checked(row.Revision + 1);
            row.Validated = row.Enabled = true;
            row.LastValidation = validation;
        });
    }

    internal PersonalApiView Remove(long expectedRevision, Guid id) => Update(expectedRevision, document =>
    {
        EnsureNotInUse(id);
        document.Keys.Remove(Find(document, id));
    });

    internal PersonalApiView SetEnabled(long expectedRevision, Guid id, bool enabled) => Update(expectedRevision, document =>
    {
        EnsureNotInUse(id);
        var row = Find(document, id);
        if (enabled && !row.Validated) throw new InvalidOperationException("Authenticate this key before enabling it.");
        row.Enabled = enabled;
    });

    // Revision checks discard late network validation after another CRUD action.
    internal bool ApplyValidation(long expectedRevision, Guid id, long credentialRevision, PersonalKeyValidation validation)
    {
        ValidateResult(validation);
        return Locked(() =>
        {
            var document = ReadDocument();
            var row = document.Keys.FirstOrDefault(value => value.Id == id);
            if (document.Revision != expectedRevision || row is null || row.Revision != credentialRevision) return false;
            row.LastValidation = validation;
            if (validation == PersonalKeyValidation.AuthenticationValidated) row.Validated = true;
            else if (validation == PersonalKeyValidation.InvalidCredential) row.Enabled = row.Validated = false;
            // A temporary network/model error does not revoke a previously authenticated key.
            SaveNext(document);
            return true;
        });
    }

    internal PersonalApiView CoolDownUnknownScopePool(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromHours(24))
            throw new ArgumentOutOfRangeException(nameof(duration));
        return Locked(() =>
        {
            var document = ReadDocument();
            var until = _utcNow().Add(duration);
            if (document.CooldownUntil is null || document.CooldownUntil < until) document.CooldownUntil = until;
            SaveNext(document);
            return View(document);
        });
    }

    public IPersonalCredentialLease Acquire(AiBackendConfiguration expectedConfiguration) => Locked(() =>
    {
        var document = ReadDocument();
        RequireRevision(document, expectedConfiguration.Revision);
        if (document.StoreId != expectedConfiguration.StoreId)
            throw new InvalidOperationException("Personal API configuration was reset; start a new operation.");
        if (document.CooldownUntil > _utcNow())
            throw new PersonalApiCooldownException();
        var eligible = document.Keys.Where(row => row.Enabled && row.Validated).ToArray();
        var chosen = eligible.FirstOrDefault(row => row.Id == _pool.Selected) ?? eligible.FirstOrDefault();
        if (chosen is null) throw new InvalidOperationException("No enabled, authenticated Personal API key is available.");
        var lease = new CredentialLease(this, chosen);
        _pool.Selected = chosen.Id;
        _pool.InUse[chosen.Id] = _pool.InUse.GetValueOrDefault(chosen.Id) + 1;
        return (IPersonalCredentialLease)lease;
    });

    // Available only at the Personal transport boundary, never through the Settings view.
    public IPersonalCredentialLease AcquireNext(AiBackendConfiguration frozen, Guid rejectedSlot) => Locked(() =>
    {
        var document = ReadDocument();
        if (document.StoreId != frozen.StoreId || document.Provider != frozen.Provider)
            throw new PersonalApiConfigurationException();
        if (document.CooldownUntil > _utcNow()) return null;
        var rejected = document.Keys.FindIndex(row => row.Id == rejectedSlot);
        if (rejected < 0 || document.Keys[rejected].Validated)
            throw new PersonalApiConfigurationException();
        // Stable circular order. Rejected slots have already been disabled, so they
        // cannot be selected again; a healthy earlier slot must not be stranded.
        var chosen = document.Keys.Skip(rejected + 1).Concat(document.Keys.Take(rejected))
            .FirstOrDefault(row => row.Enabled && row.Validated);
        if (chosen is null) return null;
        var lease = new CredentialLease(this, chosen);
        _pool.Selected = chosen.Id;
        _pool.InUse[chosen.Id] = _pool.InUse.GetValueOrDefault(chosen.Id) + 1;
        return (IPersonalCredentialLease)lease;
    });

    internal IPersonalCredentialLease AcquireForValidation(long expectedRevision, Guid id, long credentialRevision) => Locked(() =>
    {
        var document = ReadDocument();
        RequireRevision(document, expectedRevision);
        var row = Find(document, id);
        if (row.Revision != credentialRevision) throw new InvalidOperationException("Personal API key changed; reload Settings.");
        var lease = new CredentialLease(this, row);
        _pool.InUse[id] = _pool.InUse.GetValueOrDefault(id) + 1;
        return (IPersonalCredentialLease)lease;
    });

    internal string ReadSecretForTransport(IPersonalCredentialLease lease)
    {
        if (lease is not CredentialLease owned || !ReferenceEquals(owned.Vault._pool, _pool))
            throw new InvalidOperationException("Credential lease does not belong to this profile.");
        lock (_pool.Sync)
        {
            ObjectDisposedException.ThrowIf(owned.SecretBytes is null, owned);
            return Encoding.UTF8.GetString(owned.SecretBytes);
        }
    }

    internal void EnsureDispatchAllowed(IPersonalCredentialLease lease) => Locked(() =>
    {
        // A pinned lease survives policy changes, not a revoked key, damaged store or pool cooldown.
        _ = ReadSecretForTransport(lease);
        var document = ReadDocument();
        var row = Find(document, lease.SlotId);
        if (row.Revision != lease.CredentialRevision || !row.Enabled || !row.Validated)
            throw new InvalidOperationException("The owned Personal credential is no longer eligible; start new work after repairing Settings.");
        if (document.CooldownUntil > _utcNow())
            throw new PersonalApiCooldownException();
        return true;
    });

    internal void RecordCredentialRejection(IPersonalCredentialLease lease) => Locked(() =>
    {
        _ = ReadSecretForTransport(lease);
        var document = ReadDocument();
        var row = document.Keys.FirstOrDefault(value => value.Id == lease.SlotId && value.Revision == lease.CredentialRevision);
        if (row is null) return false;
        row.Enabled = row.Validated = false;
        row.LastValidation = PersonalKeyValidation.InvalidCredential;
        SaveNext(document);
        return true;
    });

    internal PersonalApiView ResetExplicitly() => Locked(() =>
    {
        if (_pool.InUse.Values.Any(count => count > 0))
            throw new InvalidOperationException("End active Personal API work before resetting credentials.");
        var document = new Document { Policy = _defaultPolicy };
        // Explicit recovery only; no attempt to decrypt/fall back to older data.
        SaveNext(document);
        _pool.Selected = Guid.Empty;
        return View(document);
    });

    private PersonalApiView Update(long expectedRevision, Action<Document> action) => Locked(() =>
    {
        var document = ReadDocument();
        RequireRevision(document, expectedRevision);
        action(document);
        SaveNext(document);
        return View(document);
    });

    private void SaveNext(Document document)
    {
        document.Revision = checked(document.Revision + 1);
        if (document.StoreId == Guid.Empty) document.StoreId = Guid.NewGuid();
        byte[] plaintext = null;
        byte[] ciphertext = null;
        try
        {
            plaintext = JsonSerializer.SerializeToUtf8Bytes(document);
            ciphertext = _protection.Protect(plaintext, _entropy);
            if (ciphertext is null || ciphertext.Length == 0) throw new CryptographicException();
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            // Marker first: a crash in first-time setup must block, not revert Personal to Managed.
            if (!ExistsStrict(_path + ".configured"))
                _atomicWrite(_path + ".configured", Encoding.UTF8.GetBytes(MarkerValue));
            _atomicWrite(_path, ciphertext);
        }
        catch (Exception ex) when (IsStorageFailure(ex)) { throw new PersonalApiConfigurationException(); }
        finally
        {
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
            if (ciphertext is not null) CryptographicOperations.ZeroMemory(ciphertext);
        }
    }

    private Document ReadDocument()
    {
        byte[] plaintext = null;
        try
        {
            var hasMarker = ExistsStrict(_path + ".configured");
            var hasDocument = ExistsStrict(_path);
            if (!hasMarker && !hasDocument) return new Document { Policy = _defaultPolicy };
            if (!hasMarker || !hasDocument || new FileInfo(_path).Length > MaximumDocumentBytes
                || new FileInfo(_path + ".configured").Length > 128
                || File.ReadAllText(_path + ".configured") != MarkerValue)
                throw new PersonalApiConfigurationException();
            plaintext = _protection.Unprotect(File.ReadAllBytes(_path), _entropy);
            var document = JsonSerializer.Deserialize<Document>(plaintext);
            if (document is null || document.Schema != 1 || document.Revision <= 0 || document.StoreId == Guid.Empty
                || !Enum.IsDefined(document.Policy) || document.Provider != PersonalAiProvider.Gemini
                || document.Keys is null || document.Keys.Count > MaximumKeys
                || document.Keys.Select(row => row.Id).Distinct().Count() != document.Keys.Count
                || document.Keys.Select(row => row.Secret).Distinct(StringComparer.Ordinal).Count() != document.Keys.Count)
                throw new PersonalApiConfigurationException();
            foreach (var row in document.Keys)
            {
                if (row.Id == Guid.Empty || row.Revision < 1 || !Enum.IsDefined(row.LastValidation)
                    || row.Enabled && !row.Validated || ValidateLabel(row.Label) != row.Label
                    || ValidateKey(row.Secret) != row.Secret)
                    throw new PersonalApiConfigurationException();
            }
            return document;
        }
        catch (Exception ex) when (IsStorageFailure(ex) || ex is ArgumentException or NullReferenceException)
        { throw new PersonalApiConfigurationException(); }
        finally { if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext); }
    }

    private PersonalApiView View(Document document) => new(document.Revision, document.Policy, document.Provider,
        document.Revision > 0, Array.AsReadOnly(document.Keys.Select(row => new PersonalKeyView(
            row.Id, row.Revision, row.Label, "••••" + row.Secret[^4..], row.Enabled, row.Validated, row.LastValidation)).ToArray()),
        document.CooldownUntil);

    private T Locked<T>(Func<T> action)
    {
        lock (_pool.Sync)
        {
            using var mutex = new Mutex(false, _mutexName);
            var acquired = false;
            try
            {
                try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) throw new PersonalApiConfigurationException();
                return action();
            }
            finally { if (acquired) mutex.ReleaseMutex(); }
        }
    }

    private void EnsureNotInUse(Guid id)
    {
        if (_pool.InUse.GetValueOrDefault(id) > 0)
            throw new InvalidOperationException("End active Personal API work before changing this key.");
    }
    private static Credential Find(Document document, Guid id) => document.Keys.FirstOrDefault(row => row.Id == id)
        ?? throw new InvalidOperationException("Personal API key no longer exists; reload Settings.");
    private static void RequireRevision(Document document, long expected)
    {
        if (document.Revision != expected) throw new InvalidOperationException("Personal API settings changed; reload Settings.");
    }
    private static void ValidateResult(PersonalKeyValidation validation)
    {
        if (!Enum.IsDefined(validation)) throw new ArgumentException("Unsupported validation result.");
    }
    private static string ValidateLabel(string label)
    {
        if (label is null || label.Length > 64 || label.Any(char.IsControl)) throw new ArgumentException("Use a label up to 64 characters without control characters.");
        return label.Trim();
    }
    internal static string ValidateKey(string key)
    {
        if (key is null || key.Length is < 16 or > 512 || key.Any(c => c < 33 || c > 126))
            throw new ArgumentException("Enter a valid API key without spaces or control characters.");
        return key;
    }
    private static bool IsStorageFailure(Exception ex) => ex is IOException or UnauthorizedAccessException
        or CryptographicException or JsonException or NotSupportedException;

    private static bool ExistsStrict(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static void WriteAtomic(string path, byte[] data)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            {
                stream.Write(data);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed class PoolState
    {
        internal object Sync { get; } = new();
        internal Guid Selected;
        internal Dictionary<Guid, int> InUse { get; } = new();
    }
    private sealed class CredentialLease(PersonalApiVault vault, Credential row) : IPersonalCredentialLease
    {
        internal PersonalApiVault Vault { get; } = vault;
        internal byte[] SecretBytes = Encoding.UTF8.GetBytes(row.Secret);
        public Guid SlotId { get; } = row.Id;
        public long CredentialRevision { get; } = row.Revision;
        public void Dispose()
        {
            lock (Vault._pool.Sync)
            {
                if (SecretBytes is null) return;
                CryptographicOperations.ZeroMemory(SecretBytes);
                SecretBytes = null;
                if (--Vault._pool.InUse[SlotId] == 0) Vault._pool.InUse.Remove(SlotId);
            }
        }
    }
    private sealed class Document
    {
        public Document() { }
        [JsonRequired] public int Schema { get; set; } = 1;
        [JsonRequired] public Guid StoreId { get; set; }
        [JsonRequired] public long Revision { get; set; }
        [JsonRequired] public AiBackendPolicy Policy { get; set; } = AiBackendPolicy.Managed;
        [JsonRequired] public PersonalAiProvider Provider { get; set; } = PersonalAiProvider.Gemini;
        [JsonRequired] public List<Credential> Keys { get; set; } = new();
        public DateTimeOffset? CooldownUntil { get; set; }
    }
    private sealed class Credential
    {
        public Credential() { }
        [JsonRequired] public Guid Id { get; set; }
        [JsonRequired] public long Revision { get; set; }
        [JsonRequired] public string Label { get; set; }
        [JsonRequired] public string Secret { get; set; }
        [JsonRequired] public bool Enabled { get; set; }
        [JsonRequired] public bool Validated { get; set; }
        [JsonRequired] public PersonalKeyValidation LastValidation { get; set; }
    }
}
