namespace KeyboardWtf.Tests;

using System.Text.Json;
using KeyboardWtf.Services.Connectors;
using Xunit;

public sealed class ConnectorResourceReferenceTests
{
    private static JsonElement Receipt(object data, string state = "completed", bool verified = true, bool partial = false)
        => JsonSerializer.SerializeToElement(new { state, verified, result = new { data, truncated = partial } });

    private static ConnectorSessionResult Context(string provider, string[] capabilities, params JsonElement[] evidence)
        => Create(provider, capabilities, evidence);

    private static ConnectorSessionResult Create(string provider, string[] capabilities, JsonElement[] evidence,
        bool ranked = true, string label = "reader@example.test", string answer = "A useful verified answer.")
        => ConnectorSessionResult.Create("task-reference", provider, "account-reference", label, "Find the requested object.",
            Receipt(new { answer }), evidence, rankedPrimaryEvidence: ranked, evidenceCapabilities: capabilities);

    private static void AssertSingle(ConnectorSessionResult context, string kind, string id, string url)
    {
        var reference = Assert.Single(context.ObjectReferences);
        Assert.Equal(kind, reference.Kind);
        Assert.Equal(id, reference.Id);
        Assert.Equal(url, reference.Url);
        Assert.Equal(url, context.ExactResourceUrl());
    }

    [Fact]
    public void Gmail_keeps_the_existing_exact_account_thread_url()
    {
        var context = Context("gmail", ["mail_read"], Receipt(new
        {
            id = "aabbccdd11", threadId = "aabbccdd22", text = "PRIVATE MESSAGE BODY",
            headers = new[] { new { name = "Subject", value = "Verified prize" }, new { name = "Date", value = "Sat, 18 Jul 2026 17:58:00 +0530" } }
        }));
        var url = "https://mail.google.com/mail/?authuser=reader%40example.test#all/aabbccdd22";
        Assert.Equal(url, context.ExactMailUrl());
        Assert.Equal(url, context.ExactResourceUrl());
        Assert.DoesNotContain("PRIVATE MESSAGE BODY", JsonSerializer.Serialize(context));
    }

    [Fact]
    public void Existing_Gmail_creation_without_new_provenance_remains_compatible()
    {
        var receipt = Receipt(new { id = "aabbccdd11", threadId = "aabbccdd22", headers = Array.Empty<object>() });
        var context = ConnectorSessionResult.Create("task-reference", "gmail", "account-reference", "reader@example.test",
            "Read this email", Receipt(new { answer = "Verified reply" }), [receipt], rankedPrimaryEvidence: true);
        Assert.Equal("https://mail.google.com/mail/?authuser=reader%40example.test#all/aabbccdd22", context.ExactMailUrl());
    }

    [Theory]
    [InlineData("drive_read", false)]
    [InlineData("drive_search", true)]
    public void Drive_retains_only_the_observed_exact_file(string capability, bool search)
    {
        var file = new { id = "verifiedFileId", name = "Project plan", mimeType = "application/pdf",
            webViewLink = "https://drive.google.com/file/d/verifiedFileId/view", modifiedTime = "2026-10-10T10:00:00Z",
            description = "PRIVATE DESCRIPTION with https://example.test/never-open" };
        var data = search ? (object)new { files = new[] { file } } : file;
        var context = Context("googledrive", [capability], Receipt(data));
        AssertSingle(context, "drive_file", "verifiedFileId", file.webViewLink);
        Assert.Equal("Project plan", context.ObjectReferences[0].Title);
        Assert.DoesNotContain("PRIVATE DESCRIPTION", JsonSerializer.Serialize(context));
        Assert.DoesNotContain("https://example.test/never-open", JsonSerializer.Serialize(context));
    }

    [Fact]
    public void Drive_can_retain_a_matching_native_document_link_without_claiming_content()
    {
        var context = Context("googledrive", ["drive_read"], Receipt(new
        {
            id = "nativeDocumentId", name = "Native document", mimeType = "application/vnd.google-apps.document",
            webViewLink = "https://docs.google.com/document/d/nativeDocumentId/edit"
        }));
        Assert.Equal("https://docs.google.com/document/d/nativeDocumentId/edit", context.ExactResourceUrl());
        Assert.Equal("nativeDocumentId", Assert.Single(context.ObjectReferences).Id);
    }

