using System.Reflection;
using System.Text.Json;
using KeyboardWtf.Staging.Api.Configuration;
using KeyboardWtf.Staging.Api.Contracts;
using KeyboardWtf.Staging.Api.Domain;
using KeyboardWtf.Staging.Api.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace KeyboardWtf.Staging.Api.Tests;

// Offline exact-count admission and settlement controls. The original red
// preparation is preserved in verification artifacts; these fakes never use
// real credentials, provider endpoints, accounts or desktop profiles.
public sealed partial class SecurityAndUsageTests
{
    [Fact]
    public void Exact_token_interface_preserves_the_complete_generation_input_contract()
    {
        var count = typeof(IVertexAiClient).GetMethod("CountTokensAsync");
        Assert.NotNull(count);
        Assert.Equal(typeof(Task<int>), count.ReturnType);
        var generate = typeof(IVertexAiClient).GetMethod(nameof(IVertexAiClient.GenerateAsync))!;
        Assert.Equal(generate.GetParameters().Select(parameter => parameter.ParameterType),
            count.GetParameters().Select(parameter => parameter.ParameterType));
    }

    [Fact]
    public async Task Exact_token_ordinary_affordable_selection_makes_no_count_call()
    {
        var fixture = CreateExactTokenFixture();
        var request = ExactTokenRequest("ordinary", new string('x', 50_000));
        var result = await fixture.Service.GenerateAsync(Caller, request, CancellationToken.None);

        Assert.True(result.Verified);
        Assert.Empty(fixture.Vertex.Counted);
        Assert.Equal(1, fixture.Store.ReserveCalls);
        Assert.Equal(1, fixture.Vertex.GenerateCalls);
    }

    [Fact]
    public async Task Exact_token_50k_cjk_counts_every_candidate_before_full_cap_reservation_and_generation()
    {
        var fixture = CreateExactTokenFixture();
        fixture.Vertex.Tokens["gemini-3.5-flash-lite"] = 50_000;
        fixture.Vertex.Tokens["gemini-3.1-flash-lite"] = 75_000;
        fixture.Vertex.BeforeCount = () => AssertExactTokenNoPaidWork(fixture.Store, fixture.Vertex);
        fixture.Vertex.BeforeGenerate = () =>
        {
            Assert.Equal(2, fixture.Vertex.Counted.Count);
            Assert.Equal(1, fixture.Store.ReserveCalls);
            Assert.Equal(1, fixture.Store.ProviderStartedCalls);
        };
        var request = ExactTokenRequest("cjk");
        var availableBefore = fixture.Store.Available;

        var result = await fixture.Service.GenerateAsync(Caller, request, CancellationToken.None);

        Assert.True(result.Verified);
        Assert.True(result.ContentComplete);
        Assert.Equal("complete offline translation", result.Text);
        Assert.Equal(new[] { "gemini-3.5-flash-lite", "gemini-3.1-flash-lite" },
            fixture.Vertex.Counted.Select(input => input.Model).OrderBy(model => model == "gemini-3.5-flash-lite" ? 0 : 1));
        Assert.All(fixture.Vertex.Counted, input =>
        {
            Assert.Equal("global", input.Location);
            Assert.Equal(request.Prompt, input.Prompt);
            Assert.Equal(request.SystemInstruction, input.SystemInstruction);
            Assert.Equal(65_536, input.MaxOutputTokens);
            Assert.Null(input.ResponseJsonSchema);
            Assert.Null(input.Images);
            Assert.Null(input.Audio);
        });
        Assert.Equal(ExactTokenTaskCap, availableBefore - fixture.Store.Available);
        Assert.Equal(1, fixture.Store.ReserveCalls);
        Assert.Equal(1, fixture.Vertex.GenerateCalls);
        Assert.Equal(request.Prompt, fixture.Vertex.Generated!.Prompt);
        Assert.Equal(request.SystemInstruction, fixture.Vertex.Generated.SystemInstruction);
        Assert.Equal(65_536, fixture.Vertex.Generated.MaxOutputTokens);
        Assert.Equal("gemini-3.5-flash-lite", result.Model);
        var (_, actualCharge) = fixture.Catalog.CalculateActualCost(fixture.Primary, fixture.Vertex.Result.Usage);
        Assert.Equal(actualCharge, result.ChargedCreditMicros);
        Assert.Equal(1, fixture.Store.SettleCalls);
        Assert.Equal(0, fixture.Store.ReleaseCalls);
        Assert.Equal(0, fixture.Store.ReconciliationCalls);
    }

