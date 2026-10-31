using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using KeyboardWtf.Services;
using Xunit;

namespace KeyboardWtf.Tests;

public sealed class VoskDownloadBoundaryTests
{
    private const string ArchiveRoot = "vosk-model-small-en-us-0.15";
    private const string FixtureUrl = "https://model-fixture.invalid/vosk-model-small-en-us-0.15.zip";
    private static readonly (string Path, byte[] Data)[] ModelEntries =
    [
        ("README", "bounded Vosk fixture"u8.ToArray()),
        ("am/model.bin", "acoustic fixture"u8.ToArray()),
        ("conf/model.conf", "configuration fixture"u8.ToArray()),
        ("graph/tree.bin", "graph fixture"u8.ToArray()),
        ("ivector/data.bin", "ivector fixture"u8.ToArray()),
    ];
    private static readonly byte[] ValidArchive = CreateArchive(ModelEntries.Select(entry => ($"{ArchiveRoot}/{entry.Path}", entry.Data)));

    [Fact]
    public async Task Complete_staged_model_is_published_with_the_existing_model_layout()
    {
        using var fixture = new Fixture();
        fixture.SeedPreviousIncompleteModel();
        var handler = new Handler((_, _) => Response(ValidArchive));
        using var http = new HttpClient(handler);

        Assert.True(await Invoke(fixture, http));
        foreach (var entry in ModelEntries)
            Assert.Equal(entry.Data, await File.ReadAllBytesAsync(fixture.ModelFile(entry.Path)));
        Assert.False(File.Exists(fixture.ModelFile("previous-owned.txt")));
        Assert.Equal(["vosk-model"], fixture.TopLevelEntries());
        Assert.Equal(FixtureUrl, Assert.Single(handler.Uris).AbsoluteUri);
    }

    [Fact]
    public async Task Existing_complete_model_skips_all_network_work_and_keeps_its_bytes()
    {
        using var fixture = new Fixture();
        fixture.SeedCompleteModel();
        var before = fixture.Snapshot();
        var handler = new Handler((_, _) => throw new InvalidOperationException("An existing complete model must not download."));
        using var http = new HttpClient(handler);

        Assert.True(await Invoke(fixture, http));
        Assert.Equal(0, handler.Requests);
        Assert.Equal(before, fixture.Snapshot());
        foreach (var entry in ModelEntries)
            Assert.Equal(entry.Data, await File.ReadAllBytesAsync(fixture.ModelFile(entry.Path)));
    }

    [Fact]
    public async Task Ordinary_ZIP_directory_entries_and_Unix_file_attributes_keep_the_existing_model_layout()
    {
        using var fixture = new Fixture();
        var directories = new[] { ArchiveRoot + "/", ArchiveRoot + "/am/", ArchiveRoot + "/conf/",
            ArchiveRoot + "/graph/", ArchiveRoot + "/ivector/" }.Select(path => (path, Array.Empty<byte>()));
        var archive = CreateArchive(directories.Concat(ModelEntries.Select(entry => ($"{ArchiveRoot}/{entry.Path}", entry.Data))),
            entry => entry.ExternalAttributes = unchecked((int)((entry.FullName.EndsWith('/') ? 0x41EDu : 0x81A4u) << 16)));
        using var http = new HttpClient(new Handler((_, _) => Response(archive)));

        Assert.True(await Invoke(fixture, http, maximumArchiveBytes: archive.Length));
        fixture.AssertCompleteModel(fixture.ModelDirectory);
        Assert.Equal(["vosk-model"], fixture.TopLevelEntries());
    }

    [Fact]
    public void Existing_public_descriptor_keeps_the_same_Vosk_model_version_and_layout()
    {
        Assert.Equal("https://alphacephei.com/vosk/models/vosk-model-small-en-us-0.15.zip", Constant<string>("VoskModelUrl"));
        Assert.Equal("vosk-model", Constant<string>("VoskModelFolder"));
        Assert.Equal("README", Constant<string>("VoskModelMarker"));
        Assert.Equal(30L * 1024 * 1024, Constant<long>("VoskMinimumArchiveBytes"));
        Assert.Equal(100L * 1024 * 1024, Constant<long>("VoskMaximumArchiveBytes"));
        Assert.Equal(512L * 1024 * 1024, Constant<long>("VoskMaximumExtractedBytes"));
        Assert.Equal(10_000, Constant<int>("VoskMaximumEntries"));
    }

