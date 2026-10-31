using System.Text.Json;
using KeyboardWtf.Services.Connectors;
using KeyboardWtf.Services.Ai.Backends;
using Xunit;

namespace KeyboardWtf.Tests;

public sealed class ConnectorPersonSearchTests
{
    [Fact]
    public void Search_preparation_does_not_turn_a_compound_request_into_a_headers_only_answer()
    {
        const string request = "Could you find an email from Mira and open that conversation in a new tab?";
        Assert.Equal("from:\"Mira\"", ConnectorPersonSearch.PrepareQuery(request));
        Assert.Null(ConnectorPersonSearch.Query(request));
        Assert.False(ConnectorPersonSearch.CanAnswerNativeLookup(request, NativeParticipantPage()));
    }

    [Fact]
    public async Task Plain_participant_lookup_returns_native_headers_without_a_reasoning_call_or_additional_search()
    {
        var reads = 0;
        var input = new ConnectorReadReasonerInput("Find my emails with Daisy Edwards.",
            [new("mail_search", "Search", new Dictionary<string, string> { ["query"] = "query" })])
            { Provider = "gmail", InitialCapability = "mail_search", InitialArguments = new Dictionary<string, JsonElement>
                { ["query"] = JsonSerializer.SerializeToElement("Daisy Edwards") } };
        var result = await ConnectorReadReasoner.RunAsync(input,
            (_, _, _, _, _) => throw new InvalidOperationException("A usable native participant listing needs no structured reasoning."),
            (_, args, _) =>
            {
                reads++; Assert.Equal("{from:\"Daisy Edwards\" to:\"Daisy Edwards\" cc:\"Daisy Edwards\"}", args["query"].GetString());
                return Task.FromResult(NativeParticipantPage());
            }, null, default);
        Assert.Equal(1, reads); Assert.Equal(1, result.Executions); Assert.Equal(0, result.ModelCalls);
        Assert.Null(result.ReasoningError); Assert.True(result.Partial);
        Assert.Contains("Verified conversation", result.Answer);
        Assert.Contains("More matching pages remain", result.Answer);
        Assert.Equal("partial", result.GoalStatus); Assert.Single(result.EvidenceReceipts);
    }

    [Theory]
    [InlineData("Find my emails with Daisy Edwards.", true)]
    [InlineData("Show me emails from Dr Patel.", true)]
    [InlineData("Find my emails with Dr Patel. The subject may mention research.", false)]
    [InlineData("Summarize my emails with Dr Patel.", false)]
    [InlineData("Find my emails with Dr Patel and give me more details.", false)]
    [InlineData("Find my emails with Dr Patel today.", false)]
    public void Native_header_shortcut_never_replaces_hints_details_or_date_constraints(string request, bool expected)
        => Assert.Equal(expected, ConnectorPersonSearch.CanAnswerNativeLookup(request, NativeParticipantPage()));

    [Theory]
    [InlineData("empty")]
    [InlineData("unverified")]
    [InlineData("missing-from")]
    [InlineData("missing-subject")]
    [InlineData("unobserved-id")]
    public void Missing_or_unproved_native_headers_keep_existing_intelligent_refinement(string shape)
        => Assert.False(ConnectorPersonSearch.CanAnswerNativeLookup("Find my emails with Dr Patel.", NativeParticipantPage(shape)));

    private static JsonElement NativeParticipantPage(string shape = "complete") => JsonSerializer.SerializeToElement(new
    {
        state = "completed", verified = shape != "unverified", result = new { truncated = false, data = new
        {
            messages = new[] { new { id = "aaaabbbb", threadId = "ccccdddd" } },
            sampledMessages = shape == "empty" ? Array.Empty<object>() : new object[] { new {
                id = shape == "unobserved-id" ? "11112222" : "aaaabbbb", threadId = "ccccdddd",
                from = shape == "missing-from" ? "" : "Participant <person@example.test>",
                subject = shape == "missing-subject" ? "" : "Verified conversation" } },
            pageItemCount = 12, hasMore = true, metadataSample = new { requested = 1, returned = 1, complete = true }
        } }
    });

