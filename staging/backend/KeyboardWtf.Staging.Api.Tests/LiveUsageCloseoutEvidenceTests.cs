using KeyboardWtf.Staging.Api.Domain;
using KeyboardWtf.Staging.Api.Services;
using Microsoft.Extensions.Logging;
using Xunit;

namespace KeyboardWtf.Staging.Api.Tests;

public sealed class LiveUsageCloseoutEvidenceTests
{
    private const string FirstMetadata = """{"usageMetadata":{"promptTokenCount":10,"candidatesTokenCount":5,"totalTokenCount":15}}""";
    private const string SecondMetadata = """{"usageMetadata":{"promptTokenCount":20,"candidatesTokenCount":10,"totalTokenCount":30}}""";

    [Fact]
    public void Empty_and_rejected_metadata_are_distinguishable_without_inventing_known_usage()
    {
        var usage = new LiveUsageAccumulator(1_000_000);
        Assert.Equal(0, usage.CloseoutEvidence().AcceptedMetadata);
        Assert.Equal(0, usage.CloseoutEvidence().RejectedMetadata);
        Assert.False(usage.Snapshot().Known);
        usage.ObserveProvider("""{"usageMetadata":{}}""");
        Assert.Equal(0, usage.CloseoutEvidence().AcceptedMetadata);
        Assert.Equal(1, usage.CloseoutEvidence().RejectedMetadata);
        Assert.False(usage.Snapshot().Known);
    }

    [Fact]
    public void Fifty_local_cycles_preserve_usage_and_show_the_actual_uncovered_tail()
    {
        for (var i = 0; i < 50; i++)
        {
            var usage = new LiveUsageAccumulator(1_000_000);
            usage.ObserveClient("""{"clientContent":{"turns":[{"parts":[{"text":"synthetic question"}]}]}}""");
            usage.ObserveProvider(FirstMetadata);
            AssertEvidenceDoesNotChangeUsage(usage, true);
            usage.ObserveProvider("""{"serverContent":{"modelTurn":{"parts":[{"text":"later response"}]}}}""");
            AssertEvidenceDoesNotChangeUsage(usage, false);
            var outputTail = usage.CloseoutEvidence();
            Assert.Equal(1, outputTail.OutputGeneration);
            Assert.Equal(0, outputTail.CoveredOutputGeneration);
            Assert.Equal(outputTail.InputGeneration, outputTail.CoveredInputGeneration);
            usage.ObserveProvider(SecondMetadata);
            AssertEvidenceDoesNotChangeUsage(usage, true);
            usage.ObserveClient("""{"toolResponse":{"functionResponses":[{"id":"synthetic","name":"end_conversation","response":{"ok":true}}]}}""");
            AssertEvidenceDoesNotChangeUsage(usage, false);
            var inputTail = usage.CloseoutEvidence();
            Assert.Equal(2, inputTail.InputGeneration);
            Assert.Equal(1, inputTail.CoveredInputGeneration);
            Assert.Equal(inputTail.OutputGeneration, inputTail.CoveredOutputGeneration);
            Assert.Equal(2, inputTail.AcceptedMetadata);
            Assert.Equal(0, inputTail.RejectedMetadata);
            Assert.Equal(LiveAccountingDisposition.Reconcile,
                VertexLiveProxyService.DetermineAccountingDisposition(true,
                    usage.Snapshot().TotalTokens, usage.Snapshot().Known));
        }
    }

    [Fact]
    public void Invalid_later_metadata_cannot_weaken_the_existing_authoritative_snapshot()
    {
        var usage = new LiveUsageAccumulator(1_000_000);
        usage.ObserveProvider(FirstMetadata);
        var exact = usage.Snapshot();
        usage.ObserveProvider("""{"usageMetadata":{"promptTokenCount":999,"totalTokenCount":1}}""");
        Assert.Equal(exact, usage.Snapshot());
        Assert.Equal(1, usage.CloseoutEvidence().AcceptedMetadata);
        Assert.Equal(1, usage.CloseoutEvidence().RejectedMetadata);
    }

