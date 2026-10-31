namespace KeyboardWtf.Tests;

using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using KeyboardWtf.Models;
using KeyboardWtf.Services;
using KeyboardWtf.Services.Connectors;
using KeyboardWtf.Services.Updates;
using Xunit;

[Collection("Live event integration")]
public sealed class ConnectorExceptionCleanupTests
{
    [Fact]
    public async Task Worker_setup_fault_is_classified_and_releases_its_actual_update_lease()
    {
        await using var f = new Fixture();
        var task = await f.Resolve();
        Assert.True(f.Admission.TryAcquire(out var lease));
        Assert.False(f.Admission.TryFreeze(out _));
        // Existing owner callback injects a deterministic setup failure at the
        // last pre-dispatch check. No machine-load-dependent regex timing.
        f.FailNextOwnerRead = true;
        var result = await (Task<JsonElement>)Method("ExecuteOwnedAsync").Invoke(f.Controller,
            [task, "mail_search", "setup-fault", new Dictionary<string, JsonElement>(), false, lease, null, null, null]);

        Assert.Equal("failed", result.GetProperty("state").GetString());
        Assert.Equal("classification_timeout", result.GetProperty("errorCategory").GetString());
        Assert.False(result.GetProperty("verified").GetBoolean());
        Assert.Equal(0, f.Executions);
        Assert.Equal("failed", Field(task, "State"));
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
        Assert.True(f.Admission.TryFreeze(out var freeze));
        freeze.Dispose();
    }

    [Fact]
    public async Task Reset_observes_already_faulted_workers_and_still_disposes_every_task()
    {
        await using var f = new Fixture();
        var task = await f.Resolve();
        var lifetime = (CancellationTokenSource)Field(task, "Cancellation");
        var operations = (Dictionary<string, Task<JsonElement>>)Field(task, "Operations");
        operations.Add("mail_search:failed", Task.FromException<JsonElement>(new RegexMatchTimeoutException()));

        await f.Controller.ResetAsync();

        Assert.Throws<ObjectDisposedException>(() => lifetime.Token);
        Assert.Equal(0, f.Tasks.Count);
        Assert.False(KeyboardWtfState.HasLongTaskUiOwner);
        Assert.Equal(1, f.ServerStops);
        Assert.Equal(0, f.Executions);
        await f.Controller.ResetAsync(); // Reset remains idempotent.
    }

    [Fact]
    public async Task Background_completion_observes_worker_fault_without_announcing_success()
    {
        await using var f = new Fixture();
        var task = await f.Resolve();
        await (Task)Method("AnnounceWhenDoneAsync").Invoke(f.Controller,
            [task, "mail_search:failed", Task.FromException<JsonElement>(new RegexMatchTimeoutException())]);

        Assert.Equal(0, f.Announcements);
        Assert.Single(f.Notices);
        Assert.Contains("without verified success", f.Notices[0]);
    }

    private static MethodInfo Method(string name) => typeof(JarvisConnectorController)
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static object Field(object target, string name) => target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(target);

    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly UpdateWorkAdmissionGate Admission = new();
        internal readonly JarvisConnectorController Controller;
        internal readonly List<string> Notices = [];
        internal bool FailNextOwnerRead;
        internal int Executions, ServerStops, Announcements;
        internal IDictionary Tasks => (IDictionary)Field(Controller, "_tasks");
        private readonly string _id = Guid.NewGuid().ToString("N");
        internal Fixture()
        {
            Controller = new(Request, () =>
            {
                if (FailNextOwnerRead) { FailNextOwnerRead = false; throw new RegexMatchTimeoutException(); }
                return "owner";
            }, () => CancellationToken.None, () => "Find recent invoices", (_, _) =>
            {
                ++Announcements; return Task.FromResult(true);
            }, admission: Admission, notify: (_, message) => Notices.Add(message));
        }

        internal async Task<object> Resolve()
        {
            var result = await Controller.ExecuteAsync("connector_resolve",
                JsonSerializer.SerializeToElement(new { connector_id = "gmail" }), false, default);
            Assert.True((bool)result["ok"]);
            return Tasks[_id];
        }

        private Task<JsonElement> Request(HttpMethod method, string path, object body, CancellationToken token)
        {
            if (path.EndsWith("/resolve", StringComparison.Ordinal))
                return Task.FromResult(JsonSerializer.SerializeToElement(new
                {
                    taskId = _id, state = "ready", connectorId = "gmail", accountId = Guid.NewGuid().ToString("N"),
                    expiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
                    capabilities = new[] { new { id = "mail_search", writes = false, activity = "Searching…",
                        arguments = new Dictionary<string, string> { ["query"] = "search terms" } } }
                }));
            if (path.EndsWith("/execute", StringComparison.Ordinal)) ++Executions;
            else ++ServerStops;
            return Task.FromResult(JsonSerializer.SerializeToElement(new { state = "cancelled", verified = false }));
        }

        public async ValueTask DisposeAsync() => await Controller.ResetAsync();
    }
}
