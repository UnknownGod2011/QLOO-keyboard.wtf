using System.Reflection;
using System.Collections.Concurrent;
using KeyboardWtf.Models;
using KeyboardWtf.Services;
using KeyboardWtf.Services.Updates;
using NAudio.Wave;
using Xunit;

namespace KeyboardWtf.Tests;

// Passive finite-work accounting only. These tests do not use a real microphone,
// recognizer, provider, destination, or Windows UI. Native callback drainage
// remains a separate updater-safety boundary.
[Collection("Live event integration")]
public sealed class VoiceCaptureUpdateWorkTests
{
    [Fact]
    public void Frozen_start_refuses_before_microphone_start_and_preserves_existing_busy_eligibility()
    {
        using var fixture = new Fixture();
        var wasDownloading = KeyboardWtfState.IsDownloadingModels;
        KeyboardWtfState.IsDownloadingModels = false;
        Assert.True(fixture.Gate.TryFreeze(out var frozen));
        try
        {
            var error = Assert.Throws<InvalidOperationException>(() => fixture.Capture.Start(RecordingMode.VoiceAssistant));
            Assert.Contains("update", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, fixture.Input.StartCalls);
            Assert.False(KeyboardWtfState.IsRecording);

            KeyboardWtfState.IsProcessing = true;
            Assert.False(fixture.Capture.Start(RecordingMode.VoiceAssistant));
            Assert.Equal(0, fixture.Input.StartCalls);
        }
        finally
        {
            KeyboardWtfState.IsDownloadingModels = wasDownloading;
            KeyboardWtfState.IsProcessing = false;
            frozen.Dispose();
        }
        AssertIdle(fixture.Gate);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Frozen_watchers_refuse_before_delay_or_completion_effects(bool silenceWatcher)
    {
        using var fixture = new Fixture();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.True(fixture.Gate.TryFreeze(out var frozen));
        try
        {
            var error = await Record.ExceptionAsync(() => Watch(fixture.Capture, silenceWatcher, cancelled.Token));
            Assert.IsType<InvalidOperationException>(error);
            Assert.Contains("update", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, fixture.Input.StopCalls);
            Assert.Equal(0, fixture.HandlerCalls);
        }
        finally { frozen.Dispose(); }
        AssertIdle(fixture.Gate);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Watcher_owns_original_delay_until_cancellation_actually_unwinds(bool silenceWatcher)
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        KeyboardWtfState.IsRecording = true;
        KeyboardWtfState.CurrentRecordingMode = RecordingMode.VoiceAssistant;
        Set(fixture.Capture, "_recordingStartUtc", DateTime.UtcNow);
        var watcher = Watch(fixture.Capture, silenceWatcher, cancellation.Token);
        try
        {
            Assert.False(watcher.IsCompleted);
            AssertBusy(fixture.Gate);
        }
        finally
        {
            cancellation.Cancel();
            await watcher.WaitAsync(TimeSpan.FromSeconds(5));
        }
        AssertIdle(fixture.Gate);
        Assert.Equal(0, fixture.Input.StopCalls);
        Assert.Equal(0, fixture.HandlerCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancelled_watcher_stays_owned_while_its_original_continuation_is_queued(bool silenceWatcher)
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var heldContext = new HeldContext();
        KeyboardWtfState.IsRecording = true;
        KeyboardWtfState.CurrentRecordingMode = RecordingMode.VoiceAssistant;
        Set(fixture.Capture, "_recordingStartUtc", DateTime.UtcNow);
        var previousContext = SynchronizationContext.Current;
        Task watcher;
        try
        {
            SynchronizationContext.SetSynchronizationContext(heldContext);
            watcher = Watch(fixture.Capture, silenceWatcher, cancellation.Token);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
        try
        {
            cancellation.Cancel();
            await heldContext.Posted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(watcher.IsCompleted);
            AssertBusy(fixture.Gate);
        }
        finally
        {
            cancellation.Cancel();
            heldContext.Drain();
            await watcher.WaitAsync(TimeSpan.FromSeconds(5));
        }
        AssertIdle(fixture.Gate);
    }

    [Fact]
    public async Task Auto_stop_awaits_original_completion_and_competing_completion_still_delivers_once()
    {
        using var fixture = new Fixture();
        var entered = Signal();
        var release = Signal();
        Set(fixture.Capture, "_audioCompletionHandler", (Func<SmartWritingAudioCapture, Task>)(async audio =>
        {
            fixture.HandlerCalls++;
            Assert.True(audio.WavData.Length >= 44);
            entered.TrySetResult(true);
            await release.Task;
        }));
        fixture.StartSmartWriting();
        fixture.Input.Emit(new byte[1600]);
        var autoStop = Invoke(fixture.Capture, "AutoStopAfterLimitAsync", RecordingMode.VoiceAssistant, 0, CancellationToken.None);
        try
        {
            AssertBusy(fixture.Gate);
            fixture.Input.Finish();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Invoke(fixture.Capture, "CompleteRecordingAsync");
            Assert.Equal(1, fixture.Input.StopCalls);
            Assert.Equal(1, fixture.HandlerCalls);
            Assert.False(autoStop.IsCompleted);
            AssertBusy(fixture.Gate);
        }
        finally
        {
            fixture.Input.Finish();
            release.TrySetResult(true);
            await autoStop.WaitAsync(TimeSpan.FromSeconds(5));
        }
        await AssertEventuallyIdle(fixture.Gate);
    }

    [Fact]
    public void Frozen_otherwise_idle_capture_still_accepts_explicit_cancel()
    {
        using var fixture = new Fixture();
        Assert.True(fixture.Gate.TryFreeze(out var frozen));
        try
        {
            fixture.Capture.Cancel();
            Assert.Equal("Cancelled", KeyboardWtfState.LastSendResult);
            Assert.Equal(0, fixture.Input.StartCalls);
            Assert.Equal(0, fixture.Input.StopCalls);
        }
        finally { frozen.Dispose(); }
        AssertIdle(fixture.Gate);
    }

    [Fact]
    public async Task Frozen_completion_refuses_before_final_audio_stop_or_handler_effects()
    {
        using var fixture = new Fixture();
        KeyboardWtfState.IsRecording = true;
        KeyboardWtfState.CurrentRecordingMode = RecordingMode.VoiceAssistant;
        Set(fixture.Capture, "_audioCompletionHandler", (Func<SmartWritingAudioCapture, Task>)(_ =>
            throw new Xunit.Sdk.XunitException("Frozen completion must not invoke the handler.")));
        Assert.True(fixture.Gate.TryFreeze(out var frozen));
        try
        {
            var completion = Invoke(fixture.Capture, "CompleteRecordingAsync");
            var error = await Record.ExceptionAsync(() => completion);
            Assert.IsType<InvalidOperationException>(error);
            Assert.Contains("update", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, fixture.Input.StopCalls);
            Assert.Equal(0, fixture.HandlerCalls);
        }
        finally { frozen.Dispose(); fixture.Capture.Cancel(); }
        AssertIdle(fixture.Gate);
    }

    [Fact]
    public async Task Completion_remains_owned_through_final_audio_drain_and_awaited_audio_handler()
    {
        using var fixture = new Fixture();
        var handlerEntered = Signal();
        var releaseHandler = Signal();
        Set(fixture.Capture, "_audioCompletionHandler", (Func<SmartWritingAudioCapture, Task>)(async audio =>
        {
            fixture.HandlerCalls++;
            Assert.True(audio.WavData.Length >= 44);
            handlerEntered.TrySetResult(true);
            await releaseHandler.Task;
        }));
        fixture.StartSmartWriting();
        fixture.Input.Emit(new byte[1600]);
        var completion = Invoke(fixture.Capture, "CompleteRecordingAsync");
        Assert.False(completion.IsCompleted);
        AssertBusy(fixture.Gate);

        fixture.Input.Finish();
        await handlerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, fixture.HandlerCalls);
        AssertBusy(fixture.Gate); // UI/cancellation state does not release the awaited handler.

        releaseHandler.TrySetResult(true);
        await completion.WaitAsync(TimeSpan.FromSeconds(5));
        await AssertEventuallyIdle(fixture.Gate);
        fixture.Capture.Cancel();
    }

    [Fact]
    public async Task Frozen_direct_transcription_and_polish_paths_refuse_before_provider_work()
    {
        using var fixture = new Fixture();
        KeyboardWtfState.IsRecording = true;
        KeyboardWtfState.CurrentTranscript = "source";
        Assert.True(fixture.Gate.TryFreeze(out var frozen));
        try
        {
            var transcribe = fixture.Capture.StopAndTranscribeAsync();
            var transcribeError = await Record.ExceptionAsync(() => transcribe);
            Assert.IsType<InvalidOperationException>(transcribeError);
            Assert.Equal(0, fixture.Input.StopCalls);

            var polishError = await Record.ExceptionAsync(() => fixture.Capture.ApplyPromptToCurrentTranscriptAsync("polish"));
            Assert.IsType<InvalidOperationException>(polishError);

            var capturedError = await Record.ExceptionAsync(() => fixture.Capture.TranscribeCapturedAudioAsync(new byte[44], true, CancellationToken.None));
            Assert.IsType<InvalidOperationException>(capturedError);
        }
        finally { frozen.Dispose(); fixture.Capture.Cancel(); }
        AssertIdle(fixture.Gate);
    }

    private sealed class Fixture : IDisposable
    {
        internal UpdateWorkAdmissionGate Gate { get; } = new();
        internal FakeInput Input { get; } = new();
        internal AudioRecorderService Recorder { get; }
        internal VoiceCaptureService Capture { get; }
        internal int HandlerCalls;

        internal Fixture()
        {
            Recorder = new AudioRecorderService(_ => Input);
            Capture = new VoiceCaptureService(Recorder, null, new NotificationService(null), null, Gate);
            KeyboardWtfState.IsRecording = false;
            KeyboardWtfState.IsProcessing = false;
            KeyboardWtfState.CurrentRecordingMode = RecordingMode.None;
            KeyboardWtfState.CurrentTranscript = "";
        }

        internal void StartSmartWriting()
        {
            Recorder.StartRecording();
            KeyboardWtfState.IsRecording = true;
            KeyboardWtfState.IsProcessing = false;
            KeyboardWtfState.CurrentRecordingMode = RecordingMode.VoiceAssistant;
            Set(Capture, "_recordingStartUtc", DateTime.UtcNow);
        }

        public void Dispose()
        {
            try { Capture.Cancel(); } catch { }
            Recorder.Dispose();
            KeyboardWtfState.IsRecording = false;
            KeyboardWtfState.IsProcessing = false;
            KeyboardWtfState.CurrentRecordingMode = RecordingMode.None;
            KeyboardWtfState.CurrentTranscript = "";
        }
    }

    private sealed class FakeInput : IWaveIn
    {
        public WaveFormat WaveFormat { get; set; } = new(16000, 1);
        public event EventHandler<WaveInEventArgs> DataAvailable;
        public event EventHandler<StoppedEventArgs> RecordingStopped;
        internal int StopCalls;
        internal int StartCalls;
        public void StartRecording() => Interlocked.Increment(ref StartCalls);
        public void StopRecording() => Interlocked.Increment(ref StopCalls);
        public void Dispose() { }
        internal void Emit(byte[] data) => DataAvailable?.Invoke(this, new WaveInEventArgs(data, data.Length));
        internal void Finish() => RecordingStopped?.Invoke(this, new StoppedEventArgs());
    }

    private static Task Invoke(object target, string method, params object[] arguments) => (Task)target.GetType()
        .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, arguments)!;
    private static Task Watch(VoiceCaptureService capture, bool silenceWatcher, CancellationToken cancellation) => silenceWatcher
        ? Invoke(capture, "MonitorSilenceAsync", RecordingMode.VoiceAssistant, cancellation)
        : Invoke(capture, "AutoStopAfterLimitAsync", RecordingMode.VoiceAssistant, 3600, cancellation);
    private sealed class HeldContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object State)> _pending = new();
        internal TaskCompletionSource<bool> Posted { get; } = Signal();
        public override void Post(SendOrPostCallback callback, object state)
        {
            _pending.Enqueue((callback, state));
            Posted.TrySetResult(true);
        }
        internal void Drain()
        {
            while (_pending.TryDequeue(out var item)) item.Callback(item.State);
        }
    }
    private static void Set(object target, string field, object value) => target.GetType()
        .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static bool IsBusy(UpdateWorkAdmissionGate gate)
    {
        var froze = gate.TryFreeze(out var freeze);
        freeze?.Dispose();
        return !froze;
    }
    private static void AssertBusy(UpdateWorkAdmissionGate gate) => Assert.True(IsBusy(gate));
    private static void AssertIdle(UpdateWorkAdmissionGate gate)
    {
        Assert.True(gate.TryFreeze(out var freeze));
        freeze.Dispose();
    }
    private static async Task AssertEventuallyIdle(UpdateWorkAdmissionGate gate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (IsBusy(gate) && DateTime.UtcNow < deadline) await Task.Delay(5);
        AssertIdle(gate);
    }
}
