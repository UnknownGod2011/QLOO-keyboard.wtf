// Synthetic callbacks and receipts only; no provider, real model, profile,
// GUI/browser, credentials, microphone or external writes.
using System.Text.Json;
using KeyboardWtf.Services.Connectors;
using Xunit;
namespace KeyboardWtf.Tests;

public sealed class ConnectorExplicitTargetTests
{
    const string Supporting = "aaaaaaaa11111111", Winner = "cccccccc33333333";
    const string Human = "Find the BioCyclic winning email and tell me the prize.";
    const string Prize = "BioCyclic won Top 10 Best Performers.";
    static JsonElement Receipt(object data, bool partial=false)=>JsonSerializer.SerializeToElement(new {state="completed",verified=true,result=new{source="external_untrusted",truncated=partial,data}});
    static object Answer(string status,string target,string[] references,bool legacy=false)=>legacy
        ? new {action="answer",capability="",arguments_json="",answer=Prize,references,partial=false}
        : (object)new {action="answer",capability="",arguments_json="",answer=Prize,references,partial=false,goal_status=status,target_reference=target};
    static object Execute(string id)=>new {action="execute",capability="mail_read",arguments_json=JsonSerializer.Serialize(new{id}),answer="",references=Array.Empty<string>(),partial=true};
    static JsonElement Mail(string id,string thread,string text)=>Receipt(new{id,threadId=thread,contentTruncated=false,headers=new[]{new{name="From",value="Devpost <fixture@example.test>"},new{name="Subject",value="BioCyclic"}},text});
    sealed class Trace
    {
        internal int Models,Commits;internal List<string> Calls=new();internal List<string> Prompts=new();internal List<string> Queries=new();
        internal int Revision;internal CancellationTokenSource Stop=new();internal bool AdvanceAfterWinner,CancelAtAnswer;
        internal async Task<ConnectorReadReasonerResult> Run(object final,string provider="gmail",string request=Human,string initialQuery="BioCyclic",bool nativeDay=false,object rejected=null)
        {
            var proposals=new Queue<string>();if(!nativeDay){proposals.Enqueue(JsonSerializer.Serialize(Execute("11111111")));proposals.Enqueue(JsonSerializer.Serialize(Execute("33333333")));}
            if(rejected!=null)proposals.Enqueue(rejected is string raw?raw:JsonSerializer.Serialize(rejected));proposals.Enqueue(JsonSerializer.Serialize(final));
            var input=new ConnectorReadReasonerInput(request,[new("mail_search","Search",new Dictionary<string,string>{{"query","Gmail query"}}),new("mail_read","Read",new Dictionary<string,string>{{"id","Observed ID"}})])
            {Provider=provider,InitialCapability="mail_search",InitialArguments=new Dictionary<string,JsonElement>{{"query",JsonSerializer.SerializeToElement(initialQuery)}},RequestUtc=DateTimeOffset.Parse("2026-10-12T00:00:00Z"),RequestTimeZoneId="India Standard Time",
                Guidance=()=>new(Revision,Revision==0?"":"Focus on the winning message."){Instructions=Revision==0?[]:["Focus on the winning message."]},
                TryCommit=(candidate,revision)=>{Assert.Equal(Revision,revision);Assert.Equal(revision,candidate.GoalRevision);Commits++;return true;}};
            return await ConnectorReadReasoner.RunAsync(input,(_,prompt,_,_,_)=>{Models++;Prompts.Add(prompt);if(CancelAtAnswer&&proposals.Count==1)Stop.Cancel();return Task.FromResult(proposals.Dequeue());},
                (cap,args,_)=>{Calls.Add(cap);Assert.True(cap is "mail_search" or "mail_read");if(cap=="mail_search"){var query=args["query"].GetString();Queries.Add(query);return Task.FromResult(Receipt(new{queryUsed=query,messages=new[]{new{id="11111111",threadId=Supporting},new{id="33333333",threadId=Winner}},sampledMessages=new[]{new{id="11111111",threadId=Supporting,from="fixture@example.test",subject="BioCyclic",date="Mon, 12 Oct 2026 05:30:00 +0530"},new{id="33333333",threadId=Winner,from="fixture@example.test",subject="BioCyclic",date="Mon, 12 Oct 2026 05:30:00 +0530"}},metadataSample=new{requested=2,returned=2,complete=true},pageItemCount=2,hasMore=false}));}
                    var id=args["id"].GetString();Assert.True(id is "11111111" or "33333333");if(id=="33333333"&&AdvanceAfterWinner)Revision=1;return Task.FromResult(id=="11111111"?Mail(id,Supporting,"A BioCyclic notification."):Mail(id,Winner,Prize));},null,Stop.Token);
        }
    }
    static ConnectorSessionResult Retain(ConnectorReadReasonerResult result)
    {
        var refs=result.TargetReference.Length==0?result.EvidenceReferences:new[]{result.TargetReference}.Concat(result.EvidenceReferences.Where(r=>r!=result.TargetReference)).ToArray();
        return ConnectorSessionResult.Create("task","gmail","account","reader@example.test",Human,Receipt(new{answer=result.Answer},result.Partial),refs.Select(r=>result.EvidenceReceipts[int.Parse(r[1..])-1]),true,refs.Select(r=>result.EvidenceCapabilities[int.Parse(r[1..])-1]).ToArray(),
            goalRevision:result.GoalRevision,acceptedReadGuidance:result.GoalRevision==0?[]:["Focus on the winning message."],evidenceGoalRevisions:refs.Select(r=>result.EvidenceGoalRevisions[int.Parse(r[1..])-1]).ToArray(),allowExactResourceSelection:result.TargetReference.Length>0,goalStatus:result.GoalStatus,targetReference:result.TargetReference);
    }
    [Fact] public async Task Explicit_winner_target_is_independent_of_chronological_support_order()
    {var t=new Trace();var r=await t.Run(Answer("matched","r3",["r2","r3"]));Assert.Equal(new[]{"r2","r3"},r.EvidenceReferences);Assert.Equal("r3",r.TargetReference);Assert.Equal("matched",r.GoalStatus);Assert.Equal("evidence_available",r.GoalOutcome);Assert.EndsWith("#all/"+Winner,Retain(r).ExactMailUrl());Assert.Equal(3,t.Models);Assert.Equal(3,t.Calls.Count);}
    [Theory][InlineData("not_found")][InlineData("ambiguous")][InlineData("partial")][InlineData("matched")]
    public async Task No_selected_target_never_opens_a_supporting_object_even_with_one_citation(string status)
    {var r=await new Trace().Run(Answer(status,"",["r2"]));Assert.Equal("evidence_available",r.GoalOutcome);var c=Retain(r);Assert.Null(c.PrimaryThread);Assert.Null(c.ExactMailUrl());Assert.Null(c.ExactResourceUrl());Assert.Equal(Prize,c.Answer);Assert.Single(c.Resources);}
    [Fact] public async Task Legacy_answer_does_not_select_by_supporting_citation_order()
    {var r=await new Trace().Run(Answer("matched","r3",["r2","r3"],true));Assert.Equal("",r.TargetReference);Assert.Null(Retain(r).ExactMailUrl());}
    [Theory][InlineData("not_cited")][InlineData("unknown_ref")][InlineData("preview_target")][InlineData("negative_target")][InlineData("ambiguous_target")][InlineData("unknown_status")][InlineData("duplicate_goal")][InlineData("unknown_field")]
    public async Task Invalid_target_or_shape_repairs_without_extra_native_execution(string scenario)
    {
        object rejected=scenario switch{"not_cited"=>Answer("matched","r3",["r2"]),"unknown_ref"=>Answer("matched","r8",["r2","r3"]),"preview_target"=>Answer("matched","r1",["r1","r3"]),"negative_target"=>Answer("not_found","r3",["r3"]),"ambiguous_target"=>Answer("ambiguous","r3",["r3"]),"unknown_status"=>Answer("success","r3",["r3"]),_=>JsonSerializer.Serialize(Answer("matched","r3",["r3"]))};
        if(scenario=="duplicate_goal")rejected=((string)rejected).Replace("\"goal_status\":\"matched\"","\"goal_status\":\"matched\",\"goal_status\":\"matched\"");
        if(scenario=="unknown_field")rejected=((string)rejected)[..^1]+",\"unsupported\":true}";
        var t=new Trace();var r=await t.Run(Answer("partial","",["r2","r3"]),rejected:rejected);Assert.Equal(4,t.Models);Assert.Equal(3,t.Calls.Count);Assert.Contains("validation_error",t.Prompts.Last());Assert.Equal("",r.TargetReference);Assert.Null(Retain(r).ExactMailUrl());Assert.Equal(1,t.Commits);
    }
    [Fact] public async Task Wrong_provider_cannot_validate_a_gmail_target()
    {var t=new Trace();var r=await t.Run(Answer("partial","",["r2","r3"]),provider:"googledocs",rejected:Answer("matched","r3",["r3"]));Assert.Equal(4,t.Models);Assert.Contains("Selected target is not one observed typed object.",t.Prompts.Last());Assert.Null(Retain(r).ExactMailUrl());}
    [Fact] public async Task Old_complete_body_can_support_current_guidance_target_without_duplicate_read()
    {var t=new Trace{AdvanceAfterWinner=true};var r=await t.Run(Answer("matched","r3",["r2","r3"]));Assert.Equal(1,r.GoalRevision);Assert.Equal("r3",r.TargetReference);Assert.True(r.Partial);Assert.All(r.EvidenceGoalRevisions,v=>Assert.Equal(0,v));Assert.Equal(3,t.Calls.Count);Assert.EndsWith("#all/"+Winner,Retain(r).ExactMailUrl());}
    [Fact] public async Task Cancelled_model_reply_cannot_commit_target_or_answer()
    {var t=new Trace{CancelAtAnswer=true};await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>t.Run(Answer("matched","r3",["r3"])));Assert.Equal(0,t.Commits);Assert.Equal(3,t.Calls.Count);}
    [Fact] public async Task Frozen_today_native_preparation_still_uses_one_synthesis_and_exact_scope()
    {const string request="Check today's emails for a response.";var scope=ConnectorMailDateScope.Freeze(request,DateTimeOffset.Parse("2026-10-12T00:00:00Z"),"India Standard Time");var t=new Trace();var r=await t.Run(Answer("matched","r3",["r2","r3"]),request:request,initialQuery:scope.Query,nativeDay:true);Assert.Equal(1,t.Models);Assert.Equal(3,t.Calls.Count);Assert.Equal(scope.Query,Assert.Single(t.Queries));Assert.Equal("r3",r.TargetReference);}
    [Fact] public void Multi_object_provider_page_is_not_a_unique_target()
    {var receipt=Receipt(new{files=new[]{new{id="file1",name="one",webViewLink="https://drive.google.com/file/d/file1/view"},new{id="file2",name="two",webViewLink="https://drive.google.com/file/d/file2/view"}}});Assert.False(ConnectorReadTargetSelection.HasUniqueObject("googledrive","drive_search",new Dictionary<string,JsonElement>(),receipt));}
    [Fact] public void Changed_normalized_read_identity_cannot_be_a_target()
    {Assert.False(ConnectorReadTargetSelection.HasUniqueObject("gmail","mail_read",new Dictionary<string,JsonElement>{{"id",JsonSerializer.SerializeToElement("11111111")}},Mail("33333333",Winner,Prize)));}
    [Fact] public void Direct_native_specific_object_retention_keeps_existing_no_ai_reference()
    {var read=Receipt(new{documentId="literalDocument",body=new{content=new[]{new{paragraph=new{elements=new[]{new{textRun=new{content="Actual document content."}}}}}}}});var c=ConnectorSessionResult.Create("task","googledocs","account","reader@example.test","Read literalDocument",read,[read],evidenceCapabilities:["docs_read"]);Assert.Equal("https://docs.google.com/document/d/literalDocument/edit",c.ExactResourceUrl());Assert.True(c.ExactResourceSelectionAllowed);}
    [Fact] public async Task Literal_native_document_keeps_reference_under_legacy_synthesis_without_new_planning()
    {
        const string id="literalDocument";var calls=0;var models=0;
        var read=Receipt(new{documentId=id,body=new{content=new[]{new{paragraph=new{elements=new[]{new{textRun=new{content="Actual document content."}}}}}}}});
        var input=new ConnectorReadReasonerInput("Read document "+id,[new("docs_read","Read document",new Dictionary<string,string>{{"id","Exact document ID"}})])
            {Provider="googledocs",InitialCapability="docs_read",InitialArguments=new Dictionary<string,JsonElement>{{"id",JsonSerializer.SerializeToElement(id)}}};
        var result=await ConnectorReadReasoner.RunAsync(input,(_,_,_,_,_)=>{models++;return Task.FromResult(JsonSerializer.Serialize(Answer("matched","",["r1"],true)));},(cap,args,_)=>{calls++;Assert.Equal("docs_read",cap);Assert.Equal(id,args["id"].GetString());return Task.FromResult(read);},null,CancellationToken.None);
        Assert.Equal(1,calls);Assert.Equal(1,models);Assert.Equal("r1",result.TargetReference);
        var retained=ConnectorSessionResult.Create("task","googledocs","account","reader@example.test",input.Request,Receipt(new{answer=result.Answer}),result.EvidenceReceipts,evidenceCapabilities:result.EvidenceCapabilities,allowExactResourceSelection:result.TargetReference.Length>0,goalStatus:result.GoalStatus,targetReference:result.TargetReference);
        Assert.Equal("https://docs.google.com/document/d/"+id+"/edit",retained.ExactResourceUrl());
    }
    [Fact] public async Task Multiple_literal_document_objects_do_not_select_by_legacy_citation_order()
    {
        const string one="literalOne",two="literalTwo";var calls=0;var models=0;
        JsonElement Doc(string id)=>Receipt(new{documentId=id,body=new{content="Actual document content."}});
        var responses=new Queue<string>(new[]{JsonSerializer.Serialize(new{action="execute",capability="docs_read",arguments_json=JsonSerializer.Serialize(new{id=two}),answer="",references=Array.Empty<string>(),partial=false}),JsonSerializer.Serialize(Answer("matched","",["r1","r2"],true))});
        var input=new ConnectorReadReasonerInput("Read documents "+one+" and "+two,[new("docs_read","Read document",new Dictionary<string,string>{{"id","Exact document ID"}})])
            {Provider="googledocs",InitialCapability="docs_read",InitialArguments=new Dictionary<string,JsonElement>{{"id",JsonSerializer.SerializeToElement(one)}}};
        var result=await ConnectorReadReasoner.RunAsync(input,(_,_,_,_,_)=>{models++;return Task.FromResult(responses.Dequeue());},(cap,args,_)=>{calls++;Assert.Equal("docs_read",cap);return Task.FromResult(Doc(args["id"].GetString()));},null,CancellationToken.None);
        Assert.Equal(2,calls);Assert.Equal(2,models);Assert.Equal("",result.TargetReference);Assert.Equal(new[]{"r1","r2"},result.EvidenceReferences);
    }
    [Fact] public void Cross_provider_document_shape_cannot_be_a_target()
    {var read=Receipt(new{documentId="literalDocument",body=new{content="Actual document content."}});Assert.False(ConnectorReadTargetSelection.HasUniqueObject("googledrive","docs_read",new Dictionary<string,JsonElement>{{"id",JsonSerializer.SerializeToElement("literalDocument")}},read));}
    [Fact] public void Unverified_content_cannot_be_a_target()
    {var read=JsonSerializer.SerializeToElement(new{state="completed",verified=false,result=new{data=new{id="11111111",threadId=Winner,text=Prize}}});Assert.False(ConnectorReadTargetSelection.HasUniqueObject("gmail","mail_read",new Dictionary<string,JsonElement>{{"id",JsonSerializer.SerializeToElement("11111111")}},read));}
    [Theory][InlineData(false,false)][InlineData(false,true)][InlineData(true,false)][InlineData(true,true)]
    public async Task Two_human_literal_documents_never_select_one_legacy_citation(bool readSecond,bool canonicalUrls)
    {
        const string one="literalOne",two="literalTwo";var calls=0;var models=0;
        JsonElement Doc(string id)=>Receipt(new{documentId=id,body=new{content="Actual document content."}});
        var responses=new Queue<string>();if(readSecond)responses.Enqueue(JsonSerializer.Serialize(new{action="execute",capability="docs_read",arguments_json=JsonSerializer.Serialize(new{id=two}),answer="",references=Array.Empty<string>(),partial=false}));
        responses.Enqueue(JsonSerializer.Serialize(Answer("matched","",["r1"],true)));
        var request=canonicalUrls?"Read https://docs.google.com/document/d/"+one+"/edit and https://docs.google.com/document/d/"+two+"/edit":"Read documents "+one+" and "+two;
        var input=new ConnectorReadReasonerInput(request,[new("docs_read","Read document",new Dictionary<string,string>{{"id","Exact document ID"}})])
            {Provider="googledocs",InitialCapability="docs_read",InitialArguments=new Dictionary<string,JsonElement>{{"id",JsonSerializer.SerializeToElement(one)}}};
        var result=await ConnectorReadReasoner.RunAsync(input,(_,_,_,_,_)=>{models++;return Task.FromResult(responses.Dequeue());},(cap,args,_)=>{calls++;return Task.FromResult(Doc(args["id"].GetString()));},null,CancellationToken.None);
        Assert.Equal(readSecond?2:1,calls);Assert.Equal(readSecond?2:1,models);Assert.Equal("",result.TargetReference);Assert.Equal(new[]{"r1"},result.EvidenceReferences);
    }
    [Fact] public async Task Canonical_urls_for_two_tabs_of_same_document_keep_one_literal_target()
    {
        const string id="literalOne";var calls=0;var models=0;
        var read=Receipt(new{documentId=id,body=new{content="Actual document content."}});
        var request="Read https://docs.google.com/document/d/"+id+"/edit?tab=t.0 and https://docs.google.com/document/d/"+id+"/edit?tab=t.1";
        var input=new ConnectorReadReasonerInput(request,[new("docs_read","Read document",new Dictionary<string,string>{{"id","Exact document ID"}})])
            {Provider="googledocs",InitialCapability="docs_read",InitialArguments=new Dictionary<string,JsonElement>{{"id",JsonSerializer.SerializeToElement(id)}}};
        var result=await ConnectorReadReasoner.RunAsync(input,(_,_,_,_,_)=>{models++;return Task.FromResult(JsonSerializer.Serialize(Answer("matched","",["r1"],true)));},(_,_,_)=>{calls++;return Task.FromResult(read);},null,CancellationToken.None);
        Assert.Equal(1,calls);Assert.Equal(1,models);Assert.Equal("r1",result.TargetReference);
    }
    [Theory][InlineData(false)][InlineData(true)] public async Task Single_literal_form_keeps_legacy_native_reference(bool canonicalUrl)
    {
        const string id="literalForm";var calls=0;var models=0;
        var read=Receipt(new{formId=id,info=new{title="Existing form"},items=Array.Empty<object>()});
        var request=canonicalUrl?"Read https://docs.google.com/forms/d/"+id+"/edit":"Read form "+id;
        var input=new ConnectorReadReasonerInput(request,[new("forms_read","Read form",new Dictionary<string,string>{{"id","Exact form ID"}})])
            {Provider="googleforms",InitialCapability="forms_read",InitialArguments=new Dictionary<string,JsonElement>{{"id",JsonSerializer.SerializeToElement(id)}}};
        var result=await ConnectorReadReasoner.RunAsync(input,(_,_,_,_,_)=>{models++;return Task.FromResult(JsonSerializer.Serialize(Answer("matched","",["r1"],true)));},(_,_,_)=>{calls++;return Task.FromResult(read);},null,CancellationToken.None);
        Assert.Equal(1,calls);Assert.Equal(1,models);Assert.Equal("r1",result.TargetReference);
    }
    [Theory][InlineData("missing_truncated")][InlineData("missing_hasMore")][InlineData("missing_count")]
    [InlineData("true_truncated")][InlineData("true_hasMore")][InlineData("wrong_count")]
    [InlineData("wrong_cursor")][InlineData("incomplete_metadata")][InlineData("incomplete_coverage")]
    public void One_candidate_preview_without_known_complete_normalized_scope_cannot_be_target(string scenario)
    {
        var data=new Dictionary<string,object>{{"messages",new[]{new{id="11111111",threadId=Winner}}},{"hasMore",false},{"pageItemCount",1}};
        var result=new Dictionary<string,object>{{"data",data},{"truncated",false}};
        switch(scenario)
        {
            case "missing_truncated":result.Remove("truncated");break;
            case "missing_hasMore":data.Remove("hasMore");break;
            case "missing_count":data.Remove("pageItemCount");break;
            case "true_truncated":result["truncated"]=true;break;
            case "true_hasMore":data["hasMore"]=true;break;
            case "wrong_count":data["pageItemCount"]=2;break;
            case "wrong_cursor":data["nextPageToken"]=1;break;
            case "incomplete_metadata":data["metadataSample"]=new{complete=false};break;
            case "incomplete_coverage":result["coverage"]=new{complete=false};break;
        }
        var read=JsonSerializer.SerializeToElement(new{state="completed",verified=true,result});
        Assert.False(ConnectorReadTargetSelection.HasUniqueObject("gmail","mail_search",new Dictionary<string,JsonElement>(),read));
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void Known_complete_single_candidate_metadata_can_be_target_with_omitted_or_empty_cursor(bool emptyCursor)
    {
        var data=new Dictionary<string,object>{{"messages",new[]{new{id="11111111",threadId=Winner}}},{"hasMore",false},{"pageItemCount",1}};
        if(emptyCursor)data["nextPageToken"]="";
        var read=Receipt(data);
        Assert.True(ConnectorReadTargetSelection.HasUniqueObject("gmail","mail_search",new Dictionary<string,JsonElement>(),read));
    }
}