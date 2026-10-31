using System.Reflection;
using System.Reflection.Emit;
using KeyboardWtf.Services;
using KeyboardWtf.Services.Updates;
using Xunit;

namespace KeyboardWtf.Tests;

// Compiled call-shape tests only. Never invokes actual Program/app startup.
public sealed class UpdateProcessEntryWiringTests
{
    [Fact]
    public void Compiled_flavor_policy_cannot_be_changed_by_profile_or_environment()
    {
#if KEYBOARD_WTF_TEST_PUBLIC_PRODUCTION
        Assert.True(UpdateProcessEntry.RequiredForCurrentBuild);
#else
        Assert.False(UpdateProcessEntry.RequiredForCurrentBuild);
        var calls = 0;
        Assert.True(UpdateProcessEntry.RunForCurrentBuild(() => calls++, _ => Assert.Fail("Excluded build refused.")));
        Assert.Equal(1, calls); // testhost.exe cannot be an admitted installation; bypass was genuine.
#endif
        var policy = typeof(UpdateProcessEntry).GetProperty("RequiredForCurrentBuild", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.Empty(Calls(policy.GetMethod));
    }

    [Fact]
    public void Public_Main_admits_before_the_original_body_and_excluded_flavors_keep_direct_body()
    {
        var type = typeof(WebSettingsService).Assembly.GetType("KeyboardWtf.Program");
        Assert.NotNull(type);
        var main = Method(type, "Main");
        var body = Method(type, "RunOriginal");
        var calls = Calls(main);
#if KEYBOARD_WTF_TEST_PUBLIC_PRODUCTION
        Assert.Single(calls, call => call.DeclaringType == typeof(UpdateProcessEntry)
            && call.Name == "RunForCurrentBuild");
        Assert.DoesNotContain(calls, call => call == body);
#else
        Assert.Single(calls, call => call == body);
        Assert.DoesNotContain(calls, call => call.DeclaringType == typeof(UpdateProcessEntry));
#endif
        Assert.DoesNotContain(calls, call => call.DeclaringType?.Name is
            "BrowserNativeMessagingHost" or "WindowsAppLifecycleService" or "SettingsService" or "KeyboardWtfApp");
    }

    [Fact]
    public void Original_body_keeps_native_dispatch_before_restart_instance_lock_and_GUI()
    {
        var type = typeof(WebSettingsService).Assembly.GetType("KeyboardWtf.Program");
        Assert.NotNull(type);
        var calls = Calls(Method(type, "RunOriginal"));
        int Index(string owner, string name) => calls.FindIndex(call => call.DeclaringType?.Name == owner && call.Name == name);
        var native = Index("BrowserNativeMessagingHost", "IsNativeHostInvocation");
        var host = Index("BrowserNativeMessagingHost", "Run");
        var restart = Index("WindowsAppLifecycleService", "WaitForRestartParent");
        var instance = Index("Program", "TryAcquireInstanceLock");
        var app = Index("KeyboardWtfApp", ".ctor");
        Assert.True(native >= 0 && host > native && restart > host && instance > restart && app > instance);
        Assert.Single(calls, call => call.DeclaringType?.Name == "BrowserNativeMessagingHost" && call.Name == "Run");
        Assert.DoesNotContain(calls, call => call.DeclaringType == typeof(UpdateProcessEntry));
    }

    [Fact]
    public void Refused_native_invocation_does_not_write_stdout_or_enter_the_host()
    {
        var type = typeof(WebSettingsService).Assembly.GetType("KeyboardWtf.Program");
        Assert.NotNull(type);
        var calls = Calls(Method(type, "ReportStartupRefusal"));
        Assert.DoesNotContain(calls, call => call.DeclaringType == typeof(Console));
        Assert.DoesNotContain(calls, call => call.DeclaringType?.Name == "BrowserNativeMessagingHost" && call.Name == "Run");
        var native = calls.FindIndex(call => call.DeclaringType?.Name == "BrowserNativeMessagingHost"
            && call.Name == "IsNativeHostInvocation");
        var dialog = calls.FindIndex(call => call.DeclaringType?.Name == "MessageBox" && call.Name == "Show");
        Assert.True(native >= 0 && dialog > native);
    }

    private static MethodInfo Method(Type type, string name)
    {
        var method = type.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return method;
    }

    private static readonly IReadOnlyDictionary<short, OpCode> Codes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode))
        .Select(field => (OpCode)field.GetValue(null))
        .GroupBy(code => code.Value).ToDictionary(group => group.Key, group => group.First());

    private static List<MethodBase> Calls(MethodInfo method)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        Assert.NotNull(il);
        var calls = new List<MethodBase>();
        for (var offset = 0; offset < il.Length;)
        {
            short value = il[offset++];
            if (value == 0xfe) value = unchecked((short)(0xfe00 | il[offset++]));
            Assert.True(Codes.TryGetValue(value, out var code));
            if (code.OperandType == OperandType.InlineMethod &&
                (code == OpCodes.Call || code == OpCodes.Callvirt || code == OpCodes.Newobj))
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
                _ => throw new InvalidOperationException("Unsupported IL operand in process-entry wiring check."),
            };
        }
        return calls;
    }
}
