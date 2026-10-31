using KeyboardWtf.Services;
using KeyboardWtf.Services.Updates;
using NAudio.Wave;
using Xunit;

namespace KeyboardWtf.Tests;

// Passive recorder lifetime accounting. Fake inputs prove handler/callback
// ownership; they do not replace real Windows WaveIn drain acceptance.
[Collection("Live event integration")]
public sealed class AudioRecorderUpdateWorkTests
{
    [Fact]
    public void Frozen_recorder_refuses_before_input_factory_or_microphone_effect()
    {
        var calls = 0;
        var gate = new UpdateWorkAdmissionGate();
        using var recorder = new AudioRecorderService(_ => { calls++; return new FakeInput(); }, gate);
        Assert.True(gate.TryFreeze(out var freeze));
        try
        {
            var error = Record.Exception(() => recorder.StartRecording());
            Assert.IsType<InvalidOperationException>(error);
            Assert.Contains("update", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, calls);
            Assert.False(recorder.IsRecording);
        }
        finally { freeze.Dispose(); }
        AssertIdle(gate);
    }

    [Fact]
    public async Task Recorder_remains_owned_until_finalization_and_full_pcm_subscriber_callback_return()
    {
        var gate = new UpdateWorkAdmissionGate();
        var input = new FakeInput();
        using var recorder = new AudioRecorderService(_ => input, gate);
        var entered = Signal();
        var release = Signal();
        recorder.PcmDataAvailable += (_, _) =>
        {
            entered.TrySetResult(true);
            release.Task.GetAwaiter().GetResult();
        };
        recorder.StartRecording();
        AssertBusy(gate);

        var callback = Task.Run(() => input.Emit(new byte[1600]));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stop = recorder.StopRecordingAsync();
        input.Finish();
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(input.Disposed);
        AssertBusy(gate); // The full subscriber callback is still physically live.

        release.TrySetResult(true);
        await callback.WaitAsync(TimeSpan.FromSeconds(5));
        await AssertEventuallyIdle(gate);
    }

