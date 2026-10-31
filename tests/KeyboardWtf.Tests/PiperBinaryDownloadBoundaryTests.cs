using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using KeyboardWtf.Services;
using Xunit;

namespace KeyboardWtf.Tests;

public sealed class PiperBinaryDownloadBoundaryTests
{
    private const string FixtureUrl = "https://runtime-fixture.invalid/piper_windows_amd64.zip";
    private const string StagePrefix = ".piper-binary-download-";
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(5);
    private static readonly (string Path, byte[] Data)[] RuntimeEntries =
    [
        ("piper.exe", "synthetic executable fixture only"u8.ToArray()),
        ("piper_phonemize.dll", "phonemize fixture"u8.ToArray()),
        ("espeak-ng.dll", "espeak fixture"u8.ToArray()),
        ("onnxruntime.dll", "runtime fixture"u8.ToArray()),
        ("onnxruntime_providers_shared.dll", "providers fixture"u8.ToArray()),
        ("libtashkeel_model.ort", "tashkeel fixture"u8.ToArray()),
        ("espeak-ng-data/phondata", "phondata fixture"u8.ToArray()),
        ("espeak-ng-data/phonindex", "phonindex fixture"u8.ToArray()),
        ("espeak-ng-data/phontab", "phontab fixture"u8.ToArray()),
        ("espeak-ng-data/intonations", "intonations fixture"u8.ToArray()),
        ("espeak-ng-data/en_dict", "English dictionary fixture"u8.ToArray()),
        ("espeak-ng-data/de_dict", "German dictionary fixture"u8.ToArray()),
        ("espeak-ng-data/lang/gmw/en", "English language fixture"u8.ToArray()),
        ("espeak-ng-data/lang/gmw/en-US", "US English language fixture"u8.ToArray()),
        ("espeak-ng-data/lang/gmw/de", "German language fixture"u8.ToArray()),
    ];
    private static readonly byte[] ValidArchive = Archive(RuntimeEntries.Select(entry => ("piper/" + entry.Path, entry.Data)));

    [Fact]
    public void New_runtime_download_pins_the_existing_verified_official_Windows_archive()
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Static;
        Assert.Equal("https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_windows_amd64.zip",
            typeof(PiperInstaller).GetField("PiperZipUrl", flags)?.GetRawConstantValue());
        Assert.Equal(22_477_236L, typeof(PiperInstaller).GetField("PiperArchiveBytes", flags)?.GetRawConstantValue());
        Assert.Equal("f3c58906402b24f3a96d92145f58acba6d86c9b5db896d207f78dc80811efcea",
            typeof(PiperInstaller).GetField("PiperArchiveSha256", flags)?.GetRawConstantValue());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Complete_wrapped_or_flat_runtime_is_published_as_one_directory(bool flat, bool previous)
    {
        await using var fixture = new Fixture();
        if (previous) fixture.SeedIncomplete();
        var archive = flat ? Archive(RuntimeEntries) : ValidArchive;
        var handler = new Handler((_, _) => Response(archive));
        using var http = new HttpClient(handler);

        await Invoke(fixture, http, archive.Length, sha256: Digest(archive)).WaitAsync(CompletionTimeout);

        fixture.AssertRuntime(fixture.Canonical);
        Assert.False(File.Exists(Path.Combine(fixture.Canonical, "previous.txt")));
        Assert.Equal(["piper"], fixture.Entries());
        Assert.Equal(FixtureUrl, Assert.Single(handler.Uris).AbsoluteUri);
    }

    [Fact]
    public async Task Existing_piper_executable_alone_preserves_legacy_readiness_and_skips_HTTP()
    {
        await using var fixture = new Fixture();
        fixture.SeedIncomplete();
        var legacy = "legacy executable bytes without supporting DLLs"u8.ToArray();
        File.WriteAllBytes(Path.Combine(fixture.Canonical, "piper.exe"), legacy);
        var before = fixture.Snapshot();
        var handler = new Handler((_, _) => UnexpectedHttp());
        using var http = new HttpClient(handler);

        await Invoke(fixture, http).WaitAsync(CompletionTimeout);

        Assert.Equal(0, handler.Requests);
        Assert.Equal(before, fixture.Snapshot());
        Assert.Equal(legacy, File.ReadAllBytes(Path.Combine(fixture.Canonical, "piper.exe")));
        fixture.AssertIncomplete();
    }