    [Theory]
    [InlineData(120_533, true)]
    [InlineData(120_534, false)]
    public async Task Exact_token_boundary_preserves_existing_rounding_and_25_percent_safety_margin(int tokens, bool fits)
    {
        var fixture = CreateExactTokenFixture();
        fixture.Vertex.Tokens[fixture.Primary.Model] = tokens;
        var assumed = new ProviderUsage(tokens, 65_536, 0, 0, 0, 0,
            checked(tokens + 65_536), null, Estimated: true, Known: false);
        var (_, charge) = fixture.Catalog.CalculateActualCost(fixture.Primary, assumed);
        Assert.Equal(fits ? 25_000_000 : 25_012_500, checked(charge + Math.Max(10_000, charge / 4)));

        if (fits)
        {
            await fixture.Service.GenerateAsync(Caller, ExactTokenRequest("boundary-fit"), CancellationToken.None);
            Assert.Equal(2, fixture.Vertex.Counted.Count);
            Assert.Equal(1, fixture.Store.ReserveCalls);
        }
        else
        {
            await Assert.ThrowsAsync<RequestBudgetExceededException>(() => fixture.Service.GenerateAsync(
                Caller, ExactTokenRequest("boundary-reject"), CancellationToken.None));
            Assert.Contains(fixture.Vertex.Counted, input => input.Model == fixture.Primary.Model);
            AssertExactTokenNoPaidWork(fixture.Store, fixture.Vertex);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public async Task Exact_token_invalid_or_unaffordable_count_never_reserves_or_generates(int tokens)
    {
        var fixture = CreateExactTokenFixture();
        fixture.Vertex.Tokens[fixture.Primary.Model] = tokens;
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Service.GenerateAsync(
            Caller, ExactTokenRequest("invalid-total-" + tokens), CancellationToken.None));
        Assert.Contains(fixture.Vertex.Counted, input => input.Model == fixture.Primary.Model);
        AssertExactTokenNoPaidWork(fixture.Store, fixture.Vertex);
    }

    [Theory]
    [InlineData(false, 400)]
    [InlineData(false, 429)]
    [InlineData(true, 403)]
    [InlineData(true, 404)]
    [InlineData(true, 500)]
    public async Task Exact_token_count_http_failure_has_no_paid_fallback_or_reservation(bool fallback, int status)
    {
        var fixture = CreateExactTokenFixture();
        var model = fallback ? "gemini-3.1-flash-lite" : fixture.Primary.Model;
        fixture.Vertex.Failures[model] = new VertexRequestRejectedException(status);
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Service.GenerateAsync(
            Caller, ExactTokenRequest("http-" + status), CancellationToken.None));
        Assert.Contains(fixture.Vertex.Counted, input => input.Model == model);
        Assert.InRange(fixture.Vertex.Counted.Count, 1, 2);
        AssertExactTokenNoPaidWork(fixture.Store, fixture.Vertex);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exact_token_transport_or_count_timeout_is_pre_dispatch_not_an_unknown_paid_hold(bool timeout)
    {
        var fixture = CreateExactTokenFixture();
        fixture.Vertex.Failures[fixture.Primary.Model] = timeout
            ? new TaskCanceledException("offline count timeout")
            : new HttpRequestException("offline count transport failure");
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Service.GenerateAsync(
            Caller, ExactTokenRequest("transport-" + timeout), CancellationToken.None));
        Assert.Contains(fixture.Vertex.Counted, input => input.Model == fixture.Primary.Model);
        AssertExactTokenNoPaidWork(fixture.Store, fixture.Vertex);
    }

