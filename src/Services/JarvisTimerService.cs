namespace KeyboardWtf.Services;

using KeyboardWtf.Models;
using KeyboardWtf.Services.Updates;

public enum JarvisTimerState
{
    Running,
    Completed,
    Cancelled,
}

public sealed class JarvisTimerSnapshot
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset DueAt { get; init; }
    public JarvisTimerState State { get; init; }
    public TimeSpan Remaining =>
        State == JarvisTimerState.Running
            ? TimeSpan.FromMilliseconds(Math.Max(0, (DueAt - DateTimeOffset.Now).TotalMilliseconds))
            : TimeSpan.Zero;
}

public sealed class JarvisTimerService : IDisposable
{
    private sealed class Runtime
    {
        public JarvisTimerSnapshot Snapshot { get; init; }
        public ITimer Timer { get; set; }
        public IDisposable UpdateWork { get; init; }
        public int DrainStarted;
        public Task DrainTask { get; set; }
    }

    private readonly object _sync = new();
    private readonly Dictionary<string, Runtime> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string, string> _notify;
    private readonly Action<string> _speak;
    private readonly Action<string, string, bool> _history;
    private readonly UpdateWorkAdmissionGate _updateWorkAdmission;
    private readonly Func<TimerCallback, object, TimeSpan, TimeSpan, ITimer> _timerFactory;
    private bool _disposed;

    public JarvisTimerService(
        NotificationService notifications,
        JarvisActionHistoryService history)
        : this(notifications, history, null)
    {
    }

    internal JarvisTimerService(
        NotificationService notifications,
        JarvisActionHistoryService history,
        UpdateWorkAdmissionGate updateWorkAdmission)
        : this(
            (title, message) => notifications.Attention(title, message),
            // Timer lifecycle messages are deterministic English system text.
            // Do not use "auto", which can retain a previously selected SAPI
            // voice from another language.
            message => TtsService.Instance.Speak(message, "en", updateWorkAdmission),
            (kind, detail, ok) => history.Add(kind, detail, ok),
            updateWorkAdmission)
    {
    }

    internal JarvisTimerService(
        Action<string, string> notify,
        Action<string> speak,
        Action<string, string, bool> history,
        UpdateWorkAdmissionGate updateWorkAdmission = null,
        Func<TimerCallback, object, TimeSpan, TimeSpan, ITimer> timerFactory = null)
    {
        _notify = notify ?? ((_, _) => { });
        _speak = speak ?? (_ => { });
        _history = history ?? ((_, _, _) => { });
        _updateWorkAdmission = updateWorkAdmission;
        // Internal factories must not invoke callbacks inline, and must clean
        // up any resource they create if they throw before returning it.
        _timerFactory = timerFactory ?? ((callback, state, due, period) =>
            new System.Threading.Timer(callback, state, due, period));
    }

    public event Action<JarvisTimerSnapshot> Changed;
    public event Action<JarvisLifecycleAnnouncement> AnnouncementRequested;

    public IReadOnlyList<JarvisTimerSnapshot> ActiveTimers
    {
        get
        {
            lock (_sync)
            {
                return _active.Values
                    .Select(runtime => runtime.Snapshot)
                    .OrderBy(snapshot => snapshot.DueAt)
                    .ToArray();
            }
        }
    }

    public JarvisTimerSnapshot Start(TimeSpan duration, string label)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var startWork = AcquireUpdateWork();
        duration = TimeSpan.FromSeconds(Math.Clamp(duration.TotalSeconds, 1, 24 * 60 * 60));
        var now = DateTimeOffset.Now;
        var snapshot = new JarvisTimerSnapshot
        {
            Id = Guid.NewGuid().ToString("N"),
            Label = string.IsNullOrWhiteSpace(label)
                ? HumanDuration(duration) + " timer"
                : Limit(label.Trim(), 80),
            StartedAt = now,
            DueAt = now + duration,
            State = JarvisTimerState.Running,
        };