    [Fact]
    public async Task Partial_archive_is_in_an_owned_sibling_stage_and_unowned_stage_is_untouched()
    {
        await using var fixture = new Fixture();
        fixture.SeedIncomplete();
        var unowned = Path.Combine(fixture.TtsRoot, StagePrefix + "another-owner");
        Directory.CreateDirectory(unowned);
        var sentinel = Path.Combine(unowned, "sentinel.txt");
        File.WriteAllText(sentinel, "unrelated staging remains");
        using var stream = new HeldStream(ValidArchive);
        using var http = new HttpClient(new Handler((_, _) => Response(stream)));
        var operation = Invoke(fixture, http);
        await WaitForBodyAsync(stream, operation);

        fixture.AssertIncomplete();
        Assert.False(File.Exists(Path.Combine(fixture.Canonical, "piper.exe")));
        var stage = Assert.Single(Directory.GetDirectories(fixture.TtsRoot),
            path => path != fixture.Canonical && path != unowned);
        Assert.Equal(fixture.TtsRoot, Path.GetDirectoryName(stage), ignoreCase: true);
        var name = Path.GetFileName(stage);
        Assert.StartsWith(StagePrefix, name);
        Assert.True(Guid.TryParseExact(name[StagePrefix.Length..], "N", out _));
        Assert.True(File.Exists(Path.Combine(stage, "runtime.zip")));
        Assert.Equal("unrelated staging remains", File.ReadAllText(sentinel));
        stream.Release();
        await operation.WaitAsync(CompletionTimeout);

        fixture.AssertRuntime(fixture.Canonical);
        Assert.Equal(new[] { unowned, fixture.Canonical }.Order(StringComparer.Ordinal),
            Directory.GetDirectories(fixture.TtsRoot).Order(StringComparer.Ordinal));
        Assert.Equal("unrelated staging remains", File.ReadAllText(sentinel));
    }

    [Fact]
    public async Task Concurrent_calls_wait_and_recheck_readiness_with_only_one_HTTP_request()
    {
        await using var fixture = new Fixture();
        using var stream = new HeldStream(ValidArchive);
        var handler = new Handler((_, _) => Response(stream));
        using var http = new HttpClient(handler);
        var owner = Invoke(fixture, http);
        await WaitForBodyAsync(stream, owner);
        var waiter = Invoke(fixture, http);
        Assert.False(waiter.IsCompleted);
        Assert.Equal(1, handler.Requests);
        stream.Release();

        await Task.WhenAll(owner, waiter).WaitAsync(CompletionTimeout);

        Assert.Equal(1, handler.Requests);
        fixture.AssertRuntime(fixture.Canonical);
        Assert.Equal(["piper"], fixture.Entries());
    }

