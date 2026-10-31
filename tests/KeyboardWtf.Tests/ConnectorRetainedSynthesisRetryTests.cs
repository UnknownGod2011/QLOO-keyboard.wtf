using System.Text.Json;
using KeyboardWtf.Services.Ai.Backends;
using KeyboardWtf.Services.Connectors;
using Xunit;

namespace KeyboardWtf.Tests;

public sealed class ConnectorRetainedSynthesisRetryTests
{
    private const string Thread = "aabbccdd22";
    private const string Message = "aabbccdd11";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Truncated_synthesis_preserves_native_thread_and_exact_reference_without_retry(bool preferNative)
    {
        var models = 0; var reads = 0;
        var result = await ConnectorReadReasoner.RunAsync(Input(preferNative),
            (_, _, _, _, _) => { ++models; throw new PersonalAiException(PersonalAiFailure.Truncated) { HttpStatusCode = 200 }; },
            (capability, arguments, _) =>
            {
                ++reads; Assert.Equal("mail_thread", capability); Assert.Equal(Thread, arguments["id"].GetString());
                return Task.FromResult(Body());
            }, null, default);

        Assert.Equal(1, models); Assert.Equal(1, result.ModelCalls); Assert.Equal(1, reads);
        Assert.Equal("ai_response_incomplete", result.ReasoningError);
        Assert.True(result.Partial); Assert.Equal("partial", result.GoalStatus);
        Assert.Equal("", result.TargetReference); Assert.Single(result.EvidenceReferences);
        var evidence = Assert.Single(result.EvidenceReceipts);
        Assert.Equal(Thread, evidence.GetProperty("result").GetProperty("data").GetProperty("id").GetString());
        Assert.Contains("Verified project request", evidence.GetRawText());
        Assert.DoesNotContain("disconnected", result.Answer);
        Assert.DoesNotContain("503", result.Answer);

        var context = ConnectorSessionResult.Create("retained-truncated", "gmail", "same-account", "reader@example.test",
            "Read the retained conversation details", default, result.EvidenceReceipts,
            evidenceCapabilities: result.EvidenceCapabilities);
        Assert.Equal("same-account", context.AccountId);
        Assert.Equal("https://mail.google.com/mail/?authuser=reader%40example.test#all/" + Thread, context.ExactMailUrl());
        Assert.DoesNotContain("Verified project request", JsonSerializer.Serialize(context));
    }

    [Fact]
    public async Task Headers_only_update_during_truncated_synthesis_excludes_already_read_bodies_from_fallback()
    {
        var models = 0; var reads = 0; var revision = 0;
        const string restriction = "Only headers; do not read message bodies.";
        var input = Input(preferNative: true) with
        {
            Guidance = () => revision == 0 ? new(0, "") : new(1, restriction) { Instructions = [restriction] }
        };
        var result = await ConnectorReadReasoner.RunAsync(input,
            (_, _, _, _, _) =>
            {
                ++models; revision = 1;
                throw new PersonalAiException(PersonalAiFailure.Truncated) { HttpStatusCode = 200 };
            }, (_, _, _) => { ++reads; return Task.FromResult(Body()); }, null, default);

        Assert.Equal(1, models); Assert.Equal(1, reads); Assert.Equal(1, result.GoalRevision);
        Assert.Equal("ai_response_incomplete", result.ReasoningError);
        Assert.All(result.EvidenceGoalRevisions, value => Assert.Equal(0, value));
        Assert.DoesNotContain("Verified project request", JsonSerializer.Serialize(result.EvidenceReceipts));
        Assert.DoesNotContain("Verified project request", result.Answer);
    }

    [Fact]
    public async Task Cancellation_during_truncated_synthesis_cannot_publish_retained_native_fallback()
    {
        using var cancellation = new CancellationTokenSource();
        var models = 0; var reads = 0;
        var failure = await Assert.ThrowsAsync<PersonalAiException>(() => ConnectorReadReasoner.RunAsync(Input(preferNative: true),
            (_, _, _, _, _) =>
            {
                ++models; cancellation.Cancel();
                throw new PersonalAiException(PersonalAiFailure.Truncated) { HttpStatusCode = 200 };
            }, (_, _, _) => { ++reads; return Task.FromResult(Body()); }, null, cancellation.Token));

        Assert.Equal(PersonalAiFailure.Truncated, failure.Failure);
        Assert.True(cancellation.IsCancellationRequested); Assert.Equal(1, models); Assert.Equal(1, reads);
    }

    [Fact]
    public async Task Direct_retained_details_preserve_verified_body_after_first_503_without_a_second_synthesis_call()
    {
        var models = 0; var reads = 0;
        var result = await ConnectorReadReasoner.RunAsync(Input(preferNative: true),
            (_, _, _, _, _) => { ++models; throw Unavailable(); },
            (_, _, _) => { ++reads; return Task.FromResult(Body()); }, null, default);
        Assert.Equal(1, models); Assert.Equal(1, result.ModelCalls); Assert.Equal(1, reads);
        Assert.Equal("ai_provider_unavailable", result.ReasoningError);
        Assert.True(result.Partial); Assert.Equal("partial", result.GoalStatus);
        Assert.Contains("Verified project request", Assert.Single(result.EvidenceReceipts).GetRawText());
        Assert.Single(result.EvidenceReferences);
        Assert.DoesNotContain("disconnected", result.Answer);
    }

