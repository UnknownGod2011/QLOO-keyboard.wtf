using Google.Cloud.Firestore;
using KeyboardWtf.Staging.Api.Configuration;
using KeyboardWtf.Staging.Api.Domain;
using KeyboardWtf.Staging.Api.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace KeyboardWtf.Staging.Api.Tests;

// Exercise the existing GetAsync/FromDocument path, not a test-only parser.
// The shared collection and emulator helper prevent parallel config mutations
// and make cloud/ADC fallback impossible. Construct a new service for each case
// so the normal two-minute cache cannot conceal an invalid replacement document.
[Collection("Local accounting emulator")]
public sealed class ProductionConfigurationEmulatorTests
{
    private static readonly DateTimeOffset FixtureTime = DateTimeOffset.Parse("2026-10-04T00:00:00Z");
    private const string FixturePlanId = "plan_ReleaseLocalFixture";

    private static readonly string[] RequiredRootFields =
    [
        "version", "killSwitch", "paymentsKillSwitch", "pricing", "products",
    ];

    private static readonly string[] RequiredPricingFields =
    [
        "version", "model", "inputUsdMicrosPerMillionTokens", "outputUsdMicrosPerMillionTokens",
        "cachedInputUsdMicrosPerMillionTokens", "reasoningUsdMicrosPerMillionTokens",
        "inputAudioUsdMicrosPerMillionTokens", "usdToInrPaise", "creditMicrosPerInrPaise",
        "liveInputTextUsdMicrosPerMillionTokens", "liveInputAudioUsdMicrosPerMillionTokens",
        "liveInputImageUsdMicrosPerMillionTokens", "liveOutputTextUsdMicrosPerMillionTokens",
        "liveOutputAudioUsdMicrosPerMillionTokens",
    ];

    private static readonly string[] RequiredProductFields =
    [
        "sku", "kind", "displayName", "amountMinor", "currency", "creditMicros", "enabled", "razorpayPlanId",
    ];

    public static IEnumerable<object[]> MissingRootFields() =>
        RequiredRootFields.Select(field => new object[] { field });

    public static IEnumerable<object[]> MissingPricingFields() =>
        RequiredPricingFields.Select(field => new object[] { field });

    public static IEnumerable<object[]> MissingProductFields() =>
        RequiredProductFields.Select(field => new object[] { field });

    public static IEnumerable<object[]> InvalidPricingNumbers()
    {
        foreach (var field in RequiredPricingFields.Where(field => field is not ("version" or "model")))
            foreach (var shape in new[] { "null", "zero", "negative", "fractional", "numeric-string" })
                yield return [field, shape];
    }

    public static IEnumerable<object[]> InvalidRootFields()
    {
        foreach (var shape in new[] { "null", "blank", "boolean" })
            yield return ["version", shape];
        foreach (var field in new[] { "killSwitch", "paymentsKillSwitch" })
            foreach (var shape in new[] { "null", "boolean-string", "positive" })
                yield return [field, shape];
        foreach (var field in new[] { "pricing", "products" })
            foreach (var shape in new[] { "null", "boolean" })
                yield return [field, shape];
    }

    [LocalFirestoreFact]
    [Trait("Category", "FirestoreEmulator")]
    public async Task Complete_production_catalog_preserves_exact_launch_script_values()
    {
        var catalog = await ReadAsync(LaunchCatalog());

        Assert.Equal("launch-monthly-inr999-v1", catalog.Version);
        Assert.False(catalog.KillSwitch);
        Assert.False(catalog.PaymentsKillSwitch);
        Assert.Equal("", catalog.KillSwitchMessage);
        Assert.Equal("", catalog.PaymentsKillSwitchMessage);
        AssertLaunchPricing(catalog.Pricing);
        var product = Assert.Single(catalog.Products);
        Assert.Equal(new CatalogProduct(
            "monthly_standard_inr_999_v1", "subscription", "Jarvis Monthly", 99_900,
            "INR", 500_000_000, FixturePlanId, true), product);
    }