    [Fact]
    public async Task Stopped_event_then_finalization_releases_exactly_once()
    {
        var gate = new UpdateWorkAdmissionGate();
        var input = new FakeInput();
        using var recorder = new AudioRecorderService(_ => input, gate);
        recorder.StartRecording();
        var stop = recorder.StopRecordingAsync();
        input.Finish();
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, input.StopCalls);
        Assert.True(input.Disposed);
        await AssertEventuallyIdle(gate);
    }

    [Fact]
    public async Task Retired_input_cannot_publish_pcm_to_a_restarted_recording()
    {
        var gate = new UpdateWorkAdmissionGate();
        var oldInput = new FakeInput();
        var newInput = new FakeInput();
        var inputs = new Queue<FakeInput>(new[] { oldInput, newInput });
        using var recorder = new AudioRecorderService(_ => inputs.Dequeue(), gate);
        var entered = Signal();
        var release = Signal();
        var publications = 0;
        recorder.PcmDataAvailable += (_, _) =>
        {
            if (Interlocked.Increment(ref publications) != 1) return;
            entered.TrySetResult(true);
            release.Task.GetAwaiter().GetResult();
        };
        recorder.StartRecording();
        var queuedOldCallback = oldInput.SnapshotCallback();
        var callback = Task.Run(() => oldInput.Emit(new byte[1600]));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var stop = recorder.StopRecordingAsync();
            oldInput.Finish();
            await stop.WaitAsync(TimeSpan.FromSeconds(5));
            recorder.StartRecording();
            queuedOldCallback(oldInput, new(new byte[1600], 1600));
            Assert.Equal(1, Volatile.Read(ref publications));
            newInput.Emit(new byte[1600]);
            Assert.Equal(2, Volatile.Read(ref publications));
        }
        finally
        {
            release.TrySetResult(true);
            await callback.WaitAsync(TimeSpan.FromSeconds(5));
            var stop = recorder.StopRecordingAsync();
            newInput.Finish();
            await stop.WaitAsync(TimeSpan.FromSeconds(5));
        }
        AssertIdle(gate);
    }

    [Fact]
    public async Task Failed_device_disposal_cannot_certify_idle()
    {
        var gate = new UpdateWorkAdmissionGate();
        var input = new FakeInput { FailDispose = true };
        using var recorder = new AudioRecorderService(_ => input, gate);
        recorder.StartRecording();
        var stop = recorder.StopRecordingAsync();
        input.Finish();
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        AssertBusy(gate);
    }

    [Fact]
    public void Failed_start_without_terminal_evidence_cannot_certify_idle()
    {
        var gate = new UpdateWorkAdmissionGate();
        var input = new FakeInput { FailStart = true };
        using var recorder = new AudioRecorderService(_ => input, gate);
        Assert.IsType<IOException>(Record.Exception(() => recorder.StartRecording()));
        Assert.False(recorder.IsRecording);
        Assert.True(input.Disposed);
        AssertBusy(gate);
    }

    [Fact]
    public async Task Failed_pcm_processing_does_not_leak_callback_ownership()
    {
        var gate = new UpdateWorkAdmissionGate();
        var input = new FakeInput();
        using var recorder = new AudioRecorderService(_ => input, gate);
        recorder.StartRecording();
        Assert.NotNull(Record.Exception(() => input.Emit(new byte[2], 4)));
        var stop = recorder.StopRecordingAsync();
        input.Finish();
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        AssertIdle(gate);
    }

    [Fact]
    public void Failed_factory_releases_unstarted_work()
    {
        var gate = new UpdateWorkAdmissionGate();
        using var recorder = new AudioRecorderService(_ => throw new IOException("factory failed"), gate);
        Assert.IsType<IOException>(Record.Exception(() => recorder.StartRecording()));
        AssertIdle(gate);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stop_error_is_not_terminal_proof_but_later_real_event_can_complete_evidence(bool failDispose)
    {
        var gate = new UpdateWorkAdmissionGate();
        var input = new FakeInput { FailStop = true, FailDispose = failDispose };
        using var recorder = new AudioRecorderService(_ => input, gate);
        recorder.StartRecording();
        var delayedStopped = input.SnapshotStoppedCallback();
        await recorder.StopRecordingAsync().WaitAsync(TimeSpan.FromSeconds(5));
        AssertBusy(gate);
        delayedStopped(input, new StoppedEventArgs());
        if (failDispose) AssertBusy(gate);
        else AssertIdle(gate);
    }

    private sealed class FakeInput : IWaveIn
    {
        public WaveFormat WaveFormat { get; set; } = new(16000, 1);
        public event EventHandler<WaveInEventArgs> DataAvailable;
        public event EventHandler<StoppedEventArgs> RecordingStopped;
        internal int StopCalls;
        internal bool Disposed;
        internal bool FailDispose;
        internal bool FailStart;
        internal bool FailStop;
        public void StartRecording() { if (FailStart) throw new IOException("start failed"); }
        public void StopRecording()
        {
            Interlocked.Increment(ref StopCalls);
            if (FailStop) throw new IOException("stop failed");
        }
        public void Dispose()
        {
            if (FailDispose) throw new IOException("dispose failed");
            Disposed = true;
        }
        internal void Emit(byte[] data) => DataAvailable?.Invoke(this, new(data, data.Length));
        internal void Emit(byte[] data, int count) => DataAvailable?.Invoke(this, new(data, count));
        internal EventHandler<WaveInEventArgs> SnapshotCallback() => DataAvailable;
        internal EventHandler<StoppedEventArgs> SnapshotStoppedCallback() => RecordingStopped;
        internal void Finish() => RecordingStopped?.Invoke(this, new StoppedEventArgs());
    }

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
