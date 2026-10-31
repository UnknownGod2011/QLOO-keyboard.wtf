using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using KeyboardWtf.Services.Ai.Backends;
using Xunit;

namespace KeyboardWtf.Tests;

// Actual inert STA/form lifetime only. No app, key, vault, provider, browser,
// registry, installer or profile. Original dialog threads are positively joined
// before fixtures are disposed, including failed assertions or abandoned waiters.
public sealed class NativePersonalDialogCompletionTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Result_waits_for_field_cleanup_and_form_disposal()
    {
        using var fixture = new Fixture(holdClear: true);
        var original = fixture.Start();
        try
        {
            await fixture.Clearing.Task.WaitAsync(Deadline);
            Assert.False(original.IsCompleted);
            Assert.False(fixture.Disposed.Task.IsCompleted);
        }
        finally { fixture.ReleaseClear.Set(); }
        Assert.Equal(17, await original.WaitAsync(Deadline));
        Assert.True(fixture.Disposed.Task.IsCompletedSuccessfully);
        Assert.Equal(1, fixture.Reads);
        Assert.Equal(1, fixture.Clears);
    }

    [Fact]
    public async Task Result_waits_for_actual_form_disposal_callback_to_return()
    {
        using var fixture = new Fixture(holdDispose: true);
        var original = fixture.Start();
        try
        {
            await fixture.Disposed.Task.WaitAsync(Deadline);
            Assert.False(original.IsCompleted);
            Assert.Equal(1, fixture.Clears);
        }
        finally { fixture.ReleaseDispose.Set(); }
        Assert.Equal(17, await original.WaitAsync(Deadline));
    }

    [Fact]
    public async Task Repeated_native_cycles_finish_cleanup_before_success()
    {
        for (var cycle = 0; cycle < 30; cycle++)
        {
            using var fixture = new Fixture();
            Assert.Equal(17, await fixture.Start().WaitAsync(Deadline));
            Assert.True(fixture.Disposed.Task.IsCompletedSuccessfully);
            Assert.Equal(1, fixture.Clears);
            Assert.Equal(1, fixture.Reads);
        }
    }

    [Fact]
    public async Task Abandoned_waiter_does_not_complete_original_before_cleanup()
    {
        using var fixture = new Fixture(holdClear: true);
        var original = fixture.Start();
        try
        {
            await fixture.Clearing.Task.WaitAsync(Deadline);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => original.WaitAsync(cancellation.Token));
            Assert.False(original.IsCompleted);
        }
        finally { fixture.ReleaseClear.Set(); }
        Assert.Equal(17, await original.WaitAsync(Deadline));
        Assert.True(fixture.Disposed.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Cleanup_failure_cannot_publish_false_success()
    {
        using var fixture = new Fixture(failClear: true);
        var original = fixture.Start();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => original.WaitAsync(Deadline));
        Assert.Equal("The native Personal API dialog could not be opened.", error.Message);
        Assert.True(fixture.Disposed.Task.IsCompletedSuccessfully);
        Assert.Equal(1, fixture.Reads);
        Assert.Equal(1, fixture.Clears);
    }

    [Fact]
    public async Task User_cancel_returns_default_after_cleanup_without_reading()
    {
        using var fixture = new Fixture(accept: false);
        Assert.Equal(0, await fixture.Start().WaitAsync(Deadline));
        Assert.True(fixture.Disposed.Task.IsCompletedSuccessfully);
        Assert.Equal(0, fixture.Reads);
        Assert.Equal(1, fixture.Clears);
    }

    [Fact]
    public async Task Cancellation_after_close_is_not_success_and_still_cleans()
    {
        using var cancellation = new CancellationTokenSource();
        using var fixture = new Fixture(afterClose: cancellation.Cancel);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Start(cancellation.Token).WaitAsync(Deadline));
        Assert.True(fixture.Disposed.Task.IsCompletedSuccessfully);
        Assert.Equal(0, fixture.Reads);
        Assert.Equal(1, fixture.Clears);
    }

    [Fact]
    public async Task Read_failure_keeps_existing_content_free_error_after_cleanup()
    {
        using var fixture = new Fixture(failRead: true);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Start().WaitAsync(Deadline));
        Assert.Equal("The native Personal API dialog could not be opened.", error.Message);
        Assert.True(fixture.Disposed.Task.IsCompletedSuccessfully);
        Assert.Equal(1, fixture.Clears);
    }

    [Fact]
    public void Already_cancelled_request_creates_no_form_or_thread()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => { _ = fixture.Start(cancellation.Token); });
        Assert.Null(fixture.Thread);
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly ManualResetEventSlim ReleaseClear = new(false);
        internal readonly ManualResetEventSlim ReleaseDispose = new(false);
        private readonly ManualResetEventSlim _created = new(false);
        internal readonly TaskCompletionSource<bool> Clearing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<bool> Disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Thread Thread;
        internal int Reads;
        internal int Clears;
        private readonly bool _holdClear, _failClear, _accept, _failRead, _holdDispose;
        private readonly Action _afterClose;
        private readonly NativePersonalKeyEntryDialog _dialog = new();
        private bool _started;

        internal Fixture(bool holdClear = false, bool failClear = false, bool accept = true,
            Action afterClose = null, bool failRead = false, bool holdDispose = false)
        {
            _holdClear = holdClear; _failClear = failClear; _accept = accept;
            _afterClose = afterClose; _failRead = failRead;
            _holdDispose = holdDispose;
        }

        internal Task<int> Start(CancellationToken token = default)
        {
            Func<(Form Form, Func<int> Read, Action Clear)> create = () =>
            {
                Thread = System.Threading.Thread.CurrentThread;
                _created.Set();
                var form = new Form { Opacity = 0, ShowInTaskbar = false, Width = 1, Height = 1 };
                form.Disposed += (_, _) =>
                {
                    Disposed.TrySetResult(true);
                    if (_holdDispose && !ReleaseDispose.Wait(Deadline))
                        throw new TimeoutException("Fixture native disposal release was not supplied.");
                };
                form.FormClosed += (_, _) => _afterClose?.Invoke();
                form.Shown += (_, _) => form.BeginInvoke((Action)(() =>
                {
                    form.DialogResult = _accept ? DialogResult.OK : DialogResult.Cancel;
                    form.Close();
                }));
                return (form, () =>
                {
                    Interlocked.Increment(ref Reads);
                    if (_failRead) throw new InvalidOperationException("inert read failure");
                    return 17;
                }, () =>
                {
                    Interlocked.Increment(ref Clears);
                    Clearing.TrySetResult(true);
                    if (_holdClear && !ReleaseClear.Wait(Deadline))
                        throw new TimeoutException("Fixture cleanup release was not supplied.");
                    if (_failClear) throw new InvalidOperationException("inert cleanup failure");
                });
            };
            var method = typeof(NativePersonalKeyEntryDialog).GetMethod("RunDialogAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Existing native dialog boundary is missing.");
            try
            {
                var original = (Task<int>)method.MakeGenericMethod(typeof(int)).Invoke(_dialog, new object[] { create, token });
                _started = true;
                return original;
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
        }

        public void Dispose()
        {
            ReleaseClear.Set();
            ReleaseDispose.Set();
            // A Task result or timeout is not a join of its original STA owner.
            if (_started && !_created.Wait(Deadline))
                throw new TimeoutException("Original native dialog fixture thread has not entered.");
            if (Thread is not null && !Thread.Join(Deadline))
                throw new TimeoutException("Original native dialog fixture thread has not exited.");
            ReleaseClear.Dispose();
            ReleaseDispose.Dispose();
            _created.Dispose();
        }
    }
}
