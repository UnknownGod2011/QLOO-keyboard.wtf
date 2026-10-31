using Google.Cloud.Firestore;
using KeyboardWtf.Staging.Api.Configuration;
using KeyboardWtf.Staging.Api.Domain;
using KeyboardWtf.Staging.Api.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace KeyboardWtf.Staging.Api.Tests;

[Collection("Local accounting emulator")]
public sealed class RouteConfigurationTests
{
    private const string RouteVersion = "routes-production-2026-08-11";
    private static readonly DateTimeOffset FixtureTime = DateTimeOffset.Parse("2026-10-04T00:00:00Z");
    private static readonly string[] CommonNumberSuffixes =
    [
        "InputUsdMicrosPerMillionTokens", "OutputUsdMicrosPerMillionTokens",
        "CachedInputUsdMicrosPerMillionTokens", "ReasoningUsdMicrosPerMillionTokens",
        "InputAudioUsdMicrosPerMillionTokens", "UsdToInrPaise", "CreditMicrosPerInrPaise",
    ];
    private static readonly string[] LiveNumberSuffixes =
    [
        "LiveInputTextUsdMicrosPerMillionTokens", "LiveInputAudioUsdMicrosPerMillionTokens",
        "LiveInputImageUsdMicrosPerMillionTokens", "LiveOutputTextUsdMicrosPerMillionTokens",
        "LiveOutputAudioUsdMicrosPerMillionTokens",
    ];
    private static readonly string[] RequiredRouteFields =
    [
        "version", "provider", "enabled", "killSwitch", "maxInputCharacters", "maxOutputTokens",
        "timeoutSeconds", "thinkingBudget", "canaryBasisPoints", "minimumDesktopVersion",
        "lifecycleNote", "primaryModel", "primaryLocation",
    ];

    public static IEnumerable<object[]> MissingPricingFields()
    {
        foreach (var prefix in new[] { "primary", "fallback" })
            foreach (var live in new[] { false, true })
                foreach (var suffix in new[] { "PricingVersion" }.Concat(CommonNumberSuffixes)
                    .Concat(live ? LiveNumberSuffixes : []))
                    yield return [prefix, live, suffix];
    }

    public static IEnumerable<object[]> InvalidPricingNumbers()
    {
        foreach (var prefix in new[] { "primary", "fallback" })
        {
            foreach (var suffix in CommonNumberSuffixes)
                foreach (var shape in new[] { "null", "zero", "negative", "fractional", "numeric-string" })
                    yield return [prefix, false, suffix, shape];
            foreach (var suffix in LiveNumberSuffixes)
                foreach (var shape in new[] { "null", "zero", "negative", "fractional", "numeric-string" })
                    yield return [prefix, true, suffix, shape];
        }
    }

    public static IEnumerable<object[]> InvalidRouteNumbers()
    {
        foreach (var field in new[] { "maxInputCharacters", "maxOutputTokens", "timeoutSeconds", "thinkingBudget", "canaryBasisPoints" })
            foreach (var shape in new[] { "null", "numeric-string", "fractional" })
                yield return ["fast_text", field, shape];
        foreach (var field in new[] { "maxSessionSeconds", "reservationWindowCreditMicros", "maxSessionCostCreditMicros" })
            foreach (var shape in new[] { "null", "numeric-string", "fractional" })
                yield return ["live_audio", field, shape];
    }

    public static IEnumerable<object[]> InvalidRouteTexts()
    {
        foreach (var field in new[] { "version", "provider", "primaryModel", "primaryLocation", "fallbackLocation", "minimumDesktopVersion" })
            foreach (var shape in new[] { "null", "blank", "boolean" })
                yield return [field, shape];
        // Omitted fallback is legitimate. A present null/non-string model is
        // not an instruction to silently drop the configured fallback.
        foreach (var shape in new[] { "null", "boolean" })
            yield return ["fallbackModel", shape];
    }

    public static IEnumerable<object[]> MissingRouteFields() =>
        RequiredRouteFields.Select(field => new object[] { field });

