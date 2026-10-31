namespace KeyboardWtf.Services;

// Whisper processors borrow their factory's native context. Retirement must
// stop new borrowers without freeing that context underneath an active one.
// T is only to test this ownership boundary without loading native models.
internal sealed class WhisperModelLifetime<T> : IDisposable where T : class, IDisposable
{
    private readonly object _gate = new();
    private Generation _current;
    private bool _disposed;

    internal bool IsLoaded { get { lock (_gate) return _current is not null; } }
    internal bool IsDisposed { get { lock (_gate) return _disposed; } }
    internal bool Matches(string path) { lock (_gate) return _current is { } current && current.Path == path; }

    internal Lease Borrow()
    {
        lock (_gate)
        {
            if (_disposed || _current is null) return null;
            _current.Borrowers++;
            return new Lease(this, _current, _current.Value);
        }
    }

    // Takes ownership even when shutdown already won publication.
    internal bool TryReplace(T resource, string path)
    {
        ArgumentNullException.ThrowIfNull(resource);
        T release;
        bool accepted;
        lock (_gate)
        {
            accepted = !_disposed;
            release = accepted ? Retire(_current) : resource;
            if (accepted) _current = new Generation(resource, path);
        }
        release?.Dispose();
        return accepted;
    }

    internal void Clear() => RetireCurrent(shutdown: false);
    public void Dispose() => RetireCurrent(shutdown: true);

    private void RetireCurrent(bool shutdown)
    {
        T release;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = shutdown;
            release = Retire(_current);
            _current = null;
        }
        release?.Dispose();
    }

    private static T Retire(Generation generation)
    {
        if (generation is null) return null;
        generation.Retired = true;
        return generation.Borrowers == 0 ? TakeValue(generation) : null;
    }

    private static T TakeValue(Generation generation)
    {
        var value = generation.Value;
        generation.Value = null;
        return value;
    }

    private void Release(Generation generation)
    {
        T release;
        lock (_gate)
        {
            generation.Borrowers--;
            release = generation.Retired && generation.Borrowers == 0 ? TakeValue(generation) : null;
        }
        release?.Dispose();
    }

    internal sealed class Generation(T value, string path)
    {
        internal T Value = value;
        internal readonly string Path = path;
        internal int Borrowers;
        internal bool Retired;
    }

    internal sealed class Lease : IDisposable
    {
        private WhisperModelLifetime<T> _owner;
        private readonly Generation _generation;
        internal T Value { get; }

        internal Lease(WhisperModelLifetime<T> owner, Generation generation, T value)
        { _owner = owner; _generation = generation; Value = value; }

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(_generation);
    }
}
