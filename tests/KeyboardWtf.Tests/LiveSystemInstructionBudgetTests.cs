using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using KeyboardWtf.Models;
using KeyboardWtf.Services;
using KeyboardWtf.Services.Ai.Backends;
using Xunit;
using Xunit.Abstractions;

namespace KeyboardWtf.Tests;

// Exercise the assembled production prompt and the real setup adapter together.
// No microphone, socket, real profile, provider, or Windows clipboard is used.
[Collection("Live event integration")]
public sealed class LiveSystemInstructionBudgetTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "keyboard-wtf-live-budget-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Both_bound_backends_receive_the_exact_same_behavior_instruction_and_tools()
    {
        var settings = new SettingsService(Path.Combine(_directory, "settings.json"));
        var vault = new PersonalApiVault(Path.Combine(_directory, "keys.bin"));
        var view = vault.Add(0, "synthetic", "synthetic-parity-credential-0000", PersonalKeyValidation.AuthenticationValidated);
        vault.SetPolicy(view.Revision, AiBackendPolicy.PersonalOnly);
        var router = new AiBackendRouter(vault, vault);
        using var personal = router.BeginOperation();
        view = vault.GetView();
        vault.SetPolicy(view.Revision, AiBackendPolicy.Managed);
        using var managed = router.BeginOperation();
        managed.Binding.BindManaged(); // No transport or reservation is involved in prompt construction.
        using var live = new GeminiLiveConversationService(null, settings, new IntentMemoryService(Path.Combine(_directory, "memory.json")));
        var ownerField = typeof(GeminiLiveConversationService).GetField("_backendOperation", BindingFlags.Instance | BindingFlags.NonPublic)!;
        typeof(GeminiLiveConversationService).GetField("_sessionCts", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(live, new CancellationTokenSource());
        var build = typeof(GeminiLiveConversationService).GetMethod("BuildSystemInstruction", BindingFlags.Instance | BindingFlags.NonPublic)!;
        ownerField.SetValue(live, personal);
        var reference = (string)build.Invoke(live, null);
        ownerField.SetValue(live, managed);
        Assert.Equal(reference, (string)build.Invoke(live, null));
        Assert.Contains("announcement_owner=selected_text_translation_lifecycle", reference);
        Assert.Contains("prefer connector_resolve over browser automation or draft tools", reference);
        Assert.Contains("Connector writes require existing permission approval", reference);
        Assert.Contains("Legacy Gmail and WhatsApp tools remain draft-only", reference);
        Assert.Contains("For source=connector, retrieve the exact step via connector_control action=result", reference);
        Assert.Contains("For other sources, never call tools", reference);
        Assert.DoesNotContain("Use browser automation only when the requested outcome actually depends on live inbox/page state", reference);
        Assert.True(reference.Length <= 20_000);
        // Tool declarations already have a single static implementation, not
        // a mode-specific schema. Keep that contract explicit.
        Assert.True(typeof(GeminiLiveConversationService).GetMethod("BuildTools", BindingFlags.Static | BindingFlags.NonPublic)!.IsStatic);
    }

    [Theory]
    [InlineData(0, JarvisPermissionMode.AutoExecute)]
    [InlineData(0, JarvisPermissionMode.AlwaysAsk)]
    [InlineData(1, JarvisPermissionMode.AutoExecute)]
    [InlineData(1, JarvisPermissionMode.AlwaysAsk)]
    [InlineData(2, JarvisPermissionMode.AutoExecute)]
    [InlineData(2, JarvisPermissionMode.AlwaysAsk)]
    public void Complete_personal_prompt_preserves_all_behavior_and_saved_digests(int profile, JarvisPermissionMode permission)
    {
        var settings = new SettingsService(Path.Combine(_directory, "settings.json"));
        settings.Current.JarvisPermissionMode = permission;
        var memory = new IntentMemoryService(Path.Combine(_directory, "memory.json"));
        var mappings = new LearnedMappingService(Path.Combine(_directory, "mappings.json"));
        if (profile != 0)
        {
            // Profile 1 matches the size of the accepted six-workflow profile;
            // profile 2 exposes the pre-existing fully populated digest limit.
            for (var index = 0; index < (profile == 2 ? 24 : 6); index++)
                settings.Current.JarvisWorkflows.Add(new()
                {
                    Name = $"workflow {index:D2} " + new string('w', profile == 2 ? 48 : 13),
                    Actions = [new() { Kind = "url", Target = "https://example.com/" }],
                });
            for (var index = 0; index < (profile == 2 ? 12 : 2); index++)
                mappings.Remember(LearnedMappingKind.Link, $"open link {index:D2} " + new string('l', 58), "https://example.com/");
            if (profile == 2)
            {
                for (var index = 0; index < 10; index++)
                    memory.Remember($"memory {index:D2}", new string('m', IntentMemoryService.MaxValueLength));
                Assert.True(memory.BuildPromptDigest().Length >= 900);
                Assert.True(mappings.BuildLinkAliasPromptDigest().Length >= 900);
                Assert.True(JarvisAutomationService.BuildSavedWorkflowPromptDigest(settings.Current.JarvisWorkflows).Length >= 1_200);
            }
        }

        var vault = new PersonalApiVault(Path.Combine(_directory, "keys.bin"));
        var view = vault.Add(0, "synthetic", "synthetic-live-budget-credential-0000", PersonalKeyValidation.AuthenticationValidated);
        vault.SetPolicy(view.Revision, AiBackendPolicy.PersonalOnly);
        using var owner = new AiBackendRouter(vault, vault).BeginOperation();
        using var live = new GeminiLiveConversationService(null, settings, memory, null, mappings);
        typeof(GeminiLiveConversationService).GetField("_backendOperation", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(live, owner);
        typeof(GeminiLiveConversationService).GetField("_sessionCts", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(live, new CancellationTokenSource());
        Assert.True(live.HasPersonalSession);

        var originalName = KeyboardWtfState.AssistantName;
        var originalTone = KeyboardWtfState.AssistantTone;
        try
        {
            KeyboardWtfState.AssistantName = new string('a', 32);
            foreach (var tone in Enum.GetValues<AssistantTone>())
            {
                KeyboardWtfState.AssistantTone = tone;
                var instruction = (string)typeof(GeminiLiveConversationService)
                    .GetMethod("BuildSystemInstruction", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(live, null);
                output.WriteLine($"profile={profile} permission={permission} tone={tone} instructionChars={instruction.Length}");
                var tools = typeof(GeminiLiveConversationService)
                    .GetMethod("BuildTools", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null);
                output.WriteLine($"workflowDigestChars={JarvisAutomationService.BuildSavedWorkflowPromptDigest(settings.Current.JarvisWorkflows).Length} linkDigestChars={mappings.BuildLinkAliasPromptDigest().Length} memoryDigestChars={memory.BuildPromptDigest().Length} toolsJsonChars={JsonSerializer.Serialize(tools).Length}");
                var setup = new
                {
                    setup = new
                    {
                        systemInstruction = new { parts = new[] { new { text = instruction } } },
                        generationConfig = new { speechConfig = new { voiceConfig = new { prebuiltVoiceConfig = new { voiceName = "Kore" } } } },
                    },
                };
                if (profile == 2)
                {
                    // A populated but valid profile must not fail an arbitrary
                    // character threshold or lose any of its bounded digests.
                    Assert.True(instruction.Length > 20_000);
                }
                var prepared = PersonalGeminiLiveTransport.PrepareSetup(setup);
                Assert.Equal(PersonalGeminiLiveTransport.SafetyInstruction + "\n\nDesktop context:\n" + instruction,
                    prepared["setup"]!["systemInstruction"]!["parts"]![0]!["text"]!.GetValue<string>());
                Assert.Contains("announcement_owner=selected_text_translation_lifecycle", instruction);
            }
        }
        finally
        {
            KeyboardWtfState.AssistantName = originalName;
            KeyboardWtfState.AssistantTone = originalTone;
        }
    }

    [Theory]
    [InlineData(20_000)]
    [InlineData(25_000)]
    [InlineData(40_000)]
    [InlineData(120_000)]
    public void Large_instruction_is_accepted_without_truncation(int characters)
    {
        var instruction = new string('x', characters);
        var prepared = PersonalGeminiLiveTransport.PrepareSetup(new
        {
            setup = new
            {
                systemInstruction = new { parts = new[] { new { text = instruction } } },
                generationConfig = new { speechConfig = new { voiceConfig = new { prebuiltVoiceConfig = new { voiceName = "Kore" } } } },
            },
        });
        Assert.EndsWith(instruction, prepared["setup"]!["systemInstruction"]!["parts"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void Oversized_setup_still_fails_without_truncation_or_relaxed_payload_guard()
    {
        var error = Assert.Throws<ArgumentException>(() => PersonalGeminiLiveTransport.PrepareSetup(new
        {
            setup = new { systemInstruction = new { parts = new[] { new { text = new string('x', 2 * 1024 * 1024) } } } },
        }));
        Assert.Equal("Personal Live setup is too large.", error.Message);
    }

    [Fact]
    public void Safety_prefix_cannot_push_outgoing_setup_past_payload_bound()
    {
        var root = JsonNode.Parse("""{"setup":{"systemInstruction":{"parts":[{"text":""}]},"generationConfig":{"speechConfig":{"voiceConfig":{"prebuiltVoiceConfig":{"voiceName":"Kore"}}}}}}""")!;
        var emptyBytes = Encoding.UTF8.GetByteCount(root.ToJsonString());
        root["setup"]!["systemInstruction"]!["parts"]![0]!["text"] = new string('x', 2 * 1024 * 1024 - emptyBytes);
        Assert.Equal(2 * 1024 * 1024, Encoding.UTF8.GetByteCount(root.ToJsonString()));
        Assert.Equal("Personal Live setup is too large.",
            Assert.Throws<ArgumentException>(() => PersonalGeminiLiveTransport.PrepareSetup(root)).Message);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
