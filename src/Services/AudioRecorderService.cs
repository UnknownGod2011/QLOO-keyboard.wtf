namespace KeyboardWtf.Services;

using KeyboardWtf.Models;
using KeyboardWtf.Services.Updates;

using NAudio.Wave;

// MME gives 50ms callbacks with no exclusive microphone hold. WASAPI exclusive mode would
// starve other apps, while shared mode adds latency and extra COM setup.
// Future: WASAPI loopback (output capture) would enable "transcribe what's playing" mode.
public sealed class AudioRecorderService : IDisposable
{
    private sealed class RecordingRuntime
    {
        internal IWaveIn Input { get; init; }
        internal TaskCompletionSource Stopped { get; init; }
        internal IDisposable UpdateWork { get; init; }
        internal bool StopEventObserved;
        internal bool FinalizationCompleted;
        internal bool InputDisposalSucceeded;
        internal bool Released;
        internal int CallbackCount;
    }
    // WinMM's WAVE_MAPPER value. NAudio forwards DeviceNumber directly to
    // waveInOpen, so -1 follows the current Windows default input instead of
    // pinning first-run users to whatever happens to be device zero.
    internal const int SystemDefaultDeviceNumber = -1;

    private readonly Func<int, IWaveIn> _createInput;
    private UpdateWorkAdmissionGate _updateWorkAdmission;
    private readonly object _captureGate = new();
    private IWaveIn _waveIn;
    private MemoryStream _pcmStream;
    private WaveFormat _waveFormat;
    private bool _disposed;
    private bool _stopRequested;
    private TaskCompletionSource _recordingStopped;
    private Task<byte[]> _stopTask;
    private RecordingRuntime _currentRuntime;
    private readonly List<RecordingRuntime> _retiredRuntimes = new();
    private volatile Exception _lastRecordingError;

    public AudioRecorderService() : this(sampleRate => new WaveInEvent
    {
        DeviceNumber = ResolveDeviceNumber(KeyboardWtfState.SelectedMicrophoneIndex),
        WaveFormat = new WaveFormat(sampleRate, 1),
        BufferMilliseconds = 50,
    })
    { }

    internal AudioRecorderService(Func<int, IWaveIn> createInput, UpdateWorkAdmissionGate updateWorkAdmission = null)
    {
        _createInput = createInput;
        _updateWorkAdmission = updateWorkAdmission;
    }

    internal void AttachUpdateWorkAdmission(UpdateWorkAdmissionGate updateWorkAdmission)
    {
        lock (_captureGate)
        {
            if (_waveIn is not null || IsRecording || _retiredRuntimes.Count != 0)
                throw new InvalidOperationException("Update admission must be attached before recording starts.");
            _updateWorkAdmission = updateWorkAdmission;
        }
    }

    public bool IsRecording { get; private set; }
    public Exception LastRecordingError => _lastRecordingError;
    public event Action<byte[], int> PcmDataAvailable;

    // volatile: DataAvailable writes on the NAudio callback thread; meters read on timer threads.
    // No lock needed — a torn int read is acceptable for a level display.
    private volatile int _currentPeak;
    public int CurrentPeak => _currentPeak;
    public double CurrentPeakDb =>
        _currentPeak > 0 ? 20.0 * Math.Log10(_currentPeak / 32768.0) : -96.0;