    [Theory]
    [InlineData("zero_minimum")]
    [InlineData("maximum_below_minimum")]
    [InlineData("archive_bound_above_public_limit")]
    [InlineData("zero_extraction_bound")]
    [InlineData("extraction_bound_above_public_limit")]
    [InlineData("zero_entry_count")]
    [InlineData("entry_count_above_public_limit")]
    [InlineData("zero_timeout")]
    [InlineData("timeout_above_existing_limit")]
    [InlineData("http_url")]
    [InlineData("url_credentials")]
    [InlineData("url_fragment")]
    [InlineData("relative_url")]
    public async Task Invalid_download_descriptor_never_calls_HTTP_or_changes_the_previous_model(string invalid)
    {
        using var fixture = new Fixture();
        fixture.SeedPreviousIncompleteModel();
        var before = fixture.Snapshot();
        var handler = new Handler((_, _) => throw new InvalidOperationException("An invalid descriptor must not dispatch."));
        using var http = new HttpClient(handler);
        var url = invalid switch
        {
            "http_url" => FixtureUrl.Replace("https://", "http://", StringComparison.Ordinal),
            "url_credentials" => "https://untrusted:password@model-fixture.invalid/model.zip",
            "url_fragment" => FixtureUrl + "#fragment",
            "relative_url" => "model.zip",
            _ => FixtureUrl,
        };

        Assert.False(await Invoke(fixture, http,
            minimumArchiveBytes: invalid == "zero_minimum" ? 0 : 1,
            maximumArchiveBytes: invalid switch
            {
                "maximum_below_minimum" => 0,
                "archive_bound_above_public_limit" => 100L * 1024 * 1024 + 1,
                _ => ValidArchive.Length * 4L,
            },
            maximumExtractedBytes: invalid switch
            {
                "zero_extraction_bound" => 0,
                "extraction_bound_above_public_limit" => 512L * 1024 * 1024 + 1,
                _ => 1024 * 1024,
            },
            maximumEntries: invalid switch
            {
                "zero_entry_count" => 0,
                "entry_count_above_public_limit" => 10_001,
                _ => 100,
            },
            timeout: invalid switch
            {
                "zero_timeout" => TimeSpan.Zero,
                "timeout_above_existing_limit" => TimeSpan.FromMinutes(30) + TimeSpan.FromTicks(1),
                _ => TimeSpan.FromMinutes(30),
            },
            url: url));
        Assert.Equal(0, handler.Requests);
        fixture.AssertPreviousIncompleteModel();
        Assert.Equal(before, fixture.Snapshot());
    }

    [Theory]
    [InlineData("corrupt_zip")]
    [InlineData("truncated_zip")]
    [InlineData("wrong_structure")]
    [InlineData("http_failure")]
    [InlineData("stream_failure")]
    [InlineData("declared_truncation")]
    [InlineData("declared_overrun")]
    public async Task Bad_download_or_structure_preserves_the_previous_model_directory(string failure)
    {
        using var fixture = new Fixture();
        fixture.SeedPreviousIncompleteModel();
        var before = fixture.Snapshot();
        var handler = new Handler((_, _) => failure switch
        {
            "corrupt_zip" => Response("not an archive"u8.ToArray()),
            "truncated_zip" => Response(ValidArchive[..(ValidArchive.Length / 2)]),
            "wrong_structure" => Response(CreateArchive([($"{ArchiveRoot}/README", "wrong layout"u8.ToArray())])),
            "http_failure" => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            "stream_failure" => Response(new ThrowingStream()),
            "declared_truncation" => DeclaredResponse(ValidArchive, ValidArchive.Length + 1),
            "declared_overrun" => DeclaredResponse(ValidArchive, ValidArchive.Length - 1),
            _ => throw new ArgumentOutOfRangeException(nameof(failure)),
        });
        using var http = new HttpClient(handler);

        Assert.False(await Invoke(fixture, http));
        fixture.AssertPreviousIncompleteModel();
        Assert.Equal(before, fixture.Snapshot());
    }