    [Theory]
    [InlineData("primary", false)]
    [InlineData("fallback", false)]
    [InlineData("primary", true)]
    [InlineData("fallback", true)]
    public void Exact_current_candidate_pricing_passes_without_requiring_unused_live_prices(string prefix, bool live)
    {
        var row = PricingOnly(prefix, live);
        ModelRoutingService.RequireExplicitPricing(prefix, row, live);
        Assert.Equal(live ? 13 : 8, row.Count);
    }

    [Theory]
    [MemberData(nameof(MissingPricingFields))]
    public void Every_required_pricing_field_must_be_explicit(string prefix, bool live, string suffix)
    {
        var row = PricingOnly(prefix, live);
        Assert.True(row.Remove(prefix + suffix));
        Assert.Throws<InvalidOperationException>(() => ModelRoutingService.RequireExplicitPricing(prefix, row, live));
    }

    [Theory]
    [MemberData(nameof(InvalidPricingNumbers))]
    public void Pricing_numbers_must_be_positive_integers_not_coercible_values(string prefix, bool live, string suffix, string shape)
    {
        var row = PricingOnly(prefix, live);
        var key = prefix + suffix;
        row[key] = InvalidValue(shape, row[key])!;
        Assert.Throws<InvalidOperationException>(() => ModelRoutingService.RequireExplicitPricing(prefix, row, live));
    }

    [Theory]
    [InlineData("primary", false, "null")]
    [InlineData("primary", false, "blank")]
    [InlineData("primary", false, "boolean")]
    [InlineData("fallback", false, "null")]
    [InlineData("fallback", false, "blank")]
    [InlineData("fallback", false, "boolean")]
    [InlineData("primary", true, "null")]
    [InlineData("primary", true, "blank")]
    [InlineData("primary", true, "boolean")]
    [InlineData("fallback", true, "null")]
    [InlineData("fallback", true, "blank")]
    [InlineData("fallback", true, "boolean")]
    public void Pricing_version_must_be_a_nonblank_explicit_string(string prefix, bool live, string shape)
    {
        var row = PricingOnly(prefix, live);
        row[prefix + "PricingVersion"] = InvalidValue(shape, row[prefix + "PricingVersion"])!;
        Assert.Throws<InvalidOperationException>(() => ModelRoutingService.RequireExplicitPricing(prefix, row, live));
    }

    [LocalFirestoreFact]
    [Trait("Category", "FirestoreEmulator")]
    public async Task Six_complete_current_routes_preserve_exact_models_prices_limits_and_order()
    {
        var document = CurrentRoutes();
        var service = await ServiceAsync(document);
        foreach (var pair in Routes(document))
        {
            var row = (IDictionary<string, object>)pair.Value;
            var route = await service.ResolveAsync(TaskKind(pair.Key), "local-fixture:request", CancellationToken.None);
            Assert.Equal(pair.Key, route.Name);
            Assert.Equal(RouteVersion, route.Version);
            Assert.Equal("vertex", route.Provider);
            Assert.True(route.Enabled);
            Assert.False(route.KillSwitch);
            Assert.Equal(Convert.ToInt32(row["maxInputCharacters"]), route.MaxInputCharacters);
            Assert.Equal(Convert.ToInt32(row["maxOutputTokens"]), route.MaxOutputTokens);
            Assert.Equal(Convert.ToInt32(row["timeoutSeconds"]), route.TimeoutSeconds);
            Assert.Equal(Convert.ToInt32(row["thinkingBudget"]), route.ThinkingBudget);
            Assert.Equal(0, route.CanaryBasisPoints);
            Assert.Equal("1.4.0", route.MinimumDesktopVersion);
            Assert.Equal(row["lifecycleNote"], route.LifecycleNote);
            Assert.Equal(pair.Key == "live_audio" ? 1 : 2, route.Candidates.Count);
            AssertCandidate(route.Candidates[0], row, "primary", pair.Key == "live_audio");
            if (pair.Key == "live_audio")
                Assert.Equal(new LiveRoutePolicySnapshot(720, 1_000_000, 10_000_000), route.LivePolicy);
            else
            {
                Assert.Null(route.LivePolicy);
                AssertCandidate(route.Candidates[1], row, "fallback", false);
            }
        }
    }

