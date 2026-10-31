using System.Reflection;
using KeyboardWtf.Services;
using Xunit;

namespace KeyboardWtf.Tests;

public sealed class PublicDiagnosticExclusionTests
{
    [Theory]
    [InlineData("KeyboardWtf.Services.PrivatePersonalRecordingInput")]
    [InlineData("KeyboardWtf.Services.PersonalAudioIngressDiagnostics")]
    public void Diagnostic_type_presence_matches_the_requested_build_flavor(string name)
    {
        var type = typeof(KeyboardWtfApp).Assembly.GetType(name, throwOnError: false);
#if KEYBOARD_WTF_TEST_PERSONAL_API_CANDIDATE
        Assert.NotNull(type);
#elif KEYBOARD_WTF_TEST_CONNECTOR_STAGING_CANDIDATE
        if (name == "KeyboardWtf.Services.PrivatePersonalRecordingInput")
            Assert.NotNull(type);
        else
            Assert.Null(type); // Acoustic diagnostics are Personal-candidate-only.
#else
        Assert.Null(type);
#endif
    }

    [Fact]
    public void Recording_audit_adapter_presence_matches_the_requested_build_flavor()
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
#if KEYBOARD_WTF_TEST_PERSONAL_API_CANDIDATE || KEYBOARD_WTF_TEST_CONNECTOR_STAGING_CANDIDATE
        Assert.NotNull(typeof(KeyboardWtfApp).GetProperty("PrivateAuditConversation", flags));
        foreach (var name in new[] { "StartPrivateRecordingAuditAsync", "ReplayPrivateRecordingAsync", "ObservePrivateRecording" })
            Assert.NotNull(typeof(GeminiLiveConversationService).GetMethod(name, flags));
        Assert.NotNull(typeof(WebSettingsService).GetMethod("HandlePrivateRecordingAudit", flags));
#else
        Assert.Null(typeof(KeyboardWtfApp).GetProperty("PrivateAuditConversation", flags));
        foreach (var name in new[] { "StartPrivateRecordingAuditAsync", "ReplayPrivateRecordingAsync", "ObservePrivateRecording" })
            Assert.Null(typeof(GeminiLiveConversationService).GetMethod(name, flags));
        Assert.Null(typeof(WebSettingsService).GetMethod("HandlePrivateRecordingAudit", flags));
#endif
    }
}