    [Fact]
    public async Task Exact_token_cancellation_during_count_preserves_balance_and_never_dispatches()
    {
        var fixture = CreateExactTokenFixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Vertex.BeforeCount = () => cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.GenerateAsync(
            Caller, ExactTokenRequest("cancel"), cancellation.Token));
        Assert.NotEmpty(fixture.Vertex.Counted);
        AssertExactTokenNoPaidWork(fixture.Store, fixture.Vertex);
        Assert.Equal(1_000_000_000, fixture.Store.Available);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exact_token_linked_deadline_cancels_a_real_held_count_without_paid_work(bool fallback)
    {
        // The fixed offline routing fixture intentionally bypasses configured
        // production route validation to keep this actual timer check bounded.
        var fixture = CreateExactTokenFixture(timeoutSeconds: 1);
        fixture.Vertex.HoldCountModel = fallback ? "gemini-3.1-flash-lite" : fixture.Primary.Model;
        var pending = fixture.Service.GenerateAsync(Caller, ExactTokenRequest("held-timeout-" + fallback), CancellationToken.None);
        await fixture.Vertex.HeldCountStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(pending.IsCompleted);
        AssertExactTokenNoPaidWork(fixture.Store, fixture.Vertex);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.True(fixture.Vertex.HeldCountCancellation.IsCancellationRequested);
        Assert.Equal(fallback ? 2 : 1, fixture.Vertex.Counted.Count);
        AssertExactTokenNoPaidWork(fixture.Store, fixture.Vertex);
        Assert.Equal(1_000_000_000, fixture.Store.Available);
    }

