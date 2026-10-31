namespace KeyboardWtf.Tests;

using System.Text.Json;
using KeyboardWtf.Services.Ai.Backends;
using KeyboardWtf.Services.Connectors;
using Xunit;

public sealed class ConnectorReadReasonerTests
{
    [Fact]
    public async Task Actual_personal_pool_cooldown_preserves_fresh_retained_thread_content_without_another_dispatch()
    {
        var directory = Path.Combine(Path.GetTempPath(), "keyboard-wtf-reader-cooldown-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var vault = new PersonalApiVault(Path.Combine(directory, "personal-api.dat"));
            vault.Add(0, "synthetic", "synthetic-cooldown-secret-never-a-real-key", PersonalKeyValidation.AuthenticationValidated);
            using var lease = vault.Acquire(vault.ReadConfiguration());
            var models = 0; var reads = 0; var providerDispatches = 0;
            var thread = "aabbccdd11223344";
            var result = await ConnectorReadReasoner.RunAsync(new("Read the full retained conversation details: " + thread, [
                new("mail_thread", "Read a retained thread", new Dictionary<string, string> { ["id"] = "observed thread" })])
                {
                    Provider = "gmail", InitialCapability = "mail_thread", RetainedThreadId = thread,
                    InitialArguments = new Dictionary<string, JsonElement> { ["id"] = JsonSerializer.SerializeToElement(thread) },
                    PreferNativeRetainedContentOn503 = true
                },
                (_, _, _, _, _) =>
                {
                    ++models;
                    vault.EnsureDispatchAllowed(lease);
                    ++providerDispatches;
                    throw new Xunit.Sdk.XunitException("A cooling pool must reject dispatch before any provider call.");
                },
                (capability, arguments, _) =>
                {
                    ++reads; Assert.Equal("mail_thread", capability); Assert.Equal(thread, arguments["id"].GetString());
                    // A prior operation can cool the shared pool after this native read has succeeded.
                    vault.CoolDownUnknownScopePool(TimeSpan.FromMinutes(1));
                    return Task.FromResult(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
                        result = new { truncated = false, data = new { id = thread, messages = new[] {
                            new { id = "aabbccdd55667788", threadId = thread, text = "Please send the revised proposal by Friday.",
                                headers = new[] { new { name = "Subject", value = "Proposal revision" }, new { name = "From", value = "Person <person@example.test>" } } }
                        } } } }));
                }, null, default);

