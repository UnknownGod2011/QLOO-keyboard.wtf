namespace KeyboardWtf.Services.Updates;

// Updater-only queued UI receipt. Suppression is not cancellation of a running
// callback; completion certifies that its synchronous effects actually ended.
internal sealed class UpdateNotificationDispatchReceipt
{
    private readonly Func<bool> _dispatch;
    private readonly TaskCompletionSource<bool> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _state; // Pending=0, Running=1, Finished=2, Suppressed=3.

    internal UpdateNotificationDispatchReceipt(Func<bool> dispatch) =>
        _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));

    internal Task<bool> Completion => _completion.Task;

    internal void Run()
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0) return;
        var attempted = false;
        try { attempted = _dispatch(); }
        catch { /* A failed best-effort notice must not escape the UI callback. */ }
        finally
        {
            Volatile.Write(ref _state, 2);
            _completion.TrySetResult(attempted); // LAST, after actual callback cleanup.
        }
    }

    internal bool TrySuppress()
    {
        if (Interlocked.CompareExchange(ref _state, 3, 0) != 0) return false;
        _completion.TrySetResult(false); // Every later Run is wholly inert.
        return true;
    }
}
