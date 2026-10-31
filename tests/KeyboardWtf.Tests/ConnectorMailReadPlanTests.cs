namespace KeyboardWtf.Tests;

using System.Text.Json;
using KeyboardWtf.Services.Connectors;
using Xunit;

public sealed class ConnectorMailReadPlanTests
{
    [Theory]
    [InlineData("Read the event details and summarize my conversation. Finally count my winning emails.", true)]
    [InlineData("Summarize the invoice thread and tell me how many notification messages I have.", true)]
    [InlineData("How many winning emails do I have? Give two subject/date examples.", false)]
    [InlineData("Read the message and count its words.", false)]
    [InlineData("Summarize the thread and count the action items.", false)]
    [InlineData("Read only the headers and count the matching emails.", false)]
    [InlineData("Send the summary and count my emails.", false)]
    [InlineData("Find emails containing \"summarize the thread and count emails\".", false)]
    [InlineData("Find emails containing 'summarize the thread and count emails'.", false)]
    [InlineData("Find emails containing ‘summarize the thread and count emails’.", false)]
    [InlineData("Find emails containing 'don't count winning emails'.", false)]
    [InlineData("What's in my thread? I'm asking how many emails I have.", true)]
    public void Mixed_content_and_mailbox_count_require_separate_parts_without_changing_simple_reads(string user, bool expected)
        => Assert.Equal(expected, ConnectorMailReadPlan.Required(user));

    [Theory]
    [InlineData("Find emails containing 'count the winning emails'.")]
    [InlineData("Find emails containing ‘count the winning emails’.")]
    [InlineData("Find emails containing 'don't count the winning emails'.")]
    [InlineData("Find emails containing ‘don’t count winning emails’.")]
    [InlineData("Find emails containing 'the students' task was to count emails'.")]
    [InlineData("Don’t count these emails; read the latest message.")]
    [InlineData("How many email attachments do I have?")]
    [InlineData("Count email addresses in the messages.")]
    public void Single_quoted_search_content_is_not_a_mailbox_count(string user)
        => Assert.False(ConnectorMailReadPlan.MailCountRequested(user));

    [Theory]
    [InlineData("Find 'a' then count emails matching 'b'.")]
    [InlineData("Count emails containing ‘don’t count’, please.")]
    public void A_real_count_outside_balanced_literals_still_has_its_original_scope(string user)
        => Assert.True(ConnectorMailReadPlan.MailCountRequested(user));

    [Theory]
    [InlineData(" 'a", 1000)]
    [InlineData("‘a", 1500)]
    [InlineData("“a", 1500)]
    public void Unclosed_repeated_literals_do_not_exhaust_the_bounded_intent_parser(string unit, int copies)
        => Assert.False(ConnectorMailReadPlan.MailCountRequested(string.Concat(Enumerable.Repeat(unit, copies))));

    [Fact]
    public void Literal_ordered_parts_keep_their_distinct_body_modes()
    {
        var user = "Read my event details. Also summarize my conversation with a colleague. Finally count the winning emails.";
        var goals = ConnectorMailReadPlan.Parse(JsonSerializer.Serialize(new[]
        {
            new { request = "Read my event details", kind = "read" },
            new { request = "summarize my conversation with a colleague", kind = "thread" },
            new { request = "count the winning emails", kind = "count" }
        }), user, true);
        Assert.Equal(new[] { "g1", "g2", "g3" }, goals.Select(goal => goal.Id));
        Assert.Equal(new[] { "message", "thread", "preview" }, goals.Select(goal => goal.ReadMode));
    }

    [Theory]
    [InlineData("Read the event details. Also read my conversation with a colleague. Finally count my notification emails.")]
    [InlineData("Read the event details, summarize my conversation, and count my notification emails.")]
    [InlineData("Check my Gmail: what's happening with the event, what is pending in my conversation, and how many notification emails do I have?")]
    public void Clear_human_parts_can_be_partitioned_without_model_json_or_inferred_queries(string user)
    {
        var goals = ConnectorMailReadPlan.FromClearUserParts(user);
        Assert.Equal(3, goals.Length); Assert.Equal("count", goals[2].Kind);
        Assert.Equal("thread", goals[1].Kind);
        Assert.All(goals, goal => Assert.Contains(goal.Request, user));
    }

