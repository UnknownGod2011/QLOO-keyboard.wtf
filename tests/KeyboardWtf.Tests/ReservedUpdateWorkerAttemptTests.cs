using System.IO.Pipes;
using System.Reflection;
using System.Runtime.ExceptionServices;
using KeyboardWtf.Services.Updates;
using Xunit;

namespace KeyboardWtf.Tests;

// Synthetic declarations and unconnected GUID-local pipes only. These tests
// never launch/connect an app, native host, worker, provider or installer, and
// do not acquire an installation/process identity on any accepted Run path.
public sealed class ReservedUpdateWorkerAttemptTests
{
    private const string Target = @"C:\Synthetic Installed\keyboard.wtf.exe";
    private const string Package = @"D:\Synthetic Prepared\keyboard.wtf-setup.exe";
    private const string Nonce = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private static readonly DeclaredUpdateParent Parent = new(12345, 638950000000000000, Target);
    private static readonly UpdateInstallationIngressLease.InstallationFileIdentity Installation = new(1, 1, 0);

    [Fact]
    public void Valid_synthetic_binding_and_known_installation_construct_without_OS_acquisition()
    {
        var api = Api.Require();
        Assert.NotNull(api.Create(Binding(), Installation));
    }

    [Fact]
    public void Null_binding_is_not_an_attempt()
    {
        var api = Api.Require();
        Assert.Throws<ArgumentNullException>(() => api.Create(null, Installation));
    }

    [Theory]
    [InlineData(0UL, 0UL, 0UL)]
    [InlineData(1UL, 0UL, 0UL)]
    public void Unknown_installation_file_identity_is_not_an_attempt(ulong volume, ulong low, ulong high)
    {
        var api = Api.Require();
        Assert.Throws<InvalidDataException>(() => api.Create(Binding(), new(volume, low, high)));
    }

    [Theory]
    [InlineData("pipe")]
    [InlineData("originals")]
    [InlineData("callback")]
    public async Task Null_run_dependency_is_rejected_and_consumes_the_one_shot(string dependency)
    {
        await AssertRejectedAndConsumedAsync<ArgumentNullException>(
            originals: [], nullDependency: dependency);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(30001)]
    public async Task Handshake_requires_a_positive_bound_of_at_most_thirty_seconds(int milliseconds)
    {
        await AssertRejectedAndConsumedAsync<ArgumentOutOfRangeException>(
            originals: [], handshakeTimeout: TimeSpan.FromMilliseconds(milliseconds));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(300001)]
    public async Task Exit_wait_requires_a_positive_bound_of_at_most_five_minutes(int milliseconds)
    {
        await AssertRejectedAndConsumedAsync<ArgumentOutOfRangeException>(
            originals: [], exitTimeout: TimeSpan.FromMilliseconds(milliseconds));
    }

    [Theory]
    [InlineData("null-member")]
    [InlineData("too-many")]
    [InlineData("duplicate-additional")]
    [InlineData("duplicate-parent")]
    [InlineData("self")]
    public async Task Unbounded_or_ambiguous_original_set_is_rejected_before_acquisition(string variation)
    {
        var original = Original();
        IReadOnlyList<DeclaredUpdateParent> originals = variation switch
        {
            "null-member" => [null],
            "too-many" => Enumerable.Range(20000, 65).Select(pid => original with { ProcessId = pid }).ToArray(),
            // A changed creation claim must not permit the same PID twice.
            "duplicate-additional" => [original, original with { CreationTimeUtcTicks = original.CreationTimeUtcTicks + 1 }],
            "duplicate-parent" => [Parent with { CreationTimeUtcTicks = Parent.CreationTimeUtcTicks + 1 }],
            _ => [original with { ProcessId = Environment.ProcessId }],
        };
        await AssertRejectedAndConsumedAsync<InvalidDataException>(originals);
    }

