using System.Net;
using System.Net.Http;
using System.Text.Json;
using KeyboardWtf.Services.Ai.Backends;
using Xunit;

namespace KeyboardWtf.Tests;

public sealed class PersonalGeminiKeyValidatorTests
{
    private const string Canary = "synthetic-metadata-test-not-a-real-key-1234";

    [Fact]
    public async Task Metadata_only_uses_header_auth_not_url_body_managed_endpoint_or_generation()
    {
        var calls = 0;
        using var validator = new PersonalGeminiKeyValidator(new Handler((request, _) =>
        {
            calls++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("generativelanguage.googleapis.com", request.RequestUri.Host);
            Assert.Equal("https", request.RequestUri.Scheme);
            Assert.Empty(request.RequestUri.Query);
            Assert.DoesNotContain(Canary, request.RequestUri.ToString());
            Assert.DoesNotContain(":generateContent", request.RequestUri.ToString());
            Assert.Null(request.Content);
            Assert.Equal(Canary, Assert.Single(request.Headers.GetValues("x-goog-api-key")));
            return Reply(200, JsonSerializer.Serialize(new { name = "models/" + PersonalGeminiModels.Text,
                supportedGenerationMethods = new[] { "generateContent" } }));
        }));
        var result = await validator.ValidateAsync(Canary);
        Assert.Equal(PersonalKeyValidation.AuthenticationValidated, result.Status);
        Assert.Contains("have not been tested", result.Message);
        Assert.Equal(1, calls);
        Assert.DoesNotContain(Canary, JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData(401, 2)]
    [InlineData(403, 3)]
    [InlineData(404, 3)]
    [InlineData(429, 4)]
    [InlineData(503, 4)]
    [InlineData(302, 0)]
    [InlineData(400, 0)]
    public async Task Failure_classification_is_sanitized_and_redirects_do_not_send_a_second_request(int status, int expected)
    {
        var calls = 0;
        using var validator = new PersonalGeminiKeyValidator(new Handler((_, _) =>
        {
            calls++;
            var result = Reply(status, JsonSerializer.Serialize(new { error = new { message = Canary } }));
            result.Result.Headers.Location = new Uri("https://unrelated.invalid/");
            return result;
        }));
        var result = await validator.ValidateAsync(Canary);
        Assert.Equal((PersonalKeyValidation)expected, result.Status);
        Assert.Equal(1, calls);
        Assert.DoesNotContain(Canary, JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task Definitive_structured_invalid_key_reason_is_distinct_from_model_permission_denial()
    {
        using var validator = new PersonalGeminiKeyValidator(new Handler((_, _) => Reply(400,
            "{\"error\":{\"details\":[{\"reason\":\"API_KEY_INVALID\"}]}}")));
        Assert.Equal(PersonalKeyValidation.InvalidCredential, (await validator.ValidateAsync(Canary)).Status);
    }

    [Fact]
    public async Task Raw_transport_exception_and_malformed_success_cannot_echo_secret_to_user()
    {
        using var network = new PersonalGeminiKeyValidator(new Handler((_, _) => throw new HttpRequestException(Canary)));
        using var malformed = new PersonalGeminiKeyValidator(new Handler((_, _) => Reply(200, Canary)));
        foreach (var validator in new[] { network, malformed })
        {
            var result = await validator.ValidateAsync(Canary);
            Assert.Equal(PersonalKeyValidation.Unknown, result.Status);
            Assert.DoesNotContain(Canary, JsonSerializer.Serialize(result));
        }
    }

    [Fact]
    public async Task Caller_cancellation_is_not_success_or_an_inactive_credential_update()
    {
        using var cancellation = new CancellationTokenSource();
        using var validator = new PersonalGeminiKeyValidator(new Handler(async (_, token) =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage();
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => validator.ValidateAsync(Canary, cancellation.Token));
    }

    [Fact]
    public async Task Timeout_is_bounded_and_never_falls_back_to_managed_validation()
    {
        var calls = 0;
        using var validator = new PersonalGeminiKeyValidator(new Handler(async (_, token) =>
        {
            calls++;
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage();
        }), TimeSpan.FromMilliseconds(20));
        Assert.Equal(PersonalKeyValidation.Unavailable, (await validator.ValidateAsync(Canary)).Status);
        Assert.Equal(1, calls);
    }

    private static Task<HttpResponseMessage> Reply(int status, string body) => Task.FromResult(
        new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body) });
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, cancellationToken);
    }
}