    [LocalFirestoreTheory]
    [Trait("Category", "FirestoreEmulator")]
    [InlineData("version")]
    [InlineData("routes")]
    public async Task Production_requires_explicit_document_version_and_routes(string field)
    {
        var document = CurrentRoutes();
        Assert.True(document.Remove(field));
        await AssertRejectedAsync(document);
    }

    [LocalFirestoreTheory]
    [Trait("Category", "FirestoreEmulator")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Production_cannot_enable_bootstrap_with_a_misconfigured_option(bool partialDocument)
    {
        var (db, _, _) = await AccountingEmulatorTests.CreateAccountAsync();
        if (partialDocument)
            await db.Document("config/modelRouting").SetAsync(new Dictionary<string, object>
            {
                ["version"] = RouteVersion,
                ["routes"] = new Dictionary<string, object>(),
            });
        else
            await db.Document("config/modelRouting").DeleteAsync();
        var service = new ModelRoutingService(db, Options.Create(new PaidSystemOptions
        {
            ProjectId = db.ProjectId, DeploymentEnvironment = PaidDeploymentEnvironment.Production,
            AllowBootstrapModelRouting = true,
        }), new FixtureClock());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ResolveAsync(
            "desktop-health", "local-fixture:request", CancellationToken.None));
    }

    [LocalFirestoreTheory]
    [Trait("Category", "FirestoreEmulator")]
    [InlineData("version", "null")]
    [InlineData("version", "blank")]
    [InlineData("version", "boolean")]
    [InlineData("routes", "null")]
    [InlineData("routes", "boolean")]
    public async Task Production_rejects_wrong_document_field_types(string field, string shape)
    {
        var document = CurrentRoutes();
        document[field] = InvalidValue(shape, document[field])!;
        await AssertRejectedAsync(document);
    }

    [LocalFirestoreTheory]
    [Trait("Category", "FirestoreEmulator")]
    [MemberData(nameof(MissingRouteFields))]
    public async Task Production_requires_every_explicit_route_field(string field)
    {
        var document = CurrentRoutes();
        Assert.True(Row(document, "fast_text").Remove(field));
        await AssertRejectedAsync(document);
    }

    [LocalFirestoreTheory]
    [Trait("Category", "FirestoreEmulator")]
    [MemberData(nameof(InvalidRouteTexts))]
    public async Task Production_rejects_null_or_wrongly_typed_route_text(string field, string shape)
    {
        var document = CurrentRoutes();
        var row = Row(document, "fast_text");
        row[field] = InvalidValue(shape, row[field])!;
        await AssertRejectedAsync(document);
    }

    [LocalFirestoreTheory]
    [Trait("Category", "FirestoreEmulator")]
    [InlineData("enabled", "null")]
    [InlineData("enabled", "boolean-string")]
    [InlineData("enabled", "flag-integer")]
    [InlineData("killSwitch", "null")]
    [InlineData("killSwitch", "boolean-string")]
    [InlineData("killSwitch", "flag-integer")]
    public async Task Production_route_flags_are_explicit_booleans(string field, string shape)
    {
        var document = CurrentRoutes();
        Row(document, "fast_text")[field] = InvalidValue(shape, Row(document, "fast_text")[field])!;
        await AssertRejectedAsync(document);
    }

    [LocalFirestoreTheory]
    [Trait("Category", "FirestoreEmulator")]
    [MemberData(nameof(InvalidRouteNumbers))]
    public async Task Production_route_limits_are_explicit_integral_values(string name, string field, string shape)
    {
        var document = CurrentRoutes();
        var row = Row(document, name);
        row[field] = InvalidValue(shape, row[field])!;
        await AssertRejectedAsync(document, name);
    }

