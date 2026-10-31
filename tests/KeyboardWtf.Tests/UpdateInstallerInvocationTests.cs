using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;
using KeyboardWtf.Services;
using KeyboardWtf.Services.Updates;
using Xunit;

namespace KeyboardWtf.Tests;

// Pure, UNWIRED argument planning. Every path/parent is a declared synthetic
// value, never opened/probed/executed. This does NOT authenticate an OS process,
// pin artifact bytes, prove installer behavior or establish successful relaunch.
// Inno's updater-only safety branch is not a parent handshake. Activation stays gated.
public sealed class UpdateInstallerInvocationTests
{
    private const string Target = @"C:\Program Files\keyboard.wtf\keyboard.wtf.exe";
    private const string Package = @"C:\Synthetic Downloads\keyboard.wtf-setup.exe";
    private const int ParentPid = 12345;
    private static readonly long ParentTicks = new DateTime(2026, 10, 4, 12, 34, 56, DateTimeKind.Utc).Ticks;

    [Fact]
    public void Valid_invocation_preserves_declared_identity_and_exact_prepared_tuple()
    {
        var binding = Binding.Require(); // Missing production seam fails BEFORE test work.
        var approved = Update();
        var prepared = binding.Prepared(approved, Package);
        var parent = binding.Parent(ParentPid, ParentTicks, Target);
        var invocation = binding.Create(prepared, parent, Target);
        Assert.Same(prepared, binding.Read(invocation, "Prepared"));
        Assert.Same(parent, binding.Read(invocation, "Parent"));
        Assert.Equal(Target, binding.Read(invocation, "TargetExecutablePath"));
        Assert.Same(approved, binding.PreparedUpdate.GetValue(prepared));
        Assert.Equal(Package, binding.PreparedPath.GetValue(prepared));
        Assert.Equal(ParentPid, binding.ParentPid.GetValue(parent));
        Assert.Equal(ParentTicks, binding.ParentTicks.GetValue(parent));
        Assert.Equal(Target, binding.ParentPath.GetValue(parent));
        AssertVector(binding.Arguments(invocation), Target, ParentPid, ParentTicks, "2.3.4", "stable");
    }

    [Theory]
    [InlineData(@"C:\Release = Ready\keyboard.wtf.exe", @"D:\Downloads = Ready\keyboard.wtf-setup.exe")]
    [InlineData(@"C:\Données\keyboard.wtf.exe", @"D:\文档\keyboard.wtf-setup.exe")]
    [InlineData(@"C:\keyboard.wtf.exe", @"D:\keyboard.wtf-setup.exe")]
    public void Legitimate_spaces_equals_unicode_and_drive_root_preserve_argument_boundaries(string target, string package)
    {
        var binding = Binding.Require();
        var invocation = binding.Create(binding.Prepared(Update(), package), binding.Parent(ParentPid, ParentTicks, target), target);
        AssertVector(binding.Arguments(invocation), target, ParentPid, ParentTicks, "2.3.4", "stable");
        Assert.Equal(target, binding.Read(invocation, "TargetExecutablePath"));
        Assert.Equal(package, binding.PreparedPath.GetValue(binding.Read(invocation, "Prepared")));
    }

    [Fact]
    public void Case_insensitive_parent_equality_uses_trusted_target_spelling_in_every_target_argument()
    {
        var binding = Binding.Require();
        const string trusted = @"C:\Trusted Folder\keyboard.wtf.exe";
        const string declaration = @"c:\TRUSTED FOLDER\KEYBOARD.WTF.EXE";
        const string package = @"d:\DOWNLOADS\KEYBOARD.WTF-SETUP.EXE";
        var parent = binding.Parent(ParentPid, ParentTicks, declaration);
        var invocation = binding.Create(binding.Prepared(Update(), package), parent, trusted);
        Assert.Same(parent, binding.Read(invocation, "Parent"));
        Assert.Equal(declaration, binding.ParentPath.GetValue(parent));
        Assert.Equal(trusted, binding.Read(invocation, "TargetExecutablePath"));
        AssertVector(binding.Arguments(invocation), trusted, ParentPid, ParentTicks, "2.3.4", "stable");
    }

