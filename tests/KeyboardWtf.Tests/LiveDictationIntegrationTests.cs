namespace KeyboardWtf.Tests;

using KeyboardWtf.Services;
using Xunit;

public sealed class LiveDictationIntegrationTests
{
    [Fact]
    public void InstalledVoskModelAcceptsStreamingPcm()
    {
        if (!Directory.Exists(ModelManager.VoskModelPath))
            return;

        using var vosk = new VoskRecognitionService();
        vosk.LoadModel(ModelManager.VoskModelPath);
        using var session = vosk.CreateStreamingSession(16000);

        Assert.NotNull(session);
        var preview = session.AcceptPcm(new byte[3200], 3200);
        Assert.NotNull(preview);
    }
}
