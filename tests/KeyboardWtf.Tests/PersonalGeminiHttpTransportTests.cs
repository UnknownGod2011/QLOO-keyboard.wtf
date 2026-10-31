using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using KeyboardWtf.Services.Ai.Backends;
using Xunit;

namespace KeyboardWtf.Tests;

public sealed class PersonalGeminiHttpTransportTests : IDisposable
{
    private const string Canary = "synthetic-personal-transport-secret-1234";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "keyboard-wtf-personal-http-" + Guid.NewGuid());
    private readonly PersonalApiVault _vault;
    private readonly AiBackendRouter _router;
    public PersonalGeminiHttpTransportTests()
    {
        _vault = new(Path.Combine(_directory, "personal.bin"));
        var view = _vault.Add(0, "test", Canary, PersonalKeyValidation.AuthenticationValidated);
        _vault.SetPolicy(view.Revision, AiBackendPolicy.PersonalOnly);
        _router = new(_vault, _vault);
    }

    [Fact]
    public async Task Personal_text_schema_image_audio_requests_bypass_managed_and_keep_feature_payloads()
    {
        var managed = 0;
        var calls = 0;
        var prompt = "Private fixture\n日本語 😀";
        using var transport = new PersonalGeminiHttpTransport(_vault, new Handler(async (request, token) =>
        {
            calls++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https", request.RequestUri.Scheme);
            Assert.Equal("generativelanguage.googleapis.com", request.RequestUri.Host);
            Assert.Empty(request.RequestUri.Query);
            Assert.DoesNotContain(Canary, request.RequestUri.ToString());
            Assert.Equal(Canary, Assert.Single(request.Headers.GetValues("x-goog-api-key")));
            var serialized = await request.Content.ReadAsStringAsync(token);
            Assert.DoesNotContain(Canary, serialized);
            using var payload = JsonDocument.Parse(serialized);
            var root = payload.RootElement;
            Assert.Equal(prompt, root.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString());
            Assert.Equal("Existing instruction", root.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString());
            Assert.Equal(1500, root.GetProperty("generationConfig").GetProperty("maxOutputTokens").GetInt32());
            Assert.Equal(0.3, root.GetProperty("generationConfig").GetProperty("temperature").GetDouble());
            return Reply(200, Complete("{\"result\":\"ok\"}"));
        }));
        using var operation = _router.BeginOperation();
        foreach (var capability in new[] { AiCapability.Text, AiCapability.StructuredPlanning, AiCapability.BrowserPlanning, AiCapability.Images, AiCapability.AudioWriting })
        {
            var request = new AiGenerationRequest(prompt, "Existing instruction", 1500,
                capability == AiCapability.StructuredPlanning ? new { type = "object" } : null,
                capability == AiCapability.Images ? new[] { new AiInlinePart("image/jpeg", "AQID") }
                    : capability == AiCapability.AudioWriting ? new[] { new AiInlinePart("audio/wav", "AQID") } : null);
            var result = await _router.ExecuteAsync(operation, capability,
                _ => { managed++; throw new Exception("Managed must not be entered"); },
                (key, model, token) => transport.GenerateAsync(key, model, request, token));
            Assert.Equal("{\"result\":\"ok\"}", result.Text);
            Assert.Equal("STOP", result.FinishReason);
        }
        Assert.Equal(5, calls);
        Assert.Equal(0, managed);
    }

    [Fact]
    public void Schema_and_multimodal_data_are_serialized_without_losing_order_or_content()
    {
        using var payload = JsonDocument.Parse(PersonalGeminiHttpTransport.BuildPayload(new("text", null, 1024,
            new { type = "object", properties = new { value = new { type = "string" } } },
            new[] { new AiInlinePart("image/png", "AQID"), new AiInlinePart("audio/wav", "BAUG") })));
        var parts = payload.RootElement.GetProperty("contents")[0].GetProperty("parts");
        Assert.Equal("AQID", parts[1].GetProperty("inlineData").GetProperty("data").GetString());
        Assert.Equal("audio/wav", parts[2].GetProperty("inlineData").GetProperty("mimeType").GetString());
        var config = payload.RootElement.GetProperty("generationConfig");
        Assert.Equal("application/json", config.GetProperty("responseMimeType").GetString());
        Assert.Equal("string", config.GetProperty("responseJsonSchema").GetProperty("properties").GetProperty("value").GetProperty("type").GetString());
    }

    [Fact]
    public void Full_selection_output_allowance_is_isolated_from_existing_generation_requests()
    {
        Assert.Throws<ArgumentException>(() => PersonalGeminiHttpTransport.BuildPayload(new("text", null, 8193)));
        Assert.Throws<ArgumentException>(() => PersonalGeminiHttpTransport.BuildPayload(new("text", null, 65537, CompleteSelectedText: true)));
        using var payload = JsonDocument.Parse(PersonalGeminiHttpTransport.BuildPayload(
            new(new string('x', 50000), "Translate the full document.", 65536, CompleteSelectedText: true)));
        Assert.Equal(50000, payload.RootElement.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString()!.Length);
        Assert.Equal(65536, payload.RootElement.GetProperty("generationConfig").GetProperty("maxOutputTokens").GetInt32());
    }

    [Fact]
    public void Complete_selection_uses_existing_byte_envelope_not_an_arbitrary_character_ceiling()
    {
        var source = string.Concat(Enumerable.Repeat("\r\nमराठी 日本語 👋\n", 15000));
        Assert.True(source.Length > 200000);
        using var payload = JsonDocument.Parse(PersonalGeminiHttpTransport.BuildPayload(
            new(source, "Translate the full document.", 65536, CompleteSelectedText: true)));
        Assert.Equal(source, payload.RootElement.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString());
        Assert.Throws<ArgumentException>(() => PersonalGeminiHttpTransport.BuildPayload(new(new string('x', 200001), null, 8192)));
        Assert.Throws<ArgumentException>(() => PersonalGeminiHttpTransport.BuildPayload(
            new(new string('x', 20_000_000), null, 65536, CompleteSelectedText: true)));
    }

    [Theory]
    [InlineData("MAX_TOKENS", 7)]
    [InlineData("SAFETY", 6)]
    [InlineData("RECITATION", 6)]
    [InlineData("OTHER", 5)]
    [InlineData("MALFORMED_FUNCTION_CALL", 5)]
    public async Task Incomplete_or_blocked_output_is_never_presented_as_success(string reason, int failure)
    {
        var calls = 0;
        using var transport = new PersonalGeminiHttpTransport(_vault, new Handler((_, _) =>
        { calls++; return Task.FromResult(Reply(200, Complete("partial " + Canary, reason))); }));
        var error = await Assert.ThrowsAsync<PersonalAiException>(() => Run(transport));
        Assert.Equal((PersonalAiFailure)failure, error.Failure);
        Assert.Equal(200, error.HttpStatusCode);
        Assert.DoesNotContain(Canary, error.ToString());
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"candidates\":[]}")]
    [InlineData("{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"partial\"}]}}]}")]
    [InlineData("{\"candidates\":[{\"finishReason\":\"STOP\",\"content\":{\"parts\":[{\"functionCall\":{}}]}}]}")]
    [InlineData("not json")]
    public async Task Malformed_missing_finish_or_tool_content_is_not_retried_or_executed(string body)
    {
        var calls = 0;
        using var transport = new PersonalGeminiHttpTransport(_vault, new Handler((_, _) =>
        { calls++; return Task.FromResult(Reply(200, body)); }));
        Assert.Equal(PersonalAiFailure.InvalidResponse, (await Assert.ThrowsAsync<PersonalAiException>(() => Run(transport))).Failure);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Prompt_block_is_truthful_and_thought_parts_are_not_user_output()
    {
        using var blocked = new PersonalGeminiHttpTransport(_vault, new Handler((_, _) => Task.FromResult(Reply(200,
            "{\"promptFeedback\":{\"blockReason\":\"SAFETY\"}}"))));
        Assert.Equal(PersonalAiFailure.Blocked, (await Assert.ThrowsAsync<PersonalAiException>(() => Run(blocked))).Failure);
        using var thought = new PersonalGeminiHttpTransport(_vault, new Handler((_, _) => Task.FromResult(Reply(200,
            "{\"candidates\":[{\"finishReason\":\"STOP\",\"content\":{\"parts\":[{\"thought\":true,\"text\":\"internal\"},{\"text\":\"answer\"}]}}]}"))));
        Assert.Equal("answer", (await Run(thought)).Text);
    }

    [Fact]
    public async Task Unknown_scope_quota_cools_entire_pool_including_existing_owner_without_rotation()
    {
        var view = _vault.GetView();
        _vault.Add(view.Revision, "second", "synthetic-second-secret-5678", PersonalKeyValidation.AuthenticationValidated);
        var calls = 0;
        using var transport = new PersonalGeminiHttpTransport(_vault, new Handler((_, _) =>
        { calls++; return Task.FromResult(Reply(429, Canary)); }));
        using var operation = _router.BeginOperation();
        Assert.Equal(PersonalAiFailure.Quota, (await Assert.ThrowsAsync<PersonalAiException>(() => Run(transport, operation))).Failure);
        await Assert.ThrowsAsync<PersonalApiCooldownException>(() => Run(transport, operation));
        Assert.Throws<PersonalApiCooldownException>(() => _router.BeginOperation());
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(401, 0, true)]
    [InlineData(403, 1, false)]
    [InlineData(404, 1, false)]
    [InlineData(302, 3, false)]
    [InlineData(400, 3, false)]
    [InlineData(500, 3, false)]
    [InlineData(503, 3, false)]
    public async Task Credentials_and_capabilities_are_classified_without_leaking_provider_errors(int status, int failure, bool disabled)
    {
        var calls = 0;
        using var transport = new PersonalGeminiHttpTransport(_vault, new Handler((_, _) =>
        {
            calls++;
            var reply = Reply(status, JsonSerializer.Serialize(new { error = new { message = Canary } }));
            reply.Headers.Location = new Uri("https://unrelated.invalid/");
            return Task.FromResult(reply);
        }));
        var error = await Assert.ThrowsAsync<PersonalAiException>(() => Run(transport));
        Assert.Equal((PersonalAiFailure)failure, error.Failure);
        Assert.Equal(status, error.HttpStatusCode);
        Assert.DoesNotContain(Canary, error.ToString());
        Assert.Equal(!disabled, _vault.GetView().Keys[0].Enabled);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Definitive_invalid_key_rejection_disables_owned_slot_without_replaying_request()
    {
        using var transport = new PersonalGeminiHttpTransport(_vault, new Handler((_, _) => Task.FromResult(Reply(400,
            "{\"error\":{\"details\":[{\"reason\":\"API_KEY_INVALID\"}]}}"))));
        Assert.Equal(PersonalAiFailure.Credential, (await Assert.ThrowsAsync<PersonalAiException>(() => Run(transport))).Failure);
        Assert.False(_vault.GetView().Keys[0].Enabled);
    }

    [Fact]
    public async Task Ambiguous_transport_and_internal_deadline_never_retry_or_leak_secret()
    {
        using var network = new PersonalGeminiHttpTransport(_vault, new Handler((_, _) => throw new HttpRequestException(Canary)));
        using var timeout = new PersonalGeminiHttpTransport(_vault, new Handler(async (_, token) =>
        { await Task.Delay(Timeout.Infinite, token); return Reply(200, Complete("late")); }), TimeSpan.FromMilliseconds(20));
        foreach (var transport in new[] { network, timeout })
        {
            var error = await Assert.ThrowsAsync<PersonalAiException>(() => Run(transport));
            Assert.Equal(PersonalAiFailure.AmbiguousTransport, error.Failure);
            Assert.Null(error.HttpStatusCode);
            Assert.DoesNotContain(Canary, error.ToString());
        }
    }

    [Fact]
    public async Task Caller_cancel_rejects_late_success_and_sends_only_once()
    {
        using var cancellation = new CancellationTokenSource();
        using var operation = _router.BeginOperation(cancellation.Token);
        var calls = 0;
        using var transport = new PersonalGeminiHttpTransport(_vault, new Handler((_, _) =>
        { calls++; cancellation.Cancel(); return Task.FromResult(Reply(200, Complete("late"))); }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(transport, operation));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Image_timings_are_correlated_numeric_and_content_free_without_changing_the_payload()
    {
        var timings = new List<string>();
        const string prompt = "Private image instruction";
        const string media = "AQIDBAUG";
        const string answer = "Private screen answer";
        using var transport = new PersonalGeminiHttpTransport(_vault, new Handler(async (request, token) =>
        {
            using var payload = JsonDocument.Parse(await request.Content.ReadAsStringAsync(token));
            var parts = payload.RootElement.GetProperty("contents")[0].GetProperty("parts");
            Assert.Equal(prompt, parts[0].GetProperty("text").GetString());
            Assert.Equal(media, parts[1].GetProperty("inlineData").GetProperty("data").GetString());
            return Reply(200, Complete(answer));
        }), diagnostics: timings.Add);

        Assert.Equal(answer, (await RunImage(transport, prompt, media)).Text);
        var stages = new List<string>();
        var requests = new HashSet<string>();
        foreach (var timing in timings)
        {
            var fields = Regex.Match(timing,
                @"^Personal image request timing: request=([0-9a-f]{32}) stage=([a-z-]+) elapsedMs=([0-9]+)$");
            Assert.True(fields.Success, timing);
            requests.Add(fields.Groups[1].Value);
            stages.Add(fields.Groups[2].Value);
            Assert.DoesNotContain(Canary, timing);
            Assert.DoesNotContain(prompt, timing);
            Assert.DoesNotContain(media, timing);
            Assert.DoesNotContain(answer, timing);
        }
        Assert.Single(requests);
        Assert.Equal(new[] { "payload", "request-setup", "response-headers-start", "response-headers",
            "response-body", "response-parse", "total" }, stages);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Non_image_requests_do_not_publish_image_timings(bool audio)
    {
        var timings = new List<string>();
        using var transport = new PersonalGeminiHttpTransport(_vault,
            new Handler((_, _) => Task.FromResult(Reply(200, Complete("answer")))), diagnostics: timings.Add);
        using var lease = _vault.Acquire(_vault.ReadConfiguration());
        Assert.Equal("answer", (await transport.GenerateAsync(lease, PersonalGeminiModels.Text,
            new("text", null, 16, Media: audio ? new[] { new AiInlinePart("audio/wav", "AQID") } : null), default)).Text);
        Assert.Empty(timings);
    }

    [Fact]
    public async Task Image_diagnostic_sink_failure_cannot_change_success_or_failure_classification()
    {
        foreach (var status in new[] { 200, 503 })
        {
            var calls = 0;
            using var transport = new PersonalGeminiHttpTransport(_vault, new Handler((_, _) =>
            {
                calls++;
                return Task.FromResult(Reply(status, Complete("answer")));
            }), diagnostics: _ => throw new InvalidOperationException("Diagnostic sink failed"));
            if (status == 200) Assert.Equal("answer", (await RunImage(transport)).Text);
            else
            {
                var failure = await Assert.ThrowsAsync<PersonalAiException>(() => RunImage(transport));
                Assert.Equal(PersonalAiFailure.Provider, failure.Failure);
                Assert.Equal(503, failure.HttpStatusCode);
            }
            Assert.Equal(1, calls);
        }
    }

    [Fact]
    public async Task Image_body_wait_cancellation_reports_the_waiting_stage_without_retry_or_parse()
    {
        var bodyStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timings = new List<string>();
        var calls = 0;
        using var cancellation = new CancellationTokenSource();
        using var transport = new PersonalGeminiHttpTransport(_vault, new Handler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StreamContent(new WaitingReadStream(bodyStarted)) });
        }), diagnostics: timings.Add);
        var pending = RunImage(transport, token: cancellation.Token);
        await bodyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, calls);
        Assert.Contains(timings, timing => timing.Contains("stage=response-headers elapsedMs="));
        Assert.Contains(timings, timing => timing.Contains("stage=response-body-incomplete elapsedMs="));
        Assert.DoesNotContain(timings, timing => timing.Contains("stage=response-parse"));
        Assert.EndsWith("stage=total elapsedMs=" + timings.Last().Split("elapsedMs=")[1], timings.Last());
    }

    [Fact]
    public async Task Unsupported_model_is_rejected_before_key_can_be_sent()
    {
        using var transport = new PersonalGeminiHttpTransport(_vault, new Handler((_, _) => throw new Xunit.Sdk.XunitException("Unexpected send")));
        using var lease = _vault.Acquire(_vault.ReadConfiguration());
        Assert.Equal(PersonalAiFailure.Capability, (await Assert.ThrowsAsync<PersonalAiException>(() =>
            transport.GenerateAsync(lease, "https://unrelated.invalid/" + Canary, new("test", null, 16), default))).Failure);
    }

    private async Task<PersonalGenerationResult> Run(PersonalGeminiHttpTransport transport, AiBackendOperation existing = null)
    {
        using var own = existing is null ? _router.BeginOperation() : null;
        return await _router.ExecuteAsync(existing ?? own, AiCapability.Text,
            _ => throw new Xunit.Sdk.XunitException("Managed dispatch forbidden"),
            (key, model, token) => transport.GenerateAsync(key, model, new("test", null, 16), token));
    }

    private async Task<PersonalGenerationResult> RunImage(PersonalGeminiHttpTransport transport,
        string prompt = "test", string media = "AQID", CancellationToken token = default)
    {
        using var operation = _router.BeginOperation(token);
        return await _router.ExecuteAsync(operation, AiCapability.Images,
            _ => throw new Xunit.Sdk.XunitException("Managed dispatch forbidden"),
            (key, model, cancellation) => transport.GenerateAsync(key, model,
                new(prompt, null, 16, Media: new[] { new AiInlinePart("image/jpeg", media) }), cancellation));
    }

    private sealed class WaitingReadStream(TaskCompletionSource started) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private static HttpResponseMessage Reply(int status, string body) => new((HttpStatusCode)status) { Content = new StringContent(body) };
    private static string Complete(string text, string finish = "STOP") => JsonSerializer.Serialize(new
        { candidates = new[] { new { finishReason = finish, content = new { parts = new[] { new { text } } } } }, usageMetadata = new { totalTokenCount = 8 } });
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