    [LocalFirestoreTheory]
    [Trait("Category", "FirestoreEmulator")]
    [InlineData("primary")]
    [InlineData("fallback")]
    public async Task Production_route_fx_cannot_undercut_existing_server_floor(string prefix)
    {
        var document = CurrentRoutes();
        Row(document, "fast_text")[prefix + "UsdToInrPaise"] = 9_572L;
        await AssertRejectedAsync(document);
    }

    [LocalFirestoreTheory]
    [Trait("Category", "FirestoreEmulator")]
    [InlineData("primary", "null")]
    [InlineData("primary", "blank")]
    [InlineData("fallback", "null")]
    [InlineData("fallback", "blank")]
    public async Task Actual_production_path_does_not_inherit_a_missing_candidate_pricing_version(string prefix, string shape)
    {
        var document = CurrentRoutes();
        var row = Row(document, "fast_text");
        row[prefix + "PricingVersion"] = InvalidValue(shape, row[prefix + "PricingVersion"])!;
        await AssertRejectedAsync(document);
    }

    [LocalFirestoreFact]
    [Trait("Category", "FirestoreEmulator")]
    public async Task Staging_partial_routing_retains_existing_bootstrap_defaults()
    {
        var document = new Dictionary<string, object>
        {
            ["routes"] = new Dictionary<string, object>
            {
                ["fast_text"] = new Dictionary<string, object> { ["primaryModel"] = "gemini-3.5-flash-lite" },
            },
        };
        var service = await ServiceAsync(document, PaidDeploymentEnvironment.Staging);
        var route = await service.ResolveAsync("desktop-health", "local-fixture:request", CancellationToken.None);
        Assert.Equal("gemini-3.5-flash-lite", Assert.Single(route.Candidates).Model);
        Assert.Equal(300_000, route.Candidates[0].Pricing.InputUsdMicrosPerMillionTokens);
        Assert.Equal(2_500_000, route.Candidates[0].Pricing.OutputUsdMicrosPerMillionTokens);
        Assert.Equal(10_000, route.Candidates[0].Pricing.UsdToInrPaise);
        Assert.Equal(10_000, route.Candidates[0].Pricing.CreditMicrosPerInrPaise);
        Assert.Equal(200_000, route.MaxInputCharacters);
        Assert.Equal(2_048, route.MaxOutputTokens);
        Assert.Equal(60, route.TimeoutSeconds);
    }