            Assert.Equal(1, reads); Assert.Equal(1, models); Assert.Equal(0, providerDispatches);
            Assert.Equal(1, result.Executions); Assert.Equal(1, result.ModelCalls);
            Assert.Equal("ai_rate_limit", result.ReasoningError); Assert.True(result.Partial);
            Assert.Single(result.EvidenceReceipts); Assert.Single(result.EvidenceReferences);
            var retainedBody = Assert.Single(ConnectorMailFacts.Content(result.EvidenceCapabilities.Zip(result.EvidenceReceipts,
                (capability, receipt) => (capability, receipt))));
            Assert.Contains("Please send the revised proposal by Friday.", JsonSerializer.Serialize(retainedBody));
            Assert.Contains("Proposal revision", result.Answer);
            Assert.DoesNotContain("no longer active", result.Answer, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("disconnected", result.Answer, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retained_native_content_does_not_turn_revoked_or_corrupt_personal_credentials_into_recoverable_quota(bool corrupt)
    {
        var directory = Path.Combine(Path.GetTempPath(), "keyboard-wtf-reader-credential-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(directory, "personal-api.dat");
            var vault = new PersonalApiVault(path);
            var added = vault.Add(0, "synthetic", "synthetic-revocation-secret-never-a-real-key", PersonalKeyValidation.AuthenticationValidated);
            using var lease = vault.Acquire(vault.ReadConfiguration());
            var reads = 0; var models = 0;
            var thread = "aabbccdd11223344";
            var error = await Record.ExceptionAsync(() => ConnectorReadReasoner.RunAsync(new("Read the full retained conversation details: " + thread, [
                new("mail_thread", "Read a retained thread", new Dictionary<string, string> { ["id"] = "observed thread" })])
                {
                    Provider = "gmail", InitialCapability = "mail_thread", RetainedThreadId = thread,
                    InitialArguments = new Dictionary<string, JsonElement> { ["id"] = JsonSerializer.SerializeToElement(thread) }
                },
                (_, _, _, _, _) =>
                {
                    ++models; vault.EnsureDispatchAllowed(lease);
                    throw new Xunit.Sdk.XunitException("Invalid credentials must fail before dispatch.");
                },
                (_, _, _) =>
                {
                    ++reads;
                    if (corrupt) File.WriteAllBytes(path, [1, 2, 3]);
                    else Assert.True(vault.ApplyValidation(added.Revision, lease.SlotId, lease.CredentialRevision, PersonalKeyValidation.InvalidCredential));
                    return Task.FromResult(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
                        result = new { data = new { id = thread, messages = new[] { new { id = "aabbccdd55667788", threadId = thread, text = "Retrieved content." } } } } }));
                }, null, default));

            Assert.NotNull(error); Assert.Equal(1, reads); Assert.Equal(1, models);
            if (corrupt) Assert.IsType<PersonalApiConfigurationException>(error);
            else Assert.IsType<InvalidOperationException>(error);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Truncated_synthesis_preserves_verified_nonmail_data_without_repeating_native_read_or_model()
    {
        var models = 0; var reads = 0;
        var tools = new[] { new ConnectorReadCapability("github_repo", "Read repository", new Dictionary<string, string> { ["repository"] = "repository" }) };
        var input = new ConnectorReadReasonerInput("Give me insights about the example/project repository", tools)
        {
            Provider = "github", InitialCapability = "github_repo",
            InitialArguments = new Dictionary<string, JsonElement> { ["repository"] = JsonSerializer.SerializeToElement("example/project") }
        };
        var result = await ConnectorReadReasoner.RunAsync(input,
            (_, _, _, _, _) =>
            {
                if (++models == 1) return Task.FromResult(JsonSerializer.Serialize(new { action = "execute", capability = "github_repo",
                    arguments_json = JsonSerializer.Serialize(new { repository = "example/project" }), answer = "", references = Array.Empty<string>(), partial = false }));
                throw new PersonalAiException(PersonalAiFailure.Truncated) { HttpStatusCode = 200 };
            },
            (capability, arguments, _) =>
            {
                ++reads; Assert.Equal("github_repo", capability); Assert.Equal("example/project", arguments["repository"].GetString());
                return Task.FromResult(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
                    result = new { truncated = false, data = new { id = 123, full_name = "example/project", description = "Verified repository metadata",
                        html_url = "https://github.com/example/project", stargazers_count = 7, default_branch = "main" } } }));
            }, null, default);

        Assert.Equal("ai_response_incomplete", result.ReasoningError);
        Assert.Equal(2, models); Assert.Equal(2, result.ModelCalls); Assert.Equal(1, reads);
        Assert.True(result.Partial); Assert.Single(result.EvidenceReceipts); Assert.Single(result.EvidenceReferences);
        Assert.Contains("example/project", result.Answer); Assert.Contains("stargazers_count: 7", result.Answer);
        Assert.DoesNotContain("503", result.Answer); Assert.DoesNotContain("disconnected", result.Answer);
    }