        var runtime = new Runtime
        {
            Snapshot = snapshot,
            UpdateWork = AcquireUpdateWork(),
        };
        try
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                // Keep the original armed constructor. Its asynchronous callback
                // takes this same lock, so publication always precedes completion.
                runtime.Timer = _timerFactory(
                    _ => Complete(snapshot.Id), null, duration, Timeout.InfiniteTimeSpan)
                    ?? throw new InvalidOperationException("The timer factory did not return a timer.");
                _active[snapshot.Id] = runtime;
            }
        }
        catch
        {
            if (runtime.Timer is null) runtime.UpdateWork?.Dispose();
            else BeginDrain(runtime, stop: true);
            throw;
        }
        // A valid armed timer survives subscriber failures, as before. The
        // separate Start owner covers these effects even if the timer completes.
        _history("timer_started", snapshot.Label, true);
        Changed?.Invoke(snapshot);
        return snapshot;
    }

    public int Cancel(string query = "")
    {
        Runtime[] cancelled;
        lock (_sync)
        {
            var normalized = (query ?? "").Trim();
            var matches = normalized.Equals("all", StringComparison.OrdinalIgnoreCase)
                ? _active.Values.ToArray()
                : string.IsNullOrWhiteSpace(normalized)
                    ? _active.Values
                        .OrderByDescending(runtime => runtime.Snapshot.StartedAt)
                        .Take(1)
                        .ToArray()
                    : _active.Values
                        .Where(runtime =>
                            runtime.Snapshot.Id.StartsWith(normalized, StringComparison.OrdinalIgnoreCase)
                            || runtime.Snapshot.Label.Contains(normalized, StringComparison.OrdinalIgnoreCase))
                        .ToArray();
            cancelled = matches
                .Where(runtime => _active.Remove(runtime.Snapshot.Id))
                .ToArray();
        }

        try
        {
            foreach (var runtime in cancelled)
            {
                runtime.Timer.Dispose();
                var snapshot = Copy(runtime.Snapshot, JarvisTimerState.Cancelled);
                _history("timer_cancelled", snapshot.Label, true);
                Changed?.Invoke(snapshot);
            }
            if (cancelled.Length > 0)
            {
                RequestAnnouncement(new JarvisLifecycleAnnouncement
                {
                    Source = "timer",
                    State = "cancelled",
                    Detail = cancelled.Length == 1
                        ? $"{cancelled[0].Snapshot.Label} was cancelled."
                        : $"{cancelled.Length} timers were cancelled.",
                    FallbackSpeech = cancelled.Length == 1
                        ? "Timer cancelled."
                        : "Timers cancelled.",
                });
            }
            return cancelled.Length;
        }
        finally
        {
            // Keep ALL owners through the grouped announcement/child handoff.
            // Also stop every removed timer if an earlier observer throws.
            foreach (var runtime in cancelled) BeginDrain(runtime, stop: true);
        }
    }

    private void Complete(string id)
    {
        Runtime runtime;
        lock (_sync)
        {
            if (!_active.Remove(id, out runtime))
                return;
        }

        try
        {
            runtime.Timer.Dispose();
            var snapshot = Copy(runtime.Snapshot, JarvisTimerState.Completed);
            _notify("Jarvis timer", snapshot.Label);
            _history("timer_finished", snapshot.Label, true);
            Changed?.Invoke(snapshot);
            RequestAnnouncement(new JarvisLifecycleAnnouncement
            {
                Source = "timer",
                State = "completed",
                Detail = $"{snapshot.Label} finished.",
                FallbackSpeech = $"{snapshot.Label} finished.",
            });
        }
        finally { BeginDrain(runtime, stop: true); }
    }

    private void RequestAnnouncement(JarvisLifecycleAnnouncement announcement)
    {
        var handler = AnnouncementRequested;
        if (handler == null)
        {
            _speak(announcement.FallbackSpeech);
            return;
        }

        try
        {
            handler(announcement);
        }
        catch (Exception ex)
        {
            AppLog.Warning(ex, "Timer lifecycle announcement handler failed");
            _speak(announcement.FallbackSpeech);
        }
    }

    public void Dispose()
    {
        Runtime[] active;
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            active = _active.Values.ToArray();
            _active.Clear();
        }
        try
        {
            foreach (var runtime in active)
                runtime.Timer.Dispose();
        }
        finally
        {
            foreach (var runtime in active) BeginDrain(runtime, stop: true);
        }
    }

    private IDisposable AcquireUpdateWork()
    {
        if (_updateWorkAdmission is null) return null;
        if (!_updateWorkAdmission.TryAcquire(out var lease))
            throw new InvalidOperationException("An update is preparing installation. Please wait.");
        return lease;
    }

    private void BeginDrain(Runtime runtime, bool stop)
    {
        if (Interlocked.Exchange(ref runtime.DrainStarted, 1) != 0) return;
        // Never join a timer callback from itself. Drain observes its real
        // completion asynchronously; a failed drain remains conservatively busy.
        runtime.DrainTask = DrainAsync(runtime, stop);
    }

    private static async Task DrainAsync(Runtime runtime, bool stop)
    {
        try
        {
            if (stop) runtime.Timer.Dispose();
            if (runtime.UpdateWork is null) return; // Legacy callers need no admission accounting.
            await runtime.Timer.DisposeAsync().ConfigureAwait(false);
            runtime.UpdateWork.Dispose();
        }
        catch (Exception ex)
        {
            AppLog.Warning(ex, "Timer update ownership drain failed; work remains busy");
        }
    }

    private static JarvisTimerSnapshot Copy(
        JarvisTimerSnapshot source,
        JarvisTimerState state) => new()
    {
        Id = source.Id,
        Label = source.Label,
        StartedAt = source.StartedAt,
        DueAt = source.DueAt,
        State = state,
    };

    private static string HumanDuration(TimeSpan duration) =>
        duration.TotalMinutes >= 1 && duration.TotalSeconds % 60 == 0
            ? $"{duration.TotalMinutes:0} minute"
            : $"{duration.TotalSeconds:0} second";

    private static string Limit(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum];
}
