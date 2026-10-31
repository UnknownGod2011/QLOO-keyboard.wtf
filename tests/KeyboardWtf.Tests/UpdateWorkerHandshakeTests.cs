using System.Reflection;
using System.Runtime.ExceptionServices;
using KeyboardWtf.Services;
using KeyboardWtf.Services.Updates;
using Xunit;

namespace KeyboardWtf.Tests;

// Pure protocol envelopes. No process, pipe, executable, installer or app is
// created here. Actual Windows peer/phase checks are a separate inert fixture.
public sealed class UpdateWorkerHandshakeTests
{
    private const string Target = @"C:\Synthetic Installed\keyboard.wtf.exe";
    private const string Package = @"D:\Synthetic Prepared\keyboard.wtf-setup.exe";
    private const string Nonce = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private static readonly DeclaredUpdateParent Parent = new(1234, 638950000000000000, Target);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Each_phase_has_one_fixed_exact_envelope_and_independent_immutable_bytes(byte phase)
    {
        var api = Api.Require();
        var expected = api.Create(Invocation(), Nonce);
        var frame = api.Frame(expected, phase);
        Assert.Equal(70, frame.Length);
        Assert.Equal("KWTU", System.Text.Encoding.ASCII.GetString(frame, 0, 4));
        Assert.Equal(1, frame[4]);
        Assert.Equal(phase, frame[5]);
        Assert.Equal(Convert.FromHexString(Nonce), frame[6..38]);
        api.Validate(expected, frame, phase);
        frame[6] ^= 1;
        var second = api.Frame(expected, phase);
        Assert.Equal(Convert.FromHexString(Nonce), second[6..38]);
        Assert.Throws<InvalidDataException>(() => api.Validate(expected, frame, phase));
        api.Validate(expected, second, phase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("0123456789abcdef")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0")]
    [InlineData("g123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public void Invalid_attempt_nonce_cannot_become_a_handshake(string nonce)
    {
        var api = Api.Require();
        Assert.Throws<InvalidDataException>(() => api.Create(Invocation(), nonce));
    }

    [Fact]
    public void Null_invocation_is_not_a_prepared_binding()
    {
        var api = Api.Require();
        Assert.Throws<ArgumentNullException>(() => api.Create(null, Nonce));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(255)]
    public void Undefined_phase_is_not_readiness(byte phase)
    {
        var api = Api.Require();
        var expected = api.Create(Invocation(), Nonce);
        Assert.Throws<InvalidDataException>(() => api.Frame(expected, phase));
        Assert.Throws<InvalidDataException>(() => api.Validate(expected, api.Frame(expected, 1), phase));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(37)]
    [InlineData(38)]
    [InlineData(69)]
    public void Missing_or_mutated_frame_byte_is_not_readiness(int index)
    {
        var api = Api.Require();
        var expected = api.Create(Invocation(), Nonce);
        var frame = api.Frame(expected, 1);
        if (index == -1) frame = frame[..^1];
        else frame[index] ^= 0xff;
        Assert.Throws<InvalidDataException>(() => api.Validate(expected, frame, 1));
    }

    [Fact]
    public void Extra_or_null_frame_and_wrong_phase_are_rejected()
    {
        var api = Api.Require();
        var expected = api.Create(Invocation(), Nonce);
        Assert.Throws<InvalidDataException>(() => api.Validate(expected, null, 1));
        Assert.Throws<InvalidDataException>(() => api.Validate(expected, [.. api.Frame(expected, 1), 0], 1));
        Assert.Throws<InvalidDataException>(() => api.Validate(expected, api.Frame(expected, 1), 2));
        Assert.Throws<InvalidDataException>(() => api.Validate(expected, api.Frame(expected, 3), 1));
    }

    [Theory]
    [InlineData("nonce")]
    [InlineData("pid")]
    [InlineData("created")]
    [InlineData("target")]
    [InlineData("installer")]
    [InlineData("version")]
    [InlineData("channel")]
    [InlineData("hash")]
    [InlineData("size")]
    [InlineData("minimum")]
    [InlineData("published")]
    [InlineData("url")]
    [InlineData("notes")]
    [InlineData("key")]
    public void A_different_attempt_or_prepared_identity_cannot_replay_an_earlier_receipt(string variation)
    {
        var api = Api.Require();
        var original = api.Create(Invocation(), Nonce);
        var update = Update();
        var parent = Parent;
        var target = Target;
        var package = Package;
        var nonce = Nonce;
        switch (variation)
        {
            case "nonce": nonce = new string('b', 64); break;
            case "pid": parent = parent with { ProcessId = parent.ProcessId + 1 }; break;
            case "created": parent = parent with { CreationTimeUtcTicks = parent.CreationTimeUtcTicks + 1 }; break;
            case "target": target = @"C:\Different Installed\keyboard.wtf.exe"; parent = parent with { ExecutablePath = target }; break;
            case "installer": package = @"D:\Different Prepared\keyboard.wtf-setup.exe"; break;
            case "version": update = update with { Version = new Version(2, 3, 5) }; break;
            case "channel": update = update with { Channel = "beta" }; break;
            case "hash": update = update with { Sha256 = new string('b', 64) }; break;
            case "size": update = update with { SizeBytes = update.SizeBytes + 1 }; break;
            case "minimum": update = update with { MinimumVersion = new Version(1, 0, 1) }; break;
            case "published": update = update with { PublishedAt = update.PublishedAt.AddTicks(1) }; break;
            case "url": update = update with { PackageUri = new Uri("https://github.com/UnknownGod2011/keyboard.wtf/releases/download/v2.3.4/other.exe") }; break;
            case "notes": update = update with { ReleaseNotes = "Different release." }; break;
            case "key": update = update with { KeyId = "different-test-key" }; break;
        }
        var changed = api.Create(UpdateInstallerInvocation.Create(new(update, package), parent, target), nonce);
        Assert.Throws<InvalidDataException>(() => api.Validate(changed, api.Frame(original, 1), 1));
        Assert.Throws<InvalidDataException>(() => api.Validate(original, api.Frame(changed, 3), 3));
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData("a", 1)]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg", 1)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 0)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", -1)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 262144001)]
    public void Invalid_signed_artifact_envelope_is_not_accepted_by_protocol_binding(string hash, long size)
    {
        var api = Api.Require();
        var update = Update() with { Sha256 = hash, SizeBytes = size };
        Assert.Throws<InvalidDataException>(() => api.Create(Invocation(update), Nonce));
    }

    [Theory]
    [InlineData("minimum")]
    [InlineData("url")]
    [InlineData("notes-null")]
    [InlineData("notes-large")]
    [InlineData("key-null")]
    [InlineData("key-empty")]
    [InlineData("key-large")]
    public void Missing_or_unbounded_metadata_is_not_a_protocol_fingerprint(string variation)
    {
        var api = Api.Require();
        var update = variation switch
        {
            "minimum" => Update() with { MinimumVersion = null },
            "url" => Update() with { PackageUri = null },
            "notes-null" => Update() with { ReleaseNotes = null },
            "notes-large" => Update() with { ReleaseNotes = new string('x', 20_001) },
            "key-null" => Update() with { KeyId = null },
            "key-empty" => Update() with { KeyId = " " },
            _ => Update() with { KeyId = new string('x', 129) },
        };
        Assert.Throws<InvalidDataException>(() => api.Create(Invocation(update), Nonce));
    }

    [Fact]
    public void Fifty_independent_local_envelopes_do_not_mutate_the_prepared_record_or_each_other()
    {
        var api = Api.Require();
        var invocation = Invocation();
        for (var index = 0; index < 50; index++)
        {
            var nonce = index.ToString("x64");
            var expected = api.Create(invocation, nonce);
            foreach (byte phase in new byte[] { 1, 2, 3 }) api.Validate(expected, api.Frame(expected, phase), phase);
            var different = api.Create(invocation, (index + 1).ToString("x64"));
            Assert.Throws<InvalidDataException>(() => api.Validate(different, api.Frame(expected, 3), 3));
        }
        Assert.Equal(Update(), invocation.Prepared.Update);
        Assert.Equal(Parent, invocation.Parent);
    }

    [Fact]
    public void Protocol_preparation_cannot_activate_installation()
    {
        var ready = typeof(WebSettingsService).GetProperty("SafeUpdateInstallationReady", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(ready);
        Assert.False((bool)ready.GetValue(null));
    }

    private static UpdateInstallerInvocation Invocation(VerifiedUpdate update = null) =>
        UpdateInstallerInvocation.Create(new(update ?? Update(), Package), Parent, Target);

    private static VerifiedUpdate Update() => new(new Version(2, 3, 4), new Version(1, 0, 0), "stable",
        new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero),
        new Uri("https://github.com/UnknownGod2011/keyboard.wtf/releases/download/v2.3.4/keyboard.wtf-setup.exe"),
        new string('a', 64), 100, "Test release.", "test-key");

    private sealed class Api
    {
        private readonly Type _type;
        private Api()
        {
            _type = typeof(UpdateWorkAdmissionGate).Assembly.GetType("KeyboardWtf.Services.Updates.UpdateWorkerHandshakeBinding");
            Assert.True(_type is not null, "The bounded worker handshake binding is missing.");
        }
        internal static Api Require() => new();
        internal object Create(UpdateInstallerInvocation invocation, string nonce) => Call("Create", null, invocation, nonce);
        internal byte[] Frame(object expected, byte phase) => (byte[])Call("CreateFrame", expected, phase);
        internal void Validate(object expected, byte[] frame, byte phase) => Call("ValidateFrame", expected, frame, phase);
        private object Call(string method, object target, params object[] args)
        {
            var member = _type.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
            Assert.NotNull(member);
            try { return member.Invoke(target, args); }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }
    }
}