    [Theory]
    [InlineData("parent_slash")]
    [InlineData("parent_backslash")]
    [InlineData("nested_escape")]
    [InlineData("rooted_path")]
    public async Task Archive_traversal_is_rejected_without_writing_outside_its_stage(string traversal)
    {
        using var fixture = new Fixture();
        fixture.SeedPreviousIncompleteModel();
        var outside = Path.Combine(fixture.Root, "outside-owned-sentinel.txt");
        var sentinel = "owned outer fixture must not be overwritten"u8.ToArray();
        await File.WriteAllBytesAsync(outside, sentinel);
        var before = fixture.Snapshot();
        // Even the absolute malicious path stays inside this test's owned GUID
        // directory: an unsafe extractor can never damage another user's file.
        var entryName = traversal switch
        {
            "parent_slash" => "../outside-owned-sentinel.txt",
            "parent_backslash" => "..\\outside-owned-sentinel.txt",
            "nested_escape" => $"{ArchiveRoot}/../../outside-owned-sentinel.txt",
            "rooted_path" => outside.Replace('\\', '/'),
            _ => throw new ArgumentOutOfRangeException(nameof(traversal)),
        };
        var archive = CreateArchive(ModelEntries.Select(entry => ($"{ArchiveRoot}/{entry.Path}", entry.Data))
            .Append((entryName, "must not publish"u8.ToArray())));
        var handler = new Handler((_, _) => Response(archive));
        using var http = new HttpClient(handler);

        Assert.False(await Invoke(fixture, http, maximumArchiveBytes: archive.Length));
        Assert.Equal(sentinel, await File.ReadAllBytesAsync(outside));
        fixture.AssertPreviousIncompleteModel();
        Assert.Equal(before, fixture.Snapshot());
    }

    [Theory]
    [InlineData("symlink")]
    [InlineData("case_collision")]
    [InlineData("duplicate_file")]
    [InlineData("file_directory_collision")]
    [InlineData("alternate_data_stream")]
    [InlineData("trailing_dot")]
    [InlineData("trailing_space")]
    [InlineData("reserved_name")]
    public async Task Unsafe_entry_type_or_Windows_path_alias_cannot_replace_the_previous_model(string unsafeEntry)
    {
        using var fixture = new Fixture();
        fixture.SeedPreviousIncompleteModel();
        var before = fixture.Snapshot();
        var entryName = unsafeEntry switch
        {
            "symlink" => $"{ArchiveRoot}/symbolic-link",
            "case_collision" => $"{ArchiveRoot}/readme",
            "duplicate_file" => $"{ArchiveRoot}/README",
            "file_directory_collision" => $"{ArchiveRoot}/am",
            "alternate_data_stream" => $"{ArchiveRoot}/am/extra.bin:payload",
            "trailing_dot" => $"{ArchiveRoot}/am/extra.bin.",
            "trailing_space" => $"{ArchiveRoot}/am/extra.bin ",
            "reserved_name" => $"{ArchiveRoot}/am/NUL.txt",
            _ => throw new ArgumentOutOfRangeException(nameof(unsafeEntry)),
        };
        var archive = CreateArchive(ModelEntries.Select(entry => ($"{ArchiveRoot}/{entry.Path}", entry.Data))
            .Append((entryName, "unsafe fixture entry"u8.ToArray())), entry =>
        {
            if (unsafeEntry == "symlink" && entry.FullName == entryName)
                entry.ExternalAttributes = unchecked((int)(0xA1FFu << 16));
        });
        var handler = new Handler((_, _) => Response(archive));
        using var http = new HttpClient(handler);

        Assert.False(await Invoke(fixture, http, maximumArchiveBytes: archive.Length));
        fixture.AssertPreviousIncompleteModel();
        Assert.Equal(before, fixture.Snapshot());
    }

