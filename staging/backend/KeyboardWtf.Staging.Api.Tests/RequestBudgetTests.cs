using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using KeyboardWtf.Staging.Api.Configuration;
using KeyboardWtf.Staging.Api.Contracts;
using KeyboardWtf.Staging.Api.Domain;
using KeyboardWtf.Staging.Api.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace KeyboardWtf.Staging.Api.Tests;

public sealed partial class SecurityAndUsageTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Request_budget_utf8_and_reasoning_upper_bound_are_not_underpriced(bool reasoning)
    {
        var pricing = RequestBudgetPricing("budget-primary");
        if (reasoning) pricing = pricing with { ReasoningUsdMicrosPerMillionTokens = 100_000_000 };
        var fixture = CreateRequestBudgetFixture(100_000, pricing);
        await Assert.ThrowsAsync<RequestBudgetExceededException>(() => fixture.Service.GenerateAsync(
            Caller, new GenerateRequest("budget-upper-" + reasoning,
                reasoning ? "hello" : new string('\u754c', 4_000), null, 128), CancellationToken.None));
        Assert.Equal(0, fixture.Store.ReserveCalls);
        Assert.Equal(0, fixture.Vertex.Calls);
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(20_000L)]
    public async Task Request_budget_live_rejects_mismatched_units_before_account_or_provider_work(long denomination)
    {
        var pricing = RequestBudgetPricing("live-test");
        var catalog = new RequestBudgetRealMathCatalog(pricing, new PaidSystemOptions());
        var route = new ModelRouteSnapshot("live_audio", "offline-live-route", "vertex",
            [new("live-test", "test-region", pricing with { CreditMicrosPerInrPaise = denomination })],
            true, false, 20_000, 1, 100, 0, 0, "1.3.0", "offline-test",
            new LiveRoutePolicySnapshot(60, 100_000, 1_000_000));
        var store = new FakeStore { Available = 1_000_000_000 };
        var service = new VertexLiveProxyService(store, catalog, new RequestBudgetFixedRouting(route), null!,
            Options.Create(new PaidSystemOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<VertexLiveProxyService>.Instance);
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpWebSocketFeature>(new BudgetWebSocketFeature());
        context.Request.QueryString = new Microsoft.AspNetCore.Http.QueryString("?requestId=budget-live-fixture");
        context.Request.Headers[ClientCompatibility.DesktopVersionHeader] = "1.4.19";
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunAsync(context, Caller, CancellationToken.None));
        Assert.Contains("denomination", error.Message);
        Assert.Equal(0, store.ReserveCalls);
        Assert.Equal(0, store.ProviderStartedCalls);
    }

    private sealed class BudgetWebSocketFeature : Microsoft.AspNetCore.Http.Features.IHttpWebSocketFeature
    {
        public bool IsWebSocketRequest => true;
        public Task<System.Net.WebSockets.WebSocket> AcceptAsync(Microsoft.AspNetCore.Http.WebSocketAcceptContext context) =>
            throw new InvalidOperationException("This budget test must not accept a real websocket.");
    }

    [Theory]
    [InlineData('x', 50_000)]
    [InlineData('\u754c', 40_000)]
    public async Task Request_budget_affordable_full_selection_preserves_current_output_and_task_cap(char character, int length)
    {
        var primary = RequestBudgetPricing("gemini-3.5-flash-lite") with
        {
            InputUsdMicrosPerMillionTokens = 300_000, OutputUsdMicrosPerMillionTokens = 2_500_000,
            ReasoningUsdMicrosPerMillionTokens = 2_500_000,
        };
        var fallback = primary with
        {
            Model = "gemini-3.1-flash-lite", InputUsdMicrosPerMillionTokens = 250_000,
            OutputUsdMicrosPerMillionTokens = 1_500_000, ReasoningUsdMicrosPerMillionTokens = 1_500_000,
        };
        const long cap = 25_000_000;
        var fixture = CreateRequestBudgetFixture(cap, primary,
            [new(primary.Model, "global", primary), new(fallback.Model, "global", fallback)]);
        var source = new string(character, length);
        var result = await fixture.Service.GenerateAsync(Caller,
            new GenerateRequest("budget-full-selection-" + (int)character, source, "Translate the full source.",
                65_536, TaskKind: "selected-text-translation"), CancellationToken.None);
        Assert.True(result.Verified);
        Assert.Equal(source, fixture.Vertex.Prompt);
        Assert.Equal(65_536, fixture.Vertex.MaxOutputTokens);
        Assert.Equal(1, fixture.Vertex.Calls);
        Assert.Equal(1, fixture.Store.SettleCalls);
    }

    [Fact]
    public async Task Request_budget_50k_utf8_source_fails_closed_when_exact_counter_is_unimplemented()
    {
        var pricing = RequestBudgetPricing("gemini-3.5-flash-lite") with
        {
            InputUsdMicrosPerMillionTokens = 300_000, OutputUsdMicrosPerMillionTokens = 2_500_000,
            ReasoningUsdMicrosPerMillionTokens = 2_500_000,
        };
        // This control tests the unimplemented COUNTER boundary, not the new
        // earlier admission boundary (which has its own fail-closed tests).
        var admission = new RecordingTokenCountAdmission();
        var fixture = CreateRequestBudgetFixture(25_000_000, pricing, admission: admission);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.GenerateAsync(Caller,
            new GenerateRequest("budget-bounded-cjk", new string('\u754c', 50_000), "Translate the full source.",
                65_536, TaskKind: "selected-text-translation"), CancellationToken.None));
        Assert.Contains("counting", error.Message);
        Assert.Single(admission.Calls);
        Assert.Equal(0, fixture.Store.ReserveCalls);
        Assert.Equal(0, fixture.Vertex.Calls);
    }

    [Theory]
    [InlineData("instruction")]
    [InlineData("schema")]
    public async Task Request_budget_includes_instructions_and_schema_before_reservation(string extraInput)
    {
        using var schema = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            type = "string", description = new string('s', 8_000),
        }));
        var fixture = CreateRequestBudgetFixture(500_000);
        var request = new GenerateRequest("budget-" + extraInput, "hello",
            extraInput == "instruction" ? new string('i', 8_000) : null, 128,
            ResponseJsonSchema: extraInput == "schema" ? schema.RootElement : null);

        await Assert.ThrowsAsync<RequestBudgetExceededException>(() =>
            fixture.Service.GenerateAsync(Caller, request, CancellationToken.None));

        Assert.Equal(0, fixture.Store.ReserveCalls);
        Assert.Equal(0, fixture.Store.ProviderStartedCalls);
        Assert.Equal(0, fixture.Vertex.Calls);
    }

    [Fact]
    public async Task Request_budget_combines_text_instructions_schema_image_and_audio_in_one_preflight()
    {
        const long cap = 3_000_000;
        using var schema = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            type = "string", description = new string('s', 3_000),
        }));
        var fixture = CreateRequestBudgetFixture(cap);
        var request = new GenerateRequest("budget-combined-inputs", new string('p', 1_000),
            new string('i', 3_000), 128, ResponseJsonSchema: schema.RootElement,
            Images: [new InlineImageRequest("image/png", "AQID")], Audio: CreateRequestBudgetAudio(30));
        var pricing = fixture.Catalog.Pricing;
        var audioTokens = AiUsageService.ValidateAudio(request.Audio).EstimatedTokens;
        var schemaCharacters = JsonSerializer.SerializeToUtf8Bytes(schema.RootElement).Length;

        // Each component separately fits. Only the complete provider request
        // crosses the cap; separate image/text and audio checks are insufficient.
        foreach (var inputCharacters in new[] { 1_000, 4_000, 1_000 + schemaCharacters, 1_000 + 16_384 })
            Assert.Equal(cap, fixture.Catalog.EstimateReservationCreditMicros(pricing,
                new string('x', inputCharacters), request.MaxOutputTokens, cap));
        var audioOnly = new ProviderUsage(1_000 + audioTokens, 128, 0, 0, 0, audioTokens,
            1_128 + audioTokens, null, InputAudioTokens: audioTokens, Estimated: true, Known: false);
        var (_, audioOnlyCharge) = fixture.Catalog.CalculateActualCost(pricing, audioOnly);
        Assert.True(audioOnlyCharge + Math.Max(10_000, audioOnlyCharge / 4) <= cap);
        var combinedInput = 1_000 + 3_000 + schemaCharacters + 16_384 + audioTokens;
        var combined = audioOnly with { InputTokens = combinedInput, TotalTokens = combinedInput + 128 };
        var (_, combinedCharge) = fixture.Catalog.CalculateActualCost(pricing, combined);
        Assert.True(combinedCharge + Math.Max(10_000, combinedCharge / 4) > cap);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.GenerateAsync(Caller, request, CancellationToken.None));

        Assert.Equal(0, fixture.Store.ReserveCalls);
        Assert.Equal(0, fixture.Store.ProviderStartedCalls);
        Assert.Equal(0, fixture.Vertex.Calls);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("image")]
    [InlineData("audio")]
    public async Task Request_budget_preflights_every_eligible_candidate_before_any_provider_dispatch(string inputKind)
    {
        var primary = RequestBudgetPricing("budget-primary");
        var fallback = inputKind == "audio"
            ? RequestBudgetPricing("budget-expensive-fallback") with { InputAudioUsdMicrosPerMillionTokens = 400_000_000 }
            : RequestBudgetPricing("budget-expensive-fallback") with
            {
                InputUsdMicrosPerMillionTokens = 200_000_000,
                OutputUsdMicrosPerMillionTokens = 200_000_000,
            };
        var fixture = CreateRequestBudgetFixture(3_000_000, primary,
            [new(primary.Model, "global", primary), new(fallback.Model, "global", fallback)]);
        var request = new GenerateRequest("budget-all-candidates-" + inputKind, new string('p', 1_000), null, 128,
            Images: inputKind == "image" ? [new InlineImageRequest("image/png", "AQID")] : null,
            Audio: inputKind == "audio" ? CreateRequestBudgetAudio(30) : null);

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
            fixture.Service.GenerateAsync(Caller, request, CancellationToken.None));

        Assert.Equal(0, fixture.Store.ReserveCalls);
        Assert.Equal(0, fixture.Store.ProviderStartedCalls);
        Assert.Equal(0, fixture.Vertex.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Request_budget_rejects_candidate_credit_denomination_mismatch_before_reservation(int mismatchedCandidate)
    {
        var catalogPricing = RequestBudgetPricing("budget-catalog");
        var first = RequestBudgetPricing("budget-primary");
        var second = RequestBudgetPricing("budget-fallback");
        if (mismatchedCandidate == 0)
            first = first with { CreditMicrosPerInrPaise = 20_000 };
        else
            second = second with { CreditMicrosPerInrPaise = 20_000 };
        var fixture = CreateRequestBudgetFixture(3_000_000, catalogPricing,
            [new(first.Model, "global", first), new(second.Model, "global", second)]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.GenerateAsync(
            Caller, new GenerateRequest("budget-denomination-" + mismatchedCandidate, "safe prompt", null, 128),
            CancellationToken.None));

        Assert.Equal(0, fixture.Store.ReserveCalls);
        Assert.Equal(0, fixture.Store.ProviderStartedCalls);
        Assert.Equal(0, fixture.Vertex.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Request_budget_affordable_requests_reserve_full_server_cap_and_forward_fields_unchanged(bool multimodal)
    {
        const long cap = 3_000_000;
        var fixture = CreateRequestBudgetFixture(cap);
        using var schema = JsonDocument.Parse("""{"type":"object","properties":{"result":{"type":"string"}}}""");
        var request = new GenerateRequest("budget-forward-" + multimodal, "Keep this exact source text.",
            "Keep replies concise.", 128, MaxCreditMicros: 1,
            ResponseJsonSchema: schema.RootElement,
            Images: multimodal ? [new InlineImageRequest("image/png", "AQID")] : null,
            Audio: multimodal ? CreateRequestBudgetAudio(1) : null);
        var availableBefore = fixture.Store.Available;

        var result = await fixture.Service.GenerateAsync(Caller, request, CancellationToken.None);

        Assert.True(result.Verified);
        Assert.Equal(1, fixture.Store.ReserveCalls);
        Assert.Equal(cap, availableBefore - fixture.Store.Available);
        Assert.Equal(1, fixture.Store.ProviderStartedCalls);
        Assert.Equal(1, fixture.Vertex.Calls);
        Assert.Equal(1, fixture.Store.SettleCalls);
        Assert.Equal(0, fixture.Store.ReleaseCalls);
        Assert.Equal(0, fixture.Store.ReconciliationCalls);
        Assert.Equal(request.Prompt, fixture.Vertex.Prompt);
        Assert.Equal(request.SystemInstruction, fixture.Vertex.SystemInstruction);
        Assert.Equal(request.MaxOutputTokens, fixture.Vertex.MaxOutputTokens);
        Assert.Equal(request.ResponseJsonSchema!.Value.GetRawText(), fixture.Vertex.ResponseJsonSchema!.Value.GetRawText());
        Assert.Same(request.Images, fixture.Vertex.Images);
        Assert.Same(request.Audio, fixture.Vertex.Audio);
        Assert.Equal(fixture.Catalog.Pricing.Model, fixture.Vertex.Model);
        Assert.Equal("global", fixture.Vertex.Location);
    }

    private static PricingSnapshot RequestBudgetPricing(string model) => new(
        "request-budget-pricing-v1", model, 1_000_000, 1_000_000, 100_000, 1_000_000,
        10_000, 10_000, DateTimeOffset.Parse("2026-08-03T00:00:00Z"),
        InputAudioUsdMicrosPerMillionTokens: 4_000_000);

    private static (AiUsageService Service, FakeStore Store, RequestBudgetVertex Vertex, RequestBudgetRealMathCatalog Catalog)
        CreateRequestBudgetFixture(long cap, PricingSnapshot? catalogPricing = null,
            IReadOnlyList<ModelCandidateSnapshot>? candidates = null, ITokenCountAdmission? admission = null)
    {
        var pricing = catalogPricing ?? RequestBudgetPricing("budget-primary");
        var options = new PaidSystemOptions { DefaultTaskCreditMicrosCap = cap };
        var catalog = new RequestBudgetRealMathCatalog(pricing, options);
        var store = new FakeStore { Available = 1_000_000_000 };
        var vertex = new RequestBudgetVertex(new FakeVertex(new VertexGenerateResult("ok",
            new ProviderUsage(20, 10, 0, 0, 0, 10, 30, "offline-budget-provider", InputAudioTokens: 10))));
        var route = new ModelRouteSnapshot("fast_text", "request-budget-route-v1", "vertex",
            candidates ?? [new(pricing.Model, "global", pricing)], true, false,
            200_000, 8_192, 5, 0, 0, "1.3.0", "offline request-budget test");
        return (new AiUsageService(store, catalog, new RequestBudgetFixedRouting(route), vertex, Options.Create(options),
                tokenCountAdmission: admission),
            store, vertex, catalog);
    }

    private sealed class RequestBudgetFixedRouting(ModelRouteSnapshot route) : IModelRoutingService
    {
        public Task<ModelRouteSnapshot> ResolveAsync(string? taskKind, string routingKey, CancellationToken cancellationToken) =>
            Task.FromResult(route);
    }

    private sealed class RequestBudgetRealMathCatalog(PricingSnapshot pricing, PaidSystemOptions options) : ICatalogService
    {
        private readonly CatalogService _math = new(null!, Options.Create(options), new FakeClock());
        public PricingSnapshot Pricing => pricing;
        public Task<CatalogSnapshot> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CatalogSnapshot("request-budget-catalog-v1", [], pricing, false, null));
        public Task<CatalogProduct> GetProductAsync(string sku, CancellationToken cancellationToken, bool allowPaymentCanary = false) =>
            throw new NotSupportedException();
        public long EstimateReservationCreditMicros(PricingSnapshot modelPricing, string prompt, int maxOutputTokens,
            long taskCapCreditMicros) => _math.EstimateReservationCreditMicros(modelPricing, prompt, maxOutputTokens, taskCapCreditMicros);
        public (long ProviderCostUsdMicros, long ChargeCreditMicros) CalculateActualCost(PricingSnapshot modelPricing,
            ProviderUsage usage) => _math.CalculateActualCost(modelPricing, usage);
        public (long ProviderCostUsdMicros, long ChargeCreditMicros) CalculateLiveActualCost(PricingSnapshot modelPricing,
            ProviderUsage usage, int estimateSafetyBasisPoints) => _math.CalculateLiveActualCost(modelPricing, usage, estimateSafetyBasisPoints);
    }

    private sealed class RequestBudgetVertex(FakeVertex inner) : IVertexAiClient
    {
        public int Calls => inner.Calls;
        public string? Model { get; private set; }
        public string? Location { get; private set; }
        public string? Prompt { get; private set; }
        public string? SystemInstruction { get; private set; }
        public int MaxOutputTokens { get; private set; }
        public JsonElement? ResponseJsonSchema { get; private set; }
        public IReadOnlyList<InlineImageRequest>? Images { get; private set; }
        public InlineAudioRequest? Audio { get; private set; }
        public Task<VertexGenerateResult> GenerateAsync(string model, string location, string prompt,
            string? systemInstruction, int maxOutputTokens, JsonElement? responseJsonSchema,
            IReadOnlyList<InlineImageRequest>? images, InlineAudioRequest? audio, CancellationToken cancellationToken)
        {
            Model = model;
            Location = location;
            Prompt = prompt;
            SystemInstruction = systemInstruction;
            MaxOutputTokens = maxOutputTokens;
            ResponseJsonSchema = responseJsonSchema;
            Images = images;
            Audio = audio;
            return inner.GenerateAsync(model, location, prompt, systemInstruction, maxOutputTokens,
                responseJsonSchema, images, audio, cancellationToken);
        }
    }

    private static InlineAudioRequest CreateRequestBudgetAudio(int seconds)
    {
        var dataLength = checked(seconds * 16_000 * 2);
        var bytes = new byte[44 + dataLength];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), checked((uint)(bytes.Length - 8)));
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(bytes, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22, 2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24, 4), 16_000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28, 4), 32_000);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(32, 2), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(34, 2), 16);
        Encoding.ASCII.GetBytes("data").CopyTo(bytes, 36);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40, 4), checked((uint)dataLength));
        return new InlineAudioRequest("audio/wav", Convert.ToBase64String(bytes));
    }
}
