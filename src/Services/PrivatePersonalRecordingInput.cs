#if KEYBOARD_WTF_PERSONAL_API_CANDIDATE || KEYBOARD_WTF_CONNECTOR_STAGING_CANDIDATE
namespace KeyboardWtf.Services;

using System.Security.Cryptography;

// Bounded private-candidate audit input. Not used by normal sessions. The actual
// WaveIn callback remains the clock; only explicitly queued diagnostic PCM is
// substituted for room audio. No file access, device change or secret storage.
internal sealed class PrivatePersonalRecordingInput : IDisposable
{
    internal const int MaximumBytes = 16000 * 2 * 20;
    private readonly object _gate = new();
    private Pending _pending;
    private bool _disposed;

    internal Task<Receipt> Queue(byte[] pcm)
    {
        if (pcm is null || pcm.Length is < 3200 or > MaximumBytes || pcm.Length % 2 != 0)
            throw new ArgumentException("Private recording must be bounded PCM16 mono at 16kHz.");
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_pending != null) throw new InvalidOperationException("A private recording is already pending.");
            _pending = new Pending(pcm.ToArray());
            return _pending.Completion.Task;
        }
    }

    internal Frame Read(int count)
    {
        if (count is < 2 or > 32000 || count % 2 != 0)
            throw new ArgumentException("Unexpected capture format during private recording audit.");
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var bytes = new byte[count];
            var pending = _pending;
            if (pending is null || pending.Offset == pending.Bytes.Length) return new Frame(bytes, null, null);
            pending.FirstFrameAt ??= DateTimeOffset.UtcNow;
            var length = Math.Min(count, pending.Bytes.Length - pending.Offset);
            pending.Bytes.AsSpan(pending.Offset, length).CopyTo(bytes);
            pending.Offset += length;
            pending.Frames++;
            return new Frame(bytes, pending.Offset == pending.Bytes.Length ? () => Sent(pending) : null,
                () => Failed(pending));
        }
    }

    private void Sent(Pending pending)
    {
        lock (_gate)
        {
            if (_pending != pending || _disposed) return;
            var receipt = new Receipt(pending.Id, pending.Bytes.Length, pending.Sha256, pending.Frames,
                pending.QueuedAt, pending.FirstFrameAt!.Value, DateTimeOffset.UtcNow);
            Array.Clear(pending.Bytes);
            _pending = null;
            pending.Completion.TrySetResult(receipt);
        }
    }
    private void Failed(Pending pending)
    {
        lock (_gate)
        {
            if (_pending != pending) return;
            Array.Clear(pending.Bytes);
            _pending = null;
            pending.Completion.TrySetCanceled();
        }
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_pending != null) Failed(_pending);
        }
    }
    internal sealed record Frame(byte[] Bytes, Action Sent, Action Failed);
    internal sealed record Receipt(Guid RecordingId, int PcmBytes, string PcmSha256, int CaptureFrames,
        DateTimeOffset QueuedAt, DateTimeOffset FirstFrameAt, DateTimeOffset LastFrameSentAt);
    private sealed class Pending(byte[] bytes)
    {
        internal Guid Id { get; } = Guid.NewGuid();
        internal byte[] Bytes { get; } = bytes;
        internal string Sha256 { get; } = Convert.ToHexString(SHA256.HashData(bytes));
        internal DateTimeOffset QueuedAt { get; } = DateTimeOffset.UtcNow;
        internal DateTimeOffset? FirstFrameAt;
        internal int Offset, Frames;
        internal TaskCompletionSource<Receipt> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

#endif
