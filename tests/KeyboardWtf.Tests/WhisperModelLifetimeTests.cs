using KeyboardWtf.Services;
using Xunit;

namespace KeyboardWtf.Tests;

public sealed class WhisperModelLifetimeTests
{
    [Fact]
    public void Unborrowed_factory_is_released_once_and_new_borrows_stop_at_shutdown()
    {
        var lifetime = new WhisperModelLifetime<FakeContext>();
        var context = new FakeContext();
        Assert.True(lifetime.TryReplace(context, "model-a"));
        Assert.True(lifetime.IsLoaded);
        Assert.True(lifetime.Matches("model-a"));
        Assert.False(lifetime.Matches("model-b"));

        lifetime.Dispose();
        lifetime.Dispose();

        Assert.Equal(1, context.Disposals);
        Assert.True(lifetime.IsDisposed);
        Assert.False(lifetime.IsLoaded);
        Assert.False(lifetime.Matches("model-a"));
        Assert.Null(lifetime.Borrow());
    }

    [Fact]
    public void Shutdown_does_not_free_a_borrowed_factory_or_wait_for_its_processor()
    {
        var lifetime = new WhisperModelLifetime<FakeContext>();
        var context = new FakeContext();
        lifetime.TryReplace(context, "model-a");
        var lease = lifetime.Borrow();

        lifetime.Dispose(); // Must return without waiting for this still-owned lease.

        Assert.Equal(context, lease.Value);
        Assert.Equal(0, context.Disposals);
        Assert.Null(lifetime.Borrow());
        lease.Dispose();
        lease.Dispose();
        Assert.Equal(1, context.Disposals);
    }

    [Fact]
    public void Replacement_preserves_old_processors_and_binds_new_processors_to_the_new_model()
    {
        using var lifetime = new WhisperModelLifetime<FakeContext>();
        var first = new FakeContext();
        var second = new FakeContext();
        lifetime.TryReplace(first, "first");
        var oldLease = lifetime.Borrow();

        Assert.True(lifetime.TryReplace(second, "second"));
        var newLease = lifetime.Borrow();

        Assert.Same(first, oldLease.Value);
        Assert.Same(second, newLease.Value);
        Assert.Equal(0, first.Disposals);
        Assert.True(lifetime.Matches("second"));
        oldLease.Dispose();
        Assert.Equal(1, first.Disposals);
        Assert.Equal(0, second.Disposals);
        newLease.Dispose();
        Assert.Equal(0, second.Disposals); // The current model remains loaded.
    }

    [Fact]
    public void Model_clear_retires_only_its_generation_and_can_publish_the_replacement()
    {
        using var lifetime = new WhisperModelLifetime<FakeContext>();
        var first = new FakeContext();
        var second = new FakeContext();
        lifetime.TryReplace(first, "first");
        var firstLease = lifetime.Borrow();
        lifetime.Clear();
        Assert.Null(lifetime.Borrow());
        Assert.Equal(0, first.Disposals);
        Assert.False(lifetime.IsDisposed);
        Assert.True(lifetime.TryReplace(second, "second"));
        firstLease.Dispose();
        Assert.Equal(1, first.Disposals);
        Assert.Equal(0, second.Disposals);
    }

    [Fact]
    public void Shutdown_winning_model_load_publication_releases_the_unpublished_factory()
    {
        using var lifetime = new WhisperModelLifetime<FakeContext>();
        var context = new FakeContext();
        lifetime.Dispose();
        Assert.False(lifetime.TryReplace(context, "late-model"));
        Assert.Equal(1, context.Disposals);
        Assert.Null(lifetime.Borrow());
    }

    [Fact]
    public async Task Concurrent_shutdown_and_multiple_processor_releases_dispose_exactly_once()
    {
        var lifetime = new WhisperModelLifetime<FakeContext>();
        var context = new FakeContext();
        lifetime.TryReplace(context, "model");
        var first = lifetime.Borrow();
        var second = lifetime.Borrow();
        await Task.WhenAll(Task.Run(lifetime.Dispose), Task.Run(first.Dispose), Task.Run(first.Dispose),
            Task.Run(second.Dispose), Task.Run(lifetime.Dispose));
        Assert.Equal(1, context.Disposals);
        Assert.Null(lifetime.Borrow());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Canceled_or_failed_enumeration_keeps_the_factory_until_async_processor_cleanup_finishes(bool canceled)
    {
        using var lifetime = new WhisperModelLifetime<FakeContext>();
        var context = new FakeContext();
        lifetime.TryReplace(context, "model");
        var workerFinished = NewSignal();
        var cleanupStarted = NewSignal();
        var operation = Recognize();
        try
        {
            await cleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            lifetime.Dispose();
            Assert.Equal(0, context.Disposals);
            Assert.Equal(1, context.ActiveProcessors);
        }
        finally { workerFinished.TrySetResult(); }
        if (canceled) await Assert.ThrowsAsync<OperationCanceledException>(() => operation);
        else await Assert.ThrowsAsync<InvalidOperationException>(() => operation);
        Assert.Equal(0, context.ActiveProcessors);
        Assert.Equal(1, context.Disposals);
        Assert.False(context.FreedWhileProcessing);

        async Task Recognize()
        {
            using var model = lifetime.Borrow();
            await using var processor = new FakeProcessor(model.Value, cleanupStarted, workerFinished);
            // Pinned Whisper.net1.9.1 can yield cancellation/error before its
            // native worker finishes. Async disposal owns that remaining work.
            await Task.Yield();
            if (canceled) throw new OperationCanceledException();
            throw new InvalidOperationException("Controlled processor failure");
        }
    }

    [Fact]
    public async Task Controlled_original_direct_disposal_reproduces_the_unsafe_native_lifetime_order()
    {
        var context = new FakeContext();
        var workerFinished = NewSignal();
        var cleanupStarted = NewSignal();
        var processor = new FakeProcessor(context, cleanupStarted, workerFinished);

        // This is the exact unsafe ownership order in the original wrapper:
        // factory.Dispose can run independently of the still-active processor.
        // It records the violated boundary; it never loads native code/crashes.
        context.Dispose();
        Assert.True(context.FreedWhileProcessing);
        Console.WriteLine("ORIGINAL_LIFETIME_VIOLATION_REPRODUCED_WITH_CONTROLLED_FAKE=true");
        workerFinished.SetResult();
        await processor.DisposeAsync();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class FakeContext : IDisposable
    {
        internal int Disposals;
        internal int ActiveProcessors;
        internal bool FreedWhileProcessing;
        public void Dispose()
        {
            FreedWhileProcessing |= Volatile.Read(ref ActiveProcessors) != 0;
            Interlocked.Increment(ref Disposals);
        }
    }

    private sealed class FakeProcessor : IAsyncDisposable
    {
        private readonly FakeContext _context;
        private readonly TaskCompletionSource _cleanupStarted, _workerFinished;
        internal FakeProcessor(FakeContext context, TaskCompletionSource cleanupStarted, TaskCompletionSource workerFinished)
        {
            _context = context; _cleanupStarted = cleanupStarted; _workerFinished = workerFinished;
            Interlocked.Increment(ref _context.ActiveProcessors);
        }

        public async ValueTask DisposeAsync()
        {
            _cleanupStarted.TrySetResult();
            await _workerFinished.Task;
            Interlocked.Decrement(ref _context.ActiveProcessors);
        }
    }
}