    [Theory]
    [InlineData("declared_archive_oversize")]
    [InlineData("unknown_length_archive_oversize")]
    [InlineData("extracted_bytes")]
    [InlineData("entry_count")]
    public async Task Archive_and_extraction_resource_bounds_fail_closed_without_replacing_previous_model(string bound)
    {
        using var fixture = new Fixture();
        fixture.SeedPreviousIncompleteModel();
        var before = fixture.Snapshot();
        var archive = bound == "extracted_bytes"
            ? CreateArchive(ModelEntries.Select(entry => ($"{ArchiveRoot}/{entry.Path}", entry.Data))
                .Append(($"{ArchiveRoot}/large-zero-file.bin", new byte[256 * 1024])))
            : ValidArchive;
        var handler = new Handler((_, _) => bound switch
        {
            "declared_archive_oversize" => DeclaredResponse(archive, archive.Length + 1),
            "unknown_length_archive_oversize" => Response(new UnknownLengthStream(archive.Concat(new byte[] { 0 }).ToArray())),
            _ => Response(archive),
        });
        using var http = new HttpClient(handler);

        Assert.False(await Invoke(fixture, http,
            maximumArchiveBytes: archive.Length,
            maximumExtractedBytes: bound == "extracted_bytes" ? 1024 : 1024 * 1024,
            maximumEntries: bound == "entry_count" ? ModelEntries.Length - 1 : 100));
        fixture.AssertPreviousIncompleteModel();
        Assert.Equal(before, fixture.Snapshot());
    }

    [Fact]
    public async Task Concurrent_requests_share_one_transfer_and_recheck_the_completed_model_after_waiting()
    {
        using var fixture = new Fixture();
        using var stream = new HeldStream(ValidArchive);
        var handler = new Handler((_, _) => Response(stream));
        using var http = new HttpClient(handler);
        var first = Invoke(fixture, http);
        await stream.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = Invoke(fixture, http);
        Assert.Equal(1, handler.Requests);
        Assert.False(second.IsCompleted);
        Assert.False(Directory.Exists(fixture.ModelDirectory));
        stream.Release();

        Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(await second.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, handler.Requests);
        Assert.Equal(["vosk-model"], fixture.TopLevelEntries());
    }

