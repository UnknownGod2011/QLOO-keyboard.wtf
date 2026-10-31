using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using KeyboardWtf.Services;
using KeyboardWtf.Services.Updates;
using Xunit;

namespace KeyboardWtf.Tests;

// Compiled call-shape/ordering checks only: no real app, timer, network,
// NotifyIcon, profile, provider or installer is created. Only private builds
// invoke the proven early-refusal method on an uninitialized app object.
// Attention's guarded attempt is not proof that an OS balloon was visible.
public sealed class PublicUpdateNotificationWiringTests
{
    [Fact]
    public void Required_wiring_contract_is_resolved_before_any_synthetic_app_object()
    {
        var binding = Binding.Require();
        Assert.False(binding.Start.IsStatic);
        Assert.True(binding.Start.IsPrivate);
        Assert.Equal(typeof(void), binding.Start.ReturnType);
        Assert.Empty(binding.Start.GetParameters());
        Assert.True(binding.Check.IsStatic);
        Assert.True(binding.Check.IsPrivate);
        Assert.Equal(typeof(Task<VerifiedUpdate>), binding.Check.ReturnType);
        Assert.Equal(new[] { typeof(string), typeof(CancellationToken) },
            binding.Check.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.False(binding.Stop.IsStatic);
        Assert.True(binding.Stop.IsPrivate);
        Assert.Equal(typeof(void), binding.Stop.ReturnType);
        Assert.Empty(binding.Stop.GetParameters());
        Assert.Equal(typeof(System.Windows.Forms.Timer), binding.Timer.FieldType);
        Assert.Equal(typeof(PublicUpdateNotificationMonitor), binding.Monitor.FieldType);
    }

    [Fact]
    public void Existing_app_Start_calls_notification_setup_once_after_first_run_setup()
    {
        var binding = Binding.Require();
        var calls = Calls(RequiredMethod(typeof(KeyboardWtfApp), "Start"));
        var notification = Assert.Single(calls, call => Same(call, binding.Start));
        var setup = Assert.Single(calls, call => call.DeclaringType == typeof(KeyboardWtfApp)
            && call.Name == "OpenFirstRunSetup");
        Assert.True(calls.IndexOf(notification) > calls.IndexOf(setup));
        Assert.Contains(calls.Take(calls.IndexOf(notification)), call => call.DeclaringType == typeof(WebSettingsService)
            && call.Name == "Start");
    }

    [Fact]
    public void Public_eligibility_is_checked_before_monitor_or_timer_resources_and_private_Start_is_inert()
    {
        var binding = Binding.Require();
        var calls = Calls(binding.Start);
        var guard = Assert.Single(calls, call => call.DeclaringType == typeof(PublicUpdateEligibility)
            && call.Name == "IsAllowed");
        var monitor = Assert.Single(calls, call => call.IsConstructor
            && call.DeclaringType == typeof(PublicUpdateNotificationMonitor));
        var timer = Assert.Single(calls, call => call.IsConstructor
            && call.DeclaringType == typeof(System.Windows.Forms.Timer));
        Assert.True(calls.IndexOf(guard) < calls.IndexOf(monitor));
        Assert.True(calls.IndexOf(guard) < calls.IndexOf(timer));
#if KEYBOARD_WTF_TEST_PRIVATE_CANDIDATE || KEYBOARD_WTF_TEST_CONNECTOR_STAGING_CANDIDATE
        // No constructor/field initializer/default profile/UI handle is run.
        // The actual candidate guard must return before touching null resources.
        Assert.False(PublicUpdateEligibility.IsAllowed());
        var app = (KeyboardWtfApp)RuntimeHelpers.GetUninitializedObject(typeof(KeyboardWtfApp));
        Assert.Null(binding.Timer.GetValue(app));
        Assert.Null(binding.Monitor.GetValue(app));
        Invoke(binding.Start, app);
        Assert.Null(binding.Timer.GetValue(app));
        Assert.Null(binding.Monitor.GetValue(app));
#endif
        // Public builds inspect compiled code only; never invoke app/network/UI.
    }

    [Fact]
    public void Signed_checker_returns_original_unwrapped_Task_Run_and_owns_its_independent_client_off_UI()
    {
        var binding = Binding.Require();
        Assert.Null(binding.Check.GetCustomAttribute<AsyncStateMachineAttribute>());
        var calls = Calls(binding.Check);
        var run = Assert.Single(calls, call => call.DeclaringType == typeof(Task) && call.Name == "Run");
        Assert.Equal(typeof(Task<VerifiedUpdate>), Assert.IsAssignableFrom<MethodInfo>(run).ReturnType);
        Assert.Equal(typeof(Func<Task<VerifiedUpdate>>), run.GetParameters()[0].ParameterType);
        Assert.DoesNotContain(calls, call => call.DeclaringType == typeof(SecureUpdateService));
        Assert.DoesNotContain(calls, IsBlockingOrTaskReplacement);
        var worker = Assert.Single(Calls(binding.Check, includeDelegateReferences: true).OfType<MethodInfo>(), call => call.DeclaringType != typeof(Task)
            && call.Name.Contains("<CheckPublicUpdateAsync>", StringComparison.Ordinal));
        var workerCalls = Calls(ExecutionBody(worker));
        Assert.Single(workerCalls, call => call.IsConstructor && call.DeclaringType == typeof(SecureUpdateService));
        Assert.Single(workerCalls, call => call.DeclaringType == typeof(SecureUpdateService)
            && call.Name == "CheckAsync");
        Assert.Contains(workerCalls, call => call.Name == "Dispose"
            && (call.DeclaringType == typeof(SecureUpdateService) || call.DeclaringType == typeof(IDisposable)));
        Assert.DoesNotContain(workerCalls, call => call.DeclaringType == typeof(SecureUpdateService)
            && call.Name is "DownloadAsync" or "LaunchInstaller");
    }

    [Fact]
    public void Notification_Stop_precedes_existing_resource_disposal_and_never_blocks_or_cancels_features()
    {
        var binding = Binding.Require();
        var appCalls = Calls(RequiredMethod(typeof(KeyboardWtfApp), "Dispose"));
        var stopCall = Assert.Single(appCalls, call => Same(call, binding.Stop));
        Assert.Same(stopCall, appCalls[0]);
        Assert.Contains(appCalls.Skip(1), call => call.Name == "Dispose" && call.DeclaringType == typeof(HotkeyService));
        // The overlay/timer public Dispose is inherited from Component; all
        // existing disposal calls still follow the first notification Stop.
        Assert.Contains(appCalls.Skip(1), call => call.Name == "Dispose" && call.DeclaringType == typeof(System.ComponentModel.Component));
        Assert.Contains(appCalls.Skip(1), call => call.Name == "Dispose" && call.DeclaringType == typeof(WebSettingsService));
        var calls = Calls(binding.Stop);
        Assert.Contains(calls, call => call.DeclaringType == typeof(System.Windows.Forms.Timer) && call.Name == "Stop");
        Assert.Contains(calls, call => call.Name == "Dispose"
            && (call.DeclaringType == typeof(System.ComponentModel.Component)
                || call.DeclaringType == typeof(System.Windows.Forms.Timer)));
        Assert.Contains(calls, call => call.DeclaringType == typeof(PublicUpdateNotificationMonitor) && call.Name == "Dispose");
        Assert.DoesNotContain(calls, IsBlockingOrTaskReplacement);
        Assert.DoesNotContain(calls, call => call.Name is "CancelCurrent" or "Cancel" or "StopRecording" or "StopRecordingAsync"
            || call.DeclaringType == typeof(GeminiLiveConversationService)
            || call.DeclaringType == typeof(VoiceCaptureService));
    }

    private static bool IsBlockingOrTaskReplacement(MethodBase call) =>
        call.Name is "Wait" or "WaitAll" or "WaitAny" or "get_Result" or "GetResult" or "WaitAsync" or "ContinueWith" or "Unwrap";

    private static bool Same(MethodBase first, MethodBase second) =>
        first.Module == second.Module && first.MetadataToken == second.MetadataToken;

    private static MethodInfo RequiredMethod(Type type, string name)
    {
        var method = type.GetMethod(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.True(method is not null, $"{type.Name}.{name} is missing.");
        return method;
    }

    private static MethodInfo ExecutionBody(MethodInfo method)
    {
        var state = method.GetCustomAttribute<AsyncStateMachineAttribute>();
        return state is null ? method : RequiredMethod(state.StateMachineType, "MoveNext");
    }

    private static void Invoke(MethodInfo method, object target)
    {
        try { method.Invoke(target, null); }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private sealed class Binding
    {
        internal MethodInfo Start { get; } = RequiredMethod(typeof(KeyboardWtfApp), "StartPublicUpdateNotifications");
        internal MethodInfo Check { get; } = RequiredMethod(typeof(KeyboardWtfApp), "CheckPublicUpdateAsync");
        internal MethodInfo Stop { get; } = RequiredMethod(typeof(KeyboardWtfApp), "StopPublicUpdateNotifications");
        internal FieldInfo Timer { get; }
        internal FieldInfo Monitor { get; }

        private Binding()
        {
            Timer = typeof(KeyboardWtfApp).GetField("_publicUpdateTimer", BindingFlags.Instance | BindingFlags.NonPublic);
            Monitor = typeof(KeyboardWtfApp).GetField("_publicUpdateNotifications", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(Timer);
            Assert.NotNull(Monitor);
        }

        internal static Binding Require() => new();
    }

    private static readonly IReadOnlyDictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode))
        .Select(field => (OpCode)field.GetValue(null))
        .GroupBy(code => code.Value)
        .ToDictionary(group => group.Key, group => group.First());

    private static List<MethodBase> Calls(MethodInfo method, bool includeDelegateReferences = false)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        Assert.NotNull(il);
        var calls = new List<MethodBase>();
        for (var offset = 0; offset < il.Length;)
        {
            short value = il[offset++];
            if (value == 0xfe) value = unchecked((short)(0xfe00 | il[offset++]));
            Assert.True(OpCodesByValue.TryGetValue(value, out var code), "Unknown IL opcode in updater wiring check.");
            if (code.OperandType == OperandType.InlineMethod
                && (includeDelegateReferences || code == OpCodes.Call || code == OpCodes.Callvirt || code == OpCodes.Newobj))
                calls.Add(method.Module.ResolveMethod(BitConverter.ToInt32(il, offset),
                    method.DeclaringType?.GetGenericArguments(), method.GetGenericArguments()));
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
                _ => throw new InvalidOperationException("Unsupported IL operand in updater wiring check."),
            };
        }
        return calls;
    }
}
