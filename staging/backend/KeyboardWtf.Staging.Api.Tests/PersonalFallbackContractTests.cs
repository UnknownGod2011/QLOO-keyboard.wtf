using System.Text.Json;
using KeyboardWtf.Staging.Api.Infrastructure;
using KeyboardWtf.Staging.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KeyboardWtf.Staging.Api.Tests;

public sealed class PersonalFallbackContractTests
{
    [Fact]
    public void Only_initial_insufficient_wallet_balance_is_typed_and_never_mutates_wallet()
    {
        var wallet = AllowanceWallet.FromAccount(new Dictionary<string, object>(), DateTimeOffset.UtcNow);
        Assert.Throws<ManagedAllowanceRefusedException>(() => wallet.Reserve(100, initialReservation: true));
        Assert.Equal(0, wallet.AvailableCreditMicros);
        Assert.Equal(0, wallet.ReservedCreditMicros);
        Assert.Throws<InvalidOperationException>(() => wallet.Reserve(100));
        Assert.Throws<InvalidOperationException>(() => wallet.Extend([], 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => wallet.Reserve(0, initialReservation: true));
    }

    [Theory]
    [InlineData("/v1/ai/generate", true, true)]
    [InlineData("/v1/ai/live", true, true)]
    [InlineData("/v1/account", true, false)]
    [InlineData("/v1/ai/generate", false, false)]
    [InlineData("/v1/ai/live", false, false)]
    public async Task Only_typed_initial_refusal_adds_contract_without_changing_existing_credit_status(string path, bool typed, bool fallback)
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        var middleware = new ApiExceptionMiddleware(_ => throw (typed
            ? new ManagedAllowanceRefusedException()
            : new InvalidOperationException("No usable credits are available. Add credits to continue.")),
            NullLogger<ApiExceptionMiddleware>.Instance);
        await middleware.InvokeAsync(context);
        Assert.Equal(402, context.Response.StatusCode);
        Assert.Equal(fallback, context.Response.Headers.ContainsKey("X-Keyboard-Wtf-AI-Refusal"));
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal("No usable credits are available. Add credits to continue.", document.RootElement.GetProperty("detail").GetString());
        if (fallback)
        {
            Assert.Equal("reservation_pre_dispatch", document.RootElement.GetProperty("stage").GetString());
            Assert.False(document.RootElement.GetProperty("reservationCreated").GetBoolean());
            Assert.False(document.RootElement.GetProperty("providerDispatched").GetBoolean());
        }
        else Assert.False(document.RootElement.TryGetProperty("code", out _));
    }
}
