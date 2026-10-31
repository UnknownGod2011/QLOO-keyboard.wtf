using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using KeyboardWtf.Services;
using Xunit;

namespace KeyboardWtf.Tests;

// App helper and static caller-shape proof ONLY. These synthetic factories do
// not run startup, settings, downloads, lifecycle delivery, providers or the
// desktop constructor. Full updater ownership/installer acceptance stays open.
public sealed class AppFiniteUpdateWorkTests
{
    [Fact]
    public void App_owns_one_nonstatic_internal_gate_and_helper()
    {
        var fixture = Fixture.Create();
        Assert.Same(fixture.Gate, fixture.Property.GetValue(fixture.App));
        Assert.Same(fixture.Gate, fixture.Property.GetValue(fixture.App));
        Assert.False(fixture.Property.GetMethod.IsStatic);
        Assert.False(fixture.Helper.IsStatic);
        Assert.Equal(typeof(Task), fixture.Helper.ReturnType);
    }

    [Fact]
    public async Task Helper_admits_before_factory_and_releases_after_success()
    {
        var fixture = Fixture.Create();
        var calls = 0;
        await fixture.Track(() =>
        {
            calls++;
            Assert.False(fixture.TryFreeze(out _));
            return Task.CompletedTask;
        });

        Assert.Equal(1, calls);
        fixture.AssertCanFreeze();
    }

    [Fact]
    public async Task Helper_keeps_the_actual_async_worker_owned_until_completion()
    {
        var fixture = Fixture.Create();
        var entered = Signal();
        var finish = Signal();
        var tracked = fixture.Track(() =>
        {
            entered.SetResult(true);
            return finish.Task;
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(tracked.IsCompleted);
        Assert.False(fixture.TryFreeze(out _));
        finish.SetResult(true);
        await tracked.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.AssertCanFreeze();
    }

    [Fact]
    public async Task Cancellation_request_does_not_release_unfinished_app_work()
    {
        var fixture = Fixture.Create();
        using var cancellation = new CancellationTokenSource();
        var finishCleanup = Signal();
        var tracked = fixture.Track(async () =>
        {
            await finishCleanup.Task;
            cancellation.Token.ThrowIfCancellationRequested();
        });
        cancellation.Cancel();

        Assert.False(tracked.IsCompleted);
        Assert.False(fixture.TryFreeze(out _));
        finishCleanup.SetResult(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tracked);
        fixture.AssertCanFreeze();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Frozen_or_committed_app_gate_rejects_before_factory_invocation(bool commit)
    {
        var fixture = Fixture.Create();
        Assert.True(fixture.TryFreeze(out var frozen));
        if (commit) frozen.Commit();
        var calls = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Track(() =>
        {
            calls++;
            return Task.CompletedTask;
        }));
        Assert.Equal(0, calls);
        frozen.Dispose();
        if (commit)
            Assert.False(fixture.TryFreeze(out _));
        else
            fixture.AssertCanFreeze();
    }

    [Fact]
    public async Task Synchronous_factory_error_is_preserved_and_owner_is_released()
    {
        var fixture = Fixture.Create();
        var error = new IOException("Synthetic finite factory error");
        Assert.Same(error, await Record.ExceptionAsync(() => fixture.Track(() => throw error)));
        fixture.AssertCanFreeze();
    }

    [Fact]
    public async Task Async_worker_error_is_preserved_and_owner_is_released()
    {
        var fixture = Fixture.Create();
        var finish = Signal();
        var error = new IOException("Synthetic finite worker error");
        var tracked = fixture.Track(async () =>
        {
            await finish.Task;
            throw error;
        });
        Assert.False(fixture.TryFreeze(out _));
        finish.SetResult(true);

        Assert.Same(error, await Record.ExceptionAsync(() => tracked));
        fixture.AssertCanFreeze();
    }

    [Fact]
    public async Task Already_canceled_completion_task_releases_owner()
    {
        var fixture = Fixture.Create();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Track(() => Task.FromCanceled(cancellation.Token)));
        fixture.AssertCanFreeze();
    }

    [Fact]
    public async Task Null_factory_is_rejected_without_leaking_work()
    {
        var fixture = Fixture.Create();
        await Assert.ThrowsAsync<ArgumentNullException>(() => fixture.Track(null));
        fixture.AssertCanFreeze();
    }

    [Fact]
    public async Task Null_completion_task_fails_truthfully_and_releases_owner()
    {
        var fixture = Fixture.Create();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Track(() => null));
        fixture.AssertCanFreeze();
    }

