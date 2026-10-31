using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using KeyboardWtf.Services;
using Xunit;

namespace KeyboardWtf.Tests;

public sealed class PiperVoiceDownloadBoundaryTests
{
    private const string OnnxUrl = "https://voice-fixture.invalid/fixture-amy-medium.onnx";
    private const string JsonUrl = OnnxUrl + ".json";
    private const string StagePrefix = ".piper-voice-download-";
    private static readonly byte[] OnnxBytes = "bounded ONNX voice fixture"u8.ToArray();
    private static readonly byte[] JsonBytes = "{\"voice\":\"bounded fixture\"}\n"u8.ToArray();
    private static readonly byte[] PreviousBytes = "previous incomplete voice bytes\r\n"u8.ToArray();
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData("none")]
    [InlineData("onnx")]
    [InlineData("json")]
    public async Task Exact_verified_pair_replaces_only_an_incomplete_voice_and_cleans_its_stage(string previous)
    {
        using var fixture = new Fixture();
        fixture.SeedIncomplete(previous);
        var handler = ValidHandler();
        using var http = new HttpClient(handler);

        await Invoke(fixture, http).WaitAsync(CompletionTimeout);

        fixture.AssertNewPair();
        Assert.Equal([Path.GetFileName(fixture.Onnx), Path.GetFileName(fixture.Json)], fixture.Entries());
        Assert.Equal([OnnxUrl, JsonUrl], handler.Uris.Select(uri => uri.AbsoluteUri));
    }

    [Fact]
    public async Task Existing_complete_legacy_pair_keeps_arbitrary_ONNX_and_CRLF_JSON_bytes_without_HTTP()
    {
        using var fixture = new Fixture();
        var legacyModel = "legacy installed model does not match new descriptor"u8.ToArray();
        var legacyJson = "{\r\n  \"legacy\": true\r\n}\r\n"u8.ToArray();
        File.WriteAllBytes(fixture.Onnx, legacyModel);
        File.WriteAllBytes(fixture.Json, legacyJson);
        var handler = new Handler((_, _) => UnexpectedHttp());
        using var http = new HttpClient(handler);

        await Invoke(fixture, http).WaitAsync(CompletionTimeout);

        Assert.Equal(0, handler.Requests);
        Assert.Equal(legacyModel, File.ReadAllBytes(fixture.Onnx));
        Assert.Equal(legacyJson, File.ReadAllBytes(fixture.Json));
        Assert.Equal([Path.GetFileName(fixture.Onnx), Path.GetFileName(fixture.Json)], fixture.Entries());
    }

    [Fact]
    public async Task Concurrent_requests_wait_and_recheck_readiness_so_only_one_pair_is_downloaded()
    {
        using var fixture = new Fixture();
        using var stream = new HeldStream(OnnxBytes);
        var handler = new Handler((request, _) => IsJson(request) ? Response(JsonBytes) : Response(stream));
        using var http = new HttpClient(handler);
        var owner = Invoke(fixture, http);
        await stream.ReadStarted.Task.WaitAsync(CompletionTimeout);
        var waiter = Invoke(fixture, http);
        Assert.False(waiter.IsCompleted);
        Assert.Equal(1, handler.Requests);
        stream.Release();

        await Task.WhenAll(owner, waiter).WaitAsync(CompletionTimeout);

        Assert.Equal(2, handler.Requests);
        fixture.AssertNewPair();
        Assert.Equal([Path.GetFileName(fixture.Onnx), Path.GetFileName(fixture.Json)], fixture.Entries());
    }

    [Theory]
    [InlineData("onnx")]
    [InlineData("json")]
    public async Task Partial_body_uses_a_GUID_child_stage_and_never_creates_a_canonical_pair(string heldFile)
    {
        using var fixture = new Fixture();
        var unowned = Path.Combine(fixture.Parent, StagePrefix + "unowned");
        Directory.CreateDirectory(unowned);
        var sentinel = Path.Combine(unowned, "another-operation.txt");
        File.WriteAllBytes(sentinel, PreviousBytes);
        using var stream = new HeldStream(heldFile == "json" ? JsonBytes : OnnxBytes);
        var handler = new Handler((request, _) => IsJson(request) == (heldFile == "json")
            ? Response(stream) : Response(IsJson(request) ? JsonBytes : OnnxBytes));
        using var http = new HttpClient(handler);
        var download = Invoke(fixture, http);
        await stream.ReadStarted.Task.WaitAsync(CompletionTimeout);

        Assert.False(File.Exists(fixture.Onnx));
        Assert.False(File.Exists(fixture.Json));
        var stage = Assert.Single(Directory.GetDirectories(fixture.Parent), path => path != unowned);
        Assert.Equal(fixture.Parent, Path.GetDirectoryName(stage), ignoreCase: true);
        var stageName = Path.GetFileName(stage);
        Assert.StartsWith(StagePrefix, stageName);
        Assert.True(Guid.TryParseExact(stageName[StagePrefix.Length..], "N", out _));
        Assert.Empty(Directory.GetFiles(fixture.Parent, "*.onnx"));
        Assert.Equal(PreviousBytes, File.ReadAllBytes(sentinel));
        stream.Release();
        await download.WaitAsync(CompletionTimeout);

        fixture.AssertNewPair();
        Assert.Equal([unowned], Directory.GetDirectories(fixture.Parent));
        Assert.Equal(PreviousBytes, File.ReadAllBytes(sentinel));
    }

    public static IEnumerable<object[]> TransferFailures()
    {
        foreach (var file in new[] { "onnx", "json" })
        foreach (var failure in new[] { "hash", "truncated", "oversize", "declared_short", "declared_long",
                     "declared_public_oversize", "unknown_length_excess", "http", "stream" })
            yield return [file, failure];
    }

    [Theory]
    [MemberData(nameof(TransferFailures))]
    public async Task Either_files_failed_transfer_preserves_the_previous_incomplete_file_and_removes_owned_stage(
        string failedFile, string failure)
    {
        using var fixture = new Fixture();
        fixture.SeedIncomplete(failedFile);
        var handler = new Handler((request, _) => IsJson(request) == (failedFile == "json")
            ? FailedResponse(failure, failedFile == "json" ? JsonBytes : OnnxBytes, failedFile == "json")
            : Response(IsJson(request) ? JsonBytes : OnnxBytes));
        using var http = new HttpClient(handler);
        var download = Invoke(fixture, http);

        await AssertFailed(download);

        fixture.AssertPreviousIncomplete(failedFile);
        Assert.Equal([Path.GetFileName(fixture.PathFor(failedFile))], fixture.Entries());
        Assert.InRange(handler.Requests, 1, 2);
    }

    [Theory]
    [InlineData("onnx", false)]
    [InlineData("onnx", true)]
    [InlineData("json", false)]
    [InlineData("json", true)]
    public async Task Cancellation_and_deadline_reach_either_stalled_body_and_release_the_gate(string file, bool deadline)
    {
        using var fixture = new Fixture();
        fixture.SeedIncomplete(file);
        using var stream = new HeldStream(file == "json" ? JsonBytes : OnnxBytes);
        var handler = new Handler((request, _) => IsJson(request) == (file == "json")
            ? Response(stream) : Response(IsJson(request) ? JsonBytes : OnnxBytes));
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
        using var cancellation = new CancellationTokenSource();
        var download = Invoke(fixture, http, token: cancellation.Token,
            // A wall deadline covers initial disk staging and the first file too.
            // Allow that setup under full-suite load before testing the held body.
            timeout: deadline ? TimeSpan.FromSeconds(2) : TimeSpan.FromMinutes(30));
        try { await stream.ReadStarted.Task.WaitAsync(CompletionTimeout); }
        catch (TimeoutException)
        {
            throw new Xunit.Sdk.XunitException($"Held {file} body never started: operation={download.Status}, requests={handler.Requests}, lastReadCancellable={stream.LastReadToken.CanBeCanceled}");
        }
        if (!deadline) cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download.WaitAsync(CompletionTimeout));

        Assert.True(stream.LastReadToken.CanBeCanceled);
        Assert.True(stream.LastReadToken.IsCancellationRequested);
        fixture.AssertPreviousIncomplete(file);
        Assert.Equal([Path.GetFileName(fixture.PathFor(file))], fixture.Entries());
        using var retryHttp = new HttpClient(ValidHandler());
        await Invoke(fixture, retryHttp).WaitAsync(CompletionTimeout);
        fixture.AssertNewPair();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_and_deadline_cover_stalled_response_headers_and_release_the_gate(bool deadline)
    {
        using var fixture = new Fixture();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var observedToken = CancellationToken.None;
        var handler = new Handler(respondAsync: async (_, token) =>
        {
            observedToken = token;
            started.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Response(OnnxBytes);
        });
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
        using var cancellation = new CancellationTokenSource();
        var download = Invoke(fixture, http, token: cancellation.Token,
            timeout: deadline ? TimeSpan.FromMilliseconds(250) : TimeSpan.FromMinutes(30));
        await started.Task.WaitAsync(CompletionTimeout);
        if (!deadline) cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download.WaitAsync(CompletionTimeout));

        Assert.True(observedToken.IsCancellationRequested);
        Assert.Empty(fixture.Entries());
        using var retryHttp = new HttpClient(ValidHandler());
        await Invoke(fixture, retryHttp).WaitAsync(CompletionTimeout);
        fixture.AssertNewPair();
    }

    [Fact]
    public async Task Cancelled_waiter_does_not_cancel_the_owner_or_remove_its_stage()
    {
        using var fixture = new Fixture();
        using var stream = new HeldStream(OnnxBytes);
        var handler = new Handler((request, _) => IsJson(request) ? Response(JsonBytes) : Response(stream));
        using var http = new HttpClient(handler);
        var owner = Invoke(fixture, http);
        await stream.ReadStarted.Task.WaitAsync(CompletionTimeout);
        using var cancellation = new CancellationTokenSource();
        var waiter = Invoke(fixture, http, token: cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter.WaitAsync(CompletionTimeout));

        Assert.False(owner.IsCompleted);
        Assert.Single(Directory.GetDirectories(fixture.Parent));
        Assert.Equal(1, handler.Requests);
        stream.Release();
        await owner.WaitAsync(CompletionTimeout);
        fixture.AssertNewPair();
    }

    [Theory]
    [InlineData("onnx_zero")]
    [InlineData("onnx_negative")]
    [InlineData("onnx_over_limit")]
    [InlineData("json_zero")]
    [InlineData("json_negative")]
    [InlineData("json_over_limit")]
    [InlineData("onnx_hash_short")]
    [InlineData("onnx_hash_nonhex")]
    [InlineData("json_hash_short")]
    [InlineData("json_hash_nonhex")]
    [InlineData("onnx_http")]
    [InlineData("json_http")]
    [InlineData("onnx_relative")]
    [InlineData("json_relative")]
    [InlineData("onnx_credentials")]
    [InlineData("json_credentials")]
    [InlineData("onnx_fragment")]
    [InlineData("json_fragment")]
    [InlineData("timeout_zero")]
    [InlineData("timeout_negative")]
    [InlineData("timeout_over_limit")]
    public async Task Invalid_descriptor_fails_before_HTTP_and_preserves_incomplete_canonical(string invalid)
    {
        using var fixture = new Fixture();
        fixture.SeedIncomplete("onnx");
        var handler = new Handler((_, _) => UnexpectedHttp());
        using var http = new HttpClient(handler);
        var download = Invoke(fixture, http,
            onnxUrl: InvalidUrl(invalid, "onnx", OnnxUrl), jsonUrl: InvalidUrl(invalid, "json", JsonUrl),
            onnxBytes: InvalidSize(invalid, "onnx", OnnxBytes.Length, 64L * 1024 * 1024),
            jsonBytes: InvalidSize(invalid, "json", JsonBytes.Length, 64L * 1024),
            onnxSha256: InvalidHash(invalid, "onnx", OnnxBytes), jsonSha256: InvalidHash(invalid, "json", JsonBytes),
            timeout: invalid switch
            {
                "timeout_zero" => TimeSpan.Zero,
                "timeout_negative" => TimeSpan.FromTicks(-1),
                "timeout_over_limit" => TimeSpan.FromMinutes(30) + TimeSpan.FromTicks(1),
                _ => TimeSpan.FromMinutes(30),
            });

        await AssertFailed(download);

        Assert.Equal(0, handler.Requests);
        fixture.AssertPreviousIncomplete("onnx");
        Assert.Equal([Path.GetFileName(fixture.Onnx)], fixture.Entries());
    }

    [Theory]
    [InlineData("none")]
    [InlineData("onnx")]
    [InlineData("json")]
    public void Publication_backs_up_incomplete_files_then_publishes_JSON_before_ONNX(string previous)
    {
        using var fixture = new Fixture();
        fixture.SeedIncomplete(previous);
        var stage = fixture.CreateStage();
        var published = new List<string>();

        Publish(stage, fixture.Onnx, moveFile: (source, destination) =>
        {
            File.Move(source, destination);
            if (destination == fixture.Onnx || destination == fixture.Json) published.Add(destination);
            if (destination != fixture.Onnx)
                Assert.False(File.Exists(fixture.Onnx) && File.Exists(fixture.Json));
        });

        Assert.Equal([fixture.Json, fixture.Onnx], published);
        fixture.AssertNewPair();
    }

    public static IEnumerable<object[]> PublicationFailures()
    {
        foreach (var previous in new[] { "onnx", "json" })
        foreach (var phase in new[] { 1, 2, 3 })
        foreach (var afterMove in new[] { false, true })
            yield return [previous, phase, afterMove];
    }

    [Theory]
    [MemberData(nameof(PublicationFailures))]
    public void Failure_before_or_after_each_actual_move_restores_byte_identical_incomplete_canonical(
        string previous, int phase, bool afterMove)
    {
        using var fixture = new Fixture();
        fixture.SeedIncomplete(previous);
        var stage = fixture.CreateStage();
        var moves = 0;

        Assert.Throws<IOException>(() => Publish(stage, fixture.Onnx, moveFile: (source, destination) =>
        {
            moves++;
            if (moves == phase && !afterMove) throw new IOException("Fixture failure before move.");
            File.Move(source, destination);
            if (moves == phase) throw new IOException("Fixture failure after actual move.");
        }));

        fixture.AssertPreviousIncomplete(previous);
        Assert.False(File.Exists(Path.Combine(stage, "previous-model")));
        Assert.False(File.Exists(Path.Combine(stage, "previous-config")));
    }

    [Theory]
    [InlineData("onnx", 1)]
    [InlineData("onnx", 2)]
    [InlineData("json", 1)]
    [InlineData("json", 2)]
    public void Cancellation_after_backup_or_config_move_restores_incomplete_old_bytes(string previous, int phase)
    {
        using var fixture = new Fixture();
        fixture.SeedIncomplete(previous);
        var stage = fixture.CreateStage();
        using var cancellation = new CancellationTokenSource();
        var moves = 0;

        Assert.ThrowsAny<OperationCanceledException>(() => Publish(stage, fixture.Onnx, cancellation.Token,
            (source, destination) =>
            {
                File.Move(source, destination);
                if (++moves == phase) cancellation.Cancel();
            }));

        fixture.AssertPreviousIncomplete(previous);
        Assert.False(File.Exists(Path.Combine(stage, "previous-model")));
        Assert.False(File.Exists(Path.Combine(stage, "previous-config")));
    }

    [Theory]
    [InlineData("onnx")]
    [InlineData("json")]
    public void Repeated_forward_and_restore_move_failures_retain_the_old_backup(string previous)
    {
        using var fixture = new Fixture();
        fixture.SeedIncomplete(previous);
        var stage = fixture.CreateStage();
        var moves = 0;

        Assert.Throws<IOException>(() => Publish(stage, fixture.Onnx, moveFile: (source, destination) =>
        {
            if (++moves > 1) throw new IOException("Fixture forward and restoration moves both fail.");
            File.Move(source, destination);
        }));

        Assert.True(moves >= 3); // Forward config move and subsequent backup restoration both attempted.
        Assert.False(File.Exists(fixture.Onnx));
        Assert.False(File.Exists(fixture.Json));
        Assert.Equal(PreviousBytes, File.ReadAllBytes(Path.Combine(stage,
            previous == "onnx" ? "previous-model" : "previous-config")));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void Length_mismatched_unexpected_model_is_not_read_or_deleted_after_owned_source_disappears(int difference)
    {
        using var fixture = new Fixture();
        fixture.SeedIncomplete("onnx");
        var stage = fixture.CreateStage();
        var sentinel = new byte[OnnxBytes.Length + difference];
        Array.Fill(sentinel, (byte)0xA7);
        FileStream sentinelLock = null;

        try
        {
            Assert.Throws<IOException>(() => Publish(stage, fixture.Onnx, moveFile: (source, destination) =>
            {
                File.Move(source, destination);
                if (destination != fixture.Onnx) return;
                File.WriteAllBytes(fixture.Onnx, sentinel);
                // Metadata remains readable; opening this unrelated canonical for hashing must be skipped.
                sentinelLock = new FileStream(fixture.Onnx, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                throw new IOException("An external model replaced the just-published model.");
            }));
        }
        finally { sentinelLock?.Dispose(); }

        Assert.Equal(sentinel, File.ReadAllBytes(fixture.Onnx));
        Assert.False(File.Exists(fixture.Json));
        Assert.False(File.Exists(Path.Combine(stage, "voice-model.partial")));
        Assert.Equal(JsonBytes, File.ReadAllBytes(Path.Combine(stage, "voice-config.partial")));
        Assert.Equal(PreviousBytes, File.ReadAllBytes(Path.Combine(stage, "previous-model")));
    }

    [Theory]
    [InlineData("onnx")]
    [InlineData("json")]
    public void Unexpected_canonical_is_preserved_and_prior_bytes_remain_recoverable_in_backup(string previous)
    {
        using var fixture = new Fixture();
        fixture.SeedIncomplete(previous);
        var stage = fixture.CreateStage();
        var sentinel = "newer external owner has this canonical file"u8.ToArray();
        var canonical = fixture.PathFor(previous);
        var moves = 0;

        Assert.Throws<IOException>(() => Publish(stage, fixture.Onnx, moveFile: (source, destination) =>
        {
            moves++;
            if (moves == 2)
            {
                File.WriteAllBytes(canonical, sentinel);
                throw new IOException("An unexpected canonical appeared.");
            }
            File.Move(source, destination);
        }));

        Assert.Equal(sentinel, File.ReadAllBytes(canonical));
        Assert.Equal(PreviousBytes, File.ReadAllBytes(Path.Combine(stage,
            previous == "onnx" ? "previous-model" : "previous-config")));
        Assert.False(File.Exists(fixture.PathFor(previous == "onnx" ? "json" : "onnx")));
    }

    [Fact]
    public void Existing_complete_pair_skips_every_publication_move_and_leaves_stage_untouched()
    {
        using var fixture = new Fixture();
        File.WriteAllBytes(fixture.Onnx, PreviousBytes);
        File.WriteAllBytes(fixture.Json, PreviousBytes);
        var stage = fixture.CreateStage();

        Publish(stage, fixture.Onnx, moveFile: (_, _) => throw new InvalidOperationException("A ready pair must not move."));

        Assert.Equal(PreviousBytes, File.ReadAllBytes(fixture.Onnx));
        Assert.Equal(PreviousBytes, File.ReadAllBytes(fixture.Json));
        Assert.Equal(OnnxBytes, File.ReadAllBytes(Path.Combine(stage, "voice-model.partial")));
        Assert.Equal(JsonBytes, File.ReadAllBytes(Path.Combine(stage, "voice-config.partial")));
    }

    [Theory]
    [InlineData("wrong_name")]
    [InlineData("nested_parent")]
    [InlineData("missing_model")]
    [InlineData("missing_json")]
    [InlineData("zero_model")]
    [InlineData("zero_json")]
    [InlineData("oversize_model")]
    [InlineData("oversize_json")]
    public void Invalid_publication_stage_cannot_move_or_change_incomplete_canonical(string invalid)
    {
        using var fixture = new Fixture();
        fixture.SeedIncomplete("onnx");
        var stage = fixture.CreateStage(invalid == "wrong_name" ? "unowned-stage" : null,
            nested: invalid == "nested_parent");
        if (invalid == "missing_model") File.Delete(Path.Combine(stage, "voice-model.partial"));
        if (invalid == "missing_json") File.Delete(Path.Combine(stage, "voice-config.partial"));
        if (invalid is "zero_model" or "zero_json" or "oversize_model" or "oversize_json")
        {
            var model = invalid.EndsWith("model", StringComparison.Ordinal);
            using var source = new FileStream(Path.Combine(stage, model ? "voice-model.partial" : "voice-config.partial"),
                FileMode.Open, FileAccess.Write, FileShare.None);
            source.SetLength(invalid.StartsWith("zero", StringComparison.Ordinal)
                ? 0 : (model ? 64L * 1024 * 1024 : 64L * 1024) + 1);
        }
        var moves = 0;
        _ = PublicationMethod(); // Missing production code must fail before the expected failure assertion.

        Assert.ThrowsAny<Exception>(() => Publish(stage, fixture.Onnx,
            moveFile: (_, _) => { moves++; throw new InvalidOperationException("An invalid stage must not move."); }));

        Assert.Equal(0, moves);
        fixture.AssertPreviousIncomplete("onnx");
    }

    private static Task Invoke(Fixture fixture, HttpClient http, string onnxUrl = OnnxUrl, string jsonUrl = JsonUrl,
        long? onnxBytes = null, string onnxSha256 = null, long? jsonBytes = null, string jsonSha256 = null,
        CancellationToken token = default, TimeSpan? timeout = null)
    {
        var method = typeof(PiperInstaller).GetMethod("DownloadVoiceAsync", BindingFlags.NonPublic | BindingFlags.Static,
            binder: null, [typeof(string), typeof(HttpClient), typeof(string), typeof(string), typeof(long), typeof(string),
                typeof(long), typeof(string), typeof(CancellationToken), typeof(TimeSpan)], modifiers: null);
        Assert.NotNull(method); // A missing injectable core is a red gate, without touching the user profile.
        try
        {
            return (Task)method.Invoke(null, [fixture.Onnx, http, onnxUrl, jsonUrl, onnxBytes ?? OnnxBytes.Length,
                onnxSha256 ?? Digest(OnnxBytes), jsonBytes ?? JsonBytes.Length, jsonSha256 ?? Digest(JsonBytes),
                token, timeout ?? TimeSpan.FromMinutes(30)]);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            return Task.FromException(ex.InnerException);
        }
    }

    private static void Publish(string stage, string onnxPath, CancellationToken token = default,
        Action<string, string> moveFile = null)
    {
        var method = PublicationMethod();
        try { method.Invoke(null, [stage, onnxPath, token, moveFile]); }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
        }
    }

    private static MethodInfo PublicationMethod()
    {
        var method = typeof(PiperInstaller).GetMethod("PublishVoicePair", BindingFlags.NonPublic | BindingFlags.Static,
            binder: null, [typeof(string), typeof(string), typeof(CancellationToken), typeof(Action<string, string>)], modifiers: null);
        Assert.NotNull(method);
        return method;
    }

    private static async Task AssertFailed(Task operation)
    {
        var exception = await Record.ExceptionAsync(() => operation.WaitAsync(CompletionTimeout));
        Assert.NotNull(exception);
        Assert.IsNotType<TimeoutException>(exception);
    }

    private static string InvalidUrl(string invalid, string file, string valid) => invalid switch
    {
        var value when value == file + "_http" => valid.Replace("https://", "http://", StringComparison.Ordinal),
        var value when value == file + "_relative" => "voice.onnx",
        var value when value == file + "_credentials" => "https://user:password@voice-fixture.invalid/voice.onnx",
        var value when value == file + "_fragment" => valid + "#fragment",
        _ => valid,
    };
    private static long InvalidSize(string invalid, string file, long valid, long maximum) => invalid switch
    {
        var value when value == file + "_zero" => 0,
        var value when value == file + "_negative" => -1,
        var value when value == file + "_over_limit" => maximum + 1,
        _ => valid,
    };
    private static string InvalidHash(string invalid, string file, byte[] valid) => invalid switch
    {
        var value when value == file + "_hash_short" => "0123",
        var value when value == file + "_hash_nonhex" => new string('z', 64),
        _ => Digest(valid),
    };
    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static bool IsJson(HttpRequestMessage request) => request.RequestUri.AbsoluteUri == JsonUrl;
    private static Handler ValidHandler() => new((request, _) => Response(IsJson(request) ? JsonBytes : OnnxBytes));
    private static HttpResponseMessage UnexpectedHttp() => throw new InvalidOperationException("This operation must not dispatch HTTP.");
    private static HttpResponseMessage Response(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    private static HttpResponseMessage Response(Stream stream) => new(HttpStatusCode.OK) { Content = new StreamContent(stream) };
    private static HttpResponseMessage FailedResponse(string failure, byte[] bytes, bool json) => failure switch
    {
        "hash" => Response(new byte[bytes.Length]),
        "truncated" => Response(bytes[..^1]),
        "oversize" => Response(bytes.Concat(new byte[1]).ToArray()),
        "declared_short" => DeclaredResponse(bytes, bytes.Length - 1),
        "declared_long" => DeclaredResponse(bytes, bytes.Length + 1),
        "declared_public_oversize" => DeclaredResponse(bytes, (json ? 64L * 1024 : 64L * 1024 * 1024) + 1),
        "unknown_length_excess" => Response(new UnknownLengthStream(bytes.Concat(new byte[1]).ToArray())),
        "http" => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
        _ => Response(new ThrowingStream()),
    };
    private static HttpResponseMessage DeclaredResponse(byte[] bytes, long length)
    {
        var response = Response(bytes);
        response.Content.Headers.ContentLength = length;
        return response;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _tempParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        private readonly string _name = $"kwt-piper-voice-boundary-{Guid.NewGuid():N}";
        internal string Root { get; }
        internal string Parent { get; }
        internal string Onnx => Path.Combine(Parent, "fixture-amy-medium.onnx");
        internal string Json => Onnx + ".json";
        internal Fixture()
        {
            Root = Path.GetFullPath(Path.Combine(_tempParent, _name));
            Parent = Path.Combine(Root, "voices");
            Directory.CreateDirectory(Parent);
        }
        internal string PathFor(string file) => file == "json" ? Json : Onnx;
        internal void SeedIncomplete(string file)
        {
            if (file != "none") File.WriteAllBytes(PathFor(file), PreviousBytes);
        }
        internal void AssertPreviousIncomplete(string file)
        {
            Assert.Equal(PreviousBytes, File.ReadAllBytes(PathFor(file)));
            Assert.False(File.Exists(PathFor(file == "onnx" ? "json" : "onnx")));
        }
        internal void AssertNewPair()
        {
            Assert.Equal(OnnxBytes, File.ReadAllBytes(Onnx));
            Assert.Equal(JsonBytes, File.ReadAllBytes(Json));
        }
        internal string[] Entries() => Directory.GetFileSystemEntries(Parent).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        internal string CreateStage(string name = null, bool nested = false)
        {
            var stage = Path.Combine(nested ? Path.Combine(Parent, "nested") : Parent, name ?? StagePrefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            File.WriteAllBytes(Path.Combine(stage, "voice-model.partial"), OnnxBytes);
            File.WriteAllBytes(Path.Combine(stage, "voice-config.partial"), JsonBytes);
            return stage;
        }
        public void Dispose()
        {
            Assert.Equal(_tempParent, Path.GetDirectoryName(Root), ignoreCase: true);
            Assert.Equal(_name, Path.GetFileName(Root));
            Assert.True(_name.StartsWith("kwt-piper-voice-boundary-", StringComparison.Ordinal)
                && Guid.TryParseExact(_name["kwt-piper-voice-boundary-".Length..], "N", out _));
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;
        private readonly object _sync = new();
        private int _requests;
        internal int Requests => Volatile.Read(ref _requests);
        internal List<Uri> Uris { get; } = [];
        internal Handler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond)
            : this(respondAsync: (request, token) => Task.FromResult(respond(request, token))) { }
        internal Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respondAsync) => _respond = respondAsync;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref _requests);
            Assert.Equal(Uri.UriSchemeHttps, request.RequestUri.Scheme);
            lock (_sync) Uris.Add(request.RequestUri);
            return _respond(request, token);
        }
    }

    private sealed class UnknownLengthStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public override bool CanSeek => false;
    }

    private sealed class HeldStream(byte[] bytes) : Stream
    {
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _position;
        internal TaskCompletionSource<bool> ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken LastReadToken { get; private set; }
        internal void Release() => _release.TrySetResult(true);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            LastReadToken = token;
            if (_position > 0)
            {
                ReadStarted.TrySetResult(true);
                await _release.Task.WaitAsync(token);
            }
            var count = Math.Min(buffer.Length, Math.Min(_position == 0 ? 3 : bytes.Length, bytes.Length - _position));
            bytes.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }
        protected override void Dispose(bool disposing) { _release.TrySetCanceled(); base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ThrowingStream : Stream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) =>
            ValueTask.FromException<int>(new IOException("Fixture response body interrupted."));
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("Fixture response body interrupted.");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