    [Fact]
    public async Task Exact_token_caller_cancellation_reaches_an_awaiting_provider_count()
    {
        var fixture = CreateExactTokenFixture();
        fixture.Vertex.HoldCountModel = fixture.Primary.Model;
        using var cancellation = new CancellationTokenSource();
        var pending = fixture.Service.GenerateAsync(Caller, ExactTokenRequest("held-caller-cancel"), cancellation.Token);
        await fixture.Vertex.HeldCountStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(pending.IsCompleted);
        Assert.False(fixture.Vertex.HeldCountCancellation.IsCancellationRequested);
        AssertExactTokenNoPaidWork(fixture.Store, fixture.Vertex);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(3)));

        Assert.True(fixture.Vertex.HeldCountCancellation.IsCancellationRequested);
        Assert.Single(fixture.Vertex.Counted);
        AssertExactTokenNoPaidWork(fixture.Store, fixture.Vertex);
    }

    [Fact]
    public async Task Exact_token_successful_generation_fallback_uses_the_counted_candidate_and_its_actual_price()
    {
        var fallback = ExactTokenFallbackPricing() with
        {
            Version = "offline-distinct-fallback-price", UsdToInrPaise = 12_000,
        };
        var fixture = CreateExactTokenFixture(fallback);
        var request = ExactTokenRequest("counted-fallback-generation");
        fixture.Vertex.Result = fixture.Vertex.Result with
        {
            Usage = new ProviderUsage(10_000, 10_000, 0, 0, 0, 0, 20_000, "offline-counted-fallback-result"),
        };
        fixture.Vertex.GenerateFailures[fixture.Primary.Model] = new VertexRequestRejectedException(503);
        fixture.Vertex.Tokens[fallback.Model] = 75_000;
        fixture.Vertex.BeforeCount = () => AssertExactTokenNoPaidWork(fixture.Store, fixture.Vertex);
        fixture.Vertex.BeforeGenerate = () =>
        {
            Assert.Equal(2, fixture.Vertex.Counted.Count);
            Assert.Equal(1, fixture.Store.ReserveCalls);
            Assert.Equal(1, fixture.Store.ProviderStartedCalls);
        };

        var result = await fixture.Service.GenerateAsync(Caller, request, CancellationToken.None);

        Assert.True(result.Verified);
        Assert.True(result.ContentComplete);
        Assert.Equal(new[] { fixture.Primary.Model, fallback.Model }, fixture.Vertex.GenerateAttempts.Select(input => input.Model));
        Assert.Equal(2, fixture.Vertex.GenerateCalls);
        Assert.Equal(fallback.Model, fixture.Vertex.Generated!.Model);
        Assert.Equal(fallback.Model, result.Model);
        Assert.Equal(fallback.Model, fixture.Store.CompletedModel);
        Assert.Equal(fallback.Version, fixture.Store.CompletedPricingVersion);
        foreach (var generated in fixture.Vertex.GenerateAttempts)
        {
            var counted = Assert.Single(fixture.Vertex.Counted, input => input.Model == generated.Model);
            Assert.Equal(counted, generated);
            Assert.Equal(request.Prompt, generated.Prompt);
            Assert.Equal(request.SystemInstruction, generated.SystemInstruction);
        }
        var (_, primaryCharge) = fixture.Catalog.CalculateActualCost(fixture.Primary, fixture.Vertex.Result.Usage);
        var (_, fallbackCharge) = fixture.Catalog.CalculateActualCost(fallback, fixture.Vertex.Result.Usage);
        Assert.NotEqual(primaryCharge, fallbackCharge);
        Assert.Equal(fallbackCharge, result.ChargedCreditMicros);
        Assert.Equal(1, fixture.Store.ReserveCalls);
        Assert.Equal(1, fixture.Store.ProviderCompletedCalls);
        Assert.Equal(1, fixture.Store.SettleCalls);
        Assert.Equal(0, fixture.Store.ReleaseCalls);
        Assert.Equal(0, fixture.Store.ReconciliationCalls);
    }

    [Fact]
    public async Task Exact_token_all_denominations_are_validated_before_primary_budget_rejection_can_trigger_counting()
    {
        var fixture = CreateExactTokenFixture(fallback: ExactTokenFallbackPricing() with { CreditMicrosPerInrPaise = 20_000 });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.GenerateAsync(
            Caller, ExactTokenRequest("denomination"), CancellationToken.None));
        Assert.Contains("denomination", error.Message);
        Assert.Empty(fixture.Vertex.Counted);
        AssertExactTokenNoPaidWork(fixture.Store, fixture.Vertex);
    }

    [Fact]
    public async Task Exact_token_counts_do_not_weaken_the_higher_reasoning_rate_bound_of_an_eligible_fallback()
    {
        var fixture = CreateExactTokenFixture(fallback: ExactTokenFallbackPricing() with
        {
            ReasoningUsdMicrosPerMillionTokens = 100_000_000,
        });
        await Assert.ThrowsAsync<RequestBudgetExceededException>(() => fixture.Service.GenerateAsync(
            Caller, ExactTokenRequest("reasoning"), CancellationToken.None));
        // An output-only impossibility may safely reject without counting;
        // exact input counts must never make this reasoning allowance cheap.
        Assert.InRange(fixture.Vertex.Counted.Count, 0, 2);
        AssertExactTokenNoPaidWork(fixture.Store, fixture.Vertex);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exact_token_rescue_does_not_refund_unknown_or_cancelled_generation_after_dispatch(bool cancelled)
    {
        var fixture = CreateExactTokenFixture();
        using var cancellation = new CancellationTokenSource();
        if (cancelled)
        {
            fixture.Vertex.BeforeGenerate = () => cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.GenerateAsync(
                Caller, ExactTokenRequest("cancelled-generation"), cancellation.Token));
        }
        else
        {
            fixture.Vertex.Result = fixture.Vertex.Result with
            {
                Usage = fixture.Vertex.Result.Usage with { Estimated = true, Known = false },
            };
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.GenerateAsync(
                Caller, ExactTokenRequest("unknown-generation"), cancellation.Token));
        }
        Assert.Equal(2, fixture.Vertex.Counted.Count);
        Assert.Equal(1, fixture.Store.ReserveCalls);
        Assert.Equal(1, fixture.Store.ProviderStartedCalls);
        Assert.Equal(1, fixture.Vertex.GenerateCalls);
        Assert.Equal(0, fixture.Store.SettleCalls);
        Assert.Equal(0, fixture.Store.ReleaseCalls);
        Assert.Equal(1, fixture.Store.ReconciliationCalls);
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("image")]
    [InlineData("audio")]
    public async Task Exact_token_rescue_is_not_available_for_multimodal_or_schema_selection(string extra)
    {
        var fixture = CreateExactTokenFixture();
        using var schema = JsonDocument.Parse("""{"type":"string"}""");
        var request = ExactTokenRequest("non-plain") with
        {
            ResponseJsonSchema = extra == "schema" ? schema.RootElement : null,
            Images = extra == "image" ? [new InlineImageRequest("image/png", "AQID")] : null,
            Audio = extra == "audio" ? CreateRequestBudgetAudio(1) : null,
        };
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.GenerateAsync(Caller, request, CancellationToken.None));
        Assert.Empty(fixture.Vertex.Counted);
        AssertExactTokenNoPaidWork(fixture.Store, fixture.Vertex);
    }

    [Theory]
    [InlineData("global", "aiplatform.googleapis.com")]
    [InlineData("us-central1", "us-central1-aiplatform.googleapis.com")]
    public void Exact_token_count_endpoint_uses_the_same_model_resource_as_generation(string location, string host)
    {
        var method = typeof(VertexAiClient).GetMethod("BuildCountTokensEndpoint", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var endpoint = Assert.IsType<string>(method.Invoke(null, ["offline-project", location, "model/name"]));
        Assert.Equal($"https://{host}/v1/projects/offline-project/locations/{location}/publishers/google/models/model%2Fname:countTokens", endpoint);
        Assert.Equal(VertexAiClient.BuildGenerateContentEndpoint("offline-project", location, "model/name")
            .Replace(":generateContent", ":countTokens", StringComparison.Ordinal), endpoint);
    }

    [Fact]
    public void Exact_token_shared_request_builder_preserves_system_schema_and_inline_inputs()
    {
        var method = typeof(VertexAiClient).GetMethod("BuildRequestBody", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        using var schema = JsonDocument.Parse("""{"type":"object","properties":{"result":{"type":"string"}}}""");
        var image = new InlineImageRequest("image/png", "AQID");
        var audio = new InlineAudioRequest("audio/wav", "BAUG");
        var body = method.Invoke(null, ["exact source", "exact instruction", 65_536, schema.RootElement,
            new[] { image }, audio]);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(body));
        var root = json.RootElement;
        var contents = root.GetProperty("contents");
        Assert.Equal(1, contents.GetArrayLength());
        Assert.Equal("user", contents[0].GetProperty("role").GetString());
        var parts = contents[0].GetProperty("parts");
        Assert.Equal(3, parts.GetArrayLength());
        Assert.Equal("exact source", parts[0].GetProperty("text").GetString());
        Assert.Equal(image.Data, parts[1].GetProperty("inlineData").GetProperty("data").GetString());
        Assert.Equal(image.MimeType, parts[1].GetProperty("inlineData").GetProperty("mimeType").GetString());
        Assert.Equal(audio.Data, parts[2].GetProperty("inlineData").GetProperty("data").GetString());
        Assert.Equal(audio.MimeType, parts[2].GetProperty("inlineData").GetProperty("mimeType").GetString());
        Assert.Equal("exact instruction", root.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString());
        var config = root.GetProperty("generationConfig");
        Assert.Equal(65_536, config.GetProperty("maxOutputTokens").GetInt32());
        Assert.Equal(0.3, config.GetProperty("temperature").GetDouble());
        Assert.Equal("application/json", config.GetProperty("responseMimeType").GetString());
        Assert.Equal(schema.RootElement.GetRawText(), config.GetProperty("responseJsonSchema").GetRawText());
    }

    [Theory]
    [InlineData("{\"totalTokens\":50000}")]
    [InlineData("{\"totalTokens\":50000,\"totalBillableCharacters\":50026,\"promptTokensDetails\":[{\"modality\":\"TEXT\",\"tokenCount\":50000}]}")]
    public void Exact_token_adapter_accepts_a_positive_integer_without_treating_billable_characters_as_tokens(string json) =>
        Assert.Equal(50_000, ParseExactTokenCount(json));

    public static TheoryData<string> InvalidExactTokenCounts => new()
    {
        "null", "[]", "{}", "{\"totalTokens\":null}", "{\"totalTokens\":0}",
        "{\"totalTokens\":-1}", "{\"totalTokens\":1.5}", "{\"totalTokens\":\"50000\"}",
        "{\"totalTokens\":2147483648}", "{\"totalTokens\":9223372036854775807}",
        "{\"totalTokens\":50000,\"promptTokensDetails\":null}",
        "{\"totalTokens\":50000,\"promptTokensDetails\":{}}",
        "{\"totalTokens\":50000,\"promptTokensDetails\":[]}",
        "{\"totalTokens\":50000,\"promptTokensDetails\":[{\"modality\":\"TEXT\"}]}",
        "{\"totalTokens\":50000,\"promptTokensDetails\":[{\"tokenCount\":50000}]}",
        "{\"totalTokens\":50000,\"promptTokensDetails\":[{\"modality\":\"TEXT\",\"tokenCount\":-1}]}",
        "{\"totalTokens\":50000,\"promptTokensDetails\":[{\"modality\":\"TEXT\",\"tokenCount\":1.5}]}",
        "{\"totalTokens\":50000,\"promptTokensDetails\":[{\"modality\":\"TEXT\",\"tokenCount\":\"50000\"}]}",
        "{\"totalTokens\":50000,\"promptTokensDetails\":[{\"modality\":4,\"tokenCount\":50000}]}",
        "{\"totalTokens\":50000,\"promptTokensDetails\":[{\"modality\":\"TEXT\",\"tokenCount\":49999}]}",
        "{\"totalTokens\":50000,\"totalBillableCharacters\":null}",
        "{\"totalTokens\":50000,\"totalBillableCharacters\":\"50000\"}",
        "{\"totalTokens\":50000,\"totalBillableCharacters\":-1}",
        "{\"totalTokens\":50000,\"totalBillableCharacters\":1.5}",
        "{\"totalTokens\":50000,\"totalBillableCharacters\":2147483648}",
        "{\"totalTokens\":50000,\"promptTokensDetails\":[{\"modality\":\"IMAGE\",\"tokenCount\":50000}]}",
        "{\"totalTokens\":50000,\"promptTokensDetails\":[{\"modality\":\"AUDIO\",\"tokenCount\":50000}]}",
        "{\"totalTokens\":50000,\"promptTokensDetails\":[{\"modality\":\"text\",\"tokenCount\":50000}]}",
        "{\"totalTokens\":50000,\"promptTokensDetails\":[{\"modality\":\"TEXT\",\"tokenCount\":40000},{\"modality\":\"IMAGE\",\"tokenCount\":10000}]}",
    };

    [Theory]
    [MemberData(nameof(InvalidExactTokenCounts))]
    public void Exact_token_adapter_rejects_missing_malformed_or_incoherent_count_evidence(string json)
    {
        // Assert the adapter boundary exists before examining rejection; a
        // missing method must be red, not mistaken for fail-closed parsing.
        Assert.NotNull(typeof(VertexAiClient).GetMethod("ParseCountTokensResponse", BindingFlags.Static | BindingFlags.NonPublic));
        Assert.Throws<TargetInvocationException>(() => ParseExactTokenCount(json));
    }

    private static int ParseExactTokenCount(string json)
    {
        var method = typeof(VertexAiClient).GetMethod("ParseCountTokensResponse", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        Assert.Equal(typeof(int), method.ReturnType);
        using var document = JsonDocument.Parse(json);
        return Assert.IsType<int>(method.Invoke(null, [document.RootElement]));
    }

    private const long ExactTokenTaskCap = 25_000_000;

    private static GenerateRequest ExactTokenRequest(string suffix, string? source = null) => new(
        "exact-count-" + suffix, source ?? new string('\u754c', 50_000), "Translate the full source.",
        65_536, TaskKind: "selected-text-translation");

    private static PricingSnapshot ExactTokenPrimaryPricing() => RequestBudgetPricing("gemini-3.5-flash-lite") with
    {
        InputUsdMicrosPerMillionTokens = 300_000,
        OutputUsdMicrosPerMillionTokens = 2_500_000,
        ReasoningUsdMicrosPerMillionTokens = 2_500_000,
    };

    private static PricingSnapshot ExactTokenFallbackPricing() => ExactTokenPrimaryPricing() with
    {
        Model = "gemini-3.1-flash-lite", InputUsdMicrosPerMillionTokens = 250_000,
        OutputUsdMicrosPerMillionTokens = 1_500_000, ReasoningUsdMicrosPerMillionTokens = 1_500_000,
    };

    private static (AiUsageService Service, FakeStore Store, ExactTokenVertex Vertex,
        RequestBudgetRealMathCatalog Catalog, PricingSnapshot Primary)
        CreateExactTokenFixture(PricingSnapshot? fallback = null, int timeoutSeconds = 5,
            ITokenCountAdmission? admission = null, bool omitAdmission = false)
    {
        var primary = ExactTokenPrimaryPricing();
        var second = fallback ?? ExactTokenFallbackPricing();
        var options = new PaidSystemOptions { DefaultTaskCreditMicrosCap = ExactTokenTaskCap, MaxRetries = 0 };
        var catalog = new RequestBudgetRealMathCatalog(primary, options);
        var store = new FakeStore { Available = 1_000_000_000 };
        var vertex = new ExactTokenVertex();
        var route = new ModelRouteSnapshot("fast_text", "offline-count-route", "vertex",
            [new(primary.Model, "global", primary), new(second.Model, "global", second)],
            true, false, 200_000, 8_192, timeoutSeconds, 0, 0, "1.3.0", "offline count preparation");
        return (new AiUsageService(store, catalog, new RequestBudgetFixedRouting(route), vertex, Options.Create(options),
                tokenCountAdmission: omitAdmission ? null : admission ?? new RecordingTokenCountAdmission()),
            store, vertex, catalog, primary);
    }

    private static void AssertExactTokenNoPaidWork(FakeStore store, ExactTokenVertex vertex)
    {
        Assert.Equal(0, store.ReserveCalls);
        Assert.Equal(0, store.ProviderStartedCalls);
        Assert.Equal(0, vertex.GenerateCalls);
        Assert.Equal(0, store.ProviderCompletedCalls);
        Assert.Equal(0, store.SettleCalls);
        Assert.Equal(0, store.ReleaseCalls);
        Assert.Equal(0, store.ReconciliationCalls);
    }

    private sealed record ExactTokenInput(string Model, string Location, string Prompt, string? SystemInstruction,
        int MaxOutputTokens, JsonElement? ResponseJsonSchema, IReadOnlyList<InlineImageRequest>? Images, InlineAudioRequest? Audio);

    private sealed class ExactTokenVertex : IVertexAiClient
    {
        public Dictionary<string, int> Tokens { get; } = new(StringComparer.Ordinal)
        {
            ["gemini-3.5-flash-lite"] = 50_000, ["gemini-3.1-flash-lite"] = 50_000,
        };
        public Dictionary<string, Exception> Failures { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Exception> GenerateFailures { get; } = new(StringComparer.Ordinal);
        public List<ExactTokenInput> Counted { get; } = [];
        public List<ExactTokenInput> GenerateAttempts { get; } = [];
        public int GenerateCalls { get; private set; }
        public ExactTokenInput? Generated { get; private set; }
        public Action? BeforeCount { get; set; }
        public Action? BeforeGenerate { get; set; }
        public string? HoldCountModel { get; set; }
        public TaskCompletionSource HeldCountStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken HeldCountCancellation { get; private set; }
        private readonly TaskCompletionSource<int> _heldCount = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public VertexGenerateResult Result { get; set; } = new("offline translation",
            new ProviderUsage(20, 10, 0, 0, 0, 0, 30, "offline-exact-count-provider"), "complete offline translation");

        // Capture the exact preflight contract without real provider traffic.
        public async Task<int> CountTokensAsync(string model, string location, string prompt,
            string? systemInstruction, int maxOutputTokens, JsonElement? responseJsonSchema,
            IReadOnlyList<InlineImageRequest>? images, InlineAudioRequest? audio, CancellationToken cancellationToken)
        {
            Counted.Add(new(model, location, prompt, systemInstruction, maxOutputTokens, responseJsonSchema, images, audio));
            BeforeCount?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (model == HoldCountModel)
            {
                HeldCountCancellation = cancellationToken;
                HeldCountStarted.TrySetResult();
                return await _heldCount.Task.WaitAsync(cancellationToken);
            }
            if (Failures.TryGetValue(model, out var error)) throw error;
            return Tokens[model];
        }

        public Task<VertexGenerateResult> GenerateAsync(string model, string location, string prompt,
            string? systemInstruction, int maxOutputTokens, JsonElement? responseJsonSchema,
            IReadOnlyList<InlineImageRequest>? images, InlineAudioRequest? audio, CancellationToken cancellationToken)
        {
            GenerateCalls++;
            BeforeGenerate?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            Generated = new(model, location, prompt, systemInstruction, maxOutputTokens, responseJsonSchema, images, audio);
            GenerateAttempts.Add(Generated);
            if (GenerateFailures.TryGetValue(model, out var error)) return Task.FromException<VertexGenerateResult>(error);
            return Task.FromResult(Result);
        }
    }
}
