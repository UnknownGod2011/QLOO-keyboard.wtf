using System.Collections.Concurrent;
using System.Reflection;
using KeyboardWtf.Models;
using KeyboardWtf.Services;
using KeyboardWtf.Services.Updates;
using NAudio.Wave;
using Xunit;

namespace KeyboardWtf.Tests;

// Real capture/drain/completion facade with a fake input and an awaited local
// handler. No microphone, recognizer, provider or text destination is invoked.
[Collection("Live event integration")]
public sealed class SmartWritingCompletionLifecycleTests
{
    [Fact]
    public async Task A_done_action_from_an_idle_popup_never_starts_a_capture()
    {
        using var fixture = new Fixture();
        Assert.Null(fixture.Capture.SmartWritingCompletionOperationId);
        Assert.False(await fixture.Capture.TryCompleteSmartWritingAsync(0));
        Assert.Null(fixture.Input);
        Assert.Equal(0, fixture.HandlerCalls);
        Assert.False(KeyboardWtfState.IsRecording);
        AssertIdle(fixture.Gate);
    }

    [Theory]
    [InlineData(RecordingMode.None)]
    [InlineData(RecordingMode.Dictate)]
    [InlineData(RecordingMode.Command)]
    [InlineData(RecordingMode.QuickSend)]
    [InlineData(RecordingMode.JarvisPreflight)]
    public async Task Done_cannot_start_or_complete_another_recording_mode(RecordingMode mode)
    {
        using var fixture = new Fixture();
        var operation = fixture.Arm(mode);
        Assert.Null(fixture.Capture.SmartWritingCompletionOperationId);
        Assert.False(await fixture.Capture.TryCompleteSmartWritingAsync(operation));
        Assert.Equal(1, fixture.Input.StartCalls);
        Assert.Equal(0, fixture.Input.StopCalls);
        Assert.Equal(0, fixture.HandlerCalls);
        Assert.True(KeyboardWtfState.IsRecording);
        Assert.Equal(mode, KeyboardWtfState.CurrentRecordingMode);
    }

    [Theory]
    [InlineData("processing")]
    [InlineData("polishing")]
    [InlineData("starting")]
    public async Task Done_is_unavailable_during_processing_or_microphone_start(string state)
    {
        using var fixture = new Fixture();
        var operation = fixture.Arm();
        if (state == "processing") KeyboardWtfState.IsProcessing = true;
        if (state == "polishing") KeyboardWtfState.IsProcessingAi = true;
        if (state == "starting") Set(fixture.Capture, "_recordingStartInProgress", true);
        Assert.Null(fixture.Capture.SmartWritingCompletionOperationId);
        Assert.False(await fixture.Capture.TryCompleteSmartWritingAsync(operation));
        Assert.Equal(0, fixture.Input.StopCalls);
        Assert.Equal(0, fixture.HandlerCalls);
    }

