using System.Net;
using System.Text;
using System.Text.Json;
using KeyboardWtf.Staging.Api.Configuration;
using KeyboardWtf.Staging.Api.Domain;
using KeyboardWtf.Staging.Api.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace KeyboardWtf.Staging.Api.Tests;

// These tests temporarily replace process-wide payment credentials. Keeping the
// collection nonparallel also isolates it from the existing provider tests.
[CollectionDefinition("Account deletion provider transport", DisableParallelization = true)]
public sealed class AccountDeletionProviderTransportCollection { }

[Collection("Account deletion provider transport")]
public sealed class AccountDeletionProviderTransportTests
{
    private const string SubscriptionId = "sub_123";
    private const string TestKeyId = "rzp_test_account_deletion";
    private const string TestKeySecret = "synthetic_account_deletion_secret";

    [Fact]
    public async Task Cancellation_state_inspection_reads_only_the_authenticated_subscription_and_carries_ownership_notes()
    {
        var handler = new SubscriptionTransportHandler(HttpMethod.Get, InspectionPath,
            _ => JsonResponse(SubscriptionJson("active", scheduled: true)));

        await WithProviderAsync(handler, async provider =>
        {
            var result = await provider.InspectSubscriptionCancellationStateAsync(SubscriptionId, CancellationToken.None);

            Assert.Equal(SubscriptionId, result.Id);
            Assert.Equal("active", result.Status);
            Assert.True(result.HasScheduledChanges);
            Assert.Equal("cycle_end", result.ScheduledChangeAt);
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1789257600), result.CurrentCycleEndsAt);
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1788048000), result.ChangeScheduledAt);
            Assert.Equal("pur_123", result.PurchaseId);
            Assert.Equal("monthly_test", result.ProductSku);
            Assert.Equal("staging", result.Environment);
            var request = Assert.Single(handler.Requests);
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(InspectionPath, request.Path);
            Assert.Null(request.Body);
        });
    }

    [Fact]
    public async Task Cancellation_state_inspection_preserves_missing_notes_as_missing_evidence()
    {
        var handler = new SubscriptionTransportHandler(HttpMethod.Get, InspectionPath,
            _ => JsonResponse("""{"id":"sub_123","status":"cancelled"}"""));

        await WithProviderAsync(handler, async provider =>
        {
            var result = await provider.InspectSubscriptionCancellationStateAsync(SubscriptionId, CancellationToken.None);

            Assert.Null(result.PurchaseId);
            Assert.Null(result.ProductSku);
            Assert.Null(result.Environment);
            Assert.Single(handler.Requests);
        });
    }

    [Fact]
    public async Task Immediate_cancellation_posts_false_once_without_invoice_order_payment_or_refund_requests()
    {
        var handler = new SubscriptionTransportHandler(HttpMethod.Post, CancellationPath,
            _ => JsonResponse(SubscriptionJson("cancelled")));

        await WithProviderAsync(handler, async provider =>
        {
            var result = await provider.CancelSubscriptionImmediatelyAsync(SubscriptionId, CancellationToken.None);

            Assert.Equal(SubscriptionId, result.Id);
            Assert.Equal("cancelled", result.Status);
            Assert.False(result.HasScheduledChanges);
            Assert.Equal("pur_123", result.PurchaseId);
            Assert.Equal("monthly_test", result.ProductSku);
            Assert.Equal("staging", result.Environment);
            AssertImmediateCancellationRequest(Assert.Single(handler.Requests));
        });
    }

    [Theory]
    [InlineData("created")]
    [InlineData("authenticated")]
    [InlineData("active")]
    [InlineData("pending")]
    [InlineData("halted")]
    [InlineData("")]
    public async Task Immediate_cancellation_rejects_a_provider_response_that_did_not_stop_the_subscription(string status)
    {
        var handler = new SubscriptionTransportHandler(HttpMethod.Post, CancellationPath,
            _ => JsonResponse(SubscriptionJson(status, scheduled: true)));

        await WithProviderAsync(handler, async provider =>
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                provider.CancelSubscriptionImmediatelyAsync(SubscriptionId, CancellationToken.None));

            AssertImmediateCancellationRequest(Assert.Single(handler.Requests));
        });
    }

    [Theory]
    [InlineData(false, "sub_other")]
    [InlineData(true, "sub_other")]
    [InlineData(false, "SUB_123")]
    [InlineData(true, "SUB_123")]
    [InlineData(false, "")]
    [InlineData(true, "")]
    public async Task Cancellation_operations_reject_a_provider_response_for_another_subscription(bool immediate, string returnedId)
    {
        var handler = new SubscriptionTransportHandler(
            immediate ? HttpMethod.Post : HttpMethod.Get,
            immediate ? CancellationPath : InspectionPath,
            _ => JsonResponse(SubscriptionJson("cancelled", returnedId: returnedId)));

        await WithProviderAsync(handler, async provider =>
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ExecuteAsync(provider, immediate, SubscriptionId));

            Assert.Contains("another subscription", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Single(handler.Requests);
        });
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task Cancellation_state_inspection_retries_transient_reads_and_stops_at_the_subscription_resource(HttpStatusCode status)
    {
        var handler = new SubscriptionTransportHandler(HttpMethod.Get, InspectionPath,
            attempt => attempt < 3 ? ErrorResponse(status) : JsonResponse(SubscriptionJson("cancelled")));

        await WithProviderAsync(handler, async provider =>
        {
            var result = await provider.InspectSubscriptionCancellationStateAsync(SubscriptionId, CancellationToken.None);

            Assert.Equal("cancelled", result.Status);
            Assert.Equal(3, handler.Requests.Count);
            Assert.All(handler.Requests, request => Assert.Null(request.Body));
        });
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Cancellation_state_inspection_exhausts_transient_failures_after_three_attempts(HttpStatusCode status)
    {
        var handler = new SubscriptionTransportHandler(HttpMethod.Get, InspectionPath, _ => ErrorResponse(status));

        await WithProviderAsync(handler, async provider =>
        {
            var error = await Assert.ThrowsAsync<RazorpayPaymentProvider.RazorpayProviderHttpException>(() =>
                provider.InspectSubscriptionCancellationStateAsync(SubscriptionId, CancellationToken.None));

            Assert.Equal((int)status, error.StatusCode);
            Assert.Equal(3, handler.Requests.Count);
        });
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Cancellation_state_inspection_does_not_retry_nontransient_failures(HttpStatusCode status)
    {
        var handler = new SubscriptionTransportHandler(HttpMethod.Get, InspectionPath, _ => ErrorResponse(status));

        await WithProviderAsync(handler, async provider =>
        {
            var error = await Assert.ThrowsAsync<RazorpayPaymentProvider.RazorpayProviderHttpException>(() =>
                provider.InspectSubscriptionCancellationStateAsync(SubscriptionId, CancellationToken.None));

            Assert.Equal((int)status, error.StatusCode);
            Assert.Single(handler.Requests);
        });
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Immediate_cancellation_does_not_repeat_a_mutation_after_a_transient_provider_failure(HttpStatusCode status)
    {
        var handler = new SubscriptionTransportHandler(HttpMethod.Post, CancellationPath, _ => ErrorResponse(status));

        await WithProviderAsync(handler, async provider =>
        {
            var error = await Assert.ThrowsAsync<RazorpayPaymentProvider.RazorpayProviderHttpException>(() =>
                provider.CancelSubscriptionImmediatelyAsync(SubscriptionId, CancellationToken.None));

            Assert.Equal((int)status, error.StatusCode);
            AssertImmediateCancellationRequest(Assert.Single(handler.Requests));
        });
    }

    public static IEnumerable<object?[]> InvalidSubscriptionIds =>
    [
        [null],
        [""],
        [" "],
        ["order_123"],
        ["SUB_123"],
        ["sub_123/../orders"],
        ["sub_123?expand=invoices"],
        ["sub_ä123"],
        ["sub_" + new string('a', 97)],
    ];

    [Theory]
    [MemberData(nameof(InvalidSubscriptionIds))]
    public async Task Both_cancellation_operations_reject_invalid_subscription_ids_before_http(string? subscriptionId)
    {
        var handler = new SubscriptionTransportHandler(HttpMethod.Get, InspectionPath,
            _ => throw new InvalidOperationException("Invalid IDs must never reach the transport."));

        await WithProviderAsync(handler, async provider =>
        {
            await Assert.ThrowsAsync<ArgumentException>(() =>
                provider.InspectSubscriptionCancellationStateAsync(subscriptionId!, CancellationToken.None));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                provider.CancelSubscriptionImmediatelyAsync(subscriptionId!, CancellationToken.None));

            Assert.Empty(handler.Requests);
        });
    }

    private static string InspectionPath => $"subscriptions/{SubscriptionId}";
    private static string CancellationPath => $"{InspectionPath}/cancel";

    private static Task<ProviderSubscriptionCancellation> ExecuteAsync(IPaymentProvider provider, bool immediate, string subscriptionId) =>
        immediate
            ? provider.CancelSubscriptionImmediatelyAsync(subscriptionId, CancellationToken.None)
            : provider.InspectSubscriptionCancellationStateAsync(subscriptionId, CancellationToken.None);

    private static async Task WithProviderAsync(SubscriptionTransportHandler handler, Func<IPaymentProvider, Task> assertion)
    {
        string[] names = ["RAZORPAY_KEY_ID", "RAZORPAY_KEY_SECRET", "RAZORPAY_WEBHOOK_SECRET"];
        var original = names.ToDictionary(name => name, name => Environment.GetEnvironmentVariable(name));
        try
        {
            Environment.SetEnvironmentVariable("RAZORPAY_KEY_ID", TestKeyId);
            Environment.SetEnvironmentVariable("RAZORPAY_KEY_SECRET", TestKeySecret);
            Environment.SetEnvironmentVariable("RAZORPAY_WEBHOOK_SECRET", "synthetic_account_deletion_webhook_secret");
            using var client = new HttpClient(handler);
            IPaymentProvider provider = new RazorpayPaymentProvider(client, Options.Create(new PaidSystemOptions
            {
                DeploymentEnvironment = PaidDeploymentEnvironment.Staging,
                ProjectId = "keyboard-wtf-staging",
                WebBaseUrl = "https://staging.example.test",
                AllowedOrigins = ["https://staging.example.test"],
                VertexModel = "gemini-test",
            }));

            await assertion(provider);
        }
        finally
        {
            foreach (var name in names)
                Environment.SetEnvironmentVariable(name, original[name]);
        }
    }

    private static void AssertImmediateCancellationRequest(TransportRequest request)
    {
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(CancellationPath, request.Path);
        Assert.NotNull(request.Body);
        using var payload = JsonDocument.Parse(request.Body);
        var property = Assert.Single(payload.RootElement.EnumerateObject());
        Assert.Equal("cancel_at_cycle_end", property.Name);
        Assert.Equal(JsonValueKind.False, property.Value.ValueKind);
        Assert.DoesNotContain(TestKeySecret, request.Body, StringComparison.Ordinal);
    }

    private static string SubscriptionJson(string status, bool scheduled = false, string returnedId = SubscriptionId) =>
        JsonSerializer.Serialize(new
        {
            id = returnedId,
            status,
            has_scheduled_changes = scheduled,
            schedule_change_at = scheduled ? "cycle_end" : null,
            current_end = 1789257600,
            change_scheduled_at = scheduled ? 1788048000 : (int?)null,
            notes = new { purchase_id = "pur_123", product_sku = "monthly_test", environment = "staging" },
        });

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage ErrorResponse(HttpStatusCode status) => new(status)
    {
        Content = new StringContent("synthetic provider failure", Encoding.UTF8, "application/json"),
    };

    private sealed record TransportRequest(HttpMethod Method, string Path, string? Body);

    private sealed class SubscriptionTransportHandler(
        HttpMethod expectedMethod,
        string expectedPath,
        Func<int, HttpResponseMessage> response) : HttpMessageHandler
    {
        public List<TransportRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var path = uri.PathAndQuery.TrimStart('/');
            if (path.StartsWith("v1/", StringComparison.Ordinal))
                path = path[3..];
            Requests.Add(new TransportRequest(request.Method, path, body));

            Assert.Equal("https", uri.Scheme);
            Assert.Equal("api.razorpay.com", uri.Host);
            Assert.Equal("Basic", request.Headers.Authorization?.Scheme);
            Assert.Equal(Convert.ToBase64String(Encoding.ASCII.GetBytes($"{TestKeyId}:{TestKeySecret}")),
                request.Headers.Authorization?.Parameter);
            Assert.Equal(expectedMethod, request.Method);
            Assert.Equal(expectedPath, path);
            Assert.DoesNotContain(TestKeySecret, uri.AbsoluteUri, StringComparison.Ordinal);
            return response(Requests.Count);
        }
    }
}