    [Fact]
    public async Task Finishing_one_app_factory_does_not_release_another()
    {
        var fixture = Fixture.Create();
        var firstFinish = Signal();
        var secondFinish = Signal();
        var first = fixture.Track(() => firstFinish.Task);
        var second = fixture.Track(() => secondFinish.Task);
        firstFinish.SetResult(true);
        await first.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(fixture.TryFreeze(out _));
        secondFinish.SetResult(true);
        await second.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.AssertCanFreeze();
    }

    [Fact]
    public async Task Separate_app_instances_do_not_share_gate_authority()
    {
        var first = Fixture.Create();
        var second = Fixture.Create();
        Assert.NotSame(first.Gate, second.Gate);
        Assert.True(first.TryFreeze(out var frozen));
        var calls = 0;
        await second.Track(() =>
        {
            calls++;
            return Task.CompletedTask;
        });

        Assert.Equal(1, calls);
        Assert.False(first.TryFreeze(out _));
        second.AssertCanFreeze();
        frozen.Dispose();
        first.AssertCanFreeze();
    }

    [Fact]
    public async Task Helper_does_not_add_a_task_run_or_move_initial_factory_invocation()
    {
        var fixture = Fixture.Create();
        var callerThread = Environment.CurrentManagedThreadId;
        var factoryThread = -1;
        var calls = 0;
        var tracked = fixture.Track(() =>
        {
            calls++;
            factoryThread = Environment.CurrentManagedThreadId;
            return Task.CompletedTask;
        });

        Assert.Equal(1, calls);
        Assert.Equal(callerThread, factoryThread);
        await tracked;
        fixture.AssertCanFreeze();
    }

    [Fact]
    public async Task Helper_preserves_ambient_operation_context_across_async_worker()
    {
        var fixture = Fixture.Create();
        var ambient = new AsyncLocal<string> { Value = "synthetic-operation-owner" };
        string before = null;
        string after = null;
        await fixture.Track(async () =>
        {
            before = ambient.Value;
            await Task.Yield();
            after = ambient.Value;
        });

        Assert.Equal("synthetic-operation-owner", before);
        Assert.Equal(before, after);
        Assert.Equal("synthetic-operation-owner", ambient.Value);
        fixture.AssertCanFreeze();
    }

    [Fact]
    public void Private_model_loader_returns_task_not_async_void()
    {
        var method = AppMethod("LoadModelsAsync");
        Assert.Equal(typeof(Task), method.ReturnType);
        Assert.False(method.IsStatic);
        Assert.NotNull(method.GetCustomAttribute<AsyncStateMachineAttribute>());
    }

    [Fact]
    public void Piper_start_owner_is_instance_bound_not_static()
    {
        var method = AppMethod("StartPiperInstallInBackground");
        Assert.False(method.IsStatic);
        Assert.Equal(typeof(void), method.ReturnType);
    }

    [Theory]
    [InlineData("Start", 2)]
    [InlineData("StartPiperInstallInBackground", 1)]
    [InlineData("HandleLifecycleAnnouncement", 1)]
    [InlineData("OpenFirstRunSetup", 2)]
    public void Existing_finite_caller_has_the_expected_shared_helper_calls(string methodName, int count)
    {
        var calls = CalledMethods(AppMethod(methodName));
        Assert.Equal(count, calls.Count(method => method.DeclaringType == typeof(KeyboardWtfApp)
            && method.Name == "TrackFiniteUpdateWorkAsync"));
    }

    [Fact]
    public void App_helper_delegates_to_the_same_instance_gate_track_boundary()
    {
        var fixture = Fixture.Create();
        var calls = CalledMethods(fixture.Helper);
        Assert.Contains(calls, method => method.DeclaringType == typeof(KeyboardWtfApp)
            && method.Name == "get_UpdateWorkAdmission");
        Assert.Contains(calls, method => method.DeclaringType == fixture.Gate.GetType()
            && method.Name == "TrackAsync");
        Assert.DoesNotContain(calls, method => method.DeclaringType == typeof(Task)
            && method.Name == "Run");
    }