    public void StartRecording(int sampleRate = 16000)
    {
        lock (_captureGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsRecording)
                return;
            if (_waveIn != null)
                throw new InvalidOperationException("The previous microphone capture is still closing. Try again in a moment.");

            IDisposable updateWork = null;
            RecordingRuntime runtime = null;
            try
            {
                updateWork = AcquireUpdateWork();
                _waveFormat = new WaveFormat(sampleRate, 1);
                _pcmStream = new MemoryStream();
                _recordingStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _stopTask = null;
                _stopRequested = false;
                _lastRecordingError = null;
                _currentPeak = 0;
                _waveIn = _createInput(sampleRate);
                runtime = new RecordingRuntime
                {
                    Input = _waveIn,
                    Stopped = _recordingStopped,
                    UpdateWork = updateWork,
                };
                _currentRuntime = runtime;
                _waveIn.DataAvailable += OnDataAvailable;
                _waveIn.RecordingStopped += OnRecordingStopped;
                _waveIn.StartRecording();
                IsRecording = true;
            }
            catch
            {
                CleanupRecordingResources();
                if (runtime is not null)
                {
                    // A failed start is not evidence that a possibly started
                    // input delivered its terminal event. Keep uncertain work owned.
                    runtime.FinalizationCompleted = true;
                    ReleaseRuntimeIfDrained(runtime);
                }
                else
                    updateWork?.Dispose();
                throw;
            }
        }
    }

    internal static int ResolveDeviceNumber(int selectedIndex) =>
        selectedIndex >= 0 ? selectedIndex : SystemDefaultDeviceNumber;

    public Task<byte[]> StopRecordingAsync()
    {
        lock (_captureGate)
        {
            if (_stopTask != null)
                return _stopTask;
            if (_waveIn == null)
                return Task.FromResult(Array.Empty<byte>());
            IsRecording = false;
            _stopRequested = true;
            var runtime = _currentRuntime;
            return _stopTask = FinishRecordingAsync(_waveIn, runtime.Stopped.Task, runtime);
        }
    }

    private async Task<byte[]> FinishRecordingAsync(IWaveIn input, Task stopped, RecordingRuntime runtime)
    {
        try
        {
            input.StopRecording();
            // StopRecording requests a stop; RecordingStopped confirms the last callback.
            // Await it (never block the UI synchronization context) before taking the WAV.
            await stopped.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _lastRecordingError = ex;
            AppLog.Warning(ex, "Microphone shutdown interrupted; preserving captured audio");
        }

        try
        {
            byte[] pcmData;
            WaveFormat format;
            lock (_captureGate)
            {
                pcmData = _pcmStream.ToArray();
                format = _waveFormat;
                CleanupRecordingResources();
            }
            if (KeyboardWtfState.UseNoiseGate)
                pcmData = NoiseGateFilter.Apply(pcmData, format.SampleRate);
            using var wavStream = new MemoryStream();
            // Dispose finalizes the RIFF byte counts; MemoryStream.ToArray remains valid afterward.
            using (var writer = new WaveFileWriter(wavStream, format))
                writer.Write(pcmData, 0, pcmData.Length);
            return wavStream.ToArray();
        }
        finally { MarkFinalizationCompleted(runtime); }
    }

    private void OnDataAvailable(object sender, WaveInEventArgs e)
    {
        RecordingRuntime runtime = null;
        try
        {
            lock (_captureGate)
            {
                runtime = FindRuntime(sender);
                if (runtime is null)
                    return;
                runtime.CallbackCount++;
                // Preserve the original current-input-only delivery rule.
                // Retired callback ownership must never become new capture data.
                if (!ReferenceEquals(runtime, _currentRuntime))
                    return;
                _pcmStream.Write(e.Buffer, 0, e.BytesRecorded);
                _currentPeak = Pcm16Peak(e.Buffer.AsSpan(0, e.BytesRecorded));
                KeyboardWtfState.InputLevelDb = CurrentPeakDb;
            }
            PcmDataAvailable?.Invoke(e.Buffer, e.BytesRecorded);
        }
        finally { if (runtime is not null) CallbackFinished(runtime); }
    }

    internal static int Pcm16Peak(ReadOnlySpan<byte> pcm)
    {
        var peak = 0;
        for (var i = 0; i < pcm.Length - 1; i += 2)
        {
            // -32768 is valid PCM: widen BEFORE Abs, whose short overload throws.
            var sample = Math.Abs((int)(short)(pcm[i] | (pcm[i + 1] << 8)));
            if (sample > peak)
                peak = sample;
        }
        return peak;
    }

    private void OnRecordingStopped(object sender, StoppedEventArgs e)
    {
        RecordingRuntime runtime = null;
        try
        {
            lock (_captureGate)
            {
                runtime = FindRuntime(sender);
                if (runtime is null)
                    return;
                runtime.CallbackCount++;
                if (ReferenceEquals(runtime, _currentRuntime)
                    && (e.Exception != null || !_stopRequested))
                {
                    _lastRecordingError = e.Exception ?? new IOException("Microphone capture ended unexpectedly.");
                    AppLog.Error($"Recording error: {LastRecordingError.Message}");
                }
                runtime.StopEventObserved = true;
                runtime.Stopped.TrySetResult();
            }
        }
        finally { if (runtime is not null) CallbackFinished(runtime); }
    }

    private void CleanupRecordingResources()
    {
        if (_waveIn != null)
        {
            _waveIn.DataAvailable -= OnDataAvailable;
            _waveIn.RecordingStopped -= OnRecordingStopped;
            try
            {
                _waveIn.Dispose();
                if (_currentRuntime is { } disposedRuntime)
                    disposedRuntime.InputDisposalSucceeded = true;
            }
            catch (Exception ex) { AppLog.Warning(ex, "Microphone device cleanup failed"); }
            _waveIn = null;
        }

        _pcmStream?.Dispose();
        _pcmStream = null;
        if (_currentRuntime is { } runtime)
        {
            // Without update admission, preserve the original stale-event
            // suppression without retaining failed, unowned input identities.
            if (runtime.UpdateWork is not null)
                _retiredRuntimes.Add(runtime);
            _currentRuntime = null;
        }
        KeyboardWtfState.InputLevelDb = -96;
    }

    private IDisposable AcquireUpdateWork()
    {
        if (_updateWorkAdmission is null) return null;
        if (!_updateWorkAdmission.TryAcquire(out var lease))
            throw new InvalidOperationException("An update is preparing installation. Please wait.");
        return lease;
    }

    private RecordingRuntime FindRuntime(object sender)
    {
        if (_currentRuntime is { } current && ReferenceEquals(sender, current.Input))
            return current;
        return _retiredRuntimes.FirstOrDefault(runtime => ReferenceEquals(sender, runtime.Input));
    }

    private void MarkFinalizationCompleted(RecordingRuntime runtime)
    {
        if (runtime is null) return;
        lock (_captureGate)
        {
            runtime.FinalizationCompleted = true;
            ReleaseRuntimeIfDrained(runtime);
        }
    }

    private void CallbackFinished(RecordingRuntime runtime)
    {
        lock (_captureGate)
        {
            runtime.CallbackCount--;
            ReleaseRuntimeIfDrained(runtime);
        }
    }

    private void ReleaseRuntimeIfDrained(RecordingRuntime runtime)
    {
        if (runtime.Released || !runtime.StopEventObserved || !runtime.InputDisposalSucceeded
            || !runtime.FinalizationCompleted || runtime.CallbackCount != 0)
            return;
        runtime.Released = true;
        _retiredRuntimes.Remove(runtime);
        runtime.UpdateWork?.Dispose();
    }

    public void CancelRecording() => _ = DiscardRecordingAsync();

    private async Task DiscardRecordingAsync()
    {
        try
        { await StopRecordingAsync().ConfigureAwait(false); }
        catch (Exception ex) { AppLog.Warning(ex, "Microphone cancellation cleanup failed"); }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        CancelRecording();
    }
}
