using System.Text;
using System.Text.Json;
using KeyboardWtf.Staging.Api.Connectors;
using Xunit;

namespace KeyboardWtf.Staging.Api.Tests;

public sealed partial class ConnectorPlatformTests
{
    [Fact]
    public async Task Matching_reply_verification_retains_completed_claim_and_never_sends_twice()
    {
        var f = Fixture(); await Connect(f);
        var endpoints = InstallSyntheticReplyReadback(f, wrongThread: false);
        var task = await f.Tasks.ResolveAsync(f.Caller, new("gmail", "Send an email reply to the selected original message"), default);
        var request = new ConnectorExecuteRequest(task.TaskId, "mail_send", Args(new
        {
            to = "reader@example.test", subject = "Reply verification fixture", text = "Fixture reply.",
            threadId = "expected_thread", inReplyTo = "<parent@example.test>"
        }), true);
        var reply = await f.Tasks.ExecuteAsync(f.Caller, request, default);
        Assert.Equal("completed", reply.State); Assert.True(reply.Verified); Assert.NotNull(reply.Result);
        Assert.Equal("expected_thread", reply.Result!.Value.GetProperty("threadId").GetString());
        var status = await f.Tasks.StatusAsync(f.Caller, task.TaskId, default);
        Assert.Equal("ready", status.State); Assert.Equal("completed", Assert.Single(status.Operations).State);
        var repeated = await f.Tasks.ExecuteAsync(f.Caller, request, default);
        Assert.Equal("completed", repeated.State); Assert.True(repeated.Verified);
        Assert.Equal(1, f.Provider.Writes); Assert.Equal(2, endpoints.Count);
        Assert.EndsWith("/messages/send", endpoints[0]);
        Assert.EndsWith("/messages/sent_message?format=full", endpoints[1]);
    }

    [Fact]
    public async Task Wrong_reply_thread_settles_unknown_and_a_repeat_cannot_dispatch_again()
    {
        var f = Fixture(); await Connect(f);
        var endpoints = InstallSyntheticReplyReadback(f, wrongThread: true);
        var task = await f.Tasks.ResolveAsync(f.Caller, new("gmail", "Send an email reply to the selected original message"), default);
        var request = new ConnectorExecuteRequest(task.TaskId, "mail_send", Args(new
        {
            to = "reader@example.test", subject = "Reply verification fixture", text = "Fixture reply.",
            threadId = "expected_thread", inReplyTo = "<parent@example.test>"
        }), true);
        var reply = await f.Tasks.ExecuteAsync(f.Caller, request, default);
        Assert.Equal("outcome_unknown", reply.State); Assert.False(reply.Verified); Assert.Null(reply.Result);
        var status = await f.Tasks.StatusAsync(f.Caller, task.TaskId, default);
        Assert.Equal("failed", status.State); Assert.Equal("outcome_unknown", Assert.Single(status.Operations).State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Tasks.ExecuteAsync(f.Caller, request, default));
        Assert.Equal(1, f.Provider.Writes); Assert.Equal(2, endpoints.Count);
    }

    private static List<string> InstallSyntheticReplyReadback(TestFixture f, bool wrongThread)
    {
        var endpoints = new List<string>();
        f.Provider.RequestOverride = (endpoint, ct) =>
        {
            ct.ThrowIfCancellationRequested(); endpoints.Add(endpoint);
            if (endpoint.EndsWith("/messages/send", StringComparison.Ordinal))
            {
                // Existing RequestOverride runs before FakeProvider's ordinary POST counter.
                // Count this one synthetic dispatch explicitly; no real HTTP exists here.
                f.Provider.Writes++;
                return Task.FromResult(JsonSerializer.SerializeToElement(new { id = "sent_message", threadId = "expected_thread" }));
            }
            Assert.EndsWith("/messages/sent_message?format=full", endpoint);
            return Task.FromResult(JsonSerializer.SerializeToElement(new
            {
                id = "sent_message", threadId = wrongThread ? "other_thread" : "expected_thread", labelIds = new[] { "SENT" },
                payload = new
                {
                    mimeType = "text/plain", headers = new[]
                    {
                        new { name = "Message-ID", value = "<rewritten@example.test>" },
                        new { name = "To", value = "reader@example.test" },
                        new { name = "Subject", value = "Reply verification fixture" },
                        new { name = "In-Reply-To", value = "<parent@example.test>" },
                        new { name = "References", value = "<ancestor@example.test> <parent@example.test>" }
                    },
                    body = new { data = Convert.ToBase64String(Encoding.UTF8.GetBytes("Fixture reply.")) }
                }
            }));
        };
        return endpoints;
    }
}