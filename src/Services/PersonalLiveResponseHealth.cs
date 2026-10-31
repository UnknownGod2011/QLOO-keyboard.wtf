namespace KeyboardWtf.Services;

using System.Diagnostics;

// Observation only: never gates audio, retries a turn or changes a key.
// Each physical Personal socket owns its own instance.
internal sealed class PersonalLiveResponseHealth
{
    internal static readonly TimeSpan ResponseWait = TimeSpan.FromSeconds(20);
    internal static readonly TimeSpan ActivityQuietPeriod = TimeSpan.FromSeconds(2);
    private const double ActivityPeakDb = -38;
    private readonly object _gate = new();
    private readonly long _frequency;
    private long _pendingSince, _lastActivity, _lastInput, _lastResponse;
    private double _activeMs;
    private bool _advised;
    private long _completedChunks, _activeChunks;
    private double _highestPeakDb = -96;

    internal PersonalLiveResponseHealth(long frequency = 0) =>
        _frequency = frequency > 0 ? frequency : Stopwatch.Frequency;

    internal void AudioSent(int bytes, double peakDb, long now, bool mayAwaitResponse)
    {
        lock (_gate)
        {
            _completedChunks++;
            if (double.IsFinite(peakDb)) _highestPeakDb = Math.Max(_highestPeakDb, peakDb);
            if (!mayAwaitResponse) { ResetWait(); return; }
            if (bytes <= 0 || !double.IsFinite(peakDb) || peakDb < ActivityPeakDb) return;
            if (_activeMs < 600 && _lastInput < _pendingSince && _lastActivity > 0
                && Elapsed(_lastActivity, now) > ActivityQuietPeriod) ResetWait();
            _activeChunks++;
            if (_pendingSince == 0) _pendingSince = now;
            _lastActivity = now;
            // Existing 16kHz mono PCM16; successful frames only, no content retained.
            _activeMs += bytes / 32d;
        }
    }

    internal void InputRecognized(long now)
    {
        lock (_gate)
        {
            _lastInput = now;
            if (_pendingSince == 0) _pendingSince = now;
        }
    }

    internal void ResponseReceived(long now)
    {
        lock (_gate) { _lastResponse = now; ResetWait(); }
    }

    internal string TakeAdvisory(long now, bool mayPublish)
    {
        lock (_gate)
        {
            if (!mayPublish || _advised || _pendingSince == 0
                || (_activeMs < 600 && _lastInput < _pendingSince)
                || Elapsed(_pendingSince, now) < ResponseWait
                || (_lastActivity > 0 && Elapsed(_lastActivity, now) < ActivityQuietPeriod)) return null;
            _advised = true;
            return _lastInput >= _pendingSince
                ? "Gemini has not answered this request yet. Wait a moment, or stop/start Jarvis."
                : "Audio is being sent, but no Gemini response has arrived. Wait a moment, or stop/start Jarvis.";
        }
    }

    internal string Snapshot(long now)
    {
        lock (_gate)
            return FormattableString.Invariant($"completedAudioChunks={_completedChunks} activeAudioChunks={_activeChunks} highestPeakDb={_highestPeakDb:F1} pendingMs={(_pendingSince == 0 ? 0 : Elapsed(_pendingSince, now).TotalMilliseconds):F0} recognizedInput={_lastInput >= _pendingSince && _pendingSince > 0} responseObserved={_lastResponse > 0}");
    }

    private TimeSpan Elapsed(long earlier, long now) =>
        TimeSpan.FromSeconds(Math.Max(0, now - earlier) / (double)_frequency);

    private void ResetWait()
    {
        _pendingSince = _lastActivity = 0;
        _activeMs = 0;
        _advised = false;
    }
}