    [Fact]
    public async Task Invalid_process_or_creation_declaration_never_reaches_OS_acquisition()
    {
        var original = Original();
        foreach (var invalid in new[]
        {
            original with { ProcessId = 0 },
            original with { ProcessId = -1 },
            original with { CreationTimeUtcTicks = 0 },
            original with { CreationTimeUtcTicks = -1 },
            original with { CreationTimeUtcTicks = DateTime.MaxValue.Ticks + 1 },
        })
            await AssertRejectedAndConsumedAsync<InvalidDataException>([invalid]);
    }

    [Fact]
    public async Task Noncanonical_or_different_image_path_never_becomes_an_original()
    {
        foreach (var path in new[]
        {
            null,
            @"relative\keyboard.wtf.exe",
            @"\\server\share\keyboard.wtf.exe",
            @"C:\Synthetic Installed\..\Synthetic Installed\keyboard.wtf.exe",
            @"C:\Different Installed\keyboard.wtf.exe",
            @"C:\Synthetic Installed\other.exe",
        })
            await AssertRejectedAndConsumedAsync<InvalidDataException>([Original() with { ExecutablePath = path }]);
    }

    [Fact]
    public async Task Already_cancelled_attempt_is_consumed_without_connection_or_callback()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await AssertRejectedAndConsumedAsync<OperationCanceledException>([], token: cancellation.Token);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65)]
    public async Task Custom_unbounded_count_is_rejected_without_indexing_or_enumeration(int count)
    {
        var originals = new ProbeOriginalList(count);
        await AssertRejectedAndConsumedAsync<InvalidDataException>(originals);
        Assert.Equal(1, originals.CountReads);
        Assert.Empty(originals.IndicesRead);
        Assert.Equal(0, originals.EnumerationAttempts);
    }

    [Fact]
    public async Task Custom_fixed_count_is_snapshotted_by_index_and_all_originals_validate_before_OS_acquisition()
    {
        var originals = new ProbeOriginalList(2, Original(),
            Original() with { ProcessId = 23457, ExecutablePath = @"C:\Different Installed\keyboard.wtf.exe" });
        await AssertRejectedAndConsumedAsync<InvalidDataException>(originals);
        Assert.Equal(1, originals.CountReads);
        Assert.Equal(new[] { 0, 1 }, originals.IndicesRead);
        Assert.Equal(0, originals.EnumerationAttempts);
    }

    [Fact]
    public async Task Bound_parent_cannot_be_the_current_worker_even_with_an_otherwise_valid_binding()
    {
        var binding = Binding(Parent with { ProcessId = Environment.ProcessId });
        await AssertRejectedAndConsumedAsync<InvalidDataException>([], binding: binding);
    }

    private static async Task AssertRejectedAndConsumedAsync<TException>(
        IReadOnlyList<DeclaredUpdateParent> originals,
        string nullDependency = null,
        TimeSpan? handshakeTimeout = null,
        TimeSpan? exitTimeout = null,
        CancellationToken token = default,
        UpdateWorkerHandshakeBinding binding = null) where TException : Exception
    {
        var api = Api.Require();
        var attempt = api.Create(binding ?? Binding(), Installation);
        using var pipe = new NamedPipeClientStream(".", "kwt-reserved-rejection-" + Guid.NewGuid().ToString("N"),
            PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var calls = 0;
        Func<Task> afterOriginalExitAndExclusive = () =>
        {
            calls++;
            return Task.CompletedTask;
        };

        await Assert.ThrowsAnyAsync<TException>(() => api.RunAsync(attempt,
            nullDependency == "pipe" ? null : pipe,
            nullDependency == "originals" ? null : originals,
            handshakeTimeout ?? TimeSpan.FromSeconds(1), exitTimeout ?? TimeSpan.FromSeconds(1),
            nullDependency == "callback" ? null : afterOriginalExitAndExclusive, token));
        Assert.Equal(0, calls);
        Assert.False(pipe.IsConnected);

        // Invalid/cancelled admission consumes this exact attempt. A later
        // otherwise-valid call may not acquire, connect, or invoke the callback.
        await Assert.ThrowsAsync<InvalidOperationException>(() => api.RunAsync(attempt, pipe, [],
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), afterOriginalExitAndExclusive, CancellationToken.None));
        Assert.Equal(0, calls);
        Assert.False(pipe.IsConnected);
    }

    private static DeclaredUpdateParent Original() => new(23456, Parent.CreationTimeUtcTicks, Target);

    private static UpdateWorkerHandshakeBinding Binding(DeclaredUpdateParent parent = null)
    {
        var update = new VerifiedUpdate(new Version(2, 3, 4), new Version(1, 0, 0), "stable",
            new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero),
            new Uri("https://github.com/UnknownGod2011/keyboard.wtf/releases/download/v2.3.4/keyboard.wtf-setup.exe"),
            new string('a', 64), 100, "Synthetic test release.", "test-key");
        var invocation = UpdateInstallerInvocation.Create(new(update, Package), parent ?? Parent, Target);
        return UpdateWorkerHandshakeBinding.Create(invocation, Nonce);
    }

    private sealed class ProbeOriginalList(int count, params DeclaredUpdateParent[] declarations)
        : IReadOnlyList<DeclaredUpdateParent>
    {
        internal int CountReads { get; private set; }
        internal List<int> IndicesRead { get; } = [];
        internal int EnumerationAttempts { get; private set; }

        public int Count
        {
            get
            {
                if (++CountReads != 1)
                    throw new InvalidOperationException("A bounded snapshot must retain its first Count.");
                return count;
            }
        }

        public DeclaredUpdateParent this[int index]
        {
            get
            {
                IndicesRead.Add(index);
                return declarations[index];
            }
        }

        public IEnumerator<DeclaredUpdateParent> GetEnumerator()
        {
            EnumerationAttempts++;
            throw new InvalidOperationException("A bounded original set must be copied by its declared indices.");
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class Api
    {
        private readonly ConstructorInfo _constructor;
        private readonly MethodInfo _run;

        private Api()
        {
            var type = typeof(UpdateWorkAdmissionGate).Assembly.GetType(
                "KeyboardWtf.Services.Updates.ReservedUpdateWorkerAttempt");
            Assert.True(type is not null, "The reserved original-process worker attempt is missing.");
            const BindingFlags instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            _constructor = type.GetConstructor(instance, null,
                [typeof(UpdateWorkerHandshakeBinding), typeof(UpdateInstallationIngressLease.InstallationFileIdentity)], null);
            Assert.NotNull(_constructor);
            _run = type.GetMethod("RunAsync", instance, null,
                [typeof(NamedPipeClientStream), typeof(IReadOnlyList<DeclaredUpdateParent>), typeof(TimeSpan), typeof(TimeSpan),
                    typeof(Func<Task>), typeof(CancellationToken)], null);
            Assert.NotNull(_run);
            Assert.Equal(typeof(Task<bool>), _run.ReturnType);
        }

        internal static Api Require() => new();

        internal object Create(UpdateWorkerHandshakeBinding binding,
            UpdateInstallationIngressLease.InstallationFileIdentity installation)
        {
            try { return _constructor.Invoke([binding, installation]); }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }

        internal Task<bool> RunAsync(object attempt, NamedPipeClientStream pipe,
            IReadOnlyList<DeclaredUpdateParent> originals, TimeSpan handshakeTimeout, TimeSpan exitTimeout,
            Func<Task> afterOriginalExitAndExclusive, CancellationToken token)
        {
            try
            {
                return (Task<bool>)_run.Invoke(attempt,
                    [pipe, originals, handshakeTimeout, exitTimeout, afterOriginalExitAndExclusive, token]);
            }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }
    }
}
