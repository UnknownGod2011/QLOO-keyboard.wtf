using System.Net;
using KeyboardWtf.Services.Updates;
using Xunit;

namespace KeyboardWtf.Tests;

[Collection("Live event integration")]
public sealed class PublicUpdateEligibilityTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("0", false)]
    [InlineData("1", false)]
    [InlineData("true", false)]
    [InlineData(null, true)]
    [InlineData("0", true)]
    [InlineData("1", true)]
    [InlineData("true", true)]
    public async Task Default_public_update_boundary_obeys_the_actual_build_and_staging_policy(
        string stagingEnabled, bool download)
    {
        var oldStaging = Environment.GetEnvironmentVariable("KEYBOARD_WTF_PAID_STAGING");
        var oldBackend = Environment.GetEnvironmentVariable("KEYBOARD_WTF_PAID_BACKEND_URL");
        var oldWeb = Environment.GetEnvironmentVariable("KEYBOARD_WTF_PAID_WEB_URL");
        var temporaryParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var ownedDirectoryName = $"kwt-update-eligibility-{Guid.NewGuid():N}";
        var root = Path.GetFullPath(Path.Combine(temporaryParent, ownedDirectoryName));
        try
        {
            Environment.SetEnvironmentVariable("KEYBOARD_WTF_PAID_STAGING", stagingEnabled);
            // Configuration-only fixtures: the handler below owns every HTTP request.
            Environment.SetEnvironmentVariable("KEYBOARD_WTF_PAID_BACKEND_URL", "http://127.0.0.1:1/");
            Environment.SetEnvironmentVariable("KEYBOARD_WTF_PAID_WEB_URL", "http://127.0.0.1:1/");
#if KEYBOARD_WTF_TEST_PRIVATE_CANDIDATE
            bool eligible = false;
#elif KEYBOARD_WTF_TEST_CONNECTOR_STAGING_CANDIDATE
            bool eligible = false; // Pinned connector candidate never uses the public update channel.
#elif KEYBOARD_WTF_TEST_PUBLIC_PRODUCTION
            bool eligible = true; // Public production intentionally ignores staging overrides.
#else
            var eligible = stagingEnabled is not ("1" or "true");
#endif
            var handler = new CountingRejectHandler();
            using var service = new SecureUpdateService(handler, downloadRoot: root);
            Func<Task> operation = download
                ? async () => { _ = await service.DownloadAsync(DownloadFixture(), CancellationToken.None); }
                : async () => { _ = await service.CheckAsync("stable", CancellationToken.None); };

            if (eligible)
            {
                await Assert.ThrowsAsync<HttpRequestException>(operation);
                Assert.Equal(1, handler.Requests);
            }
            else
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(operation);
                Assert.Contains("public updates", error.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(0, handler.Requests);
                Assert.False(Directory.Exists(root));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("KEYBOARD_WTF_PAID_STAGING", oldStaging);
            Environment.SetEnvironmentVariable("KEYBOARD_WTF_PAID_BACKEND_URL", oldBackend);
            Environment.SetEnvironmentVariable("KEYBOARD_WTF_PAID_WEB_URL", oldWeb);
            Assert.True(string.Equals(Path.GetDirectoryName(root), temporaryParent, StringComparison.OrdinalIgnoreCase),
                "Update fixture cleanup must stay in its direct temporary parent.");
            Assert.Equal(ownedDirectoryName, Path.GetFileName(root));
            Assert.True(ownedDirectoryName.StartsWith("kwt-update-eligibility-", StringComparison.Ordinal)
                && Guid.TryParseExact(ownedDirectoryName["kwt-update-eligibility-".Length..], "N", out _),
                "Update fixture cleanup must target only its exact generated directory.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static VerifiedUpdate DownloadFixture() => new(
        new Version(9, 0, 0), new Version(1, 4, 19), "stable", DateTimeOffset.UtcNow,
        new Uri("https://github.com/UnknownGod2011/keyboard.wtf/releases/download/v9.0.0/keyboard.wtf-setup.exe"),
        new string('a', 64), 10, "Test fixture", UpdateManifestVerifier.CurrentKeyId);

    private sealed class CountingRejectHandler : HttpMessageHandler
    {
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