    [Fact]
    public async Task Async_fixture_cleanup_cancels_and_joins_original_HTTP_work_before_deleting_its_GUID_tree()
    {
        await using var fixture = new Fixture();
        // Assert before creating a worker: the pre-correction fixture fails RED
        // without leaving a held download or a file-operation tail behind.
        var asyncFixture = Assert.IsAssignableFrom<IAsyncDisposable>(fixture);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler(async (_, token) =>
        {
            using var registration = token.Register(() => cancellationObserved.TrySetResult(true));
            entered.TrySetResult(true);
            await release.Task; // A cancelled waiter/token is not this worker's completion.
            return Response(ValidArchive);
        }));
        var operation = Invoke(fixture, http);
        Task cleanup = null;
        try
        {
            await entered.Task.WaitAsync(CompletionTimeout);
            Assert.False(operation.IsCompleted);
            Assert.True(Directory.Exists(fixture.Root));

            cleanup = asyncFixture.DisposeAsync().AsTask();
            await cancellationObserved.Task.WaitAsync(CompletionTimeout);
            Assert.False(cleanup.IsCompleted);
            Assert.False(operation.IsCompleted);
            Assert.True(Directory.Exists(fixture.Root));
            Assert.True(Directory.Exists(fixture.TtsRoot));

            release.TrySetResult(true);
            await cleanup.WaitAsync(CompletionTimeout);
            Assert.True(operation.IsCompleted);
            Assert.False(Directory.Exists(fixture.Root));
        }
        finally
        {
            release.TrySetResult(true);
            _ = await Record.ExceptionAsync(() => operation.WaitAsync(CompletionTimeout));
            if (cleanup is not null)
                await cleanup.WaitAsync(CompletionTimeout);
            else
                await asyncFixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task Cancelled_caller_waiter_cannot_authorize_deleting_a_still_held_original_body_stage()
    {
        await using var fixture = new Fixture();
        var asyncFixture = Assert.IsAssignableFrom<IAsyncDisposable>(fixture);
        using var stream = new HeldStream(ValidArchive, ignoreCancellationUntilRelease: true);
        using var http = new HttpClient(new Handler((_, _) => Response(stream)));
        using var callerCancellation = new CancellationTokenSource();
        var operation = Invoke(fixture, http);
        Task cleanup = null;
        try
        {
            await WaitForBodyAsync(stream, operation);
            var stage = Assert.Single(Directory.GetDirectories(fixture.TtsRoot));
            Assert.True(File.Exists(Path.Combine(stage, "runtime.zip")));
            var callerWait = operation.WaitAsync(callerCancellation.Token);
            callerCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => callerWait);
            Assert.False(operation.IsCompleted);

            cleanup = asyncFixture.DisposeAsync().AsTask();
            await stream.CancellationObserved.Task.WaitAsync(CompletionTimeout);
            Assert.False(cleanup.IsCompleted);
            Assert.False(operation.IsCompleted);
            Assert.True(Directory.Exists(fixture.Root));
            Assert.True(File.Exists(Path.Combine(stage, "runtime.zip")));

            stream.Release();
            await cleanup.WaitAsync(CompletionTimeout);
            Assert.True(operation.IsCompleted);
            Assert.False(Directory.Exists(fixture.Root));
        }
        finally
        {
            stream.Release();
            _ = await Record.ExceptionAsync(() => operation.WaitAsync(CompletionTimeout));
            if (cleanup is not null)
                await cleanup.WaitAsync(CompletionTimeout);
            else
                await asyncFixture.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("corrupt_zip")]
    [InlineData("truncated_body")]
    [InlineData("http_failure")]
    [InlineData("stream_failure")]
    [InlineData("declared_short")]
    [InlineData("declared_long")]
    [InlineData("declared_over_limit")]
    [InlineData("unknown_length_excess")]
    [InlineData("expanded_over_limit")]
    [InlineData("entry_length_mismatch")]
    [InlineData("too_many_entries")]
    public async Task Failed_transfer_or_archive_validation_preserves_incomplete_canonical_and_cleans_owned_stage(string failure)
    {
        await using var fixture = new Fixture();
        fixture.SeedIncomplete();
        var received = failure switch
        {
            "corrupt_zip" => new byte[ValidArchive.Length],
            "expanded_over_limit" => WithAdvertisedLength(ValidArchive, "piper/piper.exe", 128U * 1024 * 1024 + 1),
            // .NET limits an underreported entry stream to its declared bytes;
            // an overreported entry proves our actual/advertised truncation guard.
            "entry_length_mismatch" => WithAdvertisedLength(ValidArchive, "piper/piper.exe", (uint)RuntimeEntries[0].Data.Length + 1),
            "too_many_entries" => Archive(RuntimeEntries.Select(entry => ("piper/" + entry.Path, entry.Data))
                .Concat(Enumerable.Range(0, 2_001).Select(index => ($"piper/extra-{index}.txt", Array.Empty<byte>())))),
            _ => ValidArchive,
        };
        var handler = new Handler((_, _) => failure switch
        {
            "truncated_body" => Response(ValidArchive[..^1]),
            "http_failure" => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            "stream_failure" => Response(new ThrowingStream()),
            "declared_short" => DeclaredResponse(ValidArchive, ValidArchive.Length - 1),
            "declared_long" => DeclaredResponse(ValidArchive, ValidArchive.Length + 1),
            "declared_over_limit" => DeclaredResponse(ValidArchive, 64L * 1024 * 1024 + 1),
            "unknown_length_excess" => Response(new UnknownLengthStream(ValidArchive.Concat(new byte[1]).ToArray())),
            _ => Response(received),
        });
        using var http = new HttpClient(handler);
        var operation = Invoke(fixture, http, received.Length, sha256: Digest(received));

        await AssertFailed(operation);

        fixture.AssertIncomplete();
        Assert.False(File.Exists(Path.Combine(fixture.Canonical, "piper.exe")));
        Assert.Equal(["piper"], fixture.Entries());
    }

    public static IEnumerable<object[]> IncompleteRuntimes()
    {
        foreach (var entry in RuntimeEntries)
        foreach (var empty in new[] { false, true })
            yield return [entry.Path, empty];
    }

    [Theory]
    [MemberData(nameof(IncompleteRuntimes))]
    public async Task Every_required_new_runtime_asset_must_exist_and_be_nonempty_before_publication(string path, bool empty)
    {
        await using var fixture = new Fixture();
        fixture.SeedIncomplete();
        var archive = Archive(RuntimeEntries.Where(entry => empty || entry.Path != path)
            .Select(entry => ("piper/" + entry.Path, entry.Path == path ? Array.Empty<byte>() : entry.Data)));
        using var http = new HttpClient(new Handler((_, _) => Response(archive)));
        var operation = Invoke(fixture, http, archive.Length, sha256: Digest(archive));

        await AssertFailed(operation);

        fixture.AssertIncomplete();
        Assert.False(File.Exists(Path.Combine(fixture.Canonical, "piper.exe")));
        Assert.Equal(["piper"], fixture.Entries());
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("C:/drive.txt")]
    [InlineData("piper/../escape.txt")]
    [InlineData("piper\\..\\escape.txt")]
    [InlineData("piper/file:stream")]
    [InlineData("piper/PIPER.EXE")]
    [InlineData("piper/CON.txt")]
    [InlineData("piper/COM1.txt")]
    [InlineData("piper/space /file.txt")]
    [InlineData("piper/dot./file.txt")]
    [InlineData("piper//file.txt")]
    [InlineData("piper/./file.txt")]
    [InlineData("unix_symlink")]
    [InlineData("unix_fifo")]
    [InlineData("windows_reparse")]
    public async Task Unsafe_archive_paths_or_entry_types_never_publish_or_write_outside_owned_stage(string unsafeEntry)
    {
        await using var fixture = new Fixture();
        fixture.SeedIncomplete();
        var special = unsafeEntry is "unix_symlink" or "unix_fifo" or "windows_reparse";
        var path = unsafeEntry switch
        {
            "/absolute.txt" => "/" + Path.GetRelativePath(Path.GetPathRoot(fixture.Root), fixture.Root)
                .Replace('\\', '/') + "/owned-rooted-target.txt",
            "C:/drive.txt" => fixture.Root.Replace('\\', '/') + "/owned-absolute-target.txt",
            _ => special ? "piper/unsafe-entry" : unsafeEntry,
        };
        var archive = Archive(RuntimeEntries.Select(entry => ("piper/" + entry.Path, entry.Data))
            .Append((path, "untrusted entry fixture"u8.ToArray())), entry =>
            {
                if (entry.FullName != path || !special) return;
                entry.ExternalAttributes = unsafeEntry switch
                {
                    "unix_symlink" => unchecked((int)(0xA1FFu << 16)),
                    "unix_fifo" => unchecked((int)(0x11A4u << 16)),
                    _ => (int)FileAttributes.ReparsePoint,
                };
            });
        using var http = new HttpClient(new Handler((_, _) => Response(archive)));
        var operation = Invoke(fixture, http, archive.Length, sha256: Digest(archive));

        await AssertFailed(operation);

        fixture.AssertIncomplete();
        Assert.Equal(["tts"], Directory.GetFileSystemEntries(fixture.Root).Select(Path.GetFileName));
        Assert.Equal(["piper"], fixture.Entries());
    }

    [Fact]
    public async Task Ordinary_directory_entries_and_Unix_regular_file_attributes_keep_the_runtime_layout()
    {
        await using var fixture = new Fixture();
        var entries = new[] { "piper/", "piper/espeak-ng-data/", "piper/espeak-ng-data/lang/", "piper/espeak-ng-data/lang/gmw/" }
            .Select(path => (path, Array.Empty<byte>()))
            .Concat(RuntimeEntries.Select(entry => ("piper/" + entry.Path, entry.Data)));
        var archive = Archive(entries, entry => entry.ExternalAttributes =
            unchecked((int)((entry.FullName.EndsWith('/') ? 0x41EDu : 0x81A4u) << 16)));
        using var http = new HttpClient(new Handler((_, _) => Response(archive)));

        await Invoke(fixture, http, archive.Length, sha256: Digest(archive)).WaitAsync(CompletionTimeout);

        fixture.AssertRuntime(fixture.Canonical);
        Assert.Equal(["piper"], fixture.Entries());
    }

    [Theory]
    [InlineData("zero_size")]
    [InlineData("negative_size")]
    [InlineData("size_over_limit")]
    [InlineData("zero_timeout")]
    [InlineData("negative_timeout")]
    [InlineData("timeout_over_limit")]
    [InlineData("http_url")]
    [InlineData("relative_url")]
    [InlineData("url_credentials")]
    [InlineData("url_fragment")]
    [InlineData("empty_sha")]
    [InlineData("short_sha")]
    [InlineData("long_sha")]
    [InlineData("nonhex_sha")]
    public async Task Invalid_download_descriptor_fails_before_HTTP_and_preserves_existing_files(string invalid)
    {
        await using var fixture = new Fixture();
        fixture.SeedIncomplete();
        var handler = new Handler((_, _) => UnexpectedHttp());
        using var http = new HttpClient(handler);
        var operation = Invoke(fixture, http, invalid switch
        {
            "zero_size" => 0,
            "negative_size" => -1,
            "size_over_limit" => 64L * 1024 * 1024 + 1,
            _ => ValidArchive.Length,
        }, sha256: invalid switch
        {
            "empty_sha" => string.Empty,
            "short_sha" => "0123",
            "long_sha" => new string('a', 65),
            "nonhex_sha" => new string('z', 64),
            _ => Digest(ValidArchive),
        }, url: invalid switch
        {
            "http_url" => FixtureUrl.Replace("https://", "http://", StringComparison.Ordinal),
            "relative_url" => "piper.zip",
            "url_credentials" => "https://user:password@runtime-fixture.invalid/piper.zip",
            "url_fragment" => FixtureUrl + "#fragment",
            _ => FixtureUrl,
        }, timeout: invalid switch
        {
            "zero_timeout" => TimeSpan.Zero,
            "negative_timeout" => TimeSpan.FromTicks(-1),
            "timeout_over_limit" => TimeSpan.FromMinutes(30) + TimeSpan.FromTicks(1),
            _ => TimeSpan.FromMinutes(30),
        });

        await AssertFailed(operation);

        Assert.Equal(0, handler.Requests);
        fixture.AssertIncomplete();
        Assert.Equal(["piper"], fixture.Entries());
    }

    [Fact]
    public async Task Same_length_valid_ZIP_with_modified_metadata_fails_the_expected_SHA256()
    {
        await using var fixture = new Fixture();
        fixture.SeedIncomplete();
        var received = ValidArchive.ToArray();
        received[10] ^= 2; // Local-header modification time changes; paths and compressed file bytes stay intact.
        using var archive = new ZipArchive(new MemoryStream(received), ZipArchiveMode.Read);
        Assert.Equal(RuntimeEntries.Length, archive.Entries.Count);
        using var http = new HttpClient(new Handler((_, _) => Response(received)));
        var operation = Invoke(fixture, http, received.Length, sha256: Digest(ValidArchive));

        await AssertFailed(operation);

        fixture.AssertIncomplete();
        Assert.False(File.Exists(Path.Combine(fixture.Canonical, "piper.exe")));
        Assert.Equal(["piper"], fixture.Entries());
    }

    [Fact]
    public async Task Underreported_ZIP_entry_is_rejected_by_the_original_archive_pin_before_extraction()
    {
        await using var fixture = new Fixture();
        fixture.SeedIncomplete();
        var received = WithAdvertisedLength(ValidArchive, "piper/piper.exe", 1);
        Assert.Equal(ValidArchive.Length, received.Length);
        using var http = new HttpClient(new Handler((_, _) => Response(received)));

        await AssertFailed(Invoke(fixture, http, received.Length, sha256: Digest(ValidArchive)));

        fixture.AssertIncomplete();
        Assert.False(File.Exists(Path.Combine(fixture.Canonical, "piper.exe")));
        Assert.Equal(["piper"], fixture.Entries());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_or_deadline_reaches_stalled_body_and_releases_the_gate(bool deadline)
    {
        await using var fixture = new Fixture();
        fixture.SeedIncomplete();
        using var stream = new HeldStream(ValidArchive);
        using var http = new HttpClient(new Handler((_, _) => Response(stream))) { Timeout = TimeSpan.FromMinutes(30) };
        using var cancellation = new CancellationTokenSource();
        var operation = Invoke(fixture, http, token: cancellation.Token,
            timeout: deadline ? TimeSpan.FromSeconds(2) : TimeSpan.FromMinutes(30));
        await WaitForBodyAsync(stream, operation);
        if (!deadline) cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(CompletionTimeout));

        Assert.True(stream.LastReadToken.IsCancellationRequested);
        fixture.AssertIncomplete();
        Assert.Equal(["piper"], fixture.Entries());
        using var retryHttp = new HttpClient(new Handler((_, _) => Response(ValidArchive)));
        await Invoke(fixture, retryHttp).WaitAsync(CompletionTimeout);
        fixture.AssertRuntime(fixture.Canonical);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_or_deadline_reaches_stalled_headers_and_releases_the_gate(bool deadline)
    {
        await using var fixture = new Fixture();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var observedToken = CancellationToken.None;
        var handler = new Handler(respondAsync: async (_, token) =>
        {
            observedToken = token;
            started.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Response(ValidArchive);
        });
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
        using var cancellation = new CancellationTokenSource();
        var operation = Invoke(fixture, http, token: cancellation.Token,
            timeout: deadline ? TimeSpan.FromSeconds(2) : TimeSpan.FromMinutes(30));
        await started.Task.WaitAsync(CompletionTimeout);
        if (!deadline) cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(CompletionTimeout));

        Assert.True(observedToken.IsCancellationRequested);
        Assert.Empty(fixture.Entries());
        using var retryHttp = new HttpClient(new Handler((_, _) => Response(ValidArchive)));
        await Invoke(fixture, retryHttp).WaitAsync(CompletionTimeout);
        fixture.AssertRuntime(fixture.Canonical);
    }

    [Fact]
    public async Task Cancelling_a_waiter_keeps_the_download_owner_and_its_stage_intact()
    {
        await using var fixture = new Fixture();
        using var stream = new HeldStream(ValidArchive);
        var handler = new Handler((_, _) => Response(stream));
        using var http = new HttpClient(handler);
        var owner = Invoke(fixture, http);
        await WaitForBodyAsync(stream, owner);
        using var cancellation = new CancellationTokenSource();
        var waiter = Invoke(fixture, http, token: cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter.WaitAsync(CompletionTimeout));

        Assert.False(owner.IsCompleted);
        Assert.Equal(1, handler.Requests);
        Assert.Single(Directory.GetDirectories(fixture.TtsRoot));
        stream.Release();
        await owner.WaitAsync(CompletionTimeout);
        fixture.AssertRuntime(fixture.Canonical);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public void Failed_backup_or_publication_move_restores_the_previous_incomplete_directory(int phase, bool afterMove)
    {
        using var fixture = new Fixture();
        fixture.SeedIncomplete();
        var (source, backup) = fixture.CreatePublicationStage();
        var moves = 0;

        Assert.Throws<IOException>(() => Publish(source, fixture.Canonical, backup, moveDirectory: (from, to) =>
        {
            if (++moves == phase && !afterMove) throw new IOException("Fixture failure before directory move.");
            Directory.Move(from, to);
            if (moves == phase) throw new IOException("Fixture failure after backup move.");
        }));

        fixture.AssertIncomplete();
        fixture.AssertRuntime(source);
        Assert.False(Directory.Exists(backup));
    }

    [Fact]
    public void Failure_after_actual_new_publication_keeps_the_new_runtime_and_old_recoverable_backup()
    {
        using var fixture = new Fixture();
        fixture.SeedIncomplete();
        var (source, backup) = fixture.CreatePublicationStage();
        var moves = 0;

        Assert.Throws<IOException>(() => Publish(source, fixture.Canonical, backup, moveDirectory: (from, to) =>
        {
            Directory.Move(from, to);
            if (++moves == 2) throw new IOException("Fixture failure after new runtime directory move.");
        }));

        fixture.AssertRuntime(fixture.Canonical);
        fixture.AssertIncomplete(backup);
        Assert.False(Directory.Exists(source));
    }

    [Fact]
    public void Unexpected_canonical_directory_is_never_removed_and_previous_bytes_remain_in_backup()
    {
        using var fixture = new Fixture();
        fixture.SeedIncomplete();
        var (source, backup) = fixture.CreatePublicationStage();
        var sentinel = Path.Combine(fixture.Canonical, "new-owner.txt");
        var moves = 0;

        Assert.Throws<IOException>(() => Publish(source, fixture.Canonical, backup, moveDirectory: (from, to) =>
        {
            if (++moves == 2)
            {
                Directory.CreateDirectory(fixture.Canonical);
                File.WriteAllText(sentinel, "an external owner owns this directory");
                throw new IOException("Unexpected canonical appeared.");
            }
            Directory.Move(from, to);
        }));

        Assert.Equal("an external owner owns this directory", File.ReadAllText(sentinel));
        Assert.Equal(["new-owner.txt"], Directory.GetFileSystemEntries(fixture.Canonical).Select(Path.GetFileName));
        fixture.AssertIncomplete(backup);
        fixture.AssertRuntime(source);
    }

    [Fact]
    public void Cancellation_after_backup_move_restores_old_directory_before_returning()
    {
        using var fixture = new Fixture();
        fixture.SeedIncomplete();
        var (source, backup) = fixture.CreatePublicationStage();
        using var cancellation = new CancellationTokenSource();
        var moves = 0;

        Assert.ThrowsAny<OperationCanceledException>(() => Publish(source, fixture.Canonical, backup, cancellation.Token,
            (from, to) =>
            {
                Directory.Move(from, to);
                if (++moves == 1) cancellation.Cancel();
            }));

        fixture.AssertIncomplete();
        fixture.AssertRuntime(source);
        Assert.False(Directory.Exists(backup));
    }

    [Fact]
    public void Existing_canonical_executable_alone_skips_every_publication_move()
    {
        using var fixture = new Fixture();
        fixture.SeedIncomplete();
        File.WriteAllText(Path.Combine(fixture.Canonical, "piper.exe"), "legacy executable fixture");
        var (source, backup) = fixture.CreatePublicationStage();
        var before = fixture.Snapshot();

        Publish(source, fixture.Canonical, backup, moveDirectory: (_, _) =>
            throw new InvalidOperationException("Legacy readiness must skip publication."));

        Assert.Equal(before, fixture.Snapshot());
        fixture.AssertIncomplete();
        fixture.AssertRuntime(source);
        Assert.False(Directory.Exists(backup));
    }

    private static Task Invoke(Fixture fixture, HttpClient http, long? expectedArchiveBytes = null, string sha256 = null,
        string url = FixtureUrl, CancellationToken token = default, TimeSpan? timeout = null)
    {
        var method = typeof(PiperInstaller).GetMethod("DownloadPiperBinaryAsync", BindingFlags.NonPublic | BindingFlags.Static,
            binder: null, [typeof(string), typeof(HttpClient), typeof(string), typeof(long), typeof(string), typeof(CancellationToken), typeof(TimeSpan)],
            modifiers: null);
        Assert.NotNull(method); // Missing production code must fail before an expected failure assertion.
        return fixture.StartOperation(token, operationToken =>
        {
            try
            {
                // Track the ORIGINAL task, not a caller's cancellable/timed waiter.
                return (Task)method.Invoke(null, [fixture.TtsRoot, http, url, expectedArchiveBytes ?? ValidArchive.Length,
                    sha256 ?? Digest(ValidArchive), operationToken, timeout ?? TimeSpan.FromMinutes(30)]);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null) { return Task.FromException(ex.InnerException); }
        });
    }

    private static void Publish(string source, string destination, string backup, CancellationToken token = default,
        Action<string, string> moveDirectory = null)
    {
        var method = typeof(PiperInstaller).GetMethod("PublishPiperRuntime", BindingFlags.NonPublic | BindingFlags.Static,
            binder: null, [typeof(string), typeof(string), typeof(string), typeof(CancellationToken), typeof(Action<string, string>)],
            modifiers: null);
        Assert.NotNull(method);
        try { method.Invoke(null, [source, destination, backup, token, moveDirectory]); }
        catch (TargetInvocationException ex) when (ex.InnerException is not null) { ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); }
    }

    private static async Task WaitForBodyAsync(HeldStream stream, Task operation)
    {
        Task completed;
        try { completed = await Task.WhenAny(stream.ReadStarted.Task, operation).WaitAsync(CompletionTimeout); }
        catch (TimeoutException)
        {
            Assert.Fail($"Fixture body never reached its hold within {CompletionTimeout}; download status={operation.Status}.");
            return;
        }
        if (completed == operation && !stream.ReadStarted.Task.IsCompleted)
        {
            var failure = await Record.ExceptionAsync(() => operation);
            Assert.Fail($"Download ended before the fixture body reached its hold: {failure?.ToString() ?? operation.Status.ToString()}.");
        }
        await stream.ReadStarted.Task.WaitAsync(CompletionTimeout);
    }

    private static async Task AssertFailed(Task operation)
    {
        var exception = await Record.ExceptionAsync(() => operation.WaitAsync(CompletionTimeout));
        Assert.NotNull(exception);
        Assert.IsNotType<TimeoutException>(exception);
    }

    private static byte[] Archive(IEnumerable<(string Path, byte[] Data)> entries, Action<ZipArchiveEntry> configure = null)
    {
        using var bytes = new MemoryStream();
        using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var item in entries)
            {
                var entry = archive.CreateEntry(item.Path, CompressionLevel.Optimal);
                configure?.Invoke(entry);
                using var stream = entry.Open();
                stream.Write(item.Data);
            }
        }
        return bytes.ToArray();
    }

    private static byte[] WithAdvertisedLength(byte[] archive, string filename, uint length)
    {
        var result = archive.ToArray();
        for (var index = 0; index + 46 <= result.Length; index++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(index, 4)) != 0x02014B50) continue;
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(result.AsSpan(index + 28, 2));
            if (index + 46 + nameLength > result.Length) continue;
            if (Encoding.UTF8.GetString(result, index + 46, nameLength) != filename) continue;
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(index + 24, 4), length);
            return result;
        }
        throw new InvalidOperationException("Fixture ZIP central directory entry not found.");
    }

    private static HttpResponseMessage UnexpectedHttp() => throw new InvalidOperationException("This operation must not dispatch HTTP.");
    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static HttpResponseMessage Response(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    private static HttpResponseMessage Response(Stream stream) => new(HttpStatusCode.OK) { Content = new StreamContent(stream) };
    private static HttpResponseMessage DeclaredResponse(byte[] bytes, long length)
    {
        var response = Response(bytes);
        response.Content.Headers.ContentLength = length;
        return response;
    }

    private sealed class Fixture : IDisposable, IAsyncDisposable
    {
        private readonly string _tempParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        private readonly string _name = $"kwt-piper-binary-boundary-{Guid.NewGuid():N}";
        private readonly object _operationSync = new();
        private readonly CancellationTokenSource _cleanupCancellation = new();
        private readonly List<(Task OriginalTask, CancellationTokenSource Cancellation)> _operations = [];
        private Task _cleanupTask;
        private bool _cleanupStarted;
        private bool _disposed;
        internal string Root { get; }
        internal string TtsRoot { get; }
        internal string Canonical => Path.Combine(TtsRoot, "piper");
        internal Fixture()
        {
            Root = Path.GetFullPath(Path.Combine(_tempParent, _name));
            TtsRoot = Path.Combine(Root, "tts");
            Directory.CreateDirectory(TtsRoot);
        }
        internal Task StartOperation(CancellationToken callerToken, Func<CancellationToken, Task> start)
        {
            lock (_operationSync)
            {
                ObjectDisposedException.ThrowIf(_cleanupStarted, this);
                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(callerToken, _cleanupCancellation.Token);
                try
                {
                    var original = start(cancellation.Token)
                        ?? throw new InvalidOperationException("The download did not return its original task.");
                    _operations.Add((original, cancellation));
                    return original;
                }
                catch
                {
                    cancellation.Dispose();
                    throw;
                }
            }
        }
        internal void SeedIncomplete()
        {
            Directory.CreateDirectory(Path.Combine(Canonical, "espeak-ng-data"));
            File.WriteAllText(Path.Combine(Canonical, "previous.txt"), "previous runtime bytes remain");
            File.WriteAllText(Path.Combine(Canonical, "espeak-ng-data", "previous-data.txt"), "previous nested bytes remain");
        }
        internal void AssertIncomplete(string directory = null)
        {
            directory ??= Canonical;
            Assert.Equal("previous runtime bytes remain", File.ReadAllText(Path.Combine(directory, "previous.txt")));
            Assert.Equal("previous nested bytes remain", File.ReadAllText(Path.Combine(directory, "espeak-ng-data", "previous-data.txt")));
        }
        internal void AssertRuntime(string directory)
        {
            foreach (var entry in RuntimeEntries)
                Assert.Equal(entry.Data, File.ReadAllBytes(Path.Combine(directory, entry.Path.Replace('/', Path.DirectorySeparatorChar))));
        }
        internal (string Source, string Backup) CreatePublicationStage()
        {
            var stage = Path.Combine(TtsRoot, StagePrefix + Guid.NewGuid().ToString("N"));
            var source = Path.Combine(stage, "extracted", "piper");
            foreach (var entry in RuntimeEntries)
            {
                var path = Path.Combine(source, entry.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, entry.Data);
            }
            return (source, Path.Combine(stage, "previous-runtime"));
        }
        internal string[] Entries() => Directory.GetFileSystemEntries(TtsRoot).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        internal string[] Snapshot() => Directory.GetFileSystemEntries(TtsRoot, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(TtsRoot, path)).Order(StringComparer.Ordinal).ToArray();
        public ValueTask DisposeAsync()
        {
            lock (_operationSync)
            {
                if (_disposed) return ValueTask.CompletedTask;
                if (_cleanupTask is null)
                {
                    _cleanupStarted = true;
                    _cleanupTask = CleanupAsync(_operations.Select(operation => operation.OriginalTask).ToArray());
                }
                return new ValueTask(_cleanupTask);
            }
        }
        private async Task CleanupAsync(Task[] originals)
        {
            _cleanupCancellation.Cancel();
            var completion = Task.WhenAll(originals);
            try
            {
                await completion.WaitAsync(CompletionTimeout);
            }
            catch (Exception) when (completion.IsCompleted)
            {
                // Expected failure/cancellation assertions do not excuse an
                // unfinished tail. WhenAll completed only after EVERY original
                // task unwound, including extraction disposal and gate cleanup.
            }
            catch (TimeoutException)
            {
                Assert.Fail($"Fixture cleanup could not prove original worker completion within {CompletionTimeout}; "
                    + $"the owned tree is retained at {Root}. Worker statuses: {string.Join(", ", originals.Select(task => task.Status))}.");
                return;
            }
            Assert.All(originals, task => Assert.True(task.IsCompleted));
            DeleteCompletedOwnedTree();
        }
        public void Dispose()
        {
            lock (_operationSync)
            {
                if (_disposed) return;
                // Synchronous publication-only fixtures have no async work.
                // Never block/join here or delete beneath an outstanding task.
                Assert.True(_operations.All(operation => operation.OriginalTask.IsCompleted),
                    $"Synchronous fixture cleanup cannot delete a tree with outstanding original work; retained at {Root}.");
                Assert.True(_cleanupTask is null || _cleanupTask.IsCompletedSuccessfully,
                    $"Async fixture cleanup has not completed successfully; retained at {Root}.");
                _cleanupStarted = true;
                DeleteCompletedOwnedTree();
            }
        }
        private void DeleteCompletedOwnedTree()
        {
            Assert.Equal(_tempParent, Path.GetDirectoryName(Root), ignoreCase: true);
            Assert.Equal(_name, Path.GetFileName(Root));
            Assert.True(_name.StartsWith("kwt-piper-binary-boundary-", StringComparison.Ordinal)
                && Guid.TryParseExact(_name["kwt-piper-binary-boundary-".Length..], "N", out _));
            if (Directory.Exists(Root))
            {
                Assert.False((File.GetAttributes(Root) & FileAttributes.ReparsePoint) != 0);
                Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(Root, "*", SearchOption.AllDirectories),
                    path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0);
                Directory.Delete(Root, recursive: true);
            }
            foreach (var operation in _operations) operation.Cancellation.Dispose();
            _cleanupCancellation.Dispose();
            _disposed = true;
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

    private sealed class HeldStream(byte[] bytes, bool ignoreCancellationUntilRelease = false) : Stream
    {
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _position;
        internal TaskCompletionSource<bool> ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken LastReadToken { get; private set; }
        internal void Release() => _release.TrySetResult(true);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            LastReadToken = token;
            if (_position > 0)
            {
                using var registration = token.Register(() => CancellationObserved.TrySetResult(true));
                ReadStarted.TrySetResult(true);
                if (ignoreCancellationUntilRelease)
                    await _release.Task;
                else
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