    [Fact]
    public void Audio_counts_are_sizes_not_retained_or_decoded_media()
    {
        var usage = new LiveUsageAccumulator(1_000_000);
        usage.ObserveClient("""{"realtimeInput":{"audio":{"data":"AQID"}}}""");
        usage.ObserveProvider("""{"serverContent":{"modelTurn":{"parts":[{"inlineData":{"data":"AQIDBA=="}}]}}}""");
        var evidence = usage.CloseoutEvidence();
        Assert.Equal(3, evidence.InputAudioBytes);
        Assert.Equal(4, evidence.OutputAudioBytes);
        Assert.DoesNotContain("AQID", System.Text.Json.JsonSerializer.Serialize(evidence));
    }

    [Fact]
    public void Closeout_warning_contains_only_numeric_evidence_and_a_request_hash()
    {
        const string privateText = "synthetic private selected source";
        const string requestId = "synthetic-private-request-reference";
        var usage = new LiveUsageAccumulator(1_000_000);
        usage.ObserveClient("{\"clientContent\":{\"turns\":[{\"parts\":[{\"text\":\"" + privateText + "\"}]}]}}");
        usage.ObserveProvider("""{"responseId":"synthetic-provider-private-id","modelVersion":"synthetic-version","usageMetadata":{"promptTokenCount":10,"candidatesTokenCount":5,"totalTokenCount":15}}""");
        usage.ObserveProvider("""{"serverContent":{"modelTurn":{"parts":[{"text":"synthetic output"}]}}}""");
        var snapshot = usage.Snapshot();
        var logger = new CaptureLogger();
        VertexLiveProxyService.LogIncompleteAccounting(logger, requestId, usage.CloseoutEvidence(), snapshot);
        Assert.Equal(LogLevel.Warning, logger.Level);
        Assert.Equal(4401, logger.Event.Id);
        Assert.Equal("LiveAccountingPending", logger.Event.Name);
        Assert.Equal(1, logger.Calls);
        Assert.Equal(17, logger.Values.Count);
        foreach (var value in logger.Values)
        {
            if (value.Key == "{OriginalFormat}") continue;
            if (value.Key == "RequestHash")
            {
                var hash = Assert.IsType<string>(value.Value);
                Assert.Matches("^[0-9a-f]{12}$", hash);
            }
            else Assert.True(value.Value is long or bool);
        }
        Assert.DoesNotContain(privateText, logger.Message);
        Assert.DoesNotContain(requestId, logger.Message);
        Assert.DoesNotContain("synthetic-provider-private-id", logger.Message);
        Assert.DoesNotContain("synthetic-version", logger.Message);
        Assert.DoesNotContain("synthetic output", logger.Message);
        Assert.Equal(snapshot, usage.Snapshot());
    }

    [Fact]
    public void A_diagnostic_logger_failure_cannot_interrupt_the_existing_closeout()
    {
        var usage = new LiveUsageAccumulator(1_000_000);
        usage.ObserveProvider(FirstMetadata);
        usage.ObserveClient("""{"realtimeInput":{"text":"unreported tail"}}""");
        var before = usage.Snapshot();
        VertexLiveProxyService.LogIncompleteAccounting(
            new ThrowingLogger(), "synthetic-request", usage.CloseoutEvidence(), before);
        Assert.Equal(before, usage.Snapshot());
        Assert.Equal(LiveAccountingDisposition.Reconcile,
            VertexLiveProxyService.DetermineAccountingDisposition(true, before.TotalTokens, before.Known));
    }

    private static void AssertEvidenceDoesNotChangeUsage(LiveUsageAccumulator usage, bool known)
    {
        var before = usage.Snapshot();
        Assert.Equal(known, before.Known);
        _ = usage.CloseoutEvidence();
        Assert.Equal(before, usage.Snapshot());
    }

    private sealed class CaptureLogger : ILogger<VertexLiveProxyService>
    {
        public int Calls { get; private set; }
        public LogLevel Level { get; private set; }
        public EventId Event { get; private set; }
        public string Message { get; private set; } = "";
        public IReadOnlyList<KeyValuePair<string, object?>> Values { get; private set; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Calls++;
            Level = logLevel;
            Event = eventId;
            Message = formatter(state, exception);
            Values = Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object?>>>(state).ToArray();
        }
    }

    private sealed class ThrowingLogger : ILogger<VertexLiveProxyService>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            throw new IOException("Synthetic diagnostic sink unavailable.");
    }
}