    [LocalFirestoreTheory]
    [Trait("Category", "FirestoreEmulator")]
    [MemberData(nameof(MissingRootFields))]
    public async Task Production_rejects_missing_required_root_field(string field)
    {
        var document = LaunchCatalog();
        Assert.True(document.Remove(field));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync(document));
    }

    [LocalFirestoreTheory]
    [Trait("Category", "FirestoreEmulator")]
    [MemberData(nameof(InvalidRootFields))]
    public async Task Production_rejects_null_or_wrongly_typed_root_field(string field, string shape)
    {
        var document = LaunchCatalog();
        document[field] = InvalidValue(shape)!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync(document));
    }

    [LocalFirestoreTheory]
    [Trait("Category", "FirestoreEmulator")]
    [MemberData(nameof(MissingPricingFields))]
    public async Task Production_rejects_missing_pricing_field_instead_of_inheriting_bootstrap(string field)
    {
        var document = LaunchCatalog();
        Assert.True(Pricing(document).Remove(field));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync(document));
    }

    [LocalFirestoreTheory]
    [Trait("Category", "FirestoreEmulator")]
    [MemberData(nameof(InvalidPricingNumbers))]
    public async Task Production_rejects_invalid_pricing_number_without_coercion(string field, string shape)
    {
        var document = LaunchCatalog();
        Pricing(document)[field] = InvalidValue(shape)!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync(document));
    }

    [LocalFirestoreTheory]
    [Trait("Category", "FirestoreEmulator")]
    [InlineData("version", "null")]
    [InlineData("version", "blank")]
    [InlineData("version", "boolean")]
    [InlineData("model", "null")]
    [InlineData("model", "blank")]
    [InlineData("model", "boolean")]
    public async Task Production_rejects_invalid_pricing_identity(string field, string shape)
    {
        var document = LaunchCatalog();
        Pricing(document)[field] = InvalidValue(shape)!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync(document));
    }

    [LocalFirestoreTheory]
    [Trait("Category", "FirestoreEmulator")]
    [MemberData(nameof(MissingProductFields))]
    public async Task Production_rejects_incomplete_enabled_subscription(string field)
    {
        var document = LaunchCatalog();
        Assert.True(Product(document).Remove(field));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync(document));
    }

    [LocalFirestoreTheory]
    [Trait("Category", "FirestoreEmulator")]
    [InlineData("enabled", "null")]
    [InlineData("enabled", "boolean-string")]
    [InlineData("amountMinor", "numeric-string")]
    [InlineData("creditMicros", "numeric-string")]
    [InlineData("sku", "null")]
    [InlineData("sku", "blank")]
    [InlineData("kind", "null")]
    [InlineData("kind", "blank")]
    [InlineData("displayName", "null")]
    [InlineData("displayName", "blank")]
    [InlineData("razorpayPlanId", "null")]
    [InlineData("razorpayPlanId", "blank")]
    public async Task Production_rejects_invalid_enabled_product_field(string field, string shape)
    {
        var document = LaunchCatalog();
        Product(document)[field] = InvalidValue(shape)!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync(document));
    }

    [LocalFirestoreFact]
    [Trait("Category", "FirestoreEmulator")]
    public async Task Production_rejects_unknown_enabled_product_kind()
    {
        var document = LaunchCatalog();
        Product(document)["kind"] = "unknown-product-kind";
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync(document));
    }

    [LocalFirestoreFact]
    [Trait("Category", "FirestoreEmulator")]
    public async Task Production_rejects_duplicate_product_skus()
    {
        var document = LaunchCatalog();
        var duplicate = new Dictionary<string, object>(Product(document)) { ["displayName"] = "Duplicate display" };
        document["products"] = new object[] { Product(document), duplicate };
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync(document));
    }

    [LocalFirestoreTheory]
    [Trait("Category", "FirestoreEmulator")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Production_rejects_malformed_product_row_instead_of_silently_discarding_it(bool nullRow)
    {
        var document = LaunchCatalog();
        document["products"] = new object[] { nullRow ? null! : "not-a-product-map" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync(document));
    }

    [LocalFirestoreFact]
    [Trait("Category", "FirestoreEmulator")]
    public async Task Explicitly_disabled_checkout_can_have_an_empty_catalog_without_bootstrap_products()
    {
        var document = LaunchCatalog();
        document["products"] = Array.Empty<object>();
        document["paymentsKillSwitch"] = true;
        document["paymentsKillSwitchMessage"] = "Payments are not open yet.";
        var catalog = await ReadAsync(document);

        Assert.Empty(catalog.Products);
        Assert.True(catalog.PaymentsKillSwitch);
        AssertLaunchPricing(catalog.Pricing);
    }

    [LocalFirestoreFact]
    [Trait("Category", "FirestoreEmulator")]
    public async Task Deliberate_catalog_fx_floor_preserves_existing_positive_lower_reference_behavior()
    {
        var document = LaunchCatalog();
        Pricing(document)["usdToInrPaise"] = 9_572L;
        var catalog = await ReadAsync(document);

        Assert.Equal(CatalogService.ConservativeUsdToInrPaise, catalog.Pricing.UsdToInrPaise);
        Assert.Equal(10_000, catalog.Pricing.CreditMicrosPerInrPaise);
    }

    [LocalFirestoreFact]
    [Trait("Category", "FirestoreEmulator")]
    public async Task Production_rejects_missing_entire_catalog_when_bootstrap_is_disabled()
    {
        var (db, _, _) = await AccountingEmulatorTests.CreateAccountAsync();
        await db.Document("config/catalog").DeleteAsync();
        var service = Service(db, PaidDeploymentEnvironment.Production);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetAsync(CancellationToken.None));
    }

    [LocalFirestoreFact]
    [Trait("Category", "FirestoreEmulator")]
    public async Task Staging_partial_document_retains_existing_bootstrap_defaults()
    {
        var document = new Dictionary<string, object>
        {
            ["pricing"] = new Dictionary<string, object> { ["inputUsdMicrosPerMillionTokens"] = 123_456L },
        };
        var catalog = await ReadAsync(document, PaidDeploymentEnvironment.Staging);

        Assert.Equal("bootstrap-2026-08", catalog.Version);
        Assert.False(catalog.KillSwitch);
        Assert.False(catalog.PaymentsKillSwitch);
        Assert.Equal("vertex-gemini-2.5-live-fx100-2026-08-09", catalog.Pricing.Version);
        Assert.Equal("gemini-3.5-flash-lite", catalog.Pricing.Model);
        Assert.Equal(123_456, catalog.Pricing.InputUsdMicrosPerMillionTokens);
        Assert.Equal(2_500_000, catalog.Pricing.OutputUsdMicrosPerMillionTokens);
        Assert.Equal(10_000, catalog.Pricing.UsdToInrPaise);
        Assert.Equal(10_000, catalog.Pricing.CreditMicrosPerInrPaise);
        Assert.Equal(2, catalog.Products.Count);
        Assert.Contains(catalog.Products, product => product.Sku == "credits_test_100" && product.CreditMicros == 100_000_000);
        Assert.Contains(catalog.Products, product => product.Sku == "monthly_test" && product.CreditMicros == 500_000_000);
    }

    private static async Task<CatalogSnapshot> ReadAsync(
        Dictionary<string, object> document,
        PaidDeploymentEnvironment environment = PaidDeploymentEnvironment.Production)
    {
        var (db, _, _) = await AccountingEmulatorTests.CreateAccountAsync();
        await db.Document("config/catalog").SetAsync(document);
        return await Service(db, environment).GetAsync(CancellationToken.None);
    }

    private static CatalogService Service(FirestoreDb db, PaidDeploymentEnvironment environment) =>
        new(db, Options.Create(new PaidSystemOptions
        {
            ProjectId = db.ProjectId,
            DeploymentEnvironment = environment,
            VertexModel = "gemini-3.5-flash-lite",
            AllowBootstrapCatalog = environment != PaidDeploymentEnvironment.Production,
            AllowBootstrapModelRouting = environment != PaidDeploymentEnvironment.Production,
        }), new FixtureClock());

    // Same fields, rates, model and product economics as the existing
    // configure-launch-catalog.ps1 with EnableAi/EnableCheckout. The plan ID
    // and timestamp are synthetic local substitutes; no Razorpay call exists.
    private static Dictionary<string, object> LaunchCatalog() => new()
    {
        ["version"] = "launch-monthly-inr999-v1",
        ["updatedAt"] = Timestamp.FromDateTimeOffset(FixtureTime),
        ["killSwitch"] = false,
        ["killSwitchMessage"] = "",
        ["paymentsKillSwitch"] = false,
        ["paymentsKillSwitchMessage"] = "",
        ["pricing"] = new Dictionary<string, object>
        {
            ["version"] = "vertex-standard-global-fx100-2026-08-11",
            ["model"] = "gemini-3.5-flash-lite",
            ["inputUsdMicrosPerMillionTokens"] = 300_000L,
            ["outputUsdMicrosPerMillionTokens"] = 2_500_000L,
            ["cachedInputUsdMicrosPerMillionTokens"] = 30_000L,
            ["reasoningUsdMicrosPerMillionTokens"] = 2_500_000L,
            ["inputAudioUsdMicrosPerMillionTokens"] = 300_000L,
            ["usdToInrPaise"] = 10_000L,
            ["creditMicrosPerInrPaise"] = 10_000L,
            ["liveInputTextUsdMicrosPerMillionTokens"] = 500_000L,
            ["liveInputAudioUsdMicrosPerMillionTokens"] = 3_000_000L,
            ["liveInputImageUsdMicrosPerMillionTokens"] = 3_000_000L,
            ["liveOutputTextUsdMicrosPerMillionTokens"] = 2_000_000L,
            ["liveOutputAudioUsdMicrosPerMillionTokens"] = 12_000_000L,
        },
        ["products"] = new object[]
        {
            new Dictionary<string, object>
            {
                ["sku"] = "monthly_standard_inr_999_v1",
                ["kind"] = "subscription",
                ["displayName"] = "Jarvis Monthly",
                ["amountMinor"] = 99_900L,
                ["currency"] = "INR",
                ["creditMicros"] = 500_000_000L,
                ["razorpayPlanId"] = FixturePlanId,
                ["enabled"] = true,
            },
        },
    };

    private static IDictionary<string, object> Pricing(IDictionary<string, object> document) =>
        (IDictionary<string, object>)document["pricing"];

    private static IDictionary<string, object> Product(IDictionary<string, object> document) =>
        (IDictionary<string, object>)((object[])document["products"])[0];

    private static object? InvalidValue(string shape) => shape switch
    {
        "null" => null,
        "blank" => " ",
        "boolean" => true,
        "boolean-string" => "true",
        "positive" => 1L,
        "zero" => 0L,
        "negative" => -1L,
        "fractional" => 0.5d,
        "numeric-string" => "10000",
        _ => throw new ArgumentOutOfRangeException(nameof(shape)),
    };

    private static void AssertLaunchPricing(PricingSnapshot pricing)
    {
        Assert.Equal(new PricingSnapshot(
            "vertex-standard-global-fx100-2026-08-11", "gemini-3.5-flash-lite",
            300_000, 2_500_000, 30_000, 2_500_000, 10_000, 10_000, FixtureTime,
            300_000, 500_000, 3_000_000, 3_000_000, 2_000_000, 12_000_000), pricing);
    }

    private sealed class FixtureClock : IClock
    {
        public DateTimeOffset UtcNow => FixtureTime;
    }
}
