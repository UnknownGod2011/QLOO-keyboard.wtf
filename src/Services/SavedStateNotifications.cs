namespace KeyboardWtf.Services;

using System.Threading.Channels;

// Invalidation only. Existing stores retain ownership of records and persistence.
internal sealed class SavedStateNotifications : IDisposable
{
    private readonly object _lock = new();
    private readonly HashSet<Channel<long>> _subscribers = new();
    private readonly SettingsService _settings;
    private readonly LearnedMappingService _mappings;
    private readonly IntentMemoryService _memory;
    private long _revision;
    private bool _disposed;

    public SavedStateNotifications(SettingsService settings, LearnedMappingService mappings, IntentMemoryService memory)
    {
        _settings = settings;
        _mappings = mappings;
        _memory = memory;
        if (_settings != null) _settings.WorkflowsChanged += Invalidate;
        if (_mappings != null) _mappings.Changed += Invalidate;
        if (_memory != null) _memory.Changed += Invalidate;
    }

    public long Revision { get { lock (_lock) return _revision; } }
    internal int SubscriberCount { get { lock (_lock) return _subscribers.Count; } }

    public Channel<long> Subscribe()
    {
        var channel = Channel.CreateBounded<long>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            AllowSynchronousContinuations = false,
        });
        lock (_lock)
        {
            if (_disposed) channel.Writer.TryComplete();
            else
            {
                _subscribers.Add(channel);
                channel.Writer.TryWrite(_revision);
            }
        }
        return channel;
    }

    public void Unsubscribe(Channel<long> channel)
    {
        lock (_lock) _subscribers.Remove(channel);
        channel.Writer.TryComplete();
    }

    private void Invalidate()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _revision++;
            foreach (var channel in _subscribers) channel.Writer.TryWrite(_revision);
        }
    }

    internal static void Notify(Action listeners)
    {
        if (listeners == null) return;
        foreach (Action listener in listeners.GetInvocationList())
        {
            try { listener(); }
            catch (Exception ex) { AppLog.Warning(ex, "Saved-state notification failed after persistence"); }
        }
    }

    public void Dispose()
    {
        if (_settings != null) _settings.WorkflowsChanged -= Invalidate;
        if (_mappings != null) _mappings.Changed -= Invalidate;
        if (_memory != null) _memory.Changed -= Invalidate;
        lock (_lock)
        {
            _disposed = true;
            foreach (var channel in _subscribers) channel.Writer.TryComplete();
            _subscribers.Clear();
        }
    }
}