    [Fact]
    public async Task Unique_owned_stage_never_reuses_or_deletes_the_legacy_shared_zip_and_extract_directory()
    {
        using var fixture = new Fixture();
        var sharedZip = Path.Combine(fixture.ModelsDirectory, "vosk-model-download.zip");
        var sharedExtract = Path.Combine(fixture.ModelsDirectory, "vosk-extract-temp");
        var zipSentinel = "another operation owns this zip"u8.ToArray();
        await File.WriteAllBytesAsync(sharedZip, zipSentinel);
        Directory.CreateDirectory(sharedExtract);
        var extractSentinel = Path.Combine(sharedExtract, "unowned.txt");
        await File.WriteAllTextAsync(extractSentinel, "unowned extraction must remain");
        using var stream = new HeldStream(ValidArchive);
        var handler = new Handler((_, _) => Response(stream));
        using var http = new HttpClient(handler);
        var operation = Invoke(fixture, http);
        await stream.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(Directory.Exists(fixture.ModelDirectory));
        Assert.Equal(zipSentinel, await File.ReadAllBytesAsync(sharedZip));
        Assert.Equal("unowned extraction must remain", await File.ReadAllTextAsync(extractSentinel));
        Assert.Contains(fixture.TopLevelEntries(), name => name is not ("vosk-model-download.zip" or "vosk-extract-temp"));
        stream.Release();

        Assert.True(await operation.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(zipSentinel, await File.ReadAllBytesAsync(sharedZip));
        Assert.Equal("unowned extraction must remain", await File.ReadAllTextAsync(extractSentinel));
        Assert.Equal(["vosk-extract-temp", "vosk-model", "vosk-model-download.zip"], fixture.TopLevelEntries());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_and_existing_deadline_reach_a_stalled_body_and_allow_a_retry(bool deadline)
    {
        using var fixture = new Fixture();
        fixture.SeedPreviousIncompleteModel();
        var before = fixture.Snapshot();
        using var stream = new HeldStream(ValidArchive);
        var handler = new Handler((_, _) => Response(stream));
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
        using var cancellation = new CancellationTokenSource();
        var operation = Invoke(fixture, http, token: cancellation.Token,
            timeout: deadline ? TimeSpan.FromMilliseconds(150) : TimeSpan.FromMinutes(30));
        await stream.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if (!deadline) cancellation.Cancel();

        Assert.False(await operation.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(stream.LastReadToken.CanBeCanceled);
        Assert.True(stream.LastReadToken.IsCancellationRequested);
        fixture.AssertPreviousIncompleteModel();
        Assert.Equal(before, fixture.Snapshot());
        var retryHandler = new Handler((_, _) => Response(ValidArchive));
        using var retryHttp = new HttpClient(retryHandler);
        Assert.True(await Invoke(fixture, retryHttp));
        Assert.Equal(["vosk-model"], fixture.TopLevelEntries());
    }

    [Fact]
    public async Task Cancelling_a_waiter_does_not_cancel_the_transfer_owner_or_delete_its_stage()
    {
        using var fixture = new Fixture();
        using var stream = new HeldStream(ValidArchive);
        var handler = new Handler((_, _) => Response(stream));
        using var http = new HttpClient(handler);
        var first = Invoke(fixture, http);
        await stream.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var cancellation = new CancellationTokenSource();
        var second = Invoke(fixture, http, token: cancellation.Token);
        cancellation.Cancel();

        Assert.False(await second.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(first.IsCompleted);
        Assert.Equal(1, handler.Requests);
        stream.Release();
        Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(["vosk-model"], fixture.TopLevelEntries());
    }

    [Fact]
    public async Task Cancellation_before_extraction_cannot_replace_the_previous_model()
    {
        using var fixture = new Fixture();
        fixture.SeedPreviousIncompleteModel();
        var before = fixture.Snapshot();
        var handler = new Handler((_, _) => Response(ValidArchive));
        using var http = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var reachedExtraction = false;

        Assert.False(await Invoke(fixture, http, token: cancellation.Token, progress: status =>
        {
            if (!status.StartsWith("Extracting Vosk", StringComparison.Ordinal)) return;
            reachedExtraction = true;
            cancellation.Cancel();
        }));
        Assert.True(reachedExtraction);
        fixture.AssertPreviousIncompleteModel();
        Assert.Equal(before, fixture.Snapshot());
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public void Publication_failure_before_or_after_a_move_never_loses_the_previous_model(int failedMove, bool afterMove)
    {
        using var fixture = new Fixture();
        fixture.SeedPreviousIncompleteModel();
        var (source, backup) = fixture.CreatePublicationStage();
        var moves = 0;

        Assert.Throws<IOException>(() => Publish(source, fixture.ModelDirectory, backup, moveDirectory: (from, to) =>
        {
            moves++;
            if (moves == failedMove && !afterMove) throw new IOException("Move failed before acknowledgement.");
            Directory.Move(from, to);
            if (moves == failedMove && afterMove) throw new IOException("Move acknowledgement lost after success.");
        }));
        if (failedMove == 2 && afterMove)
        {
            fixture.AssertPreviousIncompleteModel(backup);
            fixture.AssertCompleteModel(fixture.ModelDirectory);
            Assert.False(Directory.Exists(source));
            Assert.Equal(2, moves);
        }
        else
        {
            fixture.AssertPreviousIncompleteModel();
            fixture.AssertCompleteModel(source);
            Assert.False(Directory.Exists(backup));
            Assert.Equal(failedMove == 1 && !afterMove ? 1 : failedMove + 1, moves);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Restore_failure_or_lost_acknowledgement_keeps_the_previous_bytes_in_backup_or_canonical(bool afterRestore)
    {
        using var fixture = new Fixture();
        fixture.SeedPreviousIncompleteModel();
        var (source, backup) = fixture.CreatePublicationStage();
        var moves = 0;

        Assert.Throws<IOException>(() => Publish(source, fixture.ModelDirectory, backup, moveDirectory: (from, to) =>
        {
            moves++;
            if (moves == 2) throw new IOException("New model publication failed.");
            if (moves == 3 && !afterRestore) throw new IOException("Backup restoration failed.");
            Directory.Move(from, to);
            if (moves == 3 && afterRestore) throw new IOException("Restoration acknowledgement lost.");
        }));
        Assert.Equal(3, moves);
        fixture.AssertCompleteModel(source);
        fixture.AssertPreviousIncompleteModel(afterRestore ? fixture.ModelDirectory : backup);
        Assert.Equal(afterRestore, Directory.Exists(fixture.ModelDirectory));
        Assert.Equal(!afterRestore, Directory.Exists(backup));
    }

    [Fact]
    public void Canonical_appearing_during_failed_publication_is_never_deleted_or_overwritten()
    {
        using var fixture = new Fixture();
        fixture.SeedPreviousIncompleteModel();
        var (source, backup) = fixture.CreatePublicationStage();
        var moves = 0;

        Assert.Throws<IOException>(() => Publish(source, fixture.ModelDirectory, backup, moveDirectory: (from, to) =>
        {
            moves++;
            if (moves == 2)
            {
                fixture.SeedCompleteModel();
                File.WriteAllText(fixture.ModelFile("newer-owner.txt"), "another operation owns canonical");
                throw new IOException("Another canonical appeared.");
            }
            Directory.Move(from, to);
        }));
        Assert.Equal(2, moves);
        Assert.Equal("another operation owns canonical", File.ReadAllText(fixture.ModelFile("newer-owner.txt")));
        fixture.AssertCompleteModel(fixture.ModelDirectory);
        fixture.AssertPreviousIncompleteModel(backup);
        fixture.AssertCompleteModel(source);
    }

    [Fact]
    public void A_complete_canonical_recheck_skips_all_publication_moves()
    {
        using var fixture = new Fixture();
        fixture.SeedCompleteModel();
        var (source, backup) = fixture.CreatePublicationStage();
        var before = fixture.Snapshot();

        Publish(source, fixture.ModelDirectory, backup, moveDirectory: (_, _) =>
            throw new InvalidOperationException("A complete canonical must not be moved."));
        Assert.Equal(before, fixture.Snapshot());
        Assert.False(Directory.Exists(backup));
        fixture.AssertCompleteModel(fixture.ModelDirectory);
        fixture.AssertCompleteModel(source);
    }

    [Fact]
    public void Cancellation_after_backup_move_restores_previous_model_before_returning()
    {
        using var fixture = new Fixture();
        fixture.SeedPreviousIncompleteModel();
        var (source, backup) = fixture.CreatePublicationStage();
        using var cancellation = new CancellationTokenSource();
        var moves = 0;

        Assert.Throws<OperationCanceledException>(() => Publish(source, fixture.ModelDirectory, backup,
            cancellation.Token, (from, to) =>
            {
                moves++;
                Directory.Move(from, to);
                if (moves == 1) cancellation.Cancel();
            }));
        Assert.Equal(2, moves);
        fixture.AssertPreviousIncompleteModel();
        fixture.AssertCompleteModel(source);
        Assert.False(Directory.Exists(backup));
    }

    [Fact]
    public void An_incomplete_stage_cannot_trigger_any_canonical_publication_move()
    {
        using var fixture = new Fixture();
        fixture.SeedPreviousIncompleteModel();
        var (source, backup) = fixture.CreatePublicationStage();
        File.Delete(Path.Combine(source, "README"));
        var before = fixture.Snapshot();

        Assert.Throws<InvalidDataException>(() => Publish(source, fixture.ModelDirectory, backup,
            moveDirectory: (_, _) => throw new InvalidOperationException("Invalid staging must not be published.")));
        fixture.AssertPreviousIncompleteModel();
        Assert.Equal(before, fixture.Snapshot());
    }

    private static Task<bool> Invoke(
        Fixture fixture, HttpClient http,
        long? maximumArchiveBytes = null, long maximumExtractedBytes = 1024 * 1024, int maximumEntries = 100,
        Action<string> progress = null, CancellationToken token = default, TimeSpan? timeout = null,
        long minimumArchiveBytes = 1, string url = FixtureUrl)
    {
        var method = typeof(ModelManager).GetMethod("DownloadVoskModelAsync", BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            [typeof(string), typeof(HttpClient), typeof(string), typeof(long), typeof(long), typeof(long),
                typeof(int), typeof(Action<string>), typeof(CancellationToken), typeof(TimeSpan)],
            modifiers: null);
        Assert.NotNull(method); // Exercise the same core without accessing the actual user profile.
        return (Task<bool>)method.Invoke(null,
            [fixture.ModelsDirectory, http, url, minimumArchiveBytes, maximumArchiveBytes ?? ValidArchive.Length * 4L,
                maximumExtractedBytes, maximumEntries, progress, token, timeout ?? TimeSpan.FromMinutes(30)]);
    }

    private static void Publish(string source, string destination, string backup, CancellationToken token = default,
        Action<string, string> moveDirectory = null)
    {
        var method = typeof(ModelManager).GetMethod("PublishVoskModel", BindingFlags.NonPublic | BindingFlags.Static,
            binder: null, [typeof(string), typeof(string), typeof(string), typeof(CancellationToken), typeof(Action<string, string>)],
            modifiers: null);
        Assert.NotNull(method);
        try { method.Invoke(null, [source, destination, backup, token, moveDirectory]); }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
        }
    }

    private static T Constant<T>(string name)
    {
        var field = typeof(ModelManager).GetField(name, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        return (T)field.GetRawConstantValue();
    }

    private static byte[] CreateArchive(IEnumerable<(string Path, byte[] Data)> entries, Action<ZipArchiveEntry> configureEntry = null)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var item in entries)
            {
                var entry = archive.CreateEntry(item.Path, CompressionLevel.Optimal);
                configureEntry?.Invoke(entry);
                using var content = entry.Open();
                content.Write(item.Data);
            }
        }
        return stream.ToArray();
    }

    private static HttpResponseMessage Response(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    private static HttpResponseMessage Response(Stream stream) => new(HttpStatusCode.OK) { Content = new StreamContent(stream) };
    private static HttpResponseMessage DeclaredResponse(byte[] bytes, long declared)
    {
        var response = Response(bytes);
        response.Content.Headers.ContentLength = declared;
        return response;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        private readonly string _name = $"kwt-vosk-boundary-{Guid.NewGuid():N}";
        internal string Root { get; }
        internal string ModelsDirectory { get; }
        internal string ModelDirectory => Path.Combine(ModelsDirectory, "vosk-model");
        internal Fixture()
        {
            Root = Path.GetFullPath(Path.Combine(_parent, _name));
            ModelsDirectory = Path.Combine(Root, "models");
            Directory.CreateDirectory(ModelsDirectory);
        }
        internal string ModelFile(string path) => Path.Combine(ModelDirectory, path.Replace('/', Path.DirectorySeparatorChar));
        internal void SeedCompleteModel() => SeedCompleteModelAt(ModelDirectory);
        private static void SeedCompleteModelAt(string directory)
        {
            foreach (var entry in ModelEntries)
            {
                var path = Path.Combine(directory, entry.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, entry.Data);
            }
        }
        internal (string Source, string Backup) CreatePublicationStage()
        {
            var stage = Path.Combine(ModelsDirectory, $".vosk-download-{Guid.NewGuid():N}");
            var source = Path.Combine(stage, "extracted", ArchiveRoot);
            SeedCompleteModelAt(source);
            return (source, Path.Combine(stage, "previous-model"));
        }
        internal void SeedPreviousIncompleteModel()
        {
            Directory.CreateDirectory(ModelDirectory);
            File.WriteAllText(ModelFile("README"), "prior README remains");
            File.WriteAllText(ModelFile("previous-owned.txt"), "prior model remains until a complete replacement exists");
        }
        internal void AssertPreviousIncompleteModel(string directory = null)
        {
            directory ??= ModelDirectory;
            Assert.Equal("prior README remains", File.ReadAllText(Path.Combine(directory, "README")));
            Assert.Equal("prior model remains until a complete replacement exists", File.ReadAllText(Path.Combine(directory, "previous-owned.txt")));
        }
        internal void AssertCompleteModel(string directory)
        {
            foreach (var entry in ModelEntries)
                Assert.Equal(entry.Data, File.ReadAllBytes(Path.Combine(directory, entry.Path.Replace('/', Path.DirectorySeparatorChar))));
        }
        internal string[] Snapshot() => Directory.GetFileSystemEntries(ModelsDirectory, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(ModelsDirectory, path)).Order(StringComparer.Ordinal).ToArray();
        internal string[] TopLevelEntries() => Directory.GetFileSystemEntries(ModelsDirectory)
            .Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        public void Dispose()
        {
            Assert.Equal(_parent, Path.GetDirectoryName(Root), ignoreCase: true);
            Assert.Equal(_name, Path.GetFileName(Root));
            Assert.True(_name.StartsWith("kwt-vosk-boundary-", StringComparison.Ordinal)
                && Guid.TryParseExact(_name["kwt-vosk-boundary-".Length..], "N", out _));
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private readonly object _sync = new();
        private int _requests;
        internal int Requests => Volatile.Read(ref _requests);
        internal List<Uri> Uris { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref _requests);
            lock (_sync) Uris.Add(request.RequestUri);
            return Task.FromResult(respond(request, token));
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
            ReadStarted.TrySetResult(true);
            await _release.Task.WaitAsync(token);
            var count = Math.Min(buffer.Length, bytes.Length - _position);
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
            ValueTask.FromException<int>(new IOException("Fixture body interrupted."));
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("Fixture body interrupted.");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