    [Fact]
    public async Task Concurrent_done_clicks_await_one_original_drain_and_polish_without_cancelling_it()
    {
        using var fixture = new Fixture();
        var operation = fixture.Arm();
        Assert.Equal(operation, fixture.Capture.SmartWritingCompletionOperationId);
        fixture.Input.Emit(new byte[1600]);
        var start = Signal();
        var clicks = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            return await fixture.Capture.TryCompleteSmartWritingAsync(operation);
        })).ToArray();
        start.TrySetResult(true);
        await fixture.Input.StopRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(fixture.Capture.SmartWritingCompletionOperationId);
        Assert.Equal(0, fixture.HandlerCalls);
        AssertBusy(fixture.Gate);
        fixture.Input.Emit(new byte[320]); // The final native callback is drained too.
        fixture.Input.Finish();
        await fixture.HandlerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, fixture.Input.StopCalls);
        Assert.Equal(1, fixture.HandlerCalls);
        Assert.True(KeyboardWtfState.IsProcessing);
        Assert.False(fixture.Capture.CurrentOperationToken.IsCancellationRequested);
        Assert.False(await fixture.Capture.TryCompleteSmartWritingAsync(operation));
        Assert.False(fixture.Capture.CurrentOperationToken.IsCancellationRequested);
        AssertBusy(fixture.Gate);
        Assert.Contains(clicks, click => !click.IsCompleted);
        fixture.ReleaseHandler.TrySetResult(true);
        var accepted = await Task.WhenAll(clicks).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(accepted, value => value);
        Assert.False(KeyboardWtfState.IsProcessing);
        Assert.Null(fixture.Capture.SmartWritingCompletionOperationId);
        using var wav = new WaveFileReader(new MemoryStream(fixture.DeliveredAudio));
        Assert.Equal(1920, wav.Length);
        AssertIdle(fixture.Gate);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Done_and_originating_timer_share_one_completion_claim(bool timerWins)
    {
        using var fixture = new Fixture();
        var operation = fixture.Arm();
        Task timer;
        Task<bool> done;
        if (timerWins)
        {
            timer = Invoke(fixture.Capture, "AutoStopOwnedRecordingAsync", RecordingMode.VoiceAssistant, 0, operation, CancellationToken.None);
            done = fixture.Capture.TryCompleteSmartWritingAsync(operation);
        }
        else
        {
            done = fixture.Capture.TryCompleteSmartWritingAsync(operation);
            timer = Invoke(fixture.Capture, "AutoStopOwnedRecordingAsync", RecordingMode.VoiceAssistant, 0, operation, CancellationToken.None);
        }
        Assert.Equal(1, fixture.Input.StopCalls);
        fixture.Input.Finish();
        await fixture.HandlerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, fixture.HandlerCalls);
        Assert.False(timerWins ? timer.IsCompleted : done.IsCompleted);
        AssertBusy(fixture.Gate);
        fixture.ReleaseHandler.TrySetResult(true);
        await timer.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(!timerWins, await done.WaitAsync(TimeSpan.FromSeconds(5)));
        AssertIdle(fixture.Gate);
    }

    [Fact]
    public async Task Automatic_final_silence_still_uses_the_existing_eight_second_completion_path()
    {
        using var fixture = new Fixture();
        fixture.Arm();
        Assert.Equal(TimeSpan.FromSeconds(8), VoiceCaptureService.SmartWritingFinalSilence);
        using var cancel = new CancellationTokenSource();
        fixture.Input.Emit([0, 64]); // Hear voice through the real peak meter.
        var watcher = Invoke(fixture.Capture, "MonitorSilenceAsync", RecordingMode.VoiceAssistant, cancel.Token);
        await Task.Delay(250);
        fixture.Input.Emit(new byte[1600]);
        try
        {
            await Task.Delay(500);
            Assert.Equal(0, fixture.Input.StopCalls);
            await fixture.Input.StopRequested.Task.WaitAsync(TimeSpan.FromSeconds(10));
            fixture.Input.Finish();
            await fixture.HandlerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, fixture.HandlerCalls);
            fixture.ReleaseHandler.TrySetResult(true);
            await watcher.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { cancel.Cancel(); }
        AssertIdle(fixture.Gate);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_queued_old_timer_cannot_finalize_a_replacement_same_mode_capture(bool silenceWatcher)
    {
        using var fixture = new Fixture();
        var oldOperation = fixture.Arm();
        using var cancelWatcher = new CancellationTokenSource();
        var context = new HeldContext();
        var previous = SynchronizationContext.Current;
        Task watcher;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            watcher = silenceWatcher
                ? Invoke(fixture.Capture, "MonitorSilenceAsync", RecordingMode.VoiceAssistant, cancelWatcher.Token)
                : Invoke(fixture.Capture, "AutoStopAfterLimitAsync", RecordingMode.VoiceAssistant, 1, cancelWatcher.Token);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        try
        {
            await context.Posted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var oldInput = fixture.Input;
            fixture.Capture.Cancel();
            oldInput.Finish();
            await fixture.Recorder.StopRecordingAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var replacement = fixture.Arm();
            Assert.NotEqual(oldOperation, replacement);
            context.Drain();
            await watcher.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(0, fixture.Input.StopCalls);
            Assert.Equal(0, fixture.HandlerCalls);
            Assert.True(KeyboardWtfState.IsRecording);
            Assert.Equal(replacement, fixture.Capture.SmartWritingCompletionOperationId);
        }
        finally
        {
            cancelWatcher.Cancel();
            context.Drain();
            await watcher.WaitAsync(TimeSpan.FromSeconds(5));
        }
        AssertIdle(fixture.Gate);
    }

    [Fact]
    public async Task A_stale_done_click_cannot_stop_the_second_dictation_cycle()
    {
        using var fixture = new Fixture();
        var first = fixture.Arm();
        var firstCompletion = fixture.Capture.TryCompleteSmartWritingAsync(first);
        fixture.Input.Finish();
        await fixture.HandlerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.ReleaseHandler.TrySetResult(true);
        Assert.True(await firstCompletion.WaitAsync(TimeSpan.FromSeconds(5)));
        var second = fixture.Arm();
        Assert.False(await fixture.Capture.TryCompleteSmartWritingAsync(first));
        Assert.Equal(0, fixture.Input.StopCalls);
        Assert.True(KeyboardWtfState.IsRecording);
        Assert.Equal(second, fixture.Capture.SmartWritingCompletionOperationId);
        var secondCompletion = fixture.Capture.TryCompleteSmartWritingAsync(second);
        fixture.Input.Finish();
        Assert.True(await secondCompletion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, fixture.Input.StopCalls);
        Assert.Equal(2, fixture.HandlerCalls);
        AssertIdle(fixture.Gate);
    }

    [Fact]
    public async Task Explicit_cancellation_during_native_drain_rejects_late_audio_and_stale_done()
    {
        using var fixture = new Fixture();
        var operation = fixture.Arm();
        var completion = fixture.Capture.TryCompleteSmartWritingAsync(operation);
        fixture.Capture.Cancel();
        Assert.Null(fixture.Capture.SmartWritingCompletionOperationId);
        Assert.False(await fixture.Capture.TryCompleteSmartWritingAsync(operation));
        fixture.Input.Finish();
        Assert.True(await completion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, fixture.HandlerCalls);
        Assert.Equal(1, fixture.Input.StopCalls);
        Assert.Equal(VoiceUiPhase.Cancelled, KeyboardWtfState.UiPhase);
        Assert.False(KeyboardWtfState.IsProcessing);
        AssertIdle(fixture.Gate);
    }

    [Fact]
    public async Task Cancellation_from_the_drained_audio_ui_notification_cannot_start_polish()
    {
        using var fixture = new Fixture();
        var operation = fixture.Arm();
        var cancelled = false;
        void CancelWhenDrained()
        {
            if (KeyboardWtfState.UiPhase != VoiceUiPhase.Transcribing || cancelled) return;
            cancelled = true;
            fixture.Capture.Cancel();
        }
        KeyboardWtfState.UiChanged += CancelWhenDrained;
        try
        {
            var completion = fixture.Capture.TryCompleteSmartWritingAsync(operation);
            fixture.Input.Finish();
            Assert.True(await completion.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(cancelled);
            Assert.Equal(0, fixture.HandlerCalls);
            Assert.Equal(VoiceUiPhase.Cancelled, KeyboardWtfState.UiPhase);
            Assert.False(KeyboardWtfState.IsProcessing);
            AssertIdle(fixture.Gate);
        }
        finally { KeyboardWtfState.UiChanged -= CancelWhenDrained; }
    }

    [Fact]
    public async Task Frozen_update_admission_refuses_done_before_audio_stop_and_releases_its_claim()
    {
        using var fixture = new Fixture();
        var operation = fixture.Arm();
        Assert.True(fixture.Gate.TryFreeze(out var frozen));
        try
        {
            var error = await Record.ExceptionAsync(() => fixture.Capture.TryCompleteSmartWritingAsync(operation));
            Assert.IsType<InvalidOperationException>(error);
            Assert.Equal(0, fixture.Input.StopCalls);
            Assert.Equal(0, fixture.HandlerCalls);
            Assert.Equal(operation, fixture.Capture.SmartWritingCompletionOperationId);
        }
        finally { frozen.Dispose(); }
        AssertIdle(fixture.Gate);
    }

    private sealed class Fixture : IDisposable
    {
        internal UpdateWorkAdmissionGate Gate { get; } = new();
        internal AudioRecorderService Recorder { get; }
        internal VoiceCaptureService Capture { get; }
        internal FakeInput Input { get; private set; }
        internal TaskCompletionSource<bool> HandlerEntered { get; } = Signal();
        internal TaskCompletionSource<bool> ReleaseHandler { get; } = Signal();
        internal int HandlerCalls;
        internal byte[] DeliveredAudio;

        internal Fixture()
        {
            Recorder = new AudioRecorderService(_ => Input);
            Capture = new VoiceCaptureService(Recorder, null, new NotificationService(null), null, Gate);
            KeyboardWtfState.IsRecording = KeyboardWtfState.IsProcessing = KeyboardWtfState.IsProcessingAi = false;
            KeyboardWtfState.CurrentRecordingMode = RecordingMode.None;
        }

        internal int Arm(RecordingMode mode = RecordingMode.VoiceAssistant)
        {
            Input = new FakeInput();
            Recorder.StartRecording();
            var operation = (int)typeof(VoiceCaptureService).GetField("_operationId", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(Capture) + 1;
            Set(Capture, "_operationId", operation);
            Set(Capture, "_recordingStartUtc", DateTime.UtcNow);
            Set(Capture, "_audioCompletionHandler", (Func<SmartWritingAudioCapture, Task>)(async audio =>
            {
                Interlocked.Increment(ref HandlerCalls);
                DeliveredAudio = audio.WavData;
                HandlerEntered.TrySetResult(true);
                await ReleaseHandler.Task;
            }));
            KeyboardWtfState.IsRecording = true;
            KeyboardWtfState.IsProcessing = KeyboardWtfState.IsProcessingAi = false;
            KeyboardWtfState.CurrentRecordingMode = mode;
            return operation;
        }

        public void Dispose()
        {
            ReleaseHandler.TrySetResult(true);
            Capture.Cancel();
            Input?.Finish();
            Recorder.Dispose();
            KeyboardWtfState.IsRecording = KeyboardWtfState.IsProcessing = KeyboardWtfState.IsProcessingAi = false;
            KeyboardWtfState.CurrentRecordingMode = RecordingMode.None;
        }
    }

    private sealed class FakeInput : IWaveIn
    {
        public WaveFormat WaveFormat { get; set; } = new(16000, 1);
        public event EventHandler<WaveInEventArgs> DataAvailable;
        public event EventHandler<StoppedEventArgs> RecordingStopped;
        internal TaskCompletionSource<bool> StopRequested { get; } = Signal();
        internal int StartCalls;
        internal int StopCalls;
        public void StartRecording() => Interlocked.Increment(ref StartCalls);
        public void StopRecording() { Interlocked.Increment(ref StopCalls); StopRequested.TrySetResult(true); }
        public void Dispose() { }
        internal void Emit(byte[] data) => DataAvailable?.Invoke(this, new(data, data.Length));
        internal void Finish() => RecordingStopped?.Invoke(this, new());
    }

    private sealed class HeldContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object State)> _pending = new();
        internal TaskCompletionSource<bool> Posted { get; } = Signal();
        public override void Post(SendOrPostCallback callback, object state) { _pending.Enqueue((callback, state)); Posted.TrySetResult(true); }
        internal void Drain() { while (_pending.TryDequeue(out var item)) item.Callback(item.State); }
    }

    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Set(object target, string field, object value) => target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
    private static Task Invoke(object target, string method, params object[] arguments) => (Task)target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, arguments);
    private static void AssertBusy(UpdateWorkAdmissionGate gate) { var frozen = gate.TryFreeze(out var lease); lease?.Dispose(); Assert.False(frozen); }
    private static void AssertIdle(UpdateWorkAdmissionGate gate) { Assert.True(gate.TryFreeze(out var lease)); lease.Dispose(); }
}
