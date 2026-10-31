using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using FirebaseAdmin;
using FirebaseAdmin.Auth;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Http;
using KeyboardWtf.Staging.Api.Domain;
using KeyboardWtf.Staging.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KeyboardWtf.Staging.Api.Tests;

// The real pinned Firebase SDK uses a wholly inert HTTP handler. No credentials,
// DNS, sockets, default Firebase app, provider account or real UID are accessed.
public sealed class FirebaseDeletionAdapterTests
{
    [Theory]
    [InlineData("success", 2)]
    [InlineData("missing", 1)]
    public async Task Delete_or_exact_uid_absence_is_terminal(string scenario, int calls)
    {
        using var f = new Fixture(scenario);
        await f.Service.DeleteFirebaseUserAsync(Fixture.Uid, default);
        Assert.Equal(calls, f.Handler.Calls);
        if (scenario == "success") Assert.Equal(new[] { "accounts:update", "accounts:delete" }, f.Handler.Routes);
    }

    [Theory]
    [InlineData("permission")]
    [InlineData("invalid")]
    public async Task Nonabsence_provider_failures_never_become_deletion_success(string scenario)
    {
        using var f = new Fixture(scenario);
        var error = await Assert.ThrowsAsync<FirebaseAuthException>(() => f.Service.DeleteFirebaseUserAsync(Fixture.Uid, default));
        Assert.NotEqual(AuthErrorCode.UserNotFound, error.AuthErrorCode);
        Assert.Equal(1, f.Handler.Calls);
    }

    [Fact]
    public async Task Cancelled_request_never_dispatches_provider_work()
    {
        using var f = new Fixture("success");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Service.DeleteFirebaseUserAsync(Fixture.Uid, cts.Token));
        Assert.Equal(0, f.Handler.Calls);
    }

    private sealed class Fixture : IDisposable
    {
        internal const string Uid = "synthetic-delete-adapter-uid";
        private readonly FirebaseApp _app;
        internal InertHandler Handler { get; }
        internal FirebaseIdentityService Service { get; }
        internal Fixture(string scenario)
        {
            Handler = new(scenario);
            _app = FirebaseApp.Create(new AppOptions
            {
                ProjectId = "synthetic-offline-deletion", Credential = GoogleCredential.FromAccessToken("synthetic-inert-access-token"),
                HttpClientFactory = new InertFactory(Handler),
            }, "offline-deletion-" + Guid.NewGuid().ToString("N"));
            Service = new(DispatchProxy.Create<IAccountStore, NoStore>(), NullLogger<FirebaseIdentityService>.Instance,
                (_, _) => throw new NotSupportedException(), FirebaseAuth.GetAuth(_app));
        }
        public void Dispose() => _app.Delete();
    }

    private sealed class InertFactory(InertHandler handler) : HttpClientFactory
    {
        protected override HttpMessageHandler CreateHandler(CreateHttpClientArgs args) => handler;
    }

    private sealed class InertHandler(string scenario) : HttpMessageHandler
    {
        internal int Calls;
        internal List<string> Routes { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assert.Equal("identitytoolkit.googleapis.com", request.RequestUri!.Host);
            Assert.Equal(HttpMethod.Post, request.Method);
            var route = request.RequestUri.Segments.Last();
            Routes.Add(route); Calls++;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var localId = body.RootElement.GetProperty("localId");
            Assert.Equal(Fixture.Uid, localId.ValueKind == JsonValueKind.Array ? localId[0].GetString() : localId.GetString());
            var code = scenario switch { "permission" => HttpStatusCode.Forbidden, "invalid" or "missing" => HttpStatusCode.BadRequest, _ => HttpStatusCode.OK };
            var json = scenario switch
            {
                "missing" => "{\"error\":{\"code\":400,\"message\":\"USER_NOT_FOUND\"}}",
                "permission" => "{\"error\":{\"code\":403,\"message\":\"PERMISSION_DENIED\"}}",
                "invalid" => "{\"error\":{\"code\":400,\"message\":\"INVALID_ARGUMENT\"}}",
                _ when route == "accounts:update" => "{\"localId\":\"" + Fixture.Uid + "\"}",
                _ when route == "accounts:delete" => "{\"kind\":\"identitytoolkit#DeleteAccountResponse\"}",
                _ => throw new InvalidOperationException("Unexpected adapter route."),
            };
            return new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    public class NoStore : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) => throw new InvalidOperationException("Adapter test never uses account storage.");
    }
}