    private static TaskCompletionSource<bool> Signal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Fixture
    {
        internal KeyboardWtfApp App { get; private init; }
        internal object Gate { get; private init; }
        internal PropertyInfo Property { get; private init; }
        internal MethodInfo Helper { get; private init; }

        internal static Fixture Create()
        {
            var property = typeof(KeyboardWtfApp).GetProperty("UpdateWorkAdmission",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.True(property is not null, "App-owned finite update work admission is not implemented.");
            Assert.NotNull(property.GetMethod);
            Assert.Equal("KeyboardWtf.Services.Updates.UpdateWorkAdmissionGate", property.PropertyType.FullName);
            var helper = AppMethod("TrackFiniteUpdateWorkAsync");
            Assert.Equal(typeof(Task), helper.ReturnType);
            var parameters = helper.GetParameters();
            Assert.Single(parameters);
            Assert.Equal(typeof(Func<Task>), parameters[0].ParameterType);

            var gate = Activator.CreateInstance(property.PropertyType, nonPublic: true);
            Assert.NotNull(gate);
            var field = typeof(KeyboardWtfApp).GetField("<UpdateWorkAdmission>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.True(field is not null, "The instance gate backing field is missing.");
            var app = (KeyboardWtfApp)RuntimeHelpers.GetUninitializedObject(typeof(KeyboardWtfApp));
            field.SetValue(app, gate);
            return new Fixture { App = app, Gate = gate, Property = property, Helper = helper };
        }

        internal Task Track(Func<Task> work) =>
            Assert.IsAssignableFrom<Task>(Invoke(App, Helper, new object[] { work }));

        internal bool TryFreeze(out Frozen frozen)
        {
            var arguments = new object[] { null };
            var accepted = (bool)Invoke(Gate, RequiredMethod(Gate.GetType(), "TryFreeze"), arguments);
            frozen = accepted ? new Frozen(arguments[0]) : null;
            return accepted;
        }

        internal void AssertCanFreeze()
        {
            Assert.True(TryFreeze(out var frozen));
            frozen.Dispose();
        }
    }

    private sealed class Frozen : IDisposable
    {
        private readonly object _value;
        private readonly IDisposable _dispose;
        internal Frozen(object value)
        {
            Assert.NotNull(value);
            _value = value;
            _dispose = Assert.IsAssignableFrom<IDisposable>(value);
        }
        internal void Commit() => Invoke(_value, RequiredMethod(_value.GetType(), "Commit"), Array.Empty<object>());
        public void Dispose() => _dispose.Dispose();
    }

    private static MethodInfo AppMethod(string name) => RequiredMethod(typeof(KeyboardWtfApp), name);

    private static MethodInfo RequiredMethod(Type type, string name)
    {
        var method = type.GetMethod(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.True(method is not null, $"{type.Name}.{name} is missing.");
        return method;
    }

    private static object Invoke(object target, MethodInfo method, object[] arguments)
    {
        try { return method.Invoke(target, arguments); }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private static readonly IReadOnlyDictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode))
        .Select(field => (OpCode)field.GetValue(null))
        .GroupBy(code => code.Value)
        .ToDictionary(group => group.Key, group => group.First());

    private static IReadOnlyList<MethodBase> CalledMethods(MethodInfo method)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        Assert.NotNull(il);
        var calls = new List<MethodBase>();
        for (var offset = 0; offset < il.Length;)
        {
            short value = il[offset++];
            if (value == 0xfe)
                value = unchecked((short)(0xfe00 | il[offset++]));
            Assert.True(OpCodesByValue.TryGetValue(value, out var code), "Unknown IL opcode in caller-shape check.");
            if (code.OperandType == OperandType.InlineMethod)
            {
                var token = BitConverter.ToInt32(il, offset);
                calls.Add(method.Module.ResolveMethod(token,
                    method.DeclaringType?.GetGenericArguments(), method.GetGenericArguments()));
            }
            offset += code.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI or OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineMethod
                    or OperandType.InlineSig or OperandType.InlineString or OperandType.InlineTok
                    or OperandType.InlineType or OperandType.ShortInlineR => 4,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => checked(4 + 4 * BitConverter.ToInt32(il, offset)),
                _ => throw new InvalidOperationException("Unsupported IL operand in caller-shape check."),
            };
        }
        return calls;
    }
}
