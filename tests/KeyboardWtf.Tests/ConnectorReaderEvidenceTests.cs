namespace KeyboardWtf.Tests;

using System.Text.Json;
using KeyboardWtf.Connectors;
using KeyboardWtf.Services.Connectors;
using Xunit;

public sealed class ConnectorReaderEvidenceTests
{
    private static readonly ConnectorReadCapability[] Mail = [new("mail_search", "Find candidates", new Dictionary<string,string> { ["query"]="native query", ["pageToken"]="cursor" }),
        new("mail_read", "Read content", new Dictionary<string,string> { ["id"]="observed id" })];
    private static string Step(string action, string capability="", string args="{}", string answer="", string[] refs=null, bool partial=false)
        => JsonSerializer.Serialize(new { action, capability, arguments_json=args, answer, references=refs ?? [], partial });
    private static JsonElement Receipt(object data, bool verified=true) => JsonSerializer.SerializeToElement(new
        { state=verified ? "completed" : "failed", verified, result=new { data } });
    private static Task<ConnectorReadReasonerResult> Run(string request, Queue<string> steps,
        Func<string,IReadOnlyDictionary<string,JsonElement>,CancellationToken,Task<JsonElement>> execute,
        ConnectorReadCapability[] tools=null, CancellationToken token=default)
        => ConnectorReadReasoner.RunAsync(new(request,tools ?? Mail), (_,_,_,_,_) => Task.FromResult(steps.Dequeue()),execute,null,token);