    [Fact]
    public async Task Retained_full_thread_fills_observed_missing_bodies_before_reasoning_without_another_search()
    {
        var reads = new List<string>(); var models = 0;
        var input = new ConnectorReadReasonerInput("Read and summarize this full thread in detail: aabbccdd22", [
            new("mail_thread", "Read thread", new Dictionary<string,string> { ["id"] = "thread" }),
            new("mail_read", "Read message", new Dictionary<string,string> { ["id"] = "message" })])
        { Provider = "gmail", RetainedThreadId = "aabbccdd22", InitialCapability = "mail_thread",
            InitialArguments = new Dictionary<string,JsonElement> { ["id"] = JsonSerializer.SerializeToElement("aabbccdd22") } };
        var result = await ConnectorReadReasoner.RunAsync(input, (_, prompt, _, limit, _) =>
        {
            Assert.Equal(4096, limit);
            ++models; Assert.Contains("Recovered full message", prompt); Assert.Equal(3, reads.Count);
            return Task.FromResult("{\"action\":\"answer\",\"capability\":\"\",\"arguments_json\":\"{}\",\"answer\":\"The read messages describe a completed project.\",\"references\":[\"r1\",\"r2\",\"r3\"],\"partial\":true,\"goal_status\":\"matched\",\"target_reference\":\"r1\"}");
        }, (capability, args, _) =>
        {
            reads.Add(capability + ":" + args["id"].GetString());
            if (capability == "mail_thread") return Task.FromResult(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
                result = new { truncated = true, data = new { id = "aabbccdd22", messageReferences = new[] {
                    new { id = "aabbccdd11", threadId = "aabbccdd22" }, new { id = "aabbccdd33", threadId = "aabbccdd22" } },
                    messages = new[] { new { id = "aabbccdd11", threadId = "aabbccdd22", text = "Clipped content", contentTruncated = true } } } } }));
            return Task.FromResult(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
                result = new { truncated = false, data = new { id = args["id"].GetString(), threadId = "aabbccdd22", text = "Recovered full message about the project." } } }));
        }, _ => {}, default);
        Assert.Equal(new[] { "mail_thread:aabbccdd22", "mail_read:aabbccdd11", "mail_read:aabbccdd33" }, reads);
        Assert.Equal(1, models); Assert.Equal(3, result.Executions); Assert.True(result.Partial);
    }
    [Theory]
    [InlineData(System.Net.HttpStatusCode.Conflict, "ai_managed_admission_rejected")]
    [InlineData(System.Net.HttpStatusCode.TooManyRequests, "ai_managed_rate_limit")]
    [InlineData(System.Net.HttpStatusCode.ServiceUnavailable, "ai_managed_unavailable")]
    public async Task Managed_reasoning_rejection_preserves_verified_Gmail_and_never_retries_or_claims_disconnection(System.Net.HttpStatusCode status, string expected)
    {
        var reads = 0; var calls = 0;
        var input = new ConnectorReadReasonerInput("Find my emails with Dr Gao.", [new("mail_search", "Search", new Dictionary<string,string> { ["query"] = "name" })])
            { Provider = "gmail", InitialCapability = "mail_search", InitialArguments = new Dictionary<string,JsonElement> { ["query"] = JsonSerializer.SerializeToElement("Gao") } };
        var result = await ConnectorReadReasoner.RunAsync(input, (_, _, _, _, _) =>
        { ++calls; throw new KeyboardWtf.Services.PaidStaging.PaidApiException(status, "PRIVATE PROVIDER ERROR"); }, (_, _, _) =>
        { ++reads; return Task.FromResult(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
            result = new { data = new { messages = new[] { new { id = "aabbccdd11", threadId = "aabbccdd22" } }, sampledMessages = new[] { new { id = "aabbccdd11", subject = "Research conversation" } } } } })); }, _ => {}, default);
        Assert.Equal(expected, result.ReasoningError); Assert.Equal(1, calls); Assert.Equal(1, reads);
        Assert.Contains("Research conversation", result.Answer); Assert.DoesNotContain("PRIVATE PROVIDER ERROR", result.Answer);
        Assert.DoesNotContain("disconnected", result.Answer); Assert.True(result.Partial); Assert.Single(result.EvidenceReferences);
    }
    [Theory]
    [InlineData("503", "ai_provider_unavailable", 2)]
    [InlineData("429", "ai_rate_limit", 1)]
    [InlineData("timeout", "ai_timeout", 1)]
    [InlineData("transport", "ai_response_unconfirmed", 1)]
    [InlineData("connection", "ai_connection_unavailable", 1)]
    [InlineData("truncated", "ai_response_incomplete", 1)]
    public async Task Gmail_reasoning_outage_returns_retrieved_headers_without_claiming_a_summary(string failure, string expected, int calls)
    {
        var input = new ConnectorReadReasonerInput("Find emails about winter internship", [new("mail_search", "Search", new Dictionary<string,string> { ["query"] = "query" })])
            { Provider = "gmail", InitialCapability = "mail_search", InitialArguments = new Dictionary<string,JsonElement> { ["query"] = JsonSerializer.SerializeToElement("winter internship") } };
        var reads = 0;
        var result = await ConnectorReadReasoner.RunAsync(input, (_, _, _, _, _) =>
        {
            if (failure == "timeout") throw new TaskCanceledException("reasoning timeout");
            if (failure == "transport") throw new PersonalAiException(PersonalAiFailure.AmbiguousTransport);
            if (failure == "connection") throw new PersonalAiException(PersonalAiFailure.PreDispatchConnection);
            if (failure == "truncated") throw new PersonalAiException(PersonalAiFailure.Truncated) { HttpStatusCode = 200 };
            throw new PersonalAiException(failure == "429" ? PersonalAiFailure.Quota : PersonalAiFailure.Provider) { HttpStatusCode = int.Parse(failure) };
        }, (_, _, _) => { reads++; return Task.FromResult(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
            result = new { data = new { messages = new[] { new { id = "aabbccdd11", threadId = "aabbccdd22" } },
                sampledMessages = new[] { new { id = "aabbccdd11", threadId = "aabbccdd22", subject = "Winter internship reply", from = "Professor Example", date = "Mon, 12 Oct 2026 09:00:00 +0530", snippet = "PRIVATE PREVIEW MUST NOT APPEAR" } } }, truncated = false } })); }, null, default);
        Assert.Equal(expected, result.ReasoningError); Assert.Equal(calls, result.ModelCalls); Assert.Equal(1, reads);
        Assert.Contains("Winter internship reply", result.Answer); Assert.Contains("Professor Example", result.Answer);
        Assert.Contains("not a complete summary", result.Answer); Assert.DoesNotContain("PRIVATE PREVIEW", result.Answer);
        Assert.True(result.Partial); Assert.Equal("partial", result.GoalStatus); Assert.Equal("", result.TargetReference);
    }
    [Theory]
    [InlineData("Find my emails with Dr Gao.", "{from:\"Gao\" to:\"Gao\" cc:\"Gao\"}")]
    [InlineData("Summarize my conversation with Professor Jane Smith", "{from:\"Jane Smith\" to:\"Jane Smith\" cc:\"Jane Smith\"}")]
    [InlineData("Show me emails from Sophia Gao", "from:\"Sophia Gao\"")]
    [InlineData("Find emails with dr Patel", "{from:\"Patel\" to:\"Patel\" cc:\"Patel\"}")]
    [InlineData("Find my emails with Alex Example and give me the details.", "{from:\"Alex Example\" to:\"Alex Example\" cc:\"Alex Example\"}")]
    [InlineData("Find my emails from Dr Patel and please read the full thread.", "from:\"Patel\"")]
    [InlineData("Find my emails with Jie Gao or Sophia Gao", "{from:\"Jie Gao\" to:\"Jie Gao\" cc:\"Jie Gao\" from:\"Sophia Gao\" to:\"Sophia Gao\" cc:\"Sophia Gao\"}")]
    [InlineData("Find my emails from Sophia Gao or Dr Gao. The subject may mention visiting research.", "{from:\"Sophia Gao\" from:\"Gao\"}")]
    [InlineData("Find my emails from Professor Jane Smith. The subject line could be something like visiting research.", "from:\"Jane Smith\"")]
    [InlineData("Find my emails with Dr Patel. The subject might contain a research invitation.", "{from:\"Patel\" to:\"Patel\" cc:\"Patel\"}")]
    public void Person_query_is_generic_and_direction_aware(string request, string expected)
        => Assert.Equal(expected, ConnectorPersonSearch.Query(request));

    [Theory]
    [InlineData("Could you please find the email by Mira?", "from:\"Mira\"", true)]
    [InlineData("Can you find my emails with Professor Mira Shah?", "{from:\"Mira Shah\" to:\"Mira Shah\" cc:\"Mira Shah\"}", true)]
    [InlineData("Please could you show me emails from Renato?", "from:\"Renato\"", true)]
    [InlineData("Would you please read the thread from Dr. Mira Shah?", "from:\"Mira Shah\"", false)]
    [InlineData("Could you find the email by Mira and give me the details?", "from:\"Mira\"", false)]
    [InlineData("Can you please find my emails from Mira or Renato?", "{from:\"Mira\" from:\"Renato\"}", true)]
    [InlineData("Could you summarize my conversations with Mira?", "{from:\"Mira\" to:\"Mira\" cc:\"Mira\"}", false)]
    public void Natural_participant_lookup_keeps_direction_and_native_header_eligibility(string request, string expected, bool headersOnly)
    {
        Assert.Equal(expected, ConnectorPersonSearch.Query(request));
        Assert.Equal(headersOnly, ConnectorPersonSearch.CanAnswerNativeLookup(request, NativeParticipantPage()));
    }

    [Theory]
    [InlineData("Could you please find an email about humanoid motion generation?")]
    [InlineData("Could you find the email by Mira about the internship?")]
    [InlineData("Could you find the email from Mira today?")]
    [InlineData("Can you find my emails with Mira from yesterday?")]
    [InlineData("Could you please find the email by Mira in my work account?")]
    [InlineData("Could you find the email from Mira and send a reply?")]
    [InlineData("Could you find the email by Mira and open it in a new tab?")]
    [InlineData("Could you find the email from Mira. Search using query: from:Mira.")]
    [InlineData("Could you find the email by \"Mira\"?")]
    [InlineData("Don't find the email by Mira.")]
    [InlineData("If I ask later, could you find the email by Mira?")]
    public void Natural_person_preparation_does_not_lose_topic_scope_or_another_action(string request)
    {
        Assert.Null(ConnectorPersonSearch.Query(request));
        Assert.False(ConnectorPersonSearch.CanAnswerNativeLookup(request, NativeParticipantPage()));
    }

    [Fact]
    public async Task Courteous_sender_lookup_uses_native_headers_without_optional_reasoning()
    {
        var reads = 0;
        var input = new ConnectorReadReasonerInput("Could you please find the email by Mira?",
            [new("mail_search", "Search", new Dictionary<string, string> { ["query"] = "query" })])
            { Provider = "gmail", InitialCapability = "mail_search", InitialArguments = new Dictionary<string, JsonElement>
                { ["query"] = JsonSerializer.SerializeToElement("Mira details") } };
        var result = await ConnectorReadReasoner.RunAsync(input,
            (_, _, _, _, _) => throw new InvalidOperationException("A verified sender listing must not depend on optional reasoning."),
            (_, arguments, _) =>
            {
                reads++;
                Assert.Equal("from:\"Mira\"", arguments["query"].GetString());
                return Task.FromResult(NativeParticipantPage());
            }, null, default);
        Assert.Equal(1, reads); Assert.Equal(1, result.Executions); Assert.Equal(0, result.ModelCalls);
        Assert.Null(result.ReasoningError);
        Assert.Contains("Verified conversation", result.Answer);
    }

    [Theory]
    [InlineData("I just received an email from Lea. Could you look into it and give me the details?", "from:\"Lea\"")]
    [InlineData("I received the message from Alex Example, please give me more details about that message.", "from:\"Alex Example\"")]
    [InlineData("I got an email from Dr. Patel and read that email in detail.", "from:\"Patel\"")]
    [InlineData("I have just received an email from María López. Could you summarize it?", "from:\"María López\"")]
    [InlineData("I had received an email from Professor Jane Smith. Please look into that email and give me all the details.", "from:\"Jane Smith\"")]
    [InlineData("I got a message from O’Neil. Give me the details about it.", "from:\"O’Neil\"")]
    public void Received_message_with_same_object_details_prepares_only_a_native_participant_query(string request, string expected)
    {
        Assert.Equal(expected, ConnectorPersonSearch.Query(request));
        Assert.False(ConnectorPersonSearch.CanAnswerNativeLookup(request, NativeParticipantPage()));
        Assert.Equal("message", JarvisConnectorController.MailReadMode(request));
    }

    [Theory]
    [InlineData("I just received an email from Lea. Could you look into it and reply saying thanks?")]
    [InlineData("I received an email from Lea. Read it and delete it.")]
    [InlineData("I got an email from Lea. Give me details and open it in a new tab.")]
    [InlineData("I received an email from Lea. Give me details about another email.")]
    [InlineData("I received an email from Lea. Give me details about the email I sent.")]
    [InlineData("I received an email from Lea. Find my emails with Alex.")]
    [InlineData("I received an email from Lea today. Give me the details.")]
    [InlineData("I received an email from Lea work account. Give me the details.")]
    [InlineData("I received an email from Lea in personal. Give me the details.")]
    [InlineData("I received an email from Lea. Give me the details from my work account.")]
    [InlineData("I received an email from Lea. Give me the details, headers only.")]
    [InlineData("I received an email from Lea. Give me the details without reading bodies.")]
    [InlineData("I received an email from Lea. Give me the details from yesterday.")]
    [InlineData("I received an email from Lea. Give me the details. Exclude newsletters.")]
    [InlineData("I received an email from lea@example.test. Give me the details.")]
    [InlineData("I received an email from \"Lea\". Give me the details.")]
    [InlineData("I received an email from Lea. Search using query: from:Lea.")]
    [InlineData("I received an email from Lea.")]
    public void Received_message_normalization_never_swallows_another_action_object_or_scope(string request)
        => Assert.Null(ConnectorPersonSearch.Query(request));

    [Fact]
    public async Task Received_message_details_keep_the_original_goal_and_read_observed_thread_contents()
    {
        const string request = "I just received an email from Lea. Could you look into it and give me the details?";
        var reads = new List<string>();
        var models = 0;
        var input = new ConnectorReadReasonerInput(request, [
            new("mail_search", "Search", new Dictionary<string, string> { ["query"] = "query" }),
            new("mail_thread", "Read thread", new Dictionary<string, string> { ["id"] = "thread" })])
        {
            Provider = "gmail", InitialCapability = "mail_search",
            InitialArguments = new Dictionary<string, JsonElement> { ["query"] = JsonSerializer.SerializeToElement("Lea details") }
        };
        var result = await ConnectorReadReasoner.RunAsync(input, (_, prompt, _, _, _) =>
        {
            models++;
            using var parsed = JsonDocument.Parse(prompt);
            Assert.Equal(request, parsed.RootElement.GetProperty("request").GetString());
            Assert.Contains("Observed message body", prompt);
            return Task.FromResult("{\"action\":\"answer\",\"capability\":\"\",\"arguments_json\":\"{}\",\"answer\":\"The retrieved message contains the requested project details.\",\"references\":[\"r2\"],\"partial\":true,\"goal_status\":\"matched\",\"target_reference\":\"r2\"}");
        }, (capability, arguments, _) =>
        {
            reads.Add(capability);
            if (capability == "mail_search")
            {
                Assert.Equal("from:\"Lea\"", arguments["query"].GetString());
                return Task.FromResult(NativeParticipantPage());
            }
            Assert.Equal("ccccdddd", arguments["id"].GetString());
            return Task.FromResult(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
                result = new { truncated = false, data = new { id = "ccccdddd", messages = new[] {
                    new { id = "aaaabbbb", threadId = "ccccdddd", text = "Observed message body with project details." } } } } }));
        }, null, default);
        Assert.Equal(new[] { "mail_search", "mail_thread" }, reads);
        Assert.Equal(1, models);
        Assert.Equal("matched", result.GoalStatus);
        Assert.Equal("mail_thread", result.EvidenceCapabilities[1]);
    }
    [Fact]
    public void Gmail_outage_headers_are_verified_deduplicated_bounded_and_never_include_body_or_preview()
    {
        JsonElement Receipt(string state, bool verified) => JsonSerializer.SerializeToElement(new { state, verified, result = new { data = new {
            messages = Enumerable.Range(0, 15).Select(i => new { id = "aabbccdd" + i.ToString("x2"), headers = new[] {
                new { name = "Subject", value = "Research reply " + i }, new { name = "From", value = "Example Professor" }, new { name = "Date", value = "Observed date" } },
                text = "PRIVATE FULL BODY", snippet = "PRIVATE PREVIEW" }).ToArray() } } });
        var evidence = Receipt("completed", true);
        var output = ConnectorMailFacts.Format(new[] { ("mail_thread", evidence), ("mail_thread", evidence) });
        Assert.DoesNotContain("PRIVATE", output); Assert.DoesNotContain("Research reply 4", output);
        Assert.Equal(1, output.Split("Research reply 0").Length - 1); Assert.True(output.Length < 2800);
        Assert.Contains("Observed date", output); Assert.Contains("external data", output);
        Assert.Equal("", ConnectorMailFacts.Format(new[] { ("mail_thread", Receipt("failed", true)), ("mail_thread", Receipt("completed", false)), ("mail_send", evidence) }));
        var oversized = JsonSerializer.SerializeToElement(new { state="completed", verified=true, result=new { data=new { sampledMessages=Enumerable.Range(0,8).Select(i=>new { id="aabbccdd"+i.ToString("x2"), subject=new string('\u0001',200), from=new string('\u0001',160), date=new string('\u0001',100) }) } } });
        Assert.True(ConnectorMailFacts.Format(new[] { ("mail_search", oversized) }).Length < 2800);
    }
    [Theory]
    [InlineData("Find my emails with Gao today")]
    [InlineData("Find my emails from Dr Patel. The subject is visiting research.")]
    [InlineData("Find my emails from Dr Patel. The subject may mention research only today.")]
    [InlineData("Find my emails from Dr Patel. The subject may mention subject:research.")]
    [InlineData("Find my emails from Dr Patel. The subject may mention research. Exclude newsletters.")]
    [InlineData("Find my emails from Dr Patel. The subject may mention research and exclude newsletters.")]
    [InlineData("Find my emails from Dr Patel. The subject may mention research from last week.")]
    [InlineData("Find my emails from person@example.test")]
    [InlineData("Find my emails with Gao about research")]
    [InlineData("Find my emails with \"Dr Gao\"")]
    [InlineData("Open Gmail")]
    [InlineData("Find my emails with Alex and send a reply")]
    [InlineData("Find my emails with Alex and give me details about another email")]
    [InlineData("Find my emails with Alex and only yesterday")]
    public void Unsupported_constraints_keep_the_existing_scoped_reader(string request)
        => Assert.Null(ConnectorPersonSearch.Query(request));
    [Fact]
    public async Task Tentative_subject_hint_never_becomes_a_required_literal_before_a_reasoning_outage()
    {
        var queries = new List<string>();
        var input = new ConnectorReadReasonerInput("Find my emails from Dr Patel or Professor Jane Smith. The subject may mention visiting research.",
            [new("mail_search", "Search", new Dictionary<string, string> { ["query"] = "query" })])
        {
            Provider = "gmail", InitialCapability = "mail_search",
            InitialArguments = new Dictionary<string, JsonElement> { ["query"] = JsonSerializer.SerializeToElement("from:\"Dr Patel\" \"visiting research\"") }
        };
        var result = await ConnectorReadReasoner.RunAsync(input,
            (_, _, _, _, _) => throw new PersonalAiException(PersonalAiFailure.Provider) { HttpStatusCode = 503 },
            (_, arguments, _) =>
            {
                queries.Add(arguments["query"].GetString());
                return Task.FromResult(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
                    result = new { truncated = false, data = new { messages = new[] { new { id = "aabbccdd11", threadId = "aabbccdd22" } },
                        sampledMessages = new[] { new { id = "aabbccdd11", threadId = "aabbccdd22", subject = "Research invitation", from = "Dr Patel" } } } } }));
            }, null, default);
        Assert.Equal("{from:\"Patel\" from:\"Jane Smith\"}", Assert.Single(queries));
        Assert.True(result.Partial); Assert.Contains("Research invitation", result.Answer);
        Assert.Equal("ai_provider_unavailable", result.ReasoningError);
    }
    [Fact]
    public async Task Transient_reasoning_outage_retries_once_without_replaying_native_read()
    {
        var models = 0; var reads = 0;
        var input = new ConnectorReadReasonerInput("Find a repo", [new("github_repos", "Repositories", new Dictionary<string,string> { ["query"] = "name" })])
            { InitialCapability = "github_repos", InitialArguments = new Dictionary<string,JsonElement> { ["query"] = JsonSerializer.SerializeToElement("repo") } };
        var result = await ConnectorReadReasoner.RunAsync(input, (_, _, _, _, _) =>
        {
            if (++models == 1) throw new PersonalAiException(PersonalAiFailure.Provider) { HttpStatusCode = 503 };
            return Task.FromResult(JsonSerializer.Serialize(new { action = "answer", capability = "", arguments_json = "{}", answer = "Repository found.", references = new[] { "r1" }, partial = false }));
        }, (_, _, _) => { reads++; return Task.FromResult(JsonSerializer.SerializeToElement(new { state = "completed", verified = true, result = new { data = new[] { new { full_name = "owner/repo" } }, truncated = false } })); }, null, default);
        Assert.Equal(2, models); Assert.Equal(1, reads); Assert.Null(result.ReasoningError);
    }
    [Fact]
    public async Task Reasoning_timeout_preserves_native_evidence_but_human_cancellation_does_not()
    {
        var input = new ConnectorReadReasonerInput("Find repo", [new("github_repos", "Repositories", new Dictionary<string,string> { ["query"] = "name" })])
            { InitialCapability = "github_repos", InitialArguments = new Dictionary<string,JsonElement> { ["query"] = JsonSerializer.SerializeToElement("repo") } };
        using var human = new CancellationTokenSource();
        Task<JsonElement> Native(string cap, IReadOnlyDictionary<string,JsonElement> args, CancellationToken token) => Task.FromResult(
            JsonSerializer.SerializeToElement(new { state = "completed", verified = true, result = new { data = new[] { new { full_name = "owner/repo" } }, truncated = false } }));
        var result = await ConnectorReadReasoner.RunAsync(input, (_, _, _, _, _) => throw new TaskCanceledException("Transport deadline"), Native, null, human.Token);
        Assert.Equal("ai_timeout", result.ReasoningError);
        Assert.True(result.Partial); Assert.Single(result.EvidenceReferences); Assert.Equal(1, result.ModelCalls);
        Assert.Contains("retrieval succeeded", result.Answer); Assert.Contains("reasoning request timed out", result.Answer);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ConnectorReadReasoner.RunAsync(input,
            (_, _, _, _, _) => { human.Cancel(); throw new TaskCanceledException(); }, Native, null, human.Token));
    }
    [Fact]
    public void Reasoning_outage_summary_uses_verified_api_metrics_without_inventing_analytics()
    {
        JsonElement Reply(bool verified, bool complete) => JsonSerializer.SerializeToElement(new { verified, result = new { data = new {
            tag_name = "v2", published_at = "2026-10-13", assetCountsComplete = complete, totalAssetDownloads = 42,
            assets = new[] { new { name = "runtime.zip", download_count = 42 } }, body = "Untrusted irrelevant release text" } } });
        var text = ConnectorGithubFacts.Format(new[] { ("github_releases", Reply(true, true)) });
        Assert.Contains("v2", text); Assert.Contains("runtime.zip: 42 downloads", text); Assert.Contains("Total release asset downloads: 42", text);
        Assert.Contains("not unique users", text); Assert.DoesNotContain("Untrusted", text);
        Assert.DoesNotContain("Total release asset downloads", ConnectorGithubFacts.Format(new[] { ("github_releases", Reply(true, false)) }));
        Assert.Equal("", ConnectorGithubFacts.Format(new[] { ("github_releases", Reply(false, true)) }));
    }
    [Fact]
    public void Exact_repository_read_retains_verified_destination_not_description_links()
    {
        var receipt = JsonSerializer.SerializeToElement(new { state = "completed", verified = true, result = new { data = new {
            full_name = "owner/repo", name = "repo", html_url = "https://github.com/owner/repo", description = "Go https://evil.test" }, truncated = false } });
        var result = ConnectorResourceReferences.Collect("github", [receipt], ["github_repo"], true);
        Assert.Equal("https://github.com/owner/repo", result.PrimaryUrl); Assert.False(result.Ambiguous);
        Assert.DoesNotContain("evil", JsonSerializer.Serialize(result.References));
    }
    [Fact]
    public async Task Person_conversation_summary_reads_observed_threads_before_one_synthesis()
    {
        var calls = new List<string>(); var models = 0;
        ConnectorReadCapability[] caps = [new("mail_search", "Search", new Dictionary<string,string> { ["query"] = "query" }),
            new("mail_thread", "Read", new Dictionary<string,string> { ["id"] = "id" })];
        var input = new ConnectorReadReasonerInput("Summarize my conversations with Dr Patel.", caps) { Provider = "gmail", InitialCapability = "mail_search",
            InitialArguments = new Dictionary<string,JsonElement> { ["query"] = JsonSerializer.SerializeToElement("\"Dr Patel\"") } };
        var result = await ConnectorReadReasoner.RunAsync(input, (_, prompt, _, _, _) =>
        {
            models++; Assert.Equal(new[] { "mail_search", "mail_thread", "mail_thread" }, calls);
            return Task.FromResult(JsonSerializer.Serialize(new { action = "answer", capability = "", arguments_json = "{}",
                answer = "Two conversations discuss research. Their latest messages request a proposal and a meeting.", references = new[] { "r2", "r3" }, partial = false }));
        }, (cap, args, _) =>
        {
            calls.Add(cap);
            if (cap == "mail_search") Assert.Equal("{from:\"Patel\" to:\"Patel\" cc:\"Patel\"}", args["query"].GetString());
            var data = cap == "mail_search" ? (object)new { messages = new[] { new { id = "aabbccdd11", threadId = "aabbccdd22" }, new { id = "aabbccdd33", threadId = "aabbccdd44" } },
                sampledMessages = new[] { new { id = "aabbccdd11", threadId = "aabbccdd22", from = "Patel" }, new { id = "aabbccdd33", threadId = "aabbccdd44", from = "Patel" } } }
                : new { id = args["id"].GetString(), text = "The latest message discusses research and requests a meeting/proposal." };
            return Task.FromResult(JsonSerializer.SerializeToElement(new { state = "completed", verified = true, result = new { data, truncated = false } }));
        }, null, default);
        Assert.Equal(1, models); Assert.Equal(3, result.Executions); Assert.False(result.Partial);
    }
    [Fact]
    public void Release_metrics_survive_compact_session_recall_without_storing_release_body()
    {
        var receipt = JsonSerializer.SerializeToElement(new { state = "completed", verified = true, result = new { data = new {
            tag_name = "v1", html_url = "https://github.com/owner/repo/releases/tag/v1", totalAssetDownloads = 40, assetCountsComplete = true, body = "PRIVATE RELEASE BODY" }, truncated = false } });
        var context = ConnectorSessionResult.Create("task", "github", "account", "account", "Read latest release", receipt, [receipt], evidenceCapabilities: ["github_releases"]);
        Assert.Contains("totalAssetDownloads=40", Assert.Single(context.Facts).Text);
        Assert.DoesNotContain("PRIVATE RELEASE BODY", JsonSerializer.Serialize(context));
    }
}