    private static async Task AssertRejectedAsync(Dictionary<string, object> document, string route = "fast_text")
    {
        var service = await ServiceAsync(document);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ResolveAsync(TaskKind(route), "local-fixture:request", CancellationToken.None));
    }

    private static async Task<ModelRoutingService> ServiceAsync(Dictionary<string, object> document,
        PaidDeploymentEnvironment environment = PaidDeploymentEnvironment.Production)
    {
        // Shared helper asserts 127.0.0.1, synthetic project, EmulatorOnly.
        var (db, _, _) = await AccountingEmulatorTests.CreateAccountAsync();
        await db.Document("config/modelRouting").SetAsync(document);
        return new ModelRoutingService(db, Options.Create(new PaidSystemOptions
        {
            ProjectId = db.ProjectId,
            DeploymentEnvironment = environment,
            AllowBootstrapCatalog = environment != PaidDeploymentEnvironment.Production,
            AllowBootstrapModelRouting = environment != PaidDeploymentEnvironment.Production,
        }), new FixtureClock());
    }

    private static string TaskKind(string route) => route switch
    {
        "fast_text" => "desktop-health", "selected_text" => "desktop-reformat",
        "agent_planner" => "desktop-structured", "browser_planner" => "browser-plan",
        "screen_reasoning" => "screen-guidance", "live_audio" => "live-audio",
        _ => throw new ArgumentOutOfRangeException(nameof(route)),
    };

    // Independent literal fixture from configure-model-routing.ps1. Do not
    // obtain expected prices/routes from the implementation's bootstrap data.
    private static Dictionary<string, object> CurrentRoutes()
    {
        var routes = new Dictionary<string, object>
        {
            ["fast_text"] = Route("gemini-3.5-flash-lite", "gemini-3.1-flash-lite", 2_048, 60, 0,
                "GA fast text route; server-owned fallback and pricing."),
            ["selected_text"] = Route("gemini-3.5-flash-lite", "gemini-3.1-flash-lite", 2_048, 60, 0,
                "GA selected-text route; server-owned fallback and pricing."),
            ["agent_planner"] = Route("gemini-3.5-flash", "gemini-3.5-flash-lite", 4_096, 90, 1_024,
                "GA reasoning route for complex desktop planning."),
            ["browser_planner"] = Route("gemini-3.5-flash-lite", "gemini-3.5-flash", 2_048, 75, 512,
                "Fast GA browser planner with a stronger GA fallback."),
            ["screen_reasoning"] = Route("gemini-3.5-flash", "gemini-3.5-flash-lite", 2_048, 90, 1_024,
                "GA multimodal screen and image reasoning route."),
        };
        var live = Route("gemini-live-2.5-flash-native-audio", null, 1, 100, 0,
            "GA bounded Live audio route with cumulative high-water accounting.");
        live["maxInputCharacters"] = 20_000L;
        live["maxSessionSeconds"] = 720L;
        live["reservationWindowCreditMicros"] = 1_000_000L;
        live["maxSessionCostCreditMicros"] = 10_000_000L;
        routes["live_audio"] = live;
        return new Dictionary<string, object> { ["version"] = RouteVersion, ["routes"] = routes };
    }

    private static Dictionary<string, object> Route(string primary, string? fallback, int output, int timeout, int thinking, string note)
    {
        var row = new Dictionary<string, object>
        {
            ["version"] = RouteVersion, ["provider"] = "vertex", ["enabled"] = true, ["killSwitch"] = false,
            ["maxInputCharacters"] = 200_000L, ["maxOutputTokens"] = (long)output,
            ["timeoutSeconds"] = (long)timeout, ["thinkingBudget"] = (long)thinking,
            ["canaryBasisPoints"] = 0L, ["minimumDesktopVersion"] = "1.4.0", ["lifecycleNote"] = note,
        };
        AddCandidate(row, "primary", primary);
        if (fallback is not null) AddCandidate(row, "fallback", fallback);
        return row;
    }

    private static void AddCandidate(IDictionary<string, object> row, string prefix, string model)
    {
        var live = model == "gemini-live-2.5-flash-native-audio";
        row[prefix + "Model"] = model;
        row[prefix + "Location"] = live ? "us-central1" : "global";
        foreach (var pair in PricingOnly(prefix, live, model)) row[pair.Key] = pair.Value;
    }

    private static Dictionary<string, object> PricingOnly(string prefix, bool live, string? model = null)
    {
        model ??= live ? "gemini-live-2.5-flash-native-audio" : "gemini-3.5-flash-lite";
        var (input, output, cache, audio) = model switch
        {
            "gemini-3.5-flash-lite" => (300_000L, 2_500_000L, 30_000L, 300_000L),
            "gemini-3.5-flash" => (1_500_000L, 9_000_000L, 150_000L, 1_500_000L),
            "gemini-3.1-flash-lite" => (250_000L, 1_500_000L, 25_000L, 500_000L),
            "gemini-live-2.5-flash-native-audio" => (500_000L, 2_000_000L, 500_000L, 3_000_000L),
            _ => throw new ArgumentOutOfRangeException(nameof(model)),
        };
        var row = new Dictionary<string, object>
        {
            [prefix + "PricingVersion"] = $"vertex-{model}-standard{(live ? "" : "-global")}-fx100-2026-08-11",
            [prefix + "InputUsdMicrosPerMillionTokens"] = input,
            [prefix + "OutputUsdMicrosPerMillionTokens"] = output,
            [prefix + "CachedInputUsdMicrosPerMillionTokens"] = cache,
            [prefix + "ReasoningUsdMicrosPerMillionTokens"] = output,
            [prefix + "InputAudioUsdMicrosPerMillionTokens"] = audio,
            [prefix + "UsdToInrPaise"] = 10_000L, [prefix + "CreditMicrosPerInrPaise"] = 10_000L,
        };
        if (live)
        {
            row[prefix + "LiveInputTextUsdMicrosPerMillionTokens"] = 500_000L;
            row[prefix + "LiveInputAudioUsdMicrosPerMillionTokens"] = 3_000_000L;
            row[prefix + "LiveInputImageUsdMicrosPerMillionTokens"] = 3_000_000L;
            row[prefix + "LiveOutputTextUsdMicrosPerMillionTokens"] = 2_000_000L;
            row[prefix + "LiveOutputAudioUsdMicrosPerMillionTokens"] = 12_000_000L;
        }
        return row;
    }

    private static IDictionary<string, object> Routes(IDictionary<string, object> document) =>
        (IDictionary<string, object>)document["routes"];

    private static IDictionary<string, object> Row(IDictionary<string, object> document, string name) =>
        (IDictionary<string, object>)Routes(document)[name];

    private static object? InvalidValue(string shape, object original) => shape switch
    {
        "null" => null, "blank" => " ", "boolean" => true,
        "boolean-string" => Convert.ToString(original, System.Globalization.CultureInfo.InvariantCulture),
        "flag-integer" => original is bool flag && flag ? 1L : 0L,
        "zero" => 0L, "negative" => -1L,
        "fractional" => Convert.ToDouble(original) + 0.25d,
        "numeric-string" => Convert.ToString(original, System.Globalization.CultureInfo.InvariantCulture),
        _ => throw new ArgumentOutOfRangeException(nameof(shape)),
    };

    private static void AssertCandidate(ModelCandidateSnapshot candidate, IDictionary<string, object> row, string prefix, bool live)
    {
        Assert.Equal((string)row[prefix + "Model"], candidate.Model);
        Assert.Equal((string)row[prefix + "Location"], candidate.Location);
        Assert.Equal(candidate.Model, candidate.Pricing.Model);
        Assert.Equal((string)row[prefix + "PricingVersion"], candidate.Pricing.Version);
        Assert.Equal(FixtureTime, candidate.Pricing.EffectiveAt);
        Assert.Equal((long)row[prefix + "InputUsdMicrosPerMillionTokens"], candidate.Pricing.InputUsdMicrosPerMillionTokens);
        Assert.Equal((long)row[prefix + "OutputUsdMicrosPerMillionTokens"], candidate.Pricing.OutputUsdMicrosPerMillionTokens);
        Assert.Equal((long)row[prefix + "CachedInputUsdMicrosPerMillionTokens"], candidate.Pricing.CachedInputUsdMicrosPerMillionTokens);
        Assert.Equal((long)row[prefix + "ReasoningUsdMicrosPerMillionTokens"], candidate.Pricing.ReasoningUsdMicrosPerMillionTokens);
        Assert.Equal((long)row[prefix + "InputAudioUsdMicrosPerMillionTokens"], candidate.Pricing.InputAudioUsdMicrosPerMillionTokens);
        Assert.Equal(10_000, candidate.Pricing.UsdToInrPaise);
        Assert.Equal(10_000, candidate.Pricing.CreditMicrosPerInrPaise);
        if (live)
        {
            Assert.Equal(500_000, candidate.Pricing.LiveInputTextUsdMicrosPerMillionTokens);
            Assert.Equal(3_000_000, candidate.Pricing.LiveInputAudioUsdMicrosPerMillionTokens);
            Assert.Equal(3_000_000, candidate.Pricing.LiveInputImageUsdMicrosPerMillionTokens);
            Assert.Equal(2_000_000, candidate.Pricing.LiveOutputTextUsdMicrosPerMillionTokens);
            Assert.Equal(12_000_000, candidate.Pricing.LiveOutputAudioUsdMicrosPerMillionTokens);
        }
    }

    private sealed class FixtureClock : IClock
    {
        public DateTimeOffset UtcNow => FixtureTime;
    }
}
