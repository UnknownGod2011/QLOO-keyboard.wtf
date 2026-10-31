using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using KeyboardWtf.Services.BrowserAutomationV2;
using Xunit;

namespace KeyboardWtf.Tests;

// Offline protocol replay only: no native host, Chrome, provider, or live action.
public sealed class BrowserHelperTimingDiagnosticsTests
{
    private const string TimingSource = "direct-tab-fastpath-1";
    private static readonly string[] NumericFields =
    [
        "cycle", "stageMs", "prePollQueryMs", "pollRoundTripMs", "authority1Ms",
        "authority2Ms", "createMs", "resultQueryMs", "resultQueryRetryMs",
        "nativeQueueDepth", "workerTotalMs",
    ];

    [Fact]
    public void Timing_logging_is_enabled_only_in_the_private_personal_candidate_build()
    {
        // The test project does not inherit the application's DefineConstants.
        // Its existing compiled pipe identity distinguishes the actual referenced build.
        var personalCandidate = string.Equals(BrowserHelperProtocol.PipeName,
            "keyboard.wtf.browser.v2.personal-api-candidate", StringComparison.Ordinal);
        Assert.Equal(personalCandidate, BrowserHelperBridge.PrivateTimingDiagnosticsEnabled);

        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "Services",
            "BrowserAutomationV2", "BrowserHelperBridge.cs"));
        var property = source.IndexOf("internal static bool PrivateTimingDiagnosticsEnabled", StringComparison.Ordinal);
        Assert.True(property >= 0, "The logging gate must be an explicit internal build property.");
        var gate = Regex.Match(source[property..],
            @"#if KEYBOARD_WTF_PERSONAL_API_CANDIDATE\s+(?<enabled>.*?)#else\s+(?<disabled>.*?)#endif",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);
        Assert.True(gate.Success, "Ordinary builds must have an explicit disabled preprocessor branch.");
        Assert.Matches(@"\btrue\b", gate.Groups["enabled"].Value);
        Assert.Matches(@"\bfalse\b", gate.Groups["disabled"].Value);
    }

    [Theory]
    [InlineData("pre_poll_query")]
    [InlineData("poll")]
    [InlineData("dispatch")]
    [InlineData("authority_1")]
    [InlineData("authority_2")]
    [InlineData("create")]
    [InlineData("result_query")]
    [InlineData("result_query_retry")]
    [InlineData("result_send")]
    public void Only_allowlisted_timing_stages_are_formatted(string stage)
    {
        var fields = FormatFields(JsonSerializer.Serialize(new
        {
            directTiming = new { source = TimingSource, stage, cycle = 7, stageMs = 12.3456 },
        }));
        Assert.Equal(TimingSource, fields["source"]);
        Assert.Equal(stage, fields["workerStage"]);
        Assert.Equal(7, ParseNumber(fields["cycle"]));
        Assert.Equal(12.35, ParseNumber(fields["stageMs"]));
        Assert.Equal(4, fields.Count);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("\"private content\"")]
    [InlineData("{}")]
    [InlineData("{\"directTiming\":null}")]
    [InlineData("{\"directTiming\":[]}")]
    [InlineData("{\"directTiming\":\"https://private.example/secret\"}")]
    [InlineData("{\"directTiming\":{\"stage\":\"poll\",\"cycle\":1}}")]
    [InlineData("{\"directTiming\":{\"source\":\"direct-tab-fastpath-2\",\"stage\":\"poll\"}}")]
    [InlineData("{\"directTiming\":{\"source\":\"DIRECT-TAB-FASTPATH-1\",\"stage\":\"poll\"}}")]
    [InlineData("{\"directTiming\":{\"source\":\"direct-tab-fastpath-1\\nsecret\",\"stage\":\"poll\"}}")]
    [InlineData("{\"directTiming\":{\"source\":{},\"stage\":\"poll\"}}")]
    [InlineData("{\"directTiming\":{\"source\":\"direct-tab-fastpath-1\"}}")]
    [InlineData("{\"directTiming\":{\"source\":\"direct-tab-fastpath-1\",\"stage\":\"unknown\"}}")]
    [InlineData("{\"directTiming\":{\"source\":\"direct-tab-fastpath-1\",\"stage\":\"POLL\"}}")]
    [InlineData("{\"directTiming\":{\"source\":\"direct-tab-fastpath-1\",\"stage\":\"poll\\r\\nurl=https://private.example/secret\"}}")]
    [InlineData("{\"directTiming\":{\"source\":\"direct-tab-fastpath-1\",\"stage\":42}}")]
    public void Missing_malformed_or_unrecognized_metadata_is_ignored(string json)
    {
        Assert.True(string.IsNullOrEmpty(Format(json)));
    }

    [Fact]
    public void Timing_output_is_invariant_rounded_and_contains_no_page_or_command_content()
    {
        var timing = NumericFields.ToDictionary(field => field, _ => (object)12.3456);
        timing["source"] = TimingSource;
        timing["stage"] = "result_send";
        timing["url"] = "https://private.example/secret";
        timing["title"] = "confidential page title";
        timing["tabId"] = 998877;
        timing["windowId"] = 887766;
        timing["taskId"] = "private task identity";
        timing["commandId"] = "private command identity";
        timing["args"] = new { text = "private selected content" };
        timing["error"] = "exception containing credentials";
        timing["unexpected"] = "\r\nforged-log=true";
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var fields = FormatFields(JsonSerializer.Serialize(new
            {
                directTiming = timing,
                url = "https://root.example/secret",
                result = new { text = "private result content" },
            }));
            Assert.Equal(TimingSource, fields["source"]);
            Assert.Equal("result_send", fields["workerStage"]);
            Assert.Equal(NumericFields.Length + 2, fields.Count);
            foreach (var field in NumericFields)
            {
                Assert.Matches(@"^\d+(?:\.\d{1,2})?$", fields[field]);
                Assert.Equal(12.35, ParseNumber(fields[field]));
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("120000", 120000)]
    [InlineData("119999.999", 120000)]
    [InlineData("0.004", 0)]
    public void Every_numeric_field_accepts_only_bounded_numbers_and_rounds_to_two_places(string number, double expected)
    {
        foreach (var field in NumericFields)
        {
            var fields = FormatFields(TimingWithField(field, number));
            Assert.Equal(expected, ParseNumber(fields[field]));
            Assert.Equal(3, fields.Count);
        }
    }

    [Theory]
    [InlineData("-0.001")]
    [InlineData("120000.001")]
    [InlineData("1e999")]
    [InlineData("-1e999")]
    [InlineData("\"NaN\"")]
    [InlineData("\"Infinity\"")]
    [InlineData("\"12.34\"")]
    [InlineData("\"0\\nurl=https://private.example/secret\"")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("[]")]
    [InlineData("{\"secret\":\"private content\"}")]
    public void Invalid_numeric_values_cannot_be_logged_or_coerced(string value)
    {
        foreach (var field in NumericFields)
        {
            var fields = FormatFields(TimingWithField(field, value));
            Assert.False(fields.ContainsKey(field), $"Invalid {field} must be omitted.");
            Assert.Equal(2, fields.Count);
        }
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Timing_metadata_cannot_bypass_extension_or_command_authority(bool trustedIdentity, bool supportsAuthority)
    {
        using var bridge = new BrowserHelperBridge();
        Process(bridge, "hello");
        using var cancellation = new CancellationTokenSource();
        var action = new BrowserActionRequest { TaskId = "owned-task", Type = "open_direct_tab" };
        var pending = SendRaw(bridge, action, cancellation.Token);
        var timing = new
        {
            source = TimingSource, stage = "dispatch", cycle = 1, stageMs = 0,
            extensionId = BrowserHelperProtocol.StoreExtensionId,
            commandAuthoritySupported = true, commandAuthorized = true,
            taskId = action.TaskId, commandId = action.CommandId,
        };

        // Each row disables one real gate while metadata claims both are valid.
        var extensionId = trustedIdentity ? BrowserHelperProtocol.StoreExtensionId : "untrusted-extension";
        Assert.Equal(JsonValueKind.Null, Process(bridge, "poll", timing: timing,
            extensionId: extensionId, supportsAuthority: supportsAuthority).GetProperty("command").ValueKind);
        Assert.False(Process(bridge, "command_check", action, timing: timing,
            extensionId: extensionId, supportsAuthority: supportsAuthority).GetProperty("commandAuthorized").GetBoolean());
        Process(bridge, "command_result", action, new { ok = true }, timing,
            extensionId, supportsAuthority);
        Assert.False(pending.IsCompleted);

        Assert.Equal(action.CommandId,
            Process(bridge, "poll", timing: timing).GetProperty("command").GetProperty("commandId").GetString());
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(Process(bridge, "command_check", action, timing: timing).GetProperty("commandAuthorized").GetBoolean());
    }

    [Fact]
    public async Task Timing_metadata_does_not_dequeue_extra_commands_or_change_result_payloads()
    {
        using var bridge = new BrowserHelperBridge();
        Process(bridge, "hello");
        using var cancellation = new CancellationTokenSource();
        var first = new BrowserActionRequest { TaskId = "first-task", Type = "open_direct_tab" };
        var second = new BrowserActionRequest { TaskId = "second-task", Type = "open_direct_tab" };
        var firstPending = SendRaw(bridge, first, cancellation.Token);
        var secondPending = SendRaw(bridge, second, cancellation.Token);
        var timing = new
        {
            source = TimingSource, stage = "result_send", cycle = 3, workerTotalMs = 20,
            type = "poll", commandId = second.CommandId, taskId = second.TaskId,
            result = new { ok = false, error = "metadata must not replace the result" },
        };

        Assert.Equal(JsonValueKind.Null, Process(bridge, "heartbeat", timing: timing).GetProperty("command").ValueKind);
        Assert.Equal(first.CommandId,
            Process(bridge, "poll", timing: timing).GetProperty("command").GetProperty("commandId").GetString());
        var authority = Process(bridge, "command_check", first, timing: timing);
        Assert.True(authority.GetProperty("commandAuthorized").GetBoolean());
        Assert.Equal(JsonValueKind.Null, authority.GetProperty("command").ValueKind);
        Assert.False(secondPending.IsCompleted);

        using var expected = JsonDocument.Parse("""
            {"ok":true,"afterUrl":"https://requested.example/path","tabId":123,"windowId":7,
             "custom":{"text":"unaltered result content","values":[1,false,null]}}
            """);
        var firstAck = Process(bridge, "command_result", first, expected.RootElement, timing);
        Assert.Equal(JsonValueKind.Null, firstAck.GetProperty("command").ValueKind);
        Assert.Equal(JsonSerializer.Serialize(expected.RootElement),
            JsonSerializer.Serialize(await firstPending.WaitAsync(TimeSpan.FromSeconds(2))));
        Assert.False(secondPending.IsCompleted);
        Assert.False(Process(bridge, "command_check", first, timing: timing).GetProperty("commandAuthorized").GetBoolean());
        Assert.Equal(second.CommandId,
            Process(bridge, "poll", timing: timing).GetProperty("command").GetProperty("commandId").GetString());
        Process(bridge, "command_result", second, new { ok = false, error = "original failure" }, timing);
        var secondResult = await secondPending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(secondResult.GetProperty("ok").GetBoolean());
        Assert.Equal("original failure", secondResult.GetProperty("error").GetString());
        Assert.Equal(JsonValueKind.Null, Process(bridge, "poll", timing: timing).GetProperty("command").ValueKind);
    }

    private static string TimingWithField(string field, string value) =>
        "{\"directTiming\":{\"source\":\"" + TimingSource + "\",\"stage\":\"poll\",\"" + field + "\":" + value + "}}";

    private static string Format(string json)
    {
        using var document = JsonDocument.Parse(json);
        return BrowserHelperBridge.FormatPrivateTimingMetadata(document.RootElement);
    }

    private static Dictionary<string, string> FormatFields(string json) => Format(json)
        .Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Select(token => token.Split('=', 2))
        .ToDictionary(pair => pair[0], pair => pair[1], StringComparer.Ordinal);

    private static double ParseNumber(string number) => double.Parse(number, CultureInfo.InvariantCulture);

    private static Task<JsonElement> SendRaw(BrowserHelperBridge bridge, BrowserActionRequest action, CancellationToken token)
    {
        var send = typeof(BrowserHelperBridge).GetMethod("SendAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (Task<JsonElement>)send.Invoke(bridge, [action, token, null])!;
    }

    private static JsonElement Process(BrowserHelperBridge bridge, string type, BrowserActionRequest action = null,
        object result = null, object timing = null, string extensionId = BrowserHelperProtocol.StoreExtensionId,
        bool supportsAuthority = true)
    {
        var json = JsonSerializer.Serialize(new
        {
            type, taskId = action?.TaskId, commandId = action?.CommandId, result, directTiming = timing,
            helper = new
            {
                extensionId, serviceWorkerVersion = BrowserHelperProtocol.Version,
                contentScriptVersion = BrowserHelperProtocol.Version, activeTabFresh = true,
                commandChannelReady = true, commandAuthoritySupported = supportsAuthority,
                activeUrl = "https://example.org/", sessionId = "offline-timing-test",
            },
        });
        var process = typeof(BrowserHelperBridge).GetMethod("ProcessMessage", BindingFlags.NonPublic | BindingFlags.Instance)!;
        using var response = JsonDocument.Parse((string)process.Invoke(bridge, [json])!);
        return response.RootElement.Clone();
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            if ((Directory.Exists(Path.Combine(directory.FullName, ".git"))
                    || File.Exists(Path.Combine(directory.FullName, ".git")))
                && Directory.Exists(Path.Combine(directory.FullName, "extensions")))
                return directory.FullName;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