    [Theory]
    [InlineData("ar-EG")]
    [InlineData("fr-FR")]
    [InlineData("tr-TR")]
    public void Numeric_arguments_and_channel_are_canonical_independent_of_current_culture(string culture)
    {
        var binding = Binding.Require();
        var original = CultureInfo.CurrentCulture;
        var originalUi = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            CultureInfo.CurrentUICulture = new CultureInfo(culture);
            var invocation = binding.Create(binding.Prepared(Update() with { Channel = "beta" }, Package),
                binding.Parent(int.MaxValue, DateTime.MaxValue.Ticks, Target), Target);
            AssertVector(binding.Arguments(invocation), Target, int.MaxValue, DateTime.MaxValue.Ticks, "2.3.4", "beta");
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
            CultureInfo.CurrentUICulture = originalUi;
        }
    }

    [Fact]
    public void Exposed_properties_are_get_only_and_argument_vector_cannot_be_mutated()
    {
        var binding = Binding.Require();
        var invocation = binding.Create(binding.Prepared(Update(), Package), binding.Parent(ParentPid, ParentTicks, Target), Target);
        foreach (var name in new[] { "Prepared", "Parent", "TargetExecutablePath", "Arguments" })
            Assert.Null(binding.InvocationType.GetProperty(name, Binding.AllInstance).SetMethod);
        var arguments = binding.Arguments(invocation);
        var before = arguments.ToArray();
        if (arguments is IList<string> generic)
        {
            Assert.True(generic.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => generic[0] = "/ARBITRARY");
            Assert.Throws<NotSupportedException>(() => generic.Add("/ARBITRARY"));
            Assert.Throws<NotSupportedException>(() => generic.Clear());
        }
        if (arguments is IList nongeneric)
        {
            Assert.True(nongeneric.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => nongeneric[0] = "/ARBITRARY");
            Assert.Throws<NotSupportedException>(() => nongeneric.Add("/ARBITRARY"));
        }
        Assert.Equal(before, binding.Arguments(invocation));
        AssertVector(arguments, Target, ParentPid, ParentTicks, "2.3.4", "stable");
    }

    [Theory]
    [InlineData("prepared")]
    [InlineData("parent")]
    [InlineData("trusted-target")]
    [InlineData("prepared-update")]
    [InlineData("prepared-path")]
    [InlineData("parent-path")]
    [InlineData("version")]
    [InlineData("channel")]
    public void Null_contract_members_are_rejected_as_validation_errors(string member)
    {
        var binding = Binding.Require();
        var approved = Update();
        if (member == "version") approved = approved with { Version = null };
        if (member == "channel") approved = approved with { Channel = null };
        var prepared = member == "prepared" ? null : binding.Prepared(member == "prepared-update" ? null : approved,
            member == "prepared-path" ? null : Package);
        var parent = member == "parent" ? null : binding.Parent(ParentPid, ParentTicks, member == "parent-path" ? null : Target);
        if (member is "prepared" or "parent" or "trusted-target" or "prepared-update")
            Assert.Throws<ArgumentNullException>(() => binding.Create(prepared, parent, member == "trusted-target" ? null : Target));
        else
            Assert.Throws<InvalidDataException>(() => binding.Create(prepared, parent, Target));
    }

    [Fact]
    public void Non_positive_declared_pid_is_rejected()
    {
        var binding = Binding.Require();
        foreach (var processId in new[] { 0, -1, int.MinValue })
            Assert.Throws<InvalidDataException>(() => binding.Create(binding.Prepared(Update(), Package),
                binding.Parent(processId, ParentTicks, Target), Target));
    }

    [Fact]
    public void Invalid_declared_creation_ticks_are_rejected()
    {
        var binding = Binding.Require();
        foreach (var ticks in new[] { 0L, -1L, long.MinValue, long.MaxValue, DateTime.MaxValue.Ticks + 1 })
            Assert.Throws<InvalidDataException>(() => binding.Create(binding.Prepared(Update(), Package),
                binding.Parent(ParentPid, ticks, Target), Target));
    }

    [Theory]
    [InlineData(1, 1L)]
    [InlineData(int.MaxValue, 3155378975999999999L)]
    public void Positive_declared_identity_boundaries_are_not_misrepresented_as_actual_os_proof(int processId, long ticks)
    {
        var binding = Binding.Require();
        var invocation = binding.Create(binding.Prepared(Update(), Package), binding.Parent(processId, ticks, Target), Target);
        AssertVector(binding.Arguments(invocation), Target, processId, ticks, "2.3.4", "stable");
        // No process with this declared PID/timestamp is opened or queried.
    }

    [Theory]
    [MemberData(nameof(InvalidCanonicalPaths))]
    public void Malformed_or_aliased_paths_are_rejected_for_each_declared_role(string group, string[] templates)
    {
        var binding = Binding.Require();
        Assert.False(string.IsNullOrWhiteSpace(group));
        foreach (var template in templates)
        {
            var badGui = template.Replace("{leaf}", "keyboard.wtf.exe", StringComparison.Ordinal);
            var badPackage = template.Replace("{leaf}", "keyboard.wtf-setup.exe", StringComparison.Ordinal);
            // A malformed target is also the declared parent so a mismatch alone
            // cannot satisfy the target-path validation test.
            Assert.Throws<InvalidDataException>(() => binding.Create(binding.Prepared(Update(), Package),
                binding.Parent(ParentPid, ParentTicks, badGui), badGui));
            Assert.Throws<InvalidDataException>(() => binding.Create(binding.Prepared(Update(), Package),
                binding.Parent(ParentPid, ParentTicks, badGui), Target));
            Assert.Throws<InvalidDataException>(() => binding.Create(binding.Prepared(Update(), badPackage),
                binding.Parent(ParentPid, ParentTicks, Target), Target));
        }
    }

    public static IEnumerable<object[]> InvalidCanonicalPaths()
    {
        yield return ["empty-relative-rooted", new[] {
            "", " ", @"relative\{leaf}", @"C:relative\{leaf}", @"\rooted\{leaf}", @"/rooted/{leaf}",
            @"C:/directory/{leaf}" }];
        yield return ["network-device-prefix", new[] {
            @"\\server\share\{leaf}", @"\\?\C:\directory\{leaf}", @"\\.\C:\directory\{leaf}" }];
        yield return ["duplicate-dot-traversal-segments", new[] {
            @"C:\directory\\{leaf}", @"C:\.\{leaf}", @"C:\directory\..\{leaf}" }];
        yield return ["trailing-leading-space-dot-aliases", new[] { @"C:\directory.\{leaf}",
            @"C:\directory \{leaf}", @"C:\directory\{leaf}.", @"C:\directory\{leaf} ", @"C:\directory\{leaf}\",
            @" C:\directory\{leaf}" }];
        yield return ["alternate-data-streams", new[] { @"C:\directory:stream\{leaf}", @"C:\directory\{leaf}:stream" }];
        yield return ["quotes-control-characters", new[] {
            "C:\\dir\"ectory\\{leaf}", "C:\\dir\0ectory\\{leaf}", "C:\\dir\nectory\\{leaf}",
            "C:\\dir\rectory\\{leaf}", "C:\\dir\tectory\\{leaf}" }];
        yield return ["wildcards-invalid-segment-characters", new[] { @"C:\dir*ectory\{leaf}",
            @"C:\dir?ectory\{leaf}", @"C:\dir|ectory\{leaf}", @"C:\dir<ectory\{leaf}", @"C:\dir>ectory\{leaf}" }];
        yield return ["reserved-dos-segments", new[] {
            @"C:\CON\{leaf}", @"C:\NUL.txt\{leaf}", @"C:\AUX\{leaf}", @"C:\COM1 .log\{leaf}",
            @"C:\LPT9\{leaf}", @"C:\CONIN$\{leaf}", @"C:\CONOUT$\{leaf}" }];
    }

    [Theory]
    [InlineData("keyboard.wtf.dll")]
    [InlineData("keyboard.wtf.exe.bak")]
    [InlineData("keyboard-wtf.exe")]
    [InlineData("native-host.exe")]
    public void Trusted_and_parent_executable_must_have_the_exact_gui_filename(string leaf)
    {
        var binding = Binding.Require();
        var bad = @"C:\installation\" + leaf;
        Assert.Throws<InvalidDataException>(() => binding.Create(binding.Prepared(Update(), Package),
            binding.Parent(ParentPid, ParentTicks, bad), bad));
    }

    [Theory]
    [InlineData("keyboard.wtf.exe")]
    [InlineData("keyboard.wtf-setup")]
    [InlineData("keyboard-wtf-setup.exe")]
    [InlineData("keyboard.wtf-setup.exe.bak")]
    public void Prepared_artifact_must_have_the_exact_downloaded_installer_filename(string leaf)
    {
        var binding = Binding.Require();
        Assert.Throws<InvalidDataException>(() => binding.Create(binding.Prepared(Update(), @"C:\download\" + leaf),
            binding.Parent(ParentPid, ParentTicks, Target), Target));
    }

    [Theory]
    [InlineData(@"D:\Program Files\keyboard.wtf\keyboard.wtf.exe")]
    [InlineData(@"C:\Other Installation\keyboard.wtf.exe")]
    [InlineData(@"C:\Program Files\keyboard.wtf-personal-api-candidate\keyboard.wtf.exe")]
    public void Declared_parent_from_a_different_installation_cannot_authorize_the_target(string declaration)
    {
        var binding = Binding.Require();
        Assert.Throws<InvalidDataException>(() => binding.Create(binding.Prepared(Update(), Package),
            binding.Parent(ParentPid, ParentTicks, declaration), Target));
    }

    [Theory]
    [InlineData("2.3")]
    [InlineData("2.3.4.0")]
    [InlineData("2.3.4.5")]
    public void Signed_version_requires_exactly_three_parts_without_silent_truncation(string version)
    {
        var binding = Binding.Require();
        Assert.Throws<InvalidDataException>(() => binding.Create(binding.Prepared(Update() with { Version = Version.Parse(version) }, Package),
            binding.Parent(ParentPid, ParentTicks, Target), Target));
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(int.MaxValue, int.MaxValue, int.MaxValue)]
    public void Three_part_version_is_formatted_exactly_without_inventing_another_version_policy(int major, int minor, int build)
    {
        var binding = Binding.Require();
        var version = new Version(major, minor, build);
        var invocation = binding.Create(binding.Prepared(Update() with { Version = version }, Package),
            binding.Parent(ParentPid, ParentTicks, Target), Target);
        AssertVector(binding.Arguments(invocation), Target, ParentPid, ParentTicks, version.ToString(3), "stable");
        // Manifest trust/anti-downgrade/minimum-version checks belong upstream.
    }

    [Fact]
    public void Noncanonical_or_injected_signed_channel_is_rejected()
    {
        var binding = Binding.Require();
        foreach (var channel in new[] { "", " ", "Stable", "BETA", "stable ", " beta", "nightly",
                     "stable /CLOSEAPPLICATIONS", "stable\n/KWTUPDATE" })
            Assert.Throws<InvalidDataException>(() => binding.Create(binding.Prepared(Update() with { Channel = channel }, Package),
                binding.Parent(ParentPid, ParentTicks, Target), Target));
    }

    [Fact]
    public void Paths_exceeding_the_existing_32767_envelope_are_rejected_without_execution()
    {
        var binding = Binding.Require();
        var prefix = @"C:\" + new string('a', 32768) + @"\";
        var badGui = prefix + "keyboard.wtf.exe";
        var badPackage = prefix + "keyboard.wtf-setup.exe";
        Assert.Throws<InvalidDataException>(() => binding.Create(binding.Prepared(Update(), Package),
            binding.Parent(ParentPid, ParentTicks, badGui), badGui));
        Assert.Throws<InvalidDataException>(() => binding.Create(binding.Prepared(Update(), badPackage),
            binding.Parent(ParentPid, ParentTicks, Target), Target));
        // No filesystem call or claim that a giant component is OS-supported.
    }

    [Fact]
    public void Fifty_cheap_distinct_plans_keep_arguments_independent_and_never_touch_files_or_processes()
    {
        var binding = Binding.Require();
        var plans = new List<(object Plan, string[] Expected)>();
        for (var index = 0; index < 50; index++)
        {
            var target = @"C:\Synthetic Slot = " + index.ToString(CultureInfo.InvariantCulture) + @"\keyboard.wtf.exe";
            var package = @"D:\Synthetic Package = " + index.ToString(CultureInfo.InvariantCulture) + @"\keyboard.wtf-setup.exe";
            var version = new Version(2, 3, index);
            var channel = index % 2 == 0 ? "stable" : "beta";
            var approved = Update() with { Version = version, Channel = channel };
            var prepared = binding.Prepared(approved, package);
            var parent = binding.Parent(index + 1, ParentTicks + index, target);
            var plan = binding.Create(prepared, parent, target);
            Assert.Same(prepared, binding.Read(plan, "Prepared"));
            Assert.Same(parent, binding.Read(plan, "Parent"));
            AssertVector(binding.Arguments(plan), target, index + 1, ParentTicks + index, version.ToString(3), channel);
            plans.Add((plan, binding.Arguments(plan).ToArray()));
        }
        foreach (var plan in plans) Assert.Equal(plan.Expected, binding.Arguments(plan.Plan));
    }

    [Fact]
    public void Pure_invocation_contract_cannot_enable_the_unfinished_installation_path()
    {
        _ = Binding.Require();
        var readiness = typeof(WebSettingsService).GetProperty("SafeUpdateInstallationReady", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(readiness);
        Assert.False((bool)readiness.GetValue(null));
    }

    private static VerifiedUpdate Update() => new(new Version(2, 3, 4), new Version(1, 0, 0), "stable",
        new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.Zero),
        new Uri("https://github.com/UnknownGod2011/keyboard.wtf/releases/download/synthetic/keyboard.wtf-setup.exe"),
        new string('a', 64), 1234, "Synthetic already-approved tuple; not reverified here", "synthetic-key");

    private static void AssertVector(IReadOnlyList<string> actual, string target, int processId, long ticks, string version, string channel)
    {
        var separator = target.LastIndexOf('\\');
        var directory = separator == 2 ? target[..3] : target[..separator];
        Assert.Equal(new[]
        {
            "/SILENT", "/KWTUPDATE",
            "/NOCLOSEAPPLICATIONS", "/NOFORCECLOSEAPPLICATIONS", "/NORESTARTAPPLICATIONS",
            "/NORESTART", "/RESTARTEXITCODE=3010",
            "/KWT_PARENT_PID=" + processId.ToString(CultureInfo.InvariantCulture),
            "/KWT_PARENT_STARTED_UTC_TICKS=" + ticks.ToString(CultureInfo.InvariantCulture),
            "/KWT_PARENT_EXE=" + target, "/KWT_TARGET_VERSION=" + version,
            "/KWT_TARGET_CHANNEL=" + channel, "/DIR=" + directory,
        }, actual);
        Assert.Equal(13, actual.Count);
        Assert.DoesNotContain(actual, argument => argument.StartsWith("/CLOSEAPPLICATIONS", StringComparison.OrdinalIgnoreCase)
            || argument.StartsWith("/RESTARTAPPLICATIONS", StringComparison.OrdinalIgnoreCase)
            || argument.StartsWith("--restart-after", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class Binding
    {
        internal const BindingFlags AllInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        internal Type InvocationType { get; }
        internal PropertyInfo PreparedUpdate { get; }
        internal PropertyInfo PreparedPath { get; }
        internal PropertyInfo ParentPid { get; }
        internal PropertyInfo ParentTicks { get; }
        internal PropertyInfo ParentPath { get; }
        private readonly ConstructorInfo _prepared;
        private readonly ConstructorInfo _parent;
        private readonly MethodInfo _create;
        private readonly PropertyInfo _arguments;

        private Binding()
        {
            var assembly = typeof(UpdateWorkAdmissionGate).Assembly;
            Type RequireType(string name)
            {
                var type = assembly.GetType("KeyboardWtf.Services.Updates." + name);
                Assert.True(type is not null, "The unwired installer invocation contract is missing: " + name);
                return type;
            }
            InvocationType = RequireType("UpdateInstallerInvocation");
            var parentType = RequireType("DeclaredUpdateParent");
            var preparedType = RequireType("PreparedUpdate");
            _prepared = preparedType.GetConstructor(AllInstance, null, [typeof(VerifiedUpdate), typeof(string)], null);
            _parent = parentType.GetConstructor(AllInstance, null, [typeof(int), typeof(long), typeof(string)], null);
            Assert.NotNull(_prepared);
            Assert.NotNull(_parent);
            PreparedUpdate = RequireProperty(preparedType, "Update", typeof(VerifiedUpdate));
            PreparedPath = RequireProperty(preparedType, "InstallerPath", typeof(string));
            ParentPid = RequireProperty(parentType, "ProcessId", typeof(int));
            ParentTicks = RequireProperty(parentType, "CreationTimeUtcTicks", typeof(long));
            ParentPath = RequireProperty(parentType, "ExecutablePath", typeof(string));
            _ = RequireProperty(InvocationType, "Prepared", preparedType);
            _ = RequireProperty(InvocationType, "Parent", parentType);
            _ = RequireProperty(InvocationType, "TargetExecutablePath", typeof(string));
            _arguments = RequireProperty(InvocationType, "Arguments", typeof(IReadOnlyList<string>));
            _create = InvocationType.GetMethod("Create", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                null, [preparedType, parentType, typeof(string)], null);
            Assert.NotNull(_create);
            Assert.Equal(InvocationType, _create.ReturnType);
        }

        internal static Binding Require() => new();
        internal object Prepared(VerifiedUpdate update, string path) => Invoke(() => _prepared.Invoke([update, path]));
        internal object Parent(int processId, long ticks, string path) => Invoke(() => _parent.Invoke([processId, ticks, path]));
        internal object Create(object prepared, object parent, string target) => Invoke(() => _create.Invoke(null, [prepared, parent, target]));
        internal object Read(object invocation, string name) => InvocationType.GetProperty(name, AllInstance).GetValue(invocation);
        internal IReadOnlyList<string> Arguments(object invocation) => Assert.IsAssignableFrom<IReadOnlyList<string>>(_arguments.GetValue(invocation));

        private static PropertyInfo RequireProperty(Type owner, string name, Type type)
        {
            var property = owner.GetProperty(name, AllInstance);
            Assert.NotNull(property);
            Assert.Equal(type, property.PropertyType);
            return property;
        }

        private static object Invoke(Func<object> action)
        {
            try { return action(); }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
        }
    }
}
