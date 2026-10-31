using System.Reflection;
using System.Text.Json;
using KeyboardWtf.Services;
using Xunit;

namespace KeyboardWtf.Tests;

// Offline provider-frame replay: no socket, credentials, microphone or real action.
[Collection("Live event integration")]
public sealed class PersonalSpeechPhaseDiagnosticsTests
{
    [Fact]
    public async Task One_provider_audio_part_has_one_playback_publication_and_one_tool_receipt()
    {
        const BindingFlags privateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        using var session = new CancellationTokenSource();
        using var service = new GeminiLiveConversationService(null, null, null);
        var diagnosticsType = typeof(GeminiLiveConversationService)
            .GetNestedType("ProviderSocketDiagnostics", BindingFlags.NonPublic)!;
        var diagnostics = Activator.CreateInstance(diagnosticsType,
            "speech-phase-replay", 1, DateTimeOffset.UtcNow, 0, false)!;
        diagnosticsType.GetProperty("UsesPersonalToolProtocol")!.SetValue(diagnostics, true);

        var executions = 0;
        var receiptBatches = 0;
        using var dispatcher = new LiveToolCallDispatcher(session.Token,
            (_, _, _) =>
            {
                Interlocked.Increment(ref executions);
                return Task.FromResult(new Dictionary<string, object> { ["ok"] = true });
            },
            (build, onSent, token) =>
            {
                token.ThrowIfCancellationRequested();
                if (build() is not null)
                {
                    Interlocked.Increment(ref receiptBatches);
                    // This fake sender models the physical sender's after-send callback.
                    var field = diagnosticsType.GetField("PersonalReceiptBatches")!;
                    field.SetValue(diagnostics, (int)field.GetValue(diagnostics)! + 1);
                }
                onSent();
                return Task.CompletedTask;
            });
        diagnosticsType.GetProperty("ToolCalls")!.SetValue(diagnostics, dispatcher);

        var buffer = GeminiLiveConversationService.CreatePlaybackBuffer();
        typeof(GeminiLiveConversationService).GetField("_playbackBuffer", privateInstance)!
            .SetValue(service, buffer);
        var handle = typeof(GeminiLiveConversationService)
            .GetMethod("HandleServerMessage", privateInstance)!;
        void Push(string frame) => handle.Invoke(service,
            [frame, new TaskCompletionSource<bool>(), diagnostics, session.Token]);
        static int[] Counts(Type type, object instance, string name) =>
            ((int[])type.GetField(name)!.GetValue(instance)!).ToArray();

        var audio = Convert.ToBase64String(new byte[4800]);
        string Speech(string text) => JsonSerializer.Serialize(new
        {
            serverContent = new
            {
                outputTranscription = new { text },
                modelTurn = new { parts = new[] { new { inlineData = new { data = audio } } } },
            },
        });
        Push(Speech("Before the action."));
        Assert.Equal(4800, buffer.BufferedBytes);
        Assert.Equal(new[] { 1, 0, 0 }, Counts(diagnosticsType, diagnostics, "PersonalProviderAudioParts"));
        Assert.Equal(new[] { 1, 0, 0 }, Counts(diagnosticsType, diagnostics, "PersonalPlaybackParts"));

        var tool = """{"toolCall":{"functionCalls":[{"id":"one","name":"test_action","args":{}}]}}""";
        Push(tool);
        await dispatcher.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Push(tool); // A repeated provider ID must not execute or publish again.
        await dispatcher.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, executions);
        Assert.Equal(1, receiptBatches);

        Push(Speech("After the receipt."));
        Assert.Equal(9600, buffer.BufferedBytes);
        Assert.Equal(new[] { 1, 0, 1 }, Counts(diagnosticsType, diagnostics, "PersonalProviderAudioParts"));
        Assert.Equal(new[] { 1, 0, 1 }, Counts(diagnosticsType, diagnostics, "PersonalPlaybackParts"));
        Assert.Equal(new[] { 1, 0, 1 }, Counts(diagnosticsType, diagnostics, "PersonalProviderOutputFrames"));
        Assert.Equal(new[] { 1, 0, 1 }, Counts(diagnosticsType, diagnostics, "PersonalUiOutputFrames"));

        Push("""{"serverContent":{"turnComplete":true}}""");
        Assert.Equal(new[] { 0, 0, 0 }, Counts(diagnosticsType, diagnostics, "PersonalProviderAudioParts"));
        Assert.Equal(1, receiptBatches);
    }
}
