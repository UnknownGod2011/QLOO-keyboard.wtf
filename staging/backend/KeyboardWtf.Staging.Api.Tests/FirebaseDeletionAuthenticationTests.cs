using System.Reflection;
using System.Text;
using System.Text.Json;
using KeyboardWtf.Staging.Api.Configuration;
using KeyboardWtf.Staging.Api.Domain;
using KeyboardWtf.Staging.Api.Endpoints;
using KeyboardWtf.Staging.Api.Infrastructure;
using KeyboardWtf.Staging.Api.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace KeyboardWtf.Staging.Api.Tests;

// The verifier seam and rejecting store double never initialize Firebase,
// credentials, Firestore, a listening host, or a real user profile.
public sealed class FirebaseDeletionAuthenticationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly CallerIdentity FreshCaller = new(
        "synthetic-delete-user", "synthetic@example.invalid", "Synthetic User", "firebase",
        AuthenticatedAt: Now.AddMinutes(-1));
    private const string FirebaseToken = "synthetic-firebase-id-token";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Normal_firebase_verification_syncs_once_and_preserves_caller(bool directVerification)
    {
        var (store, recording) = CreateStore();
        var verifier = new RecordingVerifier(FreshCaller);
        var service = CreateIdentity(store, verifier);
        var caller = directVerification
            ? await service.VerifyFirebaseAsync(FirebaseToken, CancellationToken.None)
            : await service.AuthenticateAsync(Context("Bearer " + FirebaseToken), CancellationToken.None);

        Assert.Same(FreshCaller, caller);
        Assert.Equal(1, verifier.Calls);
        Assert.Equal(FirebaseToken, verifier.LastToken);
        Assert.Equal(new[] { nameof(IAccountStore.EnsureAccountAsync) }, recording.Calls);
        Assert.Same(FreshCaller, recording.LastEnsureCaller);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Normal_firebase_auth_propagates_nonactive_account_rejection(bool directVerification)
    {
        var (store, recording) = CreateStore();
        var rejection = new UnauthorizedAccessException("The account is not active.");
        recording.EnsureFailure = rejection;
        var verifier = new RecordingVerifier(FreshCaller);
        var service = CreateIdentity(store, verifier);

        var actual = await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
        {
            if (directVerification)
                await service.VerifyFirebaseAsync(FirebaseToken, CancellationToken.None);
            else
                await service.AuthenticateAsync(Context("Bearer " + FirebaseToken), CancellationToken.None);
        });

        Assert.Same(rejection, actual);
        Assert.Equal(1, verifier.Calls);
        Assert.Equal(new[] { nameof(IAccountStore.EnsureAccountAsync) }, recording.Calls);
    }

    [Fact]
    public async Task Ordinary_desktop_auth_remains_on_its_existing_store_path()
    {
        var (store, recording) = CreateStore();
        var desktopCaller = FreshCaller with { AuthenticationKind = "desktop", SessionId = "synthetic-session" };
        recording.DesktopCaller = desktopCaller;
        var verifier = new RecordingVerifier(FreshCaller);

        var caller = await CreateIdentity(store, verifier).AuthenticateAsync(
            Context("Bearer kwt_at_synthetic-session.token"), CancellationToken.None);

        Assert.Same(desktopCaller, caller);
        Assert.Equal(0, verifier.Calls);
        Assert.Equal(new[] { nameof(IAccountStore.AuthenticateDesktopTokenAsync) }, recording.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deletion_firebase_auth_never_syncs_or_authenticates_desktop_and_preserves_auth_time(bool missingAuthTime)
    {
        var (store, recording) = CreateStore();
        // A tombstone must not be revived even if ordinary profile sync would fail.
        recording.EnsureFailure = new UnauthorizedAccessException("The account is not active.");
        var expected = missingAuthTime ? FreshCaller with { AuthenticatedAt = null } : FreshCaller;
        var verifier = new RecordingVerifier(expected);
        using var cancellation = new CancellationTokenSource();

        var caller = await CreateIdentity(store, verifier).AuthenticateForAccountDeletionAsync(
            Context("bEaReR " + FirebaseToken + " "), cancellation.Token);

        Assert.Same(expected, caller);
        Assert.Equal(expected.AuthenticationKind, caller!.AuthenticationKind);
        Assert.Equal(expected.AuthenticatedAt, caller.AuthenticatedAt);
        Assert.Equal(1, verifier.Calls);
        Assert.Equal(FirebaseToken, verifier.LastToken);
        Assert.Equal(cancellation.Token, verifier.LastCancellationToken);
        Assert.Empty(recording.Calls);
    }

    [Fact]
    public async Task Deletion_rejects_desktop_bearer_before_verifier_or_any_store_call()
    {
        var (store, recording) = CreateStore();
        recording.DesktopCaller = FreshCaller with { AuthenticationKind = "desktop" };
        var verifier = new RecordingVerifier(FreshCaller);

        var caller = await CreateIdentity(store, verifier).AuthenticateForAccountDeletionAsync(
            Context("Bearer kwt_at_synthetic-session.token"), CancellationToken.None);

        Assert.Null(caller);
        Assert.Equal(0, verifier.Calls);
        Assert.Empty(recording.Calls);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, "")]
    [InlineData(true, "")]
    [InlineData(false, "Basic synthetic")]
    [InlineData(true, "Basic synthetic")]
    public async Task Missing_or_nonbearer_header_never_reaches_verifier_or_store(bool deletion, string? header)
    {
        var (store, recording) = CreateStore();
        var verifier = new RecordingVerifier(FreshCaller);
        var service = CreateIdentity(store, verifier);

        var caller = deletion
            ? await service.AuthenticateForAccountDeletionAsync(Context(header), CancellationToken.None)
            : await service.AuthenticateAsync(Context(header), CancellationToken.None);

        Assert.Null(caller);
        Assert.Equal(0, verifier.Calls);
        Assert.Empty(recording.Calls);
    }

    [Fact]
    public async Task Older_identity_implementation_fails_closed_without_ordinary_auth_fallback()
    {
        var legacy = new LegacyIdentity();
        IIdentityService identities = legacy;

        var caller = await identities.AuthenticateForAccountDeletionAsync(
            Context("Bearer " + FirebaseToken), CancellationToken.None);

        Assert.Null(caller);
        Assert.Equal(0, legacy.OrdinaryAuthCalls);
        Assert.Equal(0, legacy.VerifyCalls);
        Assert.Equal(0, legacy.DeleteCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unexpected_verifier_failure_propagates_unchanged_without_store_mutation(bool deletion)
    {
        var (store, recording) = CreateStore();
        var failure = new InvalidOperationException("Synthetic verifier unavailable.");
        var verifier = new RecordingVerifier(FreshCaller) { Failure = failure };
        var service = CreateIdentity(store, verifier);

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            if (deletion)
                await service.AuthenticateForAccountDeletionAsync(Context("Bearer " + FirebaseToken), CancellationToken.None);
            else
                await service.AuthenticateAsync(Context("Bearer " + FirebaseToken), CancellationToken.None);
        });

        Assert.Same(failure, actual);
        Assert.Equal(1, verifier.Calls);
        Assert.Empty(recording.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Verifier_cancellation_preserves_token_and_never_syncs_profile(bool deletion)
    {
        var (store, recording) = CreateStore();
        var verifier = new RecordingVerifier(FreshCaller);
        var service = CreateIdentity(store, verifier);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            if (deletion)
                await service.AuthenticateForAccountDeletionAsync(Context("Bearer " + FirebaseToken), cancellation.Token);
            else
                await service.AuthenticateAsync(Context("Bearer " + FirebaseToken), cancellation.Token);
        });

        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.Equal(cancellation.Token, verifier.LastCancellationToken);
        Assert.Equal(1, verifier.Calls);
        Assert.Empty(recording.Calls);
    }

    [Theory]
    [InlineData(false, "firebase", -1)]
    [InlineData(true, "desktop", -1)]
    [InlineData(true, "firebase", -6)]
    [InlineData(true, "firebase", 2)]
    [InlineData(true, "firebase", null)]
    public async Task Deletion_service_keeps_confirmation_and_fresh_google_guards_before_cleanup(
        bool confirmed, string kind, int? authMinutes)
    {
        var (store, recording) = CreateStore();
        var identity = new LegacyIdentity();
        var service = new AccountDeletionService(store, identity, new FixedClock(), NoPayments());
        var caller = FreshCaller with
        {
            AuthenticationKind = kind,
            AuthenticatedAt = authMinutes is int minutes ? Now.AddMinutes(minutes) : null,
        };

        if (!confirmed)
            await Assert.ThrowsAsync<ArgumentException>(() => service.DeleteAsync(caller, confirmed, CancellationToken.None));
        else
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.DeleteAsync(caller, confirmed, CancellationToken.None));

        Assert.Empty(recording.Calls);
        Assert.Equal(0, identity.DeleteCalls);
    }

    [Fact]
    public async Task Valid_deletion_preserves_cleanup_then_identity_delete_order_without_profile_sync()
    {
        var events = new List<string>();
        var (store, recording) = CreateStore(events);
        var identity = new LegacyIdentity(events);

        await new AccountDeletionService(store, identity, new FixedClock(), NoPayments()).DeleteAsync(
            FreshCaller, true, CancellationToken.None);

        Assert.Equal(new[] { "renewal-fence", "operational-cleanup", "identity-delete", "deletion-finalize" }, events);
        Assert.Equal(new[] { nameof(IAccountStore.BeginConfirmedAccountDeletionAsync), nameof(IAccountStore.DeleteAccountOperationalDataAsync), nameof(IAccountStore.CompleteAccountDeletionAsync) }, recording.Calls);
        Assert.Equal(FreshCaller.UserId, recording.LastDeletedUserId);
        Assert.Equal(FreshCaller.UserId, identity.LastDeletedUserId);
        Assert.Equal(1, identity.DeleteCalls);
    }

    [Fact]
    public async Task Failed_operational_cleanup_cannot_proceed_to_identity_deletion()
    {
        var (store, recording) = CreateStore();
        var failure = new IOException("Synthetic cleanup failure.");
        recording.CleanupFailure = failure;
        var identity = new LegacyIdentity();

        var actual = await Assert.ThrowsAsync<IOException>(() =>
            new AccountDeletionService(store, identity, new FixedClock(), NoPayments()).DeleteAsync(
                FreshCaller, true, CancellationToken.None));

        Assert.Same(failure, actual);
        Assert.Equal(new[] { nameof(IAccountStore.BeginConfirmedAccountDeletionAsync), nameof(IAccountStore.DeleteAccountOperationalDataAsync) }, recording.Calls);
        Assert.Equal(0, identity.DeleteCalls);
    }

    [Fact]
    public async Task Real_endpoint_delegate_uses_deletion_auth_only_for_delete_and_normal_auth_for_ready()
    {
        var (store, recording) = CreateStore();
        var identities = new RoutedIdentity();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "DeletionAuthenticationTests",
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton<IIdentityService>(identities);
        builder.Services.AddSingleton<IClock>(new FixedClock());
        builder.Services.AddSingleton<IPaymentProvider>(NoPayments());
        builder.Services.AddSingleton<IOptions<PaidSystemOptions>>(Options.Create(new PaidSystemOptions()));
        builder.Services.AddSingleton<AccountDeletionService>();
        // Register endpoint service identities so Minimal API binding recognizes
        // them, but fail if any unrelated endpoint tries to instantiate one.
        foreach (var type in new[]
        {
            typeof(ICatalogService), typeof(IReviewService),
            typeof(DesktopLoginService), typeof(AiUsageService), typeof(VertexLiveProxyService),
            typeof(SubscriptionManagementService),
        })
            builder.Services.AddSingleton(type, _ => throw new InvalidOperationException("Unexpected endpoint service."));
        await using var app = builder.Build();
        app.MapKeyboardWtfApi();
        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints).OfType<RouteEndpoint>().ToArray();

        var ready = Assert.Single(endpoints, endpoint => endpoint.RoutePattern.RawText == "/v1/ready");
        var readyContext = Request(app.Services, "/v1/ready", null);
        await ready.RequestDelegate!(readyContext);
        Assert.Equal(200, readyContext.Response.StatusCode);
        Assert.Equal(1, identities.OrdinaryAuthCalls);
        Assert.Equal(0, identities.DeletionAuthCalls);
        Assert.Empty(recording.Calls);

        var deletion = Assert.Single(endpoints, endpoint => endpoint.RoutePattern.RawText == "/v1/account/delete");
        var deleteContext = Request(app.Services, "/v1/account/delete", "{\"confirmPermanentDeletion\":true}");
        await deletion.RequestDelegate!(deleteContext);
        Assert.Equal(200, deleteContext.Response.StatusCode);
        Assert.Equal(1, identities.OrdinaryAuthCalls);
        Assert.Equal(1, identities.DeletionAuthCalls);
        Assert.Equal(new[] { nameof(IAccountStore.BeginConfirmedAccountDeletionAsync), nameof(IAccountStore.DeleteAccountOperationalDataAsync), nameof(IAccountStore.CompleteAccountDeletionAsync) }, recording.Calls);
        Assert.Equal(1, identities.DeleteCalls);
        deleteContext.Response.Body.Position = 0;
        using var response = await JsonDocument.ParseAsync(deleteContext.Response.Body);
        Assert.Equal("deleted", response.RootElement.GetProperty("status").GetString());

        // A valid ordinary identity must not be used as a fallback when the
        // endpoint's separate deletion authentication refuses this request.
        identities.DeletionCaller = null;
        await Assert.ThrowsAsync<AuthenticationRequiredException>(() => deletion.RequestDelegate!(
            Request(app.Services, "/v1/account/delete", "{\"confirmPermanentDeletion\":true}")));
        Assert.Equal(1, identities.OrdinaryAuthCalls);
        Assert.Equal(2, identities.DeletionAuthCalls);
        Assert.Equal(new[] { nameof(IAccountStore.BeginConfirmedAccountDeletionAsync), nameof(IAccountStore.DeleteAccountOperationalDataAsync), nameof(IAccountStore.CompleteAccountDeletionAsync) }, recording.Calls);
        Assert.Equal(1, identities.DeleteCalls);
    }

    private static FirebaseIdentityService CreateIdentity(IAccountStore store, RecordingVerifier verifier) =>
        new(store, NullLogger<FirebaseIdentityService>.Instance, verifier.VerifyAsync);

    private static DefaultHttpContext Context(string? authorization)
    {
        var context = new DefaultHttpContext();
        if (authorization is not null)
            context.Request.Headers.Authorization = authorization;
        return context;
    }

    private static DefaultHttpContext Request(IServiceProvider services, string path, string? json)
    {
        var context = Context("Bearer " + FirebaseToken);
        context.RequestServices = services;
        context.Request.Path = path;
        context.Request.Method = json is null ? "GET" : "POST";
        context.Response.Body = new MemoryStream();
        if (json is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            context.Request.ContentType = "application/json";
            context.Request.ContentLength = bytes.Length;
            context.Request.Body = new MemoryStream(bytes);
            context.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyDetection());
        }
        return context;
    }

    private static (IAccountStore Store, RecordingStore Recording) CreateStore(List<string>? events = null)
    {
        var store = DispatchProxy.Create<IAccountStore, RecordingStore>();
        var recording = (RecordingStore)store;
        recording.Events = events;
        return (store, recording);
    }

    public class RecordingStore : DispatchProxy
    {
        public List<string> Calls { get; } = [];
        public List<string>? Events { get; set; }
        public Exception? EnsureFailure { get; set; }
        public Exception? CleanupFailure { get; set; }
        public CallerIdentity? LastEnsureCaller { get; private set; }
        public CallerIdentity? DesktopCaller { get; set; }
        public string? LastDeletedUserId { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            ArgumentNullException.ThrowIfNull(args);
            Calls.Add(targetMethod.Name);
            switch (targetMethod.Name)
            {
                case nameof(IAccountStore.BeginConfirmedAccountDeletionAsync):
                    Events?.Add("renewal-fence");
                    return Task.FromResult(new AccountDeletionSubscriptions([], false));
                case nameof(IAccountStore.EnsureAccountAsync):
                    LastEnsureCaller = (CallerIdentity)args[0]!;
                    return EnsureFailure is null ? Task.CompletedTask : Task.FromException(EnsureFailure);
                case nameof(IAccountStore.AuthenticateDesktopTokenAsync):
                    return Task.FromResult(DesktopCaller);
                case nameof(IAccountStore.DeleteAccountOperationalDataAsync):
                    LastDeletedUserId = (string)args[0]!;
                    Events?.Add("operational-cleanup");
                    return CleanupFailure is null ? Task.CompletedTask : Task.FromException(CleanupFailure);
                case nameof(IAccountStore.CompleteAccountDeletionAsync):
                    Events?.Add("deletion-finalize");
                    return Task.CompletedTask;
                default:
                    throw new InvalidOperationException("Unexpected account store call.");
            }
        }
    }

    private static IPaymentProvider NoPayments() => DispatchProxy.Create<IPaymentProvider, RejectingPayments>();

    public class RejectingPayments : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException("An account without subscriptions must not call the payment provider.");
    }

    private sealed class RecordingVerifier(CallerIdentity caller)
    {
        public int Calls { get; private set; }
        public string? LastToken { get; private set; }
        public CancellationToken LastCancellationToken { get; private set; }
        public Exception? Failure { get; init; }

        public Task<CallerIdentity> VerifyAsync(string token, CancellationToken cancellationToken)
        {
            Calls++;
            LastToken = token;
            LastCancellationToken = cancellationToken;
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled<CallerIdentity>(cancellationToken);
            return Failure is null ? Task.FromResult(caller) : Task.FromException<CallerIdentity>(Failure);
        }
    }

    // Deliberately does not implement the new default interface method.
    private sealed class LegacyIdentity(List<string>? events = null) : IIdentityService
    {
        public int OrdinaryAuthCalls { get; private set; }
        public int VerifyCalls { get; private set; }
        public int DeleteCalls { get; private set; }
        public string? LastDeletedUserId { get; private set; }

        public Task<CallerIdentity?> AuthenticateAsync(HttpContext context, CancellationToken cancellationToken)
        {
            OrdinaryAuthCalls++;
            return Task.FromResult<CallerIdentity?>(FreshCaller);
        }

        public Task<CallerIdentity> VerifyFirebaseAsync(string idToken, CancellationToken cancellationToken)
        {
            VerifyCalls++;
            return Task.FromResult(FreshCaller);
        }

        public Task DeleteFirebaseUserAsync(string userId, CancellationToken cancellationToken)
        {
            DeleteCalls++;
            LastDeletedUserId = userId;
            events?.Add("identity-delete");
            return Task.CompletedTask;
        }
    }

    private sealed class RoutedIdentity : IIdentityService
    {
        public int OrdinaryAuthCalls { get; private set; }
        public int DeletionAuthCalls { get; private set; }
        public int DeleteCalls { get; private set; }
        public CallerIdentity? DeletionCaller { get; set; } = FreshCaller;

        public Task<CallerIdentity?> AuthenticateAsync(HttpContext context, CancellationToken cancellationToken)
        {
            OrdinaryAuthCalls++;
            return Task.FromResult<CallerIdentity?>(FreshCaller);
        }

        public Task<CallerIdentity?> AuthenticateForAccountDeletionAsync(HttpContext context, CancellationToken cancellationToken)
        {
            DeletionAuthCalls++;
            return Task.FromResult(DeletionCaller);
        }

        public Task<CallerIdentity> VerifyFirebaseAsync(string idToken, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Endpoint must use context authentication.");

        public Task DeleteFirebaseUserAsync(string userId, CancellationToken cancellationToken)
        {
            DeleteCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class BodyDetection : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }
}
