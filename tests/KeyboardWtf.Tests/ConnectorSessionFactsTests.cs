namespace KeyboardWtf.Tests;

using System.Text.Json;
using KeyboardWtf.Services.Connectors;
using Xunit;

public sealed class ConnectorSessionFactsTests
{
    private static JsonElement Receipt(object data, bool verified = true, bool partial = false)
        => JsonSerializer.SerializeToElement(new { state = verified ? "completed" : "failed", verified, result = new { data, truncated = partial } });
    private static ConnectorSessionResult Retain(string provider, string capability, JsonElement receipt)
        => ConnectorSessionResult.Create("task", provider, "account", "reader@example.test", "Read requested resource", receipt,
            [receipt], evidenceCapabilities: [capability]);
    [Theory]
    [InlineData(16000)]
    [InlineData(48242)]
    [InlineData(100000)]
    public void Long_read_content_cannot_prevent_retaining_its_verified_thread(int length)
    {
        // A real completed reply failed afterward while extracting optional
        // facts from a 16k body. Large paragraphs remain valid read evidence
        // even when the bounded fact cache cannot retain an excerpt.
        var body = "Release pending " + new string('x', length) + ".\n\nNext step is approval.";
        var context = Retain("gmail", "mail_thread", Receipt(new { id = "aabbccdd22", messages = new[] {
            new { id = "aabbccdd11", threadId = "aabbccdd22", from = "Reader <reader@example.test>", text = body } } }));
        Assert.Equal("aabbccdd22", Assert.Single(context.MailThreadReferences()).ThreadId);
        Assert.Equal("https://mail.google.com/mail/?authuser=reader%40example.test#all/aabbccdd22", context.ExactResourceUrl());
        Assert.True(context.Partial); Assert.DoesNotContain(new string('x', 400), JsonSerializer.Serialize(context));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Search_previews_and_failed_reads_cannot_become_retained_content_facts(bool failed)
    {
        var receipt = failed ? Receipt(new { id = "aabbccdd11", threadId = "aabbccdd22", text = "The project won a prize." }, false)
            : Receipt(new { messages = new[] { new { id = "aabbccdd11", threadId = "aabbccdd22", snippet = "The project won a prize." } },
                sampledMessages = new[] { new { id = "aabbccdd11", threadId = "aabbccdd22", text = "The project won a prize." } } });
        Assert.Empty(Retain("gmail", failed ? "mail_read" : "mail_search", receipt).Facts);
    }
    [Fact]
    public void Facts_remain_bounded_and_do_not_save_unrelated_body_or_cut_a_sentence_into_a_claim()
    {
        var body = "PRIVATE PREAMBLE. " + string.Join(" ", Enumerable.Range(1, 10).Select(n => $"Decision {n} is agreed."))
            + " The prize " + new string('x', 400) + ". PRIVATE SIGNATURE.";
        var context = Retain("gmail", "mail_read", Receipt(new { id = "aabbccdd11", threadId = "aabbccdd22", text = body }));
        Assert.Equal(6, context.Facts.Length); Assert.True(context.Partial);
        Assert.All(context.Facts, fact => { Assert.Equal("aabbccdd11", fact.ResourceId); Assert.True(fact.Text.Length <= 320); });
        Assert.True(context.Facts.Sum(fact => fact.Text.Length) <= 1600);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(context)); Assert.DoesNotContain(new string('x', 40), JsonSerializer.Serialize(context));
    }
    [Fact]
    public void A_child_message_from_another_thread_cannot_supply_retained_facts()
    {
        var receipt = Receipt(new { id = "aabbccdd22", messages = new[] {
            new { id = "aabbccdd11", threadId = "aabbccdd22", text = "The project won a finalist prize." },
            new { id = "eeff001122", threadId = "eeff001133", text = "Unrelated project won a larger prize." } } });
        var facts = Retain("gmail", "mail_thread", receipt).Facts;
        Assert.Single(facts); Assert.Equal("aabbccdd11", facts[0].ResourceId); Assert.DoesNotContain("Unrelated", facts[0].Text);
    }
    [Fact]
    public void Document_text_runs_support_short_decisions_but_envelope_content_does_not()
    {
        var receipt = Receipt(new { documentId = "observedDoc", title = "Decisions", diagnostics = new { text = "Fake prize claim." },
            tabs = new[] { new { documentTab = new { body = new { content = new[] { new { paragraph = new { elements = new[] {
                new { textRun = new { content = "PRIVATE unrelated preamble. Release date agreed for Friday." } } } } } } } } } } });
        var context = Retain("googledocs", "docs_read", receipt);
        Assert.Single(context.Facts); Assert.Equal("Release date agreed for Friday.", context.Facts[0].Text);
        Assert.Equal("observedDoc", context.Facts[0].ResourceId); Assert.DoesNotContain("Fake prize", JsonSerializer.Serialize(context));
    }
    [Theory]
    [InlineData("The prize is Top ", "10 Best Performers.", "The prize is Top 10 Best Performers.")]
    [InlineData("The release is scheduled for Friday", " only after approval.", "The release is scheduled for Friday only after approval.")]
    public void Formatting_split_runs_cannot_drop_a_prize_or_its_condition(string first, string second, string expected)
    {
        var receipt = Receipt(new { documentId = "observedDoc", tabs = new[] { new { documentTab = new { body = new { content = new[] {
            new { paragraph = new { elements = new[] { new { textRun = new { content = first } }, new { textRun = new { content = second } } } } } } } } } } });
        var facts = Retain("googledocs", "docs_read", receipt).Facts;
        Assert.Single(facts); Assert.Equal(expected, facts[0].Text);
    }
    [Fact]
    public void A_paragraph_cut_before_its_final_formatting_run_cannot_become_a_complete_fact()
    {
        var runs = Enumerable.Range(0, 101).Select(index => new { textRun = new { content = index == 0
            ? "Release Friday" : index == 100 ? " only if approved." : " " } }).ToArray();
        var receipt = Receipt(new { documentId = "observedDoc", body = new { content = new[] { new { paragraph = new { elements = runs } } } } });
        var context = Retain("googledocs", "docs_read", receipt);
        Assert.Empty(context.Facts); Assert.True(context.Partial);
    }
    [Fact]
    public void A_wrapped_email_sentence_keeps_its_condition_in_the_same_fact()
    {
        var context = Retain("gmail", "mail_read", Receipt(new { id = "aabbccdd11", threadId = "aabbccdd22",
            text = "PRIVATE preamble. Release is scheduled for Friday\nonly after approval. PRIVATE signature." }));
        Assert.Single(context.Facts); Assert.Equal("Release is scheduled for Friday only after approval.", context.Facts[0].Text);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(context));
    }
}