    [Fact]
    public async Task Successful_first_synthesis_keeps_its_answer_with_the_direct_retained_policy()
    {
        var models = 0; var reads = 0;
        var result = await ConnectorReadReasoner.RunAsync(Input(preferNative: true),
            (_, prompt, _, _, _) =>
            {
                ++models;
                using var envelope = JsonDocument.Parse(prompt);
                using var native = JsonDocument.Parse(Assert.Single(envelope.RootElement.GetProperty("evidence").EnumerateArray())
                    .GetProperty("data").GetString());
                Assert.Contains("Verified project request", native.RootElement.GetProperty("result").GetProperty("data")
                    .GetProperty("messages")[0].GetProperty("text").GetString());
                return Task.FromResult(JsonSerializer.Serialize(new { action = "answer", capability = "",
                    arguments_json = "{}", answer = "The participant requested the project outline.",
                    references = new[] { "r1" }, partial = false, goal_status = "matched", target_reference = "r1" }));
            }, (_, _, _) => { ++reads; return Task.FromResult(Body()); }, null, default);
        Assert.Equal(1, models); Assert.Equal(1, reads); Assert.Null(result.ReasoningError);
        Assert.Equal("The participant requested the project outline.", result.Answer);
    }

    [Theory]
    [InlineData("default-policy")]
    [InlineData("missing-body")]
    [InlineData("wrong-thread")]
    [InlineData("wrong-body-thread")]
    public async Task Unavailable_or_unscoped_retained_content_keeps_the_existing_single_retry(string scenario)
    {
        var models = 0; var reads = 0;
        var input = Input(preferNative: scenario != "default-policy");
        var result = await ConnectorReadReasoner.RunAsync(input,
            (_, _, _, _, _) => { ++models; throw Unavailable(); },
            (_, _, _) => { ++reads; return Task.FromResult(Body(scenario)); }, null, default);
        Assert.Equal(2, models); Assert.Equal(2, result.ModelCalls); Assert.Equal(1, reads);
        Assert.Equal("ai_provider_unavailable", result.ReasoningError);
    }

    [Fact]
    public async Task Native_handoff_policy_does_not_remove_retry_from_a_new_search()
    {
        var models = 0; var reads = 0;
        var input = new ConnectorReadReasonerInput("Find emails about the project outline.",
            [new("mail_search", "Search", new Dictionary<string, string> { ["query"] = "query" })])
        {
            Provider = "gmail", PreferNativeRetainedContentOn503 = true,
            InitialCapability = "mail_search",
            InitialArguments = new Dictionary<string, JsonElement> { ["query"] = JsonSerializer.SerializeToElement("project outline") }
        };
        var result = await ConnectorReadReasoner.RunAsync(input,
            (_, _, _, _, _) => { ++models; throw Unavailable(); },
            (_, _, _) =>
            {
                ++reads; return Task.FromResult(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
                    result = new { truncated = false, data = new { messages = new[] { new { id = Message, threadId = Thread } },
                        sampledMessages = new[] { new { id = Message, threadId = Thread, subject = "Project outline" } } } } }));
            }, null, default);
        Assert.Equal(2, models); Assert.Equal(2, result.ModelCalls); Assert.Equal(1, reads);
        Assert.Equal("ai_provider_unavailable", result.ReasoningError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_update_during_failed_synthesis_cannot_treat_old_body_evidence_as_current(bool headersOnly)
    {
        var models = 0; var reads = 0; var revision = 0;
        var update = headersOnly ? "Only headers; do not read message bodies." : "Only the professor's messages.";
        var input = Input(preferNative: true) with
        {
            Guidance = () => revision == 0 ? new(0, "") : new(1, update) { Instructions = [update] }
        };
        var result = await ConnectorReadReasoner.RunAsync(input,
            (_, _, _, _, _) => { ++models; revision = 1; throw Unavailable(); },
            (_, _, _) => { ++reads; return Task.FromResult(Body()); }, null, default);
        Assert.Equal(2, models); Assert.Equal(1, reads); Assert.Equal(1, result.GoalRevision);
        Assert.Equal("ai_provider_unavailable", result.ReasoningError);
        Assert.All(result.EvidenceGoalRevisions, value => Assert.Equal(0, value));
        if (headersOnly) Assert.DoesNotContain("Verified project request", JsonSerializer.Serialize(result.EvidenceReceipts));
    }

    private static ConnectorReadReasonerInput Input(bool preferNative) =>
        new("Read the full retained conversation details: " + Thread,
            [new("mail_thread", "Read thread", new Dictionary<string, string> { ["id"] = "thread" })])
        {
            Provider = "gmail", RetainedThreadId = Thread, PreferNativeRetainedContentOn503 = preferNative,
            InitialCapability = "mail_thread",
            InitialArguments = new Dictionary<string, JsonElement> { ["id"] = JsonSerializer.SerializeToElement(Thread) }
        };

    private static PersonalAiException Unavailable() => new(PersonalAiFailure.Provider) { HttpStatusCode = 503 };

    private static JsonElement Body(string shape = "complete") => JsonSerializer.SerializeToElement(new
    {
        state = "completed", verified = true, result = new { truncated = false, data = new
        {
            id = shape == "wrong-thread" ? "eeeeffff22" : Thread,
            messages = new[] { new { id = Message,
                threadId = shape is "wrong-thread" or "wrong-body-thread" ? "eeeeffff22" : Thread,
                text = shape == "missing-body" ? "" : "Verified project request: please send the outline tomorrow.",
                contentTruncated = false } }
        } }
    });
}
