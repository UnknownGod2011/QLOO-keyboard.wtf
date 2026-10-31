#if KEYBOARD_WTF_TEST_PRIVATE_CANDIDATE || KEYBOARD_WTF_TEST_CONNECTOR_STAGING_CANDIDATE
using KeyboardWtf.Services;
using Xunit;

namespace KeyboardWtf.Tests;

public sealed class PrivatePersonalRecordingInputTests
{
    [Fact]
    public async Task Recording_preserves_exact_pcm_and_completes_only_after_last_socket_send()
    {
        using var input = new PrivatePersonalRecordingInput();
        var source = Enumerable.Range(0, 4000).Select(index => (byte)(index % 255)).ToArray();
        var sent = input.Queue(source);
        var first = input.Read(2880); var last = input.Read(2880);
        Assert.Equal(source, first.Bytes.Concat(last.Bytes.Take(1120)).ToArray());
        Assert.All(last.Bytes.Skip(1120), value => Assert.Equal(0, value));
        Assert.False(sent.IsCompleted); Assert.Null(first.Sent);
        Assert.Throws<InvalidOperationException>(() => { _ = input.Queue(source); });
        last.Sent();
        var receipt = await sent;
        Assert.Equal(4000, receipt.PcmBytes); Assert.Equal(2, receipt.CaptureFrames);
        Assert.All(input.Read(2880).Bytes, value => Assert.Equal(0, value));
        Assert.NotEqual(0, source[1]); // Caller input was not overwritten.
        last.Sent(); // Late/duplicate callback cannot finish a new recording.
        var next = input.Queue(source); Assert.False(next.IsCompleted);
        input.Dispose(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next);
    }
    [Fact]
    public async Task Failed_or_cancelled_send_releases_pending_recording_without_replay()
    {
        using var input = new PrivatePersonalRecordingInput();
        var sent = input.Queue(new byte[6000]); var frame = input.Read(2880);
        frame.Failed(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sent);
        Assert.All(input.Read(2880).Bytes, value => Assert.Equal(0, value));
        var next = input.Queue(new byte[3200]); input.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next);
        Assert.Throws<ObjectDisposedException>(() => input.Read(2880));
    }
    [Theory]
    [InlineData(0)] [InlineData(3199)] [InlineData(3201)] [InlineData(640002)]
    public void Out_of_bound_or_odd_pcm_is_refused(int length)
    {
        using var input = new PrivatePersonalRecordingInput();
        Assert.Throws<ArgumentException>(() => { _ = input.Queue(new byte[length]); });
    }
}

#endif