    [Theory]
    [InlineData("https://evil.test/file/d/verifiedFileId/view")]
    [InlineData("https://drive.google.com.evil.test/file/d/verifiedFileId/view")]
    [InlineData("https://drive.google.com@evil.test/file/d/verifiedFileId/view")]
    [InlineData("https://reader@drive.google.com/file/d/verifiedFileId/view")]
    [InlineData("http://drive.google.com/file/d/verifiedFileId/view")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://drive.google.com:444/file/d/verifiedFileId/view")]
    [InlineData("https://drive.google.com/drive/my-drive")]
    [InlineData("https://drive.google.com/file/d/differentFileId/view")]
    [InlineData("https://docs.google.com/document/d/differentFileId/edit")]
    [InlineData("https://drive.google.com/file/d/verifiedFileId/view/../../evil")]
    public void Drive_never_opens_unsafe_home_or_mismatched_links(string url)
    {
        var context = Context("googledrive", ["drive_read"], Receipt(new { id = "verifiedFileId", name = "File", webViewLink = url }));
        Assert.Null(context.ExactResourceUrl());
    }

    [Fact]
    public void Docs_constructs_an_editor_link_from_the_top_level_verified_document_identity()
    {
        var context = Context("googledocs", ["docs_read"], Receipt(new
        {
            documentId = "verifiedDocumentId", title = "A document", body = new
            {
                content = new[] { new { paragraph = new { elements = new[] { new { textRun = new { content = "PRIVATE DOCUMENT BODY" } } } } } },
                documentId = "untrustedNestedId", url = "https://evil.test/document"
            }
        }));
        AssertSingle(context, "document", "verifiedDocumentId", "https://docs.google.com/document/d/verifiedDocumentId/edit");
        Assert.Equal("A document", context.ObjectReferences[0].Title);
        Assert.DoesNotContain("PRIVATE DOCUMENT BODY", JsonSerializer.Serialize(context));
        Assert.DoesNotContain("untrustedNestedId", JsonSerializer.Serialize(context));
    }

    [Fact]
    public void Forms_opens_the_editor_not_the_respondent_link_and_drops_questions()
    {
        var context = Context("googleforms", ["forms_read"], Receipt(new
        {
            formId = "verifiedFormId", info = new { title = "A survey", description = "PRIVATE FORM DESCRIPTION" },
            responderUri = "https://docs.google.com/forms/d/e/unrelatedPublishedId/viewform",
            items = new[] { new { title = "PRIVATE FORM QUESTION", itemId = "anotherObject" } }
        }));
        AssertSingle(context, "form", "verifiedFormId", "https://docs.google.com/forms/d/verifiedFormId/edit");
        Assert.Equal("A survey", context.ObjectReferences[0].Title);
        var serialized = JsonSerializer.Serialize(context);
        Assert.DoesNotContain("PRIVATE FORM", serialized);
        Assert.DoesNotContain("unrelatedPublishedId", serialized);
        Assert.DoesNotContain("anotherObject", serialized);
    }

    [Theory]
    [InlineData("https://calendar.google.com/calendar/event?eid=verifiedEventToken")]
    [InlineData("https://www.google.com/calendar/event?eid=verifiedEventToken")]
    public void Calendar_uses_only_an_observed_event_page(string url)
    {
        var context = Context("googlecalendar", ["calendar_list"], Receipt(new
        {
            items = new[] { new { id = "verifiedEventId", summary = "Project meeting", htmlLink = url,
                start = new { dateTime = "2026-10-12T10:00:00+05:30" }, end = new { dateTime = "2026-10-12T11:00:00+05:30" },
                description = "PRIVATE EVENT NOTES", attendees = new[] { new { email = "private@example.test" } } } }
        }));
        AssertSingle(context, "calendar_event", "verifiedEventId", url);
        Assert.Equal("Project meeting", context.ObjectReferences[0].Title);
        Assert.DoesNotContain("PRIVATE EVENT NOTES", JsonSerializer.Serialize(context));
        Assert.DoesNotContain("private@example.test", JsonSerializer.Serialize(context));
    }

    [Theory]
    [InlineData("https://calendar.google.com/calendar/u/0/r")]
    [InlineData("https://www.google.com/search?q=event")]
    [InlineData("https://calendar.google.com.evil.test/calendar/event?eid=x")]
    [InlineData("https://calendar.google.com/calendar/event")]
    [InlineData("http://calendar.google.com/calendar/event?eid=x")]
    [InlineData("https://reader@calendar.google.com/calendar/event?eid=x")]
    public void Calendar_rejects_home_pages_missing_identity_and_unsafe_event_links(string url)
    {
        var context = Context("googlecalendar", ["calendar_list"], Receipt(new { items = new[] { new { id = "verifiedEventId", htmlLink = url } } }));
        Assert.Null(context.ExactResourceUrl());
    }

    [Fact]
    public void GitHub_repository_uses_the_matching_resource_path()
    {
        var context = Context("github", ["github_repos"], Receipt(new[]
        {
            new { id = 123, full_name = "owner/repository", name = "repository", html_url = "https://github.com/owner/repository", description = "PRIVATE REPO DESCRIPTION" }
        }));
        AssertSingle(context, "repository", "owner/repository", "https://github.com/owner/repository");
        Assert.DoesNotContain("PRIVATE REPO DESCRIPTION", JsonSerializer.Serialize(context));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GitHub_keeps_issue_and_pull_request_objects_distinct(bool pullRequest)
    {
        var issue = new Dictionary<string, object>
        {
            ["id"] = 456, ["number"] = 7, ["title"] = "Verified discussion", ["html_url"] = "https://github.com/owner/repository/issues/7",
            ["body"] = "PRIVATE ISSUE BODY", ["created_at"] = "2026-10-10T10:00:00Z"
        };
        if (pullRequest) issue["pull_request"] = new { html_url = "https://github.com/owner/repository/pull/7" };
        var context = Context("github", ["github_issues"], Receipt(new[] { issue }));
        var reference = Assert.Single(context.ObjectReferences);
        Assert.Equal(pullRequest ? "pull_request" : "issue", reference.Kind);
        Assert.Equal(pullRequest ? "https://github.com/owner/repository/pull/7" : "https://github.com/owner/repository/issues/7", context.ExactResourceUrl());
        Assert.Equal("Verified discussion", reference.Title);
        Assert.DoesNotContain("PRIVATE ISSUE BODY", JsonSerializer.Serialize(context));
    }

    [Theory]
    [InlineData("https://github.com/")]
    [InlineData("https://github.com/other/repository")]
    [InlineData("https://github.com/owner/repository/issues/7")]
    [InlineData("https://github.com.evil.test/owner/repository")]
    [InlineData("https://owner@github.com/owner/repository")]
    public void GitHub_repository_link_must_match_the_exact_repository(string url)
    {
        var context = Context("github", ["github_repos"], Receipt(new[] { new { full_name = "owner/repository", html_url = url } }));
        Assert.Null(context.ExactResourceUrl());
    }

    [Theory]
    [InlineData("https://github.com/owner/repository/issues/8", false)]
    [InlineData("https://github.com/owner/repository/pull/8", true)]
    [InlineData("https://github.com/other/repository/pull/7", true)]
    [InlineData("https://github.com/", true)]
    public void GitHub_issue_and_PR_urls_must_match_number_and_repository(string url, bool pullRequest)
    {
        var issue = new Dictionary<string, object> { ["number"] = 7, ["title"] = "Discussion", ["html_url"] = "https://github.com/owner/repository/issues/7" };
        if (pullRequest) issue["pull_request"] = new { html_url = url };
        else issue["html_url"] = url;
        var context = Context("github", ["github_issues"], Receipt(new[] { issue }));
        Assert.Empty(context.ObjectReferences);
        Assert.Null(context.ExactResourceUrl());
    }

    [Fact]
    public void YouTube_video_url_comes_from_the_verified_video_identity_not_snippet_links()
    {
        var context = Context("youtube", ["youtube_search"], Receipt(new
        {
            items = new[] { new { id = new { kind = "youtube#video", videoId = "AbCdEf12345" }, snippet = new
                { title = "Project demo", publishedAt = "2026-10-10T10:00:00Z", description = "PRIVATE VIDEO DESCRIPTION", url = "https://evil.test/video" } } }
        }));
        AssertSingle(context, "video", "AbCdEf12345", "https://www.youtube.com/watch?v=AbCdEf12345");
        Assert.Equal("Project demo", context.ObjectReferences[0].Title);
        Assert.DoesNotContain("PRIVATE VIDEO DESCRIPTION", JsonSerializer.Serialize(context));
        Assert.DoesNotContain("evil.test", JsonSerializer.Serialize(context));
    }

    [Fact]
    public void YouTube_channel_url_comes_from_the_verified_channel_identity()
    {
        const string channel = "UCabcdefghijklmnopqrstuv";
        var context = Context("youtube", ["youtube_channel"], Receipt(new { items = new[] { new { id = channel, snippet = new { title = "My channel" } } } }));
        AssertSingle(context, "channel", channel, "https://www.youtube.com/channel/" + channel);
    }

    [Theory]
    [InlineData("../other")]
    [InlineData("AbCdEf12345&redirect=evil")]
    [InlineData("https://evil.test/video")]
    [InlineData("")]
    public void YouTube_never_constructs_links_from_invalid_video_ids(string id)
    {
        var context = Context("youtube", ["youtube_search"], Receipt(new { items = new[] { new { id = new { videoId = id }, snippet = new { title = "Video" } } } }));
        Assert.Null(context.ExactResourceUrl());
    }

    [Fact]
    public void Slack_uses_a_matching_observed_message_permalink()
    {
        const string url = "https://workspace.slack.com/archives/C12345678/p1712345678123456";
        var context = Context("slack", ["slack_search"], Receipt(new
        {
            messages = new { matches = new[] { new { channel = new { id = "C12345678", name = "project" }, ts = "1712345678.123456",
                permalink = url, text = "PRIVATE SLACK MESSAGE" } } }
        }));
        var reference = Assert.Single(context.ObjectReferences);
        Assert.Equal("slack_message", reference.Kind);
        Assert.Equal(url, context.ExactResourceUrl());
        Assert.DoesNotContain("PRIVATE SLACK MESSAGE", JsonSerializer.Serialize(context));
    }

    [Theory]
    [InlineData("https://workspace.slack.com/")]
    [InlineData("https://workspace.slack.com/archives/C87654321/p1712345678123456")]
    [InlineData("https://workspace.slack.com/archives/C12345678/p1712345678000000")]
    [InlineData("https://workspace.slack.com.evil.test/archives/C12345678/p1712345678123456")]
    [InlineData("https://reader@workspace.slack.com/archives/C12345678/p1712345678123456")]
    [InlineData("http://workspace.slack.com/archives/C12345678/p1712345678123456")]
    public void Slack_rejects_home_unsafe_or_mismatched_message_links(string url)
    {
        var context = Context("slack", ["slack_search"], Receipt(new { messages = new { matches = new[]
            { new { channel = new { id = "C12345678" }, ts = "1712345678.123456", permalink = url } } } }));
        Assert.Null(context.ExactResourceUrl());
    }

    [Fact]
    public void Slack_history_without_a_real_permalink_cannot_fall_back_to_a_channel_home()
    {
        var context = Context("slack", ["slack_history"], Receipt(new { messages = new[]
            { new { ts = "1712345678.123456", text = "PRIVATE MESSAGE", channel = "C12345678" } } }));
        Assert.Null(context.ExactResourceUrl());
    }

    [Theory]
    [InlineData("failed", true)]
    [InlineData("cancelled", true)]
    [InlineData("outcome_unknown", true)]
    [InlineData("completed", false)]
    public void Failed_cancelled_unknown_or_unverified_reads_never_authorize_an_open(string state, bool verified)
    {
        var context = Context("googledocs", ["docs_read"], Receipt(new { documentId = "verifiedDocumentId", title = "Document" }, state, verified));
        Assert.Empty(context.ObjectReferences);
        Assert.Null(context.ExactResourceUrl());
    }

    [Fact]
    public void Bounded_partial_content_can_retain_the_exact_observed_object_but_not_its_body()
    {
        var context = Context("googledocs", ["docs_read"], Receipt(new { documentId = "verifiedDocumentId", title = "Document", body = "PRIVATE PARTIAL BODY" }, partial: true));
        AssertSingle(context, "document", "verifiedDocumentId", "https://docs.google.com/document/d/verifiedDocumentId/edit");
        Assert.True(context.Partial);
        Assert.DoesNotContain("PRIVATE PARTIAL BODY", JsonSerializer.Serialize(context));
    }

    [Theory]
    [InlineData("googledocs", "mail_read")]
    [InlineData("googledocs", "unknown_read")]
    [InlineData("googleforms", "docs_read")]
    [InlineData("github", "docs_read")]
    public void Resource_adapter_requires_matching_provider_and_capability_provenance(string provider, string capability)
    {
        var context = Context(provider, [capability], Receipt(new { documentId = "verifiedDocumentId", formId = "verifiedFormId", title = "Not enough provenance" }));
        Assert.Empty(context.ObjectReferences);
        Assert.Null(context.ExactResourceUrl());
    }

    [Fact]
    public void Missing_nonmail_provenance_cannot_be_guessed_from_nested_resource_shapes()
    {
        var context = Create("googledocs", [], [Receipt(new { documentId = "verifiedDocumentId", title = "Document" })]);
        Assert.Empty(context.ObjectReferences);
        Assert.Null(context.ExactResourceUrl());
    }

    [Fact]
    public void Malformed_or_missing_evidence_envelopes_do_not_authorize_navigation()
    {
        foreach (var receipt in new[]
        {
            JsonSerializer.SerializeToElement(new { documentId = "verifiedDocumentId" }),
            JsonSerializer.SerializeToElement(new { state = "completed", result = new { data = new { documentId = "verifiedDocumentId" } } }),
            JsonSerializer.SerializeToElement(new { state = "completed", verified = "true", result = new { data = new { documentId = "verifiedDocumentId" } } }),
            JsonSerializer.SerializeToElement(new { state = "completed", verified = true, result = true }),
            JsonSerializer.SerializeToElement(new { state = "completed", verified = true, result = new { data = (object)null } })
        })
        {
            var context = Context("googledocs", ["docs_read"], receipt);
            Assert.Empty(context.ObjectReferences);
            Assert.Null(context.ExactResourceUrl());
        }
    }

    [Fact]
    public void Invalid_or_oversized_document_identity_is_rejected_not_truncated_into_another_target()
    {
        foreach (var id in new[] { "../other", "document/id", "document?id=other", new string('d', 3000), "" })
        {
            var context = Context("googledocs", ["docs_read"], Receipt(new { documentId = id, title = "Document" }));
            Assert.Empty(context.ObjectReferences);
            Assert.Null(context.ExactResourceUrl());
        }
    }

    [Fact]
    public void Arbitrary_body_text_description_and_nested_ids_are_not_navigation_targets()
    {
        var context = Context("googledocs", ["docs_read"], Receipt(new
        {
            title = "No verified top-level identity",
            body = new { documentId = "inventedDocument", url = "https://docs.google.com/document/d/inventedDocument/edit", text = "PRIVATE TEXT" },
            description = new { documentId = "inventedDescription", url = "https://evil.test" }
        }));
        Assert.Empty(context.ObjectReferences);
        Assert.Null(context.ExactResourceUrl());
        Assert.DoesNotContain("PRIVATE TEXT", JsonSerializer.Serialize(context));
    }

    [Fact]
    public void Ranked_first_receipt_with_one_object_wins_over_later_supporting_objects()
    {
        var context = Context("googledocs", ["docs_read", "docs_read"],
            Receipt(new { documentId = "primaryDocument", title = "Primary" }), Receipt(new { documentId = "supportingDocument", title = "Supporting" }));
        Assert.Equal(2, context.ObjectReferences.Length);
        Assert.Equal("https://docs.google.com/document/d/primaryDocument/edit", context.ExactResourceUrl());
    }

    [Fact]
    public void Multiple_objects_inside_the_ranked_first_receipt_remain_ambiguous()
    {
        var context = Context("googledrive", ["drive_search"], Receipt(new { files = new[]
        {
            new { id = "firstFile", webViewLink = "https://drive.google.com/file/d/firstFile/view" },
            new { id = "secondFile", webViewLink = "https://drive.google.com/file/d/secondFile/view" }
        } }));
        Assert.Equal(2, context.ObjectReferences.Length);
        Assert.Null(context.ExactResourceUrl());
    }

    [Fact]
    public void No_ranked_primary_opens_only_a_sole_deduplicated_object()
    {
        var duplicate = Receipt(new { documentId = "sameDocument", title = "Document" });
        var single = Create("googledocs", ["docs_read", "docs_read"], [duplicate, duplicate], ranked: false);
        AssertSingle(single, "document", "sameDocument", "https://docs.google.com/document/d/sameDocument/edit");
        var multiple = Create("googledocs", ["docs_read", "docs_read"],
            [duplicate, Receipt(new { documentId = "anotherDocument", title = "Another" })], ranked: false);
        Assert.Null(multiple.ExactResourceUrl());
    }

    [Fact]
    public void Empty_or_failed_primary_receipt_does_not_promote_a_later_object_to_primary()
    {
        foreach (var first in new[] { Receipt(new { files = Array.Empty<object>() }), Receipt(new { files = Array.Empty<object>() }, state: "failed", verified: false) })
        {
            var context = Context("googledrive", ["drive_search", "drive_read"], first,
                Receipt(new { id = "laterFile", webViewLink = "https://drive.google.com/file/d/laterFile/view" }));
            Assert.Null(context.ExactResourceUrl());
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_candidate_after_the_inspection_limit_cannot_be_hidden_to_establish_uniqueness(bool ranked)
    {
        var files = Enumerable.Range(0, 101).Select(index => (object)(index is 0 or 100
            ? new { id = "file" + index, webViewLink = "https://drive.google.com/file/d/file" + index + "/view" }
            : new { id = "unsupported" + index, webViewLink = "https://drive.google.com/drive/my-drive" })).ToArray();
        var context = Create("googledrive", ["drive_search"], [Receipt(new { files })], ranked: ranked);
        Assert.Single(context.ObjectReferences);
        Assert.Null(context.ExactResourceUrl());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Incomplete_list_coverage_keeps_facts_but_never_guesses_the_selected_object(bool ranked)
    {
        foreach (var coverage in new object[] { new { complete = false }, new { complete = true, hasMore = true },
            new { complete = true, pageItemCount = 2 }, new { complete = true, pageItemCount = "1" }, "malformed" })
        {
            var receipt = JsonSerializer.SerializeToElement(new { state = "completed", verified = true, result = new
                { data = new { files = new[] { new { id = "singleFile", webViewLink = "https://drive.google.com/file/d/singleFile/view" } } }, coverage, truncated = false } });
            var context = Create("googledrive", ["drive_search"], [receipt], ranked: ranked);
            Assert.Single(context.ObjectReferences);
            Assert.Null(context.ExactResourceUrl());
        }
        var clipped = Create("googledrive", ["drive_search"], [Receipt(new { files = new[]
            { new { id = "singleFile", webViewLink = "https://drive.google.com/file/d/singleFile/view" } } }, partial: true)], ranked: ranked);
        Assert.Null(clipped.ExactResourceUrl());
        Assert.True(clipped.Partial);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Invalid_second_candidate_is_not_silently_eliminated_to_pick_the_valid_one(bool ranked)
    {
        foreach (var invalid in new object[] { new { id = "otherFile", webViewLink = "https://drive.google.com/drive/my-drive" },
            new { id = "otherFile", webViewLink = "https://evil.test" }, 7, "unrecognized object" })
        {
            var context = Create("googledrive", ["drive_search"], [Receipt(new { files = new object[]
                { new { id = "singleFile", webViewLink = "https://drive.google.com/file/d/singleFile/view" }, invalid } })], ranked: ranked);
            Assert.Single(context.ObjectReferences);
            Assert.Null(context.ExactResourceUrl());
        }
    }

    [Fact]
    public void Fully_valid_duplicate_list_entries_refer_to_one_object_not_ambiguity()
    {
        var file = new { id = "sameFile", webViewLink = "https://drive.google.com/file/d/sameFile/view" };
        var context = Context("googledrive", ["drive_search"], Receipt(new { files = new[] { file, file } }));
        AssertSingle(context, "drive_file", file.id, file.webViewLink);
    }

    [Theory]
    [InlineData("googledocs", "docs_read")]
    [InlineData("gmail", "mail_read")]
    public void Omitted_ninth_receipt_cannot_establish_unranked_uniqueness(string provider, string capability)
    {
        object Resource(int ordinal) => provider == "gmail"
            ? new { id = "aabbccdd" + ordinal, threadId = "aabbccdd" + ordinal, headers = Array.Empty<object>() }
            : new { documentId = "document" + ordinal };
        var evidence = Enumerable.Range(0, 9).Select(index => Receipt(Resource(index < 8 ? 1 : 2))).ToArray();
        var context = Create(provider, Enumerable.Repeat(capability, 9).ToArray(), evidence, ranked: false);
        Assert.True(context.Partial);
        Assert.Null(context.ExactResourceUrl());
        var ranked = Create(provider, Enumerable.Repeat(capability, 9).ToArray(), evidence, ranked: true);
        Assert.NotNull(ranked.ExactResourceUrl()); // Primary evidence identifies one specific object.
    }

    [Theory]
    [InlineData("completed", true)]
    [InlineData("failed", false)]
    public void Empty_or_failed_ranked_mail_primary_cannot_promote_a_later_thread(string state, bool verified)
    {
        var context = Context("gmail", ["mail_search", "mail_read"], Receipt(new { messages = Array.Empty<object>() }, state, verified),
            Receipt(new { id = "aabbccdd11", threadId = "aabbccdd22", headers = Array.Empty<object>() }));
        Assert.Single(context.Resources);
        Assert.Null(context.ExactMailUrl());
        Assert.Null(context.ExactResourceUrl());
    }

    [Fact]
    public void Retained_resources_and_metadata_remain_bounded_without_mailbox_document_or_message_bodies()
    {
        var files = Enumerable.Range(0, 30).Select(i => new { id = "file" + i, name = new string('n', 3000),
            webViewLink = "https://drive.google.com/file/d/file" + i + "/view", description = new string('z', 50000) }).ToArray();
        var context = Context("googledrive", ["drive_search"], Receipt(new { files }));
        Assert.InRange(context.ObjectReferences.Length, 1, 12);
        Assert.All(context.ObjectReferences, reference =>
        {
            Assert.InRange(reference.Title.Length, 0, 500);
            Assert.InRange(reference.Id.Length, 1, 256);
            Assert.InRange(reference.Url.Length, 1, 2048);
        });
        Assert.Null(context.ExactResourceUrl());
        Assert.DoesNotContain(new string('z', 100), JsonSerializer.Serialize(context));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Gmail_incomplete_candidate_search_cannot_establish_a_unique_open(bool ranked)
    {
        var message = new { id = "aabbccdd11", threadId = "aabbccdd22" };
        foreach (var data in new object[]
        {
            new { messages = new[] { message }, pageItemCount = 1, hasMore = true },
            new { messages = new[] { message }, pageItemCount = 1, hasMore = false, nextPageToken = "next" },
            new { messages = new[] { message }, pageItemCount = 2, hasMore = false },
            new { messages = new[] { message }, pageItemCount = "1", hasMore = false },
            new { messages = new[] { message }, pageItemCount = 1, hasMore = "false" }
        })
        {
            var context = Create("gmail", ["mail_search"], [Receipt(data)], ranked: ranked);
            Assert.Single(context.Resources);
            Assert.Null(context.ExactMailUrl());
        }
        var clipped = Create("gmail", ["mail_search"], [Receipt(new { messages = new[] { message }, pageItemCount = 1, hasMore = false }, partial: true)], ranked: ranked);
        Assert.Single(clipped.Resources);
        Assert.True(clipped.Partial);
        Assert.Null(clipped.ExactMailUrl());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Gmail_malformed_candidates_are_not_discarded_to_make_the_other_result_unique(bool ranked)
    {
        foreach (var invalid in new object[] { new { id = "aabbccdd33" }, new { id = "aabbccdd33", threadId = "../invalid" }, 7, "invalid" })
        {
            var context = Create("gmail", ["mail_search"], [Receipt(new { messages = new object[]
                { new { id = "aabbccdd11", threadId = "aabbccdd22" }, invalid }, pageItemCount = 2, hasMore = false })], ranked: ranked);
            Assert.Single(context.Resources);
            Assert.Null(context.ExactMailUrl());
        }
    }

    [Fact]
    public void Gmail_search_samples_enrich_only_existing_candidate_facts_not_selection()
    {
        var context = Context("gmail", ["mail_search"], Receipt(new
        {
            messages = new[] { new { id = "aabbccdd11", threadId = "aabbccdd22" } }, pageItemCount = 1, hasMore = false,
            sampledMessages = new[] { new { id = "aabbccdd11", threadId = "aabbccdd22", headers = new[] { new { name = "Subject", value = "Verified subject" } } },
                new { id = "aabbccdd33", threadId = "aabbccdd44", headers = new[] { new { name = "Subject", value = "Unselected sample" } } } },
            sampledContent = new { result = new { data = new { id = "aabbccdd55", threadId = "aabbccdd66", text = "PRIVATE BODY" } } }
        }));
        var resource = Assert.Single(context.Resources);
        Assert.Equal("Verified subject", resource.GetProperty("headers").GetProperty("Subject").GetString());
        Assert.EndsWith("#all/aabbccdd22", context.ExactMailUrl());
        Assert.DoesNotContain("aabbccdd44", JsonSerializer.Serialize(context));
        Assert.DoesNotContain("PRIVATE BODY", JsonSerializer.Serialize(context));
    }

    [Theory]
    [InlineData("mail_read")]
    [InlineData("mail_thread")]
    public void Gmail_direct_partial_content_does_not_lose_its_known_exact_thread(string capability)
    {
        var thread = "aabbccdd22";
        object data = capability == "mail_read" ? new { id = "aabbccdd11", threadId = thread, text = "PRIVATE BODY" }
            : new { id = thread, totalMessages = 40, messages = new[] { new { id = "aabbccdd11", threadId = thread, text = "PRIVATE BODY" } } };
        var context = Context("gmail", [capability], Receipt(data, partial: true));
        Assert.True(context.Partial);
        Assert.EndsWith("#all/" + thread, context.ExactMailUrl());
        Assert.DoesNotContain("PRIVATE BODY", JsonSerializer.Serialize(context));
    }

    [Fact]
    public void Gmail_never_traverses_envelope_diagnostics_or_arbitrary_nested_content()
    {
        var receipt = JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
            diagnostics = new { id = "aabbccdd11", threadId = "aabbccdd22" },
            result = new { data = new { body = new { id = "aabbccdd33", threadId = "aabbccdd44" } } } });
        var context = Context("gmail", ["mail_read"], receipt);
        Assert.Empty(context.Resources);
        Assert.Null(context.ExactMailUrl());
        var missing = Context("gmail", ["mail_read"], JsonSerializer.SerializeToElement(new { state = "completed", verified = true,
            result = new { diagnostics = new { id = "aabbccdd11", threadId = "aabbccdd22" } } }));
        Assert.Empty(missing.Resources);
        Assert.Null(missing.ExactMailUrl());
    }

    [Theory]
    [InlineData("googledrive", "drive_read")]
    [InlineData("googledocs", "docs_read")]
    [InlineData("googleforms", "forms_read")]
    public void Unopenable_single_read_is_not_ignored_to_select_another_unranked_object(string provider, string capability)
    {
        object valid = provider switch
        {
            "googledrive" => new { id = "verifiedFile", webViewLink = "https://drive.google.com/file/d/verifiedFile/view" },
            "googledocs" => new { documentId = "verifiedDocument" },
            _ => new { formId = "verifiedForm" }
        };
        foreach (var invalid in new object[] { new { id = "otherFile", webViewLink = "https://drive.google.com/drive/my-drive" }, new { title = "Missing identity" }, "malformed", 7 })
        {
            var context = Create(provider, [capability, capability], [Receipt(valid), Receipt(invalid)], ranked: false);
            Assert.Single(context.ObjectReferences);
            Assert.Null(context.ExactResourceUrl());
            Assert.False(context.Partial); // Navigation ambiguity is not a content/count coverage claim.
            var ranked = Create(provider, [capability, capability], [Receipt(valid), Receipt(invalid)], ranked: true);
            Assert.NotNull(ranked.ExactResourceUrl()); // A selected read remains selected.
        }
    }

    [Fact]
    public void Missing_data_and_unknown_collection_are_not_empty_evidence_for_unranked_selection()
    {
        var malformed = JsonSerializer.SerializeToElement(new { state = "completed", verified = true, result = new { diagnostic = "missing data" } });
        var validRead = Receipt(new { id = "verifiedFile", webViewLink = "https://drive.google.com/file/d/verifiedFile/view" });
        var missingRead = Create("googledrive", ["drive_read", "drive_read"], [malformed, validRead], ranked: false);
        Assert.Single(missingRead.ObjectReferences);
        Assert.Null(missingRead.ExactResourceUrl());
        var missingSearch = Create("googledrive", ["drive_search", "drive_read"], [Receipt(new { files = "not an array" }), validRead], ranked: false);
        Assert.Single(missingSearch.ObjectReferences);
        Assert.Null(missingSearch.ExactResourceUrl());
        var emptySearch = Create("googledrive", ["drive_search", "drive_read"], [Receipt(new { files = Array.Empty<object>() }), validRead], ranked: false);
        Assert.NotNull(emptySearch.ExactResourceUrl()); // A genuine empty collection has no competing object.
    }
}