    [Fact]
    public async Task Truncated_json_gets_one_correction_then_preserves_verified_native_results()
    {
        var models = 0; var reads = 0;
        var result = await ConnectorReadReasoner.RunAsync(new("Find the email about a project", Tools)
            { Provider = "gmail", InitialCapability = "mail_search", InitialArguments = new Dictionary<string, JsonElement> { ["query"] = JsonSerializer.SerializeToElement("project") } },
            (_, _, _, limit, _) => { models++; Assert.Equal(2400, limit); return Task.FromResult("{\"answer\":\"cut off"); },
            (_, _, _) => { reads++; return Task.FromResult(JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
                result = new { data = new { messages = new[] { new { id = "aabbccdd11", threadId = "aabbccdd22" } },
                    sampledMessages = new[] { new { id = "aabbccdd11", subject = "Project update" } } } } })); }, null, default);
        Assert.Equal(2, models); Assert.Equal(1, reads);
        Assert.Equal("ai_invalid_response", result.ReasoningError); Assert.True(result.Partial);
        Assert.Single(result.EvidenceReferences); Assert.Single(result.EvidenceReceipts);
        Assert.Contains("Project update", result.Answer); Assert.DoesNotContain("disconnected", result.Answer);
        Assert.DoesNotContain("cut off", result.Answer);
    }
    private static readonly ConnectorReadCapability[] Tools = [new("mail_search", "Search headers and bodies", new Dictionary<string,string> { ["query"] = "search" }),
        new("mail_read", "Read message", new Dictionary<string,string> { ["id"] = "observed reference" })];
    private static string Proposal(string action, string capability = "", string args = "{}", string answer = "", string[] refs = null, bool partial = false)
        => JsonSerializer.Serialize(new { action, capability, arguments_json = args, answer, references = refs ?? [], partial });
    [Fact]
    public async Task Semantic_search_reads_the_observed_candidate_before_a_detailed_grounded_answer()
    {
        var plan = new Queue<string>([Proposal("execute", "mail_search", "{\"query\":\"Project from:Contest\"}"),
            Proposal("execute", "mail_read", "{\"id\":\"observed-a\"}"),
            Proposal("answer", answer:"Project won the finalist prize on July 18. Pending: acknowledge the organizer.", refs:["r2"])]);
        var calls = new List<string>();
        var result = await ConnectorReadReasoner.RunAsync(new("Find the winning email for Project from Contest", Tools),
            (_, prompt, _, _, _) => Task.FromResult(plan.Dequeue()), (cap, args, _) =>
            {
                calls.Add(cap); return Task.FromResult(JsonSerializer.SerializeToElement(new { state="completed", verified=true,
                    result = new { data = cap == "mail_search" ? (object)new { messages=new[] {new { id="observed-a", threadId="thread-a" }} }
                        : new { id="observed-a", text="Project won finalist prize on July 18. Please acknowledge." }, truncated=false } }));
            }, null, default);
        Assert.Equal(new[] { "mail_search", "mail_read" }, calls); Assert.Equal(3, result.ModelCalls);
        Assert.Contains("Pending", result.Answer); Assert.Equal(new[] { "r2" }, result.EvidenceReferences); Assert.False(result.Partial);
    }
    [Theory]
    [InlineData("mail_send", "{}")]
    [InlineData("mail_read", "{\"id\":\"invented\"}")]
    [InlineData("mail_search", "{\"unknown\":true}")]
    public async Task Unknown_tools_arguments_and_unobserved_resources_never_execute(string cap, string args)
    {
        var calls = 0; var models = 0;
        var result = await ConnectorReadReasoner.RunAsync(new("Find a relevant email", Tools),
            (_,_,_,_,_) => Task.FromResult(++models == 1 ? Proposal("execute", cap, args) : Proposal("answer", answer:"No verified evidence is available.",partial:true)), (_,_,_) => { calls++; return Task.FromResult(default(JsonElement)); },null,default);
        Assert.Equal(0,calls);
        Assert.True(result.Partial); Assert.Equal(9,models); // No unsupported early not-found from zero searches.
    }
    [Fact]
    public async Task A_repeated_query_is_bounded_and_never_reexecuted()
    {
        var calls=0;
        var result=await ConnectorReadReasoner.RunAsync(new("Find a relevant email", Tools),
            (_,_,_,_,_)=>Task.FromResult(Proposal("execute","mail_search","{\"query\":\"topic\"}")),
            (_,_,_)=>{calls++;return Task.FromResult(JsonSerializer.SerializeToElement(new {state="completed",verified=true,result=new {data=new {messages=Array.Empty<object>()}}}));},null,default);
        Assert.Equal(1,calls); Assert.True(result.Partial); Assert.Empty(result.EvidenceReferences);
    }
    [Fact]
    public async Task A_fourth_refined_query_is_allowed_but_a_fifth_cannot_download_more_candidates()
    {
        var models=0; var calls=0;
        var result=await ConnectorReadReasoner.RunAsync(new("Find a vaguely remembered email",Tools),
            (_,prompt,_,_,_)=> { Assert.Contains("remainingSearches",prompt); return Task.FromResult(Proposal("execute","mail_search",JsonSerializer.Serialize(new {query="variant"+(++models)}))); },
            (_,_,_)=> {calls++;return Task.FromResult(JsonSerializer.SerializeToElement(new {state="completed",verified=true,result=new {data=new {messages=Array.Empty<object>()}}}));},null,default);
        Assert.Equal(4,calls); Assert.Equal(5,models); Assert.True(result.Partial);
    }
    [Fact]
    public async Task Failed_receipt_cannot_supply_resource_authority_for_another_read()
    {
        var plan=new Queue<string>([Proposal("execute","mail_search","{\"query\":\"topic\"}"),Proposal("execute","mail_read","{\"id\":\"failed-id\"}"),Proposal("answer",answer:"No verified evidence was available.",partial:true)]);
        var calls=0;
        var result=await ConnectorReadReasoner.RunAsync(new("Find a relevant message",Tools),(_,_,_,_,_)=>Task.FromResult(plan.Dequeue()),
            (_,_,_)=>{calls++;return Task.FromResult(JsonSerializer.SerializeToElement(new {state="failed",verified=false,result=new {data=new {id="failed-id"}}}));},null,default);
        Assert.Equal(1,calls); Assert.Empty(result.EvidenceReferences); Assert.True(result.Partial);
    }
    [Fact]
    public async Task Citation_of_failed_evidence_cannot_become_a_verified_answer()
    {
        var plan=new Queue<string>([Proposal("execute","mail_search","{\"query\":\"topic\"}"),Proposal("answer",answer:"Everything is confirmed.",refs:["r1"]),Proposal("answer",answer:"The read failed; no evidence was verified.",partial:true)]);
        var result=await ConnectorReadReasoner.RunAsync(new("Find a relevant email",Tools),
            (_,_,_,_,_)=>Task.FromResult(plan.Dequeue()),(_,_,_)=>Task.FromResult(JsonSerializer.SerializeToElement(new {state="failed",verified=false})),null,default);
        Assert.True(result.Partial); Assert.Empty(result.EvidenceReferences); Assert.DoesNotContain("confirmed",result.Answer);
    }
    [Fact]
    public async Task Cancelled_owner_cannot_publish_a_late_model_answer()
    {
        using var cancellation=new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>ConnectorReadReasoner.RunAsync(new("Find a relevant email",Tools),
            (_,_,_,_,_)=>{cancellation.Cancel();return Task.FromResult(Proposal("answer",answer:"late",partial:true));},(_,_,_)=>Task.FromResult(default(JsonElement)),null,cancellation.Token));
    }
    [Fact]
    public async Task Generic_repository_read_uses_the_same_reader_and_discloses_truncation()
    {
        var tools=new[] {new ConnectorReadCapability("github_repos","List repositories",new Dictionary<string,string>())};
        var plan=new Queue<string>([Proposal("execute","github_repos"),Proposal("answer",answer:"Two relevant projects, more results remain.",refs:["r1"])]);
        var result=await ConnectorReadReasoner.RunAsync(new("Summarize my GitHub projects",tools),(_,_,_,_,_)=>Task.FromResult(plan.Dequeue()),
            (_,_,_)=>Task.FromResult(JsonSerializer.SerializeToElement(new {state="completed",verified=true,result=new {data=new[] {new {id=1}},truncated=true}})),null,default);
        Assert.True(result.Partial); Assert.Equal(1,result.Executions);
    }
    [Theory]
    [InlineData("How many Woo hoo winning emails do I have?",false)]
    [InlineData("Send this email to a person",false)]
    [InlineData("Summarize my conversation with someone",true)]
    [InlineData("Look for the winning email about a project",true)]
    [InlineData("Locate that document about the launch",true)]
    [InlineData("Check my Gmail for the community event",true)]
    [InlineData("Check my YouTube channel statistics",false)]
    [InlineData("Check my account identity",false)]
    [InlineData("Look for a file and delete it",false)]
    [InlineData("Locate a person and send this message",false)]
    [InlineData("How many messages can you locate?",false)]
    [InlineData("What is 2 + 2?",false)]
    [InlineData("Search using: subject:winning",false)]
    [InlineData("Search Gmail for from:team@example.test subject:launch",false)]
    [InlineData("Summarize the winning thread using exact query: subject:launch",true)]
    [InlineData("Summarize https://docs.google.com/document/d/documentOne/edit",true)]
    [InlineData("Edit https://docs.google.com/document/d/documentOne/edit",false)]
    [InlineData("Summarize and delete https://docs.google.com/document/d/documentOne/edit",false)]
    [InlineData("Open https://example.test/summarize/edit",false)]
    public void Adaptive_reader_does_not_take_counts_writes_or_ordinary_conversation(string request,bool expected)
        =>Assert.Equal(expected,ConnectorReadReasoner.Required(request));
    [Fact]
    public void Unranked_multiple_read_threads_are_not_an_exact_open_target()
    {
        JsonElement Mail(string id) => JsonSerializer.SerializeToElement(new {state="completed",verified=true,result=new {data=new {id,threadId=id,headers=Array.Empty<object>()}}});
        var context=ConnectorSessionResult.Create("task","gmail","account","reader@example.com","Read matching messages",Mail("bbbbbbbb"),[Mail("aaaaaaaa"),Mail("bbbbbbbb")],rankedPrimaryEvidence:false);
        Assert.Null(context.PrimaryThread); Assert.Null(context.ExactMailUrl());
    }
    [Fact]
    public void Retained_answer_clipping_truthfully_marks_context_partial()
    {
        var mail=JsonSerializer.SerializeToElement(new {state="completed",verified=true,result=new {data=new {answer=new string('x',7000)},truncated=false}});
        var context=ConnectorSessionResult.Create("task","gmail","account","reader@example.com","Find a message",mail,[]);
        Assert.Equal(6000,context.Answer.Length); Assert.True(context.Partial);
    }
    [Fact]
    public void Compact_session_reference_has_no_body_and_uses_the_matching_account()
    {
        var mail=JsonSerializer.SerializeToElement(new {state="completed",verified=true,result=new {data=new {id="aabbccdd11",threadId="aabbccdd22",text="Private whole body should not persist",headers=new[]{new {name="Subject",value="A useful topic"}}}}});
        var context=ConnectorSessionResult.Create("task","gmail","account","reader@example.com","Find a message",mail,[mail]);
        Assert.DoesNotContain("Private whole body",JsonSerializer.Serialize(context));
        Assert.Equal("https://mail.google.com/mail/?authuser=reader%40example.com#all/aabbccdd22",context.ExactMailUrl());
        Assert.Null((context with {AccountLabel="Ambiguous account"}).ExactMailUrl());
    }
}