    [Fact]
    public void A_plan_cannot_omit_a_requested_conversation_or_merge_two_content_reads()
    {
        const string user = "Read my event details. Also read my conversation with a colleague. Finally count winning emails.";
        Assert.Throws<ArgumentException>(() => ConnectorMailReadPlan.Parse(JsonSerializer.Serialize(new[]
        {
            new { request = "Read my event details", kind = "read" },
            new { request = "count winning emails", kind = "count" }
        }), user, true));
        Assert.Throws<ArgumentException>(() => ConnectorMailReadPlan.Parse(JsonSerializer.Serialize(new[]
        {
            new { request = "Read my event details. Also read my conversation with a colleague", kind = "read" },
            new { request = "count winning emails", kind = "count" }
        }), user, true));
    }

    [Fact]
    public void Quoted_search_content_does_not_create_extra_request_parts()
    {
        const string user = "Read my event details. Then count the emails containing \"What happened? Read it.\".";
        var goals = ConnectorMailReadPlan.FromClearUserParts(user);
        Assert.Equal(2, goals.Length); Assert.Equal("count", goals[1].Kind);
        Assert.Contains("\"What happened? Read it.\"", goals[1].Request);
    }

    [Theory]
    [InlineData("[{\"request\":\"Read my event details\",\"kind\":\"write\"}]")]
    [InlineData("[{\"request\":\"Read an invented account\",\"kind\":\"read\"}]")]
    [InlineData("[{\"request\":\"Read my event details\",\"kind\":\"count\"}]")]
    [InlineData("[{\"request\":\"Read my event details\",\"kind\":\"thread\"}]")]
    [InlineData("[{\"request\":\"Read my event details\",\"kind\":\"read\",\"uid\":\"injected\"}]")]
    [InlineData("[{\"request\":\"Read my event details\",\"kind\":\"read\",\"kind\":\"read\"}]")]
    [InlineData("[{\"request\":\"Read my event details\",\"kind\":\"read\"},{\"request\":\"Read my event details\",\"kind\":\"read\"}]")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void Invented_or_overlapping_parts_and_extra_authority_are_rejected(string json)
        => Assert.Throws<ArgumentException>(() => ConnectorMailReadPlan.Parse(json,
            "Read my event details and count the winning emails.", false));

    [Fact]
    public void Required_plan_cannot_omit_or_merge_the_mailbox_count()
    {
        const string user = "Read my event details and count the winning emails.";
        Assert.Throws<ArgumentException>(() => ConnectorMailReadPlan.Parse("", user, true));
        Assert.Throws<ArgumentException>(() => ConnectorMailReadPlan.Parse(JsonSerializer.Serialize(new[]
            { new { request = "Read my event details", kind = "read" } }), user, true));
        Assert.Throws<ArgumentException>(() => ConnectorMailReadPlan.Parse(JsonSerializer.Serialize(new[]
            { new { request = user, kind = "read" } }), user, true));
    }

    [Fact]
    public void Absent_optional_plan_adds_no_owner_or_state()
        => Assert.Empty(ConnectorMailReadPlan.Parse(null, "Find recent invoices", false));

    [Theory]
    [InlineData("How many winning notification emails do I have?", true)]
    [InlineData("Count the matching emails.", true)]
    [InlineData("How often has Example emailed me to say I won? Count the winning notifications, please.", true)]
    [InlineData("Count messages matching the exact query from:team@example.test", true)]
    [InlineData("How many emails contain action items?", true)]
    [InlineData("Count emails with the word launch in the body.", true)]
    [InlineData("Count task reminder emails.", true)]
    [InlineData("How many attachment notification emails do I have?", true)]
    [InlineData("Read the latest message, then count task reminder emails.", true)]
    [InlineData("Count the words in these emails.", false)]
    [InlineData("How many action items are in my messages?", false)]
    [InlineData("Summarize the thread and count the links in the emails.", false)]
    [InlineData("How many attachments are in these messages?", false)]
    [InlineData("Find emails containing \"count the winning emails\".", false)]
    [InlineData("Do not count my emails; read the latest message.", false)]
    [InlineData("Don't count these emails.", false)]
    public void Mailbox_totals_are_distinct_from_counts_of_email_contents(string user, bool expected)
        => Assert.Equal(expected, ConnectorMailReadPlan.MailCountRequested(user));

    [Theory]
    [InlineData("Read the latest message, then count task reminder emails.", true)]
    [InlineData("Read the latest message, then count the words in these emails.", false)]
    public void Content_count_negatives_do_not_remove_existing_mixed_mailbox_plans(string user, bool required)
        => Assert.Equal(required, ConnectorMailReadPlan.Required(user));
}
