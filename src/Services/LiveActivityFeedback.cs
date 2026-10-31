namespace KeyboardWtf.Services;

using System.Diagnostics;

// Display observation only. This identity never grants task/turn authority and
// cannot complete, cancel, retry or suppress provider work or speech.
internal sealed class LiveActivityFeedback
{
    internal static readonly TimeSpan DisplayDelay = TimeSpan.FromSeconds(2);
    private readonly object _gate = new();
    private readonly long _frequency;
    private long _input, _responseInput, _since, _publishedRevision;
    private bool _inputStarted, _transcriptObserved, _waiting, _selectedSource, _afterToolReceipt, _ambiguous;

    internal LiveActivityFeedback(long frequency = 0) =>
        _frequency = frequency > 0 ? frequency : Stopwatch.Frequency;

    internal long InputIdentity { get { lock (_gate) return _input; } }
    internal long ResponseIdentity { get { lock (_gate) return _responseInput; } }

    internal long BeginInput()
    {
        lock (_gate)
        {
            var previous = _publishedRevision;
            ++_input;
            _inputStarted = true;
            _transcriptObserved = _waiting = _selectedSource = _afterToolReceipt = false;
            // No provider response ID is available. Overlapping input remains
            // visually quiet until a later tool can be bound after the old reply.
            _ambiguous = _responseInput != 0;
            _since = _publishedRevision = 0;
            return previous;
        }
    }

    internal void ObserveTranscript()
    {
        lock (_gate) { if (_inputStarted) _transcriptObserved = true; }
    }

    internal void FinalInput(long now, bool submittedText = false)
    {
        lock (_gate)
        {
            if (!_inputStarted || (!submittedText && !_transcriptObserved) || _waiting) return;
            _transcriptObserved |= submittedText;
            _waiting = true;
            _since = now;
            if (_responseInput == 0) _responseInput = _input;
            _ambiguous = _responseInput != _input;
        }
    }

    internal long BindToolInput()
    {
        lock (_gate)
        {
            if (_responseInput == 0 && _inputStarted)
            {
                _responseInput = _input;
                _ambiguous = false;
            }
            return _responseInput;
        }
    }

    internal long ToolStarted(long input)
    {
        lock (_gate)
        {
            if (input != _input) return 0;
            _inputStarted |= _transcriptObserved;
            return ClearWait();
        }
    }

    internal long ToolReceipt(long input, long now, bool selectedSource, bool featureOwned)
    {
        lock (_gate)
        {
            if (input != _input) return 0;
            var previous = ClearWait();
            if (_inputStarted && !featureOwned)
            {
                _waiting = _afterToolReceipt = true;
                _selectedSource = selectedSource;
                _since = now;
            }
            return previous;
        }
    }

    internal long OutputOrCancel(long input)
    {
        lock (_gate)
        {
            if (input != _input) return 0;
            _inputStarted = false;
            return ClearWait();
        }
    }

    // A tool-only provider boundary is not proof of a delivered answer. Release
    // only this visual revision so a later monitor tick can restore its caption.
    internal void ProviderBoundary(long input)
    {
        lock (_gate)
        {
            if (input == 0 || input != _responseInput) return;
            if (input == _input && _waiting && _afterToolReceipt)
            {
                _publishedRevision = 0;
                return;
            }
            _responseInput = 0;
            if (input == _input) ClearWait();
        }
    }

    internal long Reset(bool beginInput = false)
    {
        lock (_gate)
        {
            ++_input;
            _inputStarted = beginInput;
            _transcriptObserved = _ambiguous = false;
            _responseInput = 0;
            return ClearWait();
        }
    }

    internal long TryPublish(long now, bool mayPublish, Func<string, long> publish)
    {
        lock (_gate)
        {
            if (!mayPublish || !_waiting || _ambiguous || _publishedRevision != 0
                || (now - _since) / (double)_frequency < DisplayDelay.TotalSeconds) return 0;
            // The small synchronous revision-CAS callback performs display only.
            // Holding the observer lock prevents a new input from publishing an
            // older caption after it has invalidated this display identity.
            var revision = publish(_selectedSource ? "Processing selected text…" : "Working on your request…");
            if (revision != 0) _publishedRevision = revision;
            return revision;
        }
    }

    private long ClearWait()
    {
        var previous = _publishedRevision;
        _waiting = _selectedSource = _afterToolReceipt = false;
        _since = _publishedRevision = 0;
        return previous;
    }
}