    [Fact]
    public async Task Search_preview_cannot_masquerade_as_a_detailed_content_summary()
    {
        var plan=new Queue<string>([Step("execute","mail_search","{\"query\":\"project\"}"),
            Step("answer",answer:"Unsupported prize claim",refs:["r1"]),
            Step("execute","mail_read","{\"id\":\"observed\"}"),
            Step("answer",answer:"Project won the finalist prize. Reply by Friday.",refs:["r2"])]);
        var calls=new List<string>();
        var result=await Run("Summarize the winning email",plan,(cap,_,_) =>
        { calls.Add(cap); return Task.FromResult(Receipt(cap=="mail_search" ? (object)new { messages=new[] {new {id="observed"}} } : new {id="observed",text="Finalist prize. Reply by Friday."})); });
        Assert.Equal(new[]{"mail_search","mail_read"},calls);
        Assert.Equal(4,result.ModelCalls); Assert.Equal(new[]{"r2"},result.EvidenceReferences); Assert.Equal("evidence_available",result.GoalOutcome);
        Assert.DoesNotContain("Unsupported",result.Answer);
    }
    [Fact]
    public async Task Empty_inferred_search_requires_refinement_before_a_negative_answer()
    {
        var plan=new Queue<string>([Step("answer",answer:"Found nothing",partial:true),
            Step("execute","mail_search","{\"query\":\"misspelled project\"}"),Step("answer",answer:"Found nothing",partial:true),
            Step("execute","mail_search","{\"query\":\"project topic\"}"),Step("answer",answer:"No confident match after two searches.",partial:true)]);
        var calls=0;
        var result=await Run("Find the vaguely remembered email",plan,(_,_,_) => {calls++;return Task.FromResult(Receipt(new {messages=Array.Empty<object>()}));});
        Assert.Equal(2,calls); Assert.Equal(5,result.ModelCalls); Assert.Equal("not_found",result.GoalOutcome); Assert.True(result.Partial);
    }
    [Fact]
    public async Task Literal_query_does_not_require_broadening_its_empty_result()
    {
        var plan=new Queue<string>([Step("execute","mail_search","{\"query\":\"subject:literal\"}"),Step("answer",answer:"No match for that exact query.",partial:true)]);
        var result=await Run("Search using: subject:literal",plan,(_,_,_)=>Task.FromResult(Receipt(new {messages=Array.Empty<object>()})));
        Assert.Equal(1,result.Executions); Assert.Equal("not_found",result.GoalOutcome);
    }
    [Fact]
    public async Task Two_safe_queries_share_one_model_turn_and_one_serial_execution_owner()
    {
        var plan=new Queue<string>([Step("search_batch","mail_search","[{\"query\":\"project winner\"},{\"query\":\"project prize\"}]"),
            Step("execute","mail_read","{\"id\":\"observed\"}"),Step("answer",answer:"The observed project won the prize.",refs:["r3"])]);
        var active=0; var maximum=0; var queries=new List<string>();
        var result=await Run("Find the winning project email",plan,async (cap,args,token)=>
        {
            active++; maximum=Math.Max(maximum,active); await Task.Yield(); token.ThrowIfCancellationRequested(); active--;
            if(cap=="mail_search") {queries.Add(args["query"].GetString());return Receipt(new {messages=new[] {new {id="observed"}}});}
            return Receipt(new {id="observed",text="The project won the prize."});
        });
        Assert.Equal(1,maximum); Assert.Equal(2,queries.Count); Assert.Equal(3,result.Executions); Assert.Equal(3,result.ModelCalls);
    }
    [Fact]
    public async Task Repository_metadata_summary_is_valid_with_the_full_repository_and_issue_bundle()
    {
        var tools=new[]{new ConnectorReadCapability("github_repos","Repository metadata",new Dictionary<string,string>()),
            new ConnectorReadCapability("github_issues","Issue contents",new Dictionary<string,string>())};
        var plan=new Queue<string>([Step("execute","github_repos"),Step("answer",answer:"Two repositories: A and B, both updated recently.",refs:["r1"])]);
        var calls=new List<string>();
        var result=await Run("Summarize my GitHub projects",plan,(cap,_,_)=>{calls.Add(cap);return Task.FromResult(Receipt(new[]{new {id=1,name="A"},new {id=2,name="B"}}));},tools);
        Assert.Equal(new[]{"github_repos"},calls); Assert.Equal("evidence_available",result.GoalOutcome);
    }
    [Fact]
    public async Task Empty_verified_search_cannot_be_reported_as_a_positive_object_match()
    {
        var plan=new Queue<string>([Step("execute","mail_search","{\"query\":\"topic\"}"),Step("answer",answer:"Found the object",refs:["r1"]),
            Step("execute","mail_search","{\"query\":\"other topic\"}"),Step("answer",answer:"No confident match after refinement",partial:true)]);
        var result=await Run("Find a message",plan,(_,_,_)=>Task.FromResult(Receipt(new {messages=Array.Empty<object>()})));
        Assert.Equal("not_found",result.GoalOutcome); Assert.Empty(result.EvidenceReferences); Assert.DoesNotContain("Found the object",result.Answer);
    }
    [Fact]
    public async Task Empty_content_classification_cannot_support_a_detailed_content_answer()
    {
        var plan=new Queue<string>([Step("execute","mail_search","{\"query\":\"topic\"}"),Step("execute","mail_read","{\"id\":\"observed\"}"),
            Step("answer",answer:"Unsupported detailed claim",refs:["r2"]),Step("execute","mail_search","{\"query\":\"other topic\"}"),
            Step("answer",answer:"No verified content established the answer",partial:true)]);
        var result=await Run("Summarize the conversation",plan,(cap,_,_)=>Task.FromResult(Receipt(cap=="mail_search"
            ? (object)new {messages=new[]{new {id="observed"}}} : new {id="observed",text=""})));
        Assert.Equal("partial",result.GoalOutcome); Assert.Empty(result.EvidenceReferences); Assert.DoesNotContain("Unsupported",result.Answer);
    }
    [Fact]
    public async Task Literal_query_cannot_be_batched_into_a_rewritten_search()
    {
        var plan=new Queue<string>([Step("search_batch","mail_search","[{\"query\":\"broader\"},{\"query\":\"rewritten\"}]"),
            Step("execute","mail_search","{\"query\":\"subject:literal\"}"),Step("answer",answer:"No match for that exact query",partial:true)]);
        var queries=new List<string>();
        await Run("Search using: subject:literal",plan,(_,args,_)=>{queries.Add(args["query"].GetString());return Task.FromResult(Receipt(new {messages=Array.Empty<object>()}));});
        Assert.Equal(new[]{"subject:literal"},queries);
    }
    [Theory]
    [InlineData("Search using: subject:literal", "subject:literal")]
    [InlineData("Use the exact query: from:team@example.test subject:\"Project launch\"", "from:team@example.test subject:\"Project launch\"")]
    public async Task Literal_query_cannot_be_rewritten_by_a_single_reader_proposal(string request, string literal)
    {
        var plan = new Queue<string>([Step("execute", "mail_search", "{\"query\":\"broader\"}"),
            Step("execute", "mail_search", JsonSerializer.Serialize(new { query = literal })),
            Step("answer", answer: "No match for the supplied query.", partial: true)]);
        var queries = new List<string>();
        await Run(request, plan, (_, args, _) =>
        {
            queries.Add(args["query"].GetString());
            return Task.FromResult(Receipt(new { messages = Array.Empty<object>() }));
        });
        Assert.Equal(new[] { literal }, queries);
    }
    [Fact]
    public async Task Native_search_seeds_evidence_before_reasoning_and_synthesis_reads_actual_content()
    {
        var sequence = new List<string>();
        var plan = new Queue<string>([Step("execute", "mail_read", "{\"id\":\"observed\"}"),
            Step("answer", answer: "The project won a finalist prize; acknowledge by Friday.", refs: ["r2"])]);
        var result = await ConnectorReadReasoner.RunAsync(new("Summarize the winning email", Mail)
            { InitialCapability = "mail_search", InitialArguments = JarvisConnectorController.ParseArguments("{\"query\":\"project winner\"}") },
            (_, prompt, _, _, _) =>
            {
                sequence.Add("model"); Assert.Contains("project winner", prompt); Assert.Contains("r1", prompt);
                return Task.FromResult(plan.Dequeue());
            }, (cap, _, _) =>
            {
                sequence.Add(cap); return Task.FromResult(Receipt(cap == "mail_search"
                    ? (object)new { messages = new[] { new { id = "observed" } } }
                    : new { id = "observed", text = "The project won a finalist prize. Acknowledge by Friday." }));
            }, null, default);
        Assert.Equal(new[] { "mail_search", "model", "mail_read", "model" }, sequence);
        Assert.Equal(2, result.ModelCalls); Assert.Equal(2, result.Executions); Assert.Equal(new[] { "r2" }, result.EvidenceReferences);
    }
    [Fact]
    public async Task Cancellation_of_the_native_first_search_prevents_any_late_reasoning()
    {
        using var cancellation = new CancellationTokenSource(); var models = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ConnectorReadReasoner.RunAsync(new("Find the winning email", Mail)
            { InitialCapability = "mail_search", InitialArguments = JarvisConnectorController.ParseArguments("{\"query\":\"project\"}") },
            (_,_,_,_,_) => { ++models; return Task.FromResult(""); },
            (_,_,_) => { cancellation.Cancel(); return Task.FromResult(Receipt(new { messages = Array.Empty<object>() })); },
            null, cancellation.Token));
        Assert.Equal(0, models);
    }
    [Fact]
    public async Task Rejected_native_seed_cannot_bypass_literal_query_authority()
    {
        var plan = new Queue<string>([Step("execute", "mail_search", "{\"query\":\"subject:literal\"}"),
            Step("answer", answer: "No match for the literal query.", partial: true)]);
        var queries = new List<string>();
        await ConnectorReadReasoner.RunAsync(new("Search using: subject:literal", Mail)
            { InitialCapability = "mail_search", InitialArguments = JarvisConnectorController.ParseArguments("{\"query\":\"broader\"}") },
            (_,_,_,_,_) => Task.FromResult(plan.Dequeue()), (_, args, _) =>
            { queries.Add(args["query"].GetString()); return Task.FromResult(Receipt(new { messages = Array.Empty<object>() })); }, null, default);
        Assert.Equal(new[] { "subject:literal" }, queries);
    }
    [Fact]
    public async Task Native_seed_search_cannot_be_replayed_by_changing_owner_controlled_search_knobs()
    {
        var tools = new[] { Mail[0] with { Arguments = new Dictionary<string,string> {
            ["query"] = "native query", ["matchMode"] = "owner controlled", ["readMode"] = "owner controlled" } }, Mail[1] };
        var plan = new Queue<string>([Step("execute", "mail_search", "{\"query\":\"project\"}"),
            Step("answer", answer: "One matching candidate is available.", refs: ["r1"])]);
        var calls = 0;
        var result = await ConnectorReadReasoner.RunAsync(new("Find a relevant email", tools)
            { InitialCapability = "mail_search", InitialArguments = JarvisConnectorController.ParseArguments("{\"query\":\"project\",\"matchMode\":\"topic\",\"readMode\":\"message\"}") },
            (_,_,_,_,_) => Task.FromResult(plan.Dequeue()), (_,_,_) =>
            { ++calls; return Task.FromResult(Receipt(new { messages = new[] { new { id = "observed" } } })); }, null, default);
        Assert.Equal(1, calls); Assert.Equal(1, result.Executions); Assert.Equal(2, result.ModelCalls);
    }
    [Fact]
    public async Task Long_content_clipping_remains_partial_even_if_the_synthesis_claims_complete_coverage()
    {
        var plan = new Queue<string>([Step("execute", "mail_search", "{\"query\":\"long conversation\"}"),
            Step("execute", "mail_read", "{\"id\":\"observed\"}"),
            Step("answer", answer: "Topic: launch. Decision: release Friday. Pending action: owner approval. Latest status: review.", refs: ["r2"])]);
        var result = await Run("Summarize the long conversation in detail", plan, (cap, _, _) =>
            Task.FromResult(Receipt(cap == "mail_search" ? (object)new { messages = new[] { new { id = "observed" } } }
                : new { id = "observed", text = new string('x', 30000) + " Release Friday after owner approval." })));
        Assert.True(result.Partial); Assert.Equal(new[] { "r2" }, result.EvidenceReferences);
        Assert.Equal(2, result.Executions); Assert.Contains("Pending action", result.Answer);
    }
    [Theory]
    [InlineData("docs_read", "https://docs.google.com/document/d/documentOne/edit", "documentOne")]
    [InlineData("forms_read", "https://docs.google.com/forms/d/formOne/edit", "formOne")]
    public async Task Explicit_document_or_form_is_read_before_its_first_synthesis_call(string capability, string url, string id)
    {
        var tools = new[] { new ConnectorReadCapability(capability, "Read selected resource", new Dictionary<string,string> { ["id"] = "literal or observed id" }) };
        var calls = 0;
        var result = await ConnectorReadReasoner.RunAsync(new("Summarize " + url, tools)
            { InitialCapability = capability, InitialArguments = JarvisConnectorController.ParseArguments(JsonSerializer.Serialize(new { id })) },
            (_, prompt, _, _, _) =>
            {
                Assert.Equal(1, calls); Assert.Contains("r1", prompt); Assert.Contains(id, prompt);
                return Task.FromResult(Step("answer", answer: capability == "docs_read" ? "The release awaits approval." : "The selected private form is blank.", refs: ["r1"]));
            }, (selected, args, _) =>
            {
                ++calls; Assert.Equal(capability, selected); Assert.Equal(id, args["id"].GetString());
                object data = capability == "docs_read" ? (object)new { documentId = id, body = new { content = new[] {
                    new { paragraph = new { elements = new[] { new { textRun = new { content = "The release awaits approval." } } } } } } } }
                    : new { formId = id, info = new { title = "Private blank form" } };
                return Task.FromResult(Receipt(data));
            }, null, default);
        Assert.Equal(1, calls); Assert.Equal(1, result.Executions); Assert.Equal(1, result.ModelCalls);
    }
    [Theory]
    [InlineData("docs_read", "guessedDocument")]
    [InlineData("forms_read", "guessedForm")]
    [InlineData("mail_thread", "guessedThread")]
    public async Task An_unobserved_object_seed_does_not_grant_read_authority(string capability, string id)
    {
        var tools = new[] { new ConnectorReadCapability(capability, "Read selected resource", new Dictionary<string,string> { ["id"] = "observed id" }) };
        var calls = 0;
        var result = await ConnectorReadReasoner.RunAsync(new("Summarize the resource I mentioned", tools)
            { InitialCapability = capability, InitialArguments = JarvisConnectorController.ParseArguments(JsonSerializer.Serialize(new { id })) },
            (_,_,_,_,_) => Task.FromResult(Step("answer", answer: "No resource was identified.", partial: true)),
            (_,_,_) => { ++calls; return Task.FromResult(Receipt(new { })); }, null, default);
        Assert.Equal(0, calls); Assert.Equal(0, result.Executions); Assert.Equal(9, result.ModelCalls);
    }
    [Theory]
    [InlineData("docs_read",false)]
    [InlineData("docs_read",true)]
    [InlineData("forms_read",false)]
    [InlineData("forms_read",true)]
    [InlineData("slack_history",false)]
    public async Task Actual_document_form_and_channel_shapes_support_content_and_verified_blank_resources(string cap,bool blank)
    {
        object data = cap switch
        {
            "docs_read" => new {documentId="doc-observed",tabs=new[]{new {documentTab=new {body=new {content=new[]{new {paragraph=new {elements=new[]{new {textRun=new {content=blank ? "\n" : "A documented decision."}}}}}}}}}}},
            "forms_read" => blank ? (object)new {formId="form-observed",info=new {title="Blank form"}} : new {formId="form-observed",info=new {title="Survey"},items=new[]{new {title="Which day works?",questionItem=new {question=new {textQuestion=new {}}}}}},
            _ => new {messages=new[]{new {text="A channel decision."}}}
        };
        var tools=new[]{new ConnectorReadCapability(cap,"Read selected content",new Dictionary<string,string>())};
        var plan=new Queue<string>([Step("execute",cap),Step("answer",answer:blank ? "This resource is blank." : "A documented decision is available.",refs:["r1"])]);
        var receipt=JsonSerializer.SerializeToElement(new {state="completed",verified=true,result=new {data,coverage=new {complete=true},truncated=false}});
        var result=await Run("Summarize this resource",plan,(_,_,_)=>Task.FromResult(receipt),tools);
        Assert.Equal(new[]{"r1"},result.EvidenceReferences); Assert.False(result.Partial);
    }
    [Fact]
    public async Task Missing_document_coverage_does_not_prove_a_blank_document()
    {
        var tools=new[]{new ConnectorReadCapability("docs_read","Read selected content",new Dictionary<string,string>())};
        var plan=new Queue<string>([Step("execute","docs_read"),Step("answer",answer:"It is definitely blank",refs:["r1"]),
            Step("answer",answer:"Complete document coverage was not available.",partial:true)]);
        var result=await Run("Summarize this document",plan,(_,_,_)=>Task.FromResult(Receipt(new {documentId="doc-observed",title="Only metadata"})),tools);
        Assert.Empty(result.EvidenceReferences); Assert.True(result.Partial);
    }
    [Theory]
    [InlineData("mail_search","[{\"query\":\"safe\"},{\"unknown\":true}]")]
    [InlineData("mail_search","[{\"query\":\"safe\"},{\"query\":\"safe\"}]")]
    [InlineData("mail_search","[{\"query\":\"safe\"},{\"query\":\"other\",\"pageToken\":\"invented\"}]")]
    [InlineData("mail_search","[{\"query\":\"safe\"}]")]
    [InlineData("mail_read","[{\"id\":\"observed\"},{\"id\":\"other\"}]")]
    [InlineData("mail_send","[{\"query\":\"safe\"},{\"query\":\"other\"}]")]
    public async Task Whole_batch_is_validated_before_either_action_runs(string cap,string args)
    {
        var models=0; var calls=0;
        var result=await ConnectorReadReasoner.RunAsync(new("Find a message",Mail),(_,_,_,_,_)=>Task.FromResult(++models==1
            ? Step("search_batch",cap,args) : Step("answer",answer:"No verified evidence",partial:true)),
            (_,_,_)=>{calls++;return Task.FromResult(Receipt(new{}));},null,default);
        Assert.Equal(0,calls); Assert.Equal(9,models); Assert.Equal("partial",result.GoalOutcome);
    }
    [Fact]
    public async Task Cancel_after_first_batch_member_prevents_second_search_and_any_answer()
    {
        using var cancellation=new CancellationTokenSource(); var calls=0;
        var plan=new Queue<string>([Step("search_batch","mail_search","[{\"query\":\"first\"},{\"query\":\"second\"}]")]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Run("Find a message",plan,(_,_,_)=>
        {calls++;cancellation.Cancel();return Task.FromResult(Receipt(new{}));},token:cancellation.Token));
        Assert.Equal(1,calls);
    }
    [Fact]
    public async Task Failed_first_batch_member_stops_remaining_provider_execution()
    {
        var calls=0; var plan=new Queue<string>([Step("search_batch","mail_search","[{\"query\":\"first\"},{\"query\":\"second\"}]"),
            Step("execute","mail_search","{\"query\":\"third\"}"),Step("answer",answer:"Provider read failed; no answer verified.",partial:true)]);
        var result=await Run("Find a message",plan,(_,_,_)=>{calls++;return Task.FromResult(Receipt(new{},false));});
        Assert.Equal(1,calls); Assert.Equal("partial",result.GoalOutcome); Assert.Empty(result.EvidenceReferences);
    }
    [Fact]
    public async Task Four_query_budget_cannot_be_exceeded_by_a_fifth_batch_member()
    {
        var models=0; var calls=0;
        var result=await ConnectorReadReasoner.RunAsync(new("Find a vague message",Mail),(_,_,_,_,_)=>Task.FromResult(Step("search_batch","mail_search",
            JsonSerializer.Serialize(new[]{new {query="variant"+(++models)+"a"},new {query="variant"+models+"b"}}))),
            (_,_,_)=>{calls++;return Task.FromResult(Receipt(new {messages=Array.Empty<object>()}));},null,default);
        Assert.Equal(4,calls); Assert.Equal(3,models); Assert.True(result.Partial);
    }
    [Theory]
    [InlineData("mail_search","search")]
    [InlineData("mail_thread","content")]
    [InlineData("docs_read","content")]
    [InlineData("drive_read","metadata")]
    [InlineData("youtube_channel","metadata")]
    [InlineData("mail_send","unknown")]
    [InlineData("not_registered","unknown")]
    public void Normalized_evidence_traits_do_not_advertise_content_for_metadata_or_writes(string cap,string expected)
        => Assert.Equal(expected,ConnectorEvidenceTraits.For(cap));
}
