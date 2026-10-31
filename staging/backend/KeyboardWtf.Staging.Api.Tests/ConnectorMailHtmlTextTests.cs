using System.Text;
using System.Text.Json;
using KeyboardWtf.Staging.Api.Connectors;
using Xunit;

namespace KeyboardWtf.Staging.Api.Tests;

public sealed class ConnectorMailHtmlTextTests
{
    [Fact]
    public void Html_only_message_keeps_late_body_instead_of_spending_the_existing_budget_on_styles()
    {
        var source = "<!DOCTYPE html><html><head><style>" + new string('s', 20000)
            + "</style></head><body><p>Your certificate is ready.</p><p>Review the organizer's instructions.</p></body></html>";
        var safe = ConnectorOperations.SafeResult(Message(Part("text/html", source)), "mail_read");
        var data = safe.GetProperty("data");
        Assert.Equal("Your certificate is ready.\nReview the organizer's instructions.", data.GetProperty("text").GetString());
        Assert.False(data.GetProperty("contentTruncated").GetBoolean());
        Assert.False(safe.GetProperty("truncated").GetBoolean());
        Assert.True(safe.GetRawText().Length < 3000);
    }

    [Fact]
    public void Plain_mime_alternative_remains_preferred_and_attachment_metadata_is_preserved()
    {
        var payload = new
        {
            mimeType = "multipart/mixed", parts = new object[]
            {
                new { mimeType = "multipart/alternative", parts = new[]
                    { Part("text/plain", "Plain message content stays unchanged.\nSecond line."), Part("text/html", "<script>Unclosed ignored alternative") } },
                new { mimeType = "application/pdf", filename = "certificate.pdf", body = new { attachmentId = "observed-attachment", size = 42 } }
            }
        };
        var safe = ConnectorOperations.SafeResult(Message(payload), "mail_read");
        var data = safe.GetProperty("data");
        Assert.Equal("Plain message content stays unchanged.\nSecond line.", data.GetProperty("text").GetString());
        Assert.False(data.GetProperty("contentTruncated").GetBoolean());
        Assert.False(safe.GetProperty("truncated").GetBoolean());
        var attachment = Assert.Single(data.GetProperty("attachments").EnumerateArray());
        Assert.Equal("certificate.pdf", attachment.GetProperty("filename").GetString());
        Assert.Equal("observed-attachment", attachment.GetProperty("attachmentId").GetString());
    }

    [Fact]
    public void Hidden_markup_and_remote_attributes_are_never_message_text()
    {
        var source = "<head><title>HIDDEN TITLE</title><style>HIDDEN CSS</style></head>"
            + "<!-- HIDDEN COMMENT --><script>if(a < b) { fetch('https://remote.invalid/'); }</script>"
            + "<template>HIDDEN TEMPLATE<template>NESTED HIDDEN</template>STILL HIDDEN</template>"
            + "<noscript>HIDDEN NOSCRIPT</noscript><p title='attribute > delimiter'>Hello &amp; world</p>"
            + "<div>Next&nbsp;line<br>Second</div><a href='https://remote.invalid/'>Read <strong>this</strong></a>"
            + "<img src='https://remote.invalid/pixel' />";
        var text = ConnectorMailHtmlText.Extract(source, out var partial);
        Assert.Equal("Hello & world\nNext line\nSecond\nRead this", text);
        Assert.DoesNotContain("HIDDEN", text);
        Assert.DoesNotContain("https://", text);
        Assert.False(partial);
    }

    [Fact]
    public void Escaped_markup_is_decoded_once_as_literal_text_and_block_boundaries_are_kept()
    {
        var text = ConnectorMailHtmlText.Extract("<p>Cost &lt; $5 &amp; terms &#x2713;</p><table><tr><td>A</td><td>B</td></tr></table><p>&lt;em&gt;literal&lt;/em&gt;</p>", out var partial);
        Assert.Equal("Cost < $5 & terms ✓\nA\nB\n<em>literal</em>", text);
        Assert.False(partial);
    }

    [Theory]
    [InlineData("<p>Visible</p><style>HIDDEN")]
    [InlineData("<p>Visible</p><!-- HIDDEN")]
    [InlineData("<p>Visible</p><a title='unterminated > HIDDEN")]
    [InlineData("<p>Visible</p><script>HIDDEN</script-extra>STILL HIDDEN")]
    [InlineData("<p>Visible</p><template>HIDDEN")]
    public void Malformed_or_unclosed_markup_preserves_only_the_known_prefix_and_marks_partial(string source)
    {
        var safe = ConnectorOperations.SafeResult(Message(Part("text/html", source)), "mail_read");
        Assert.Equal("Visible", safe.GetProperty("data").GetProperty("text").GetString());
        Assert.True(safe.GetProperty("data").GetProperty("contentTruncated").GetBoolean());
        Assert.True(safe.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void A_self_closing_marker_does_not_expose_nonvoid_hidden_element_content()
    {
        var text = ConnectorMailHtmlText.Extract("<style/>HIDDEN CSS</style><script/>HIDDEN CODE</script><template/>HIDDEN DATA</template><p>Visible</p>", out var partial);
        Assert.Equal("Visible", text);
        Assert.False(partial);
    }

    [Fact]
    public void Alternating_hidden_elements_have_one_total_recursion_bound()
    {
        var opening = string.Concat(Enumerable.Range(0, 40).Select(index => index % 2 == 0 ? "<head>" : "<template>"));
        var closing = string.Concat(Enumerable.Range(0, 40).Reverse().Select(index => index % 2 == 0 ? "</head>" : "</template>"));
        var text = ConnectorMailHtmlText.Extract("<p>Before</p>" + opening + "HIDDEN" + closing + "<p>After</p>", out var partial);
        Assert.Equal("Before", text);
        Assert.True(partial);
    }

    [Fact]
    public void Input_and_tag_bounds_return_partial_evidence_without_unbounded_work()
    {
        var text = ConnectorMailHtmlText.Extract(new string('x', ConnectorMailHtmlText.MaximumInputCharacters + 1), out var partial);
        Assert.Equal(ConnectorMailHtmlText.MaximumInputCharacters, text.Length);
        Assert.True(partial);
        var excessiveTags = string.Concat(Enumerable.Repeat("<br>", 8193));
        Assert.Equal("", ConnectorMailHtmlText.Extract(excessiveTags, out var tagPartial));
        Assert.True(tagPartial);
    }

    [Fact]
    public void Thread_normalization_uses_the_same_readable_html_fallback()
    {
        var message = Message(Part("text/html", "<style>HIDDEN</style><p>Verified thread request.</p>"));
        var safe = ConnectorOperations.SafeResult(JsonSerializer.SerializeToElement(new { id = "ccccdddd", messages = new[] { message } }), "mail_thread");
        var data = safe.GetProperty("data");
        Assert.Equal("Verified thread request.", Assert.Single(data.GetProperty("messages").EnumerateArray()).GetProperty("text").GetString());
        Assert.False(safe.GetProperty("truncated").GetBoolean());
    }

    private static JsonElement Message(object payload) => JsonSerializer.SerializeToElement(new
        { id = "aaaabbbb", threadId = "ccccdddd", snippet = "Preview only.", payload });

    private static object Part(string mime, string text) => new
        { mimeType = mime, body = new { data = Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_') } };
}
