using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using KeyboardWtf.Models;
using KeyboardWtf.Services;
using Xunit;

namespace KeyboardWtf.Tests;

public sealed class WhisperDownloadBoundaryTests
{
    private static readonly byte[] BaseBytes = Encoding.UTF8.GetBytes("bounded base model fixture");
    private static readonly byte[] SmallBytes = Encoding.UTF8.GetBytes("bounded small model fixture");

    [Fact]
    public async Task Selected_size_url_destination_and_checksum_are_frozen_before_transfer()
    {
        using var fixture = new Fixture();
        using var stream = new HeldStream(BaseBytes);
        var handler = new Handler((_, _) => Response(stream));
        using var http = new HttpClient(handler);
        var selected = WhisperModelSize.Base;
        var sizeReads = 0;
        var urlSizes = new List<WhisperModelSize>();
        var checksumSizes = new List<WhisperModelSize>();
        var operation = Invoke(() => { sizeReads++; return selected; }, fixture.Root, http,
            downloadUrl: size => { urlSizes.Add(size); return Url(size); },
            sha1: size => { checksumSizes.Add(size); return Digest(Bytes(size)); });
        await stream.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        selected = WhisperModelSize.Small;
        stream.Release();

        Assert.True(await operation.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, sizeReads);
        Assert.Equal([WhisperModelSize.Base], urlSizes);
        Assert.All(checksumSizes, size => Assert.Equal(WhisperModelSize.Base, size));
        Assert.Equal(Url(WhisperModelSize.Base), Assert.Single(handler.Uris).AbsoluteUri);
        Assert.Equal(BaseBytes, await File.ReadAllBytesAsync(fixture.PathFor(WhisperModelSize.Base)));
        Assert.False(File.Exists(fixture.PathFor(WhisperModelSize.Small)));
        Assert.Equal(["ggml-base.bin"], fixture.Files());
    }

    [Fact]
    public async Task Partial_bytes_are_not_published_and_an_unowned_shared_temporary_file_is_untouched()
    {
        using var fixture = new Fixture();
        var destination = fixture.PathFor(WhisperModelSize.Base);
        var unownedShared = destination + ".download";
        var sentinel = "another operation owns this file"u8.ToArray();
        await File.WriteAllBytesAsync(unownedShared, sentinel);
        using var stream = new HeldStream(BaseBytes);
        var handler = new Handler((_, _) => Response(stream));
        using var http = new HttpClient(handler);
        var operation = Invoke(() => WhisperModelSize.Base, fixture.Root, http);
        await stream.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(File.Exists(destination));
        Assert.Equal(sentinel, await File.ReadAllBytesAsync(unownedShared));
        var ownedPartials = Directory.GetFiles(fixture.Root)
            .Where(path => !string.Equals(path, unownedShared, StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.Single(ownedPartials);
        Assert.NotEqual(unownedShared, ownedPartials[0]);
        stream.Release();

        Assert.True(await operation.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(BaseBytes, await File.ReadAllBytesAsync(destination));
        Assert.Equal(sentinel, await File.ReadAllBytesAsync(unownedShared));
        Assert.Equal(["ggml-base.bin", "ggml-base.bin.download"], fixture.Files());
    }

    [Fact]
    public async Task Concurrent_requests_for_one_destination_share_the_download_and_recheck_after_waiting()
    {
        using var fixture = new Fixture();
        using var stream = new HeldStream(BaseBytes);
        var handler = new Handler((_, _) => Response(stream));
        using var http = new HttpClient(handler);
        var first = Invoke(() => WhisperModelSize.Base, fixture.Root, http);
        await stream.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = Invoke(() => WhisperModelSize.Base, fixture.Root, http);
        Assert.Equal(1, handler.Requests);
        Assert.False(second.IsCompleted);
        stream.Release();

        Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(await second.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, handler.Requests);
        Assert.Equal(BaseBytes, await File.ReadAllBytesAsync(fixture.PathFor(WhisperModelSize.Base)));
        Assert.Equal(["ggml-base.bin"], fixture.Files());
    }

    [Fact]
    public async Task Valid_existing_model_skips_all_network_work()
    {
        using var fixture = new Fixture();
        await File.WriteAllBytesAsync(fixture.PathFor(WhisperModelSize.Base), BaseBytes);
        var handler = new Handler((_, _) => throw new InvalidOperationException("A valid existing model must not download."));
        using var http = new HttpClient(handler);

        Assert.True(await Invoke(() => WhisperModelSize.Base, fixture.Root, http));
        Assert.Equal(0, handler.Requests);
        Assert.Equal(BaseBytes, await File.ReadAllBytesAsync(fixture.PathFor(WhisperModelSize.Base)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Sha256_download_verification_accepts_exact_bytes_and_rejects_modified_bytes(bool valid)
    {
        using var fixture = new Fixture();
        var destination = fixture.PathFor(WhisperModelSize.Base);
        var previous = "previous complete model fixture"u8.ToArray();
        await File.WriteAllBytesAsync(destination, previous);
        var received = BaseBytes.ToArray();
        if (!valid) received[0] ^= 1;
        var handler = new Handler((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(received) });
        using var http = new HttpClient(handler);

        Assert.Equal(valid, await Invoke(() => WhisperModelSize.Base, fixture.Root, http,
            sha1: size => Convert.ToHexString(SHA256.HashData(Bytes(size)))));
        Assert.Equal(valid ? BaseBytes : previous, await File.ReadAllBytesAsync(destination));
        Assert.Equal(["ggml-base.bin"], fixture.Files());
        Assert.Equal(1, handler.Requests);
        if (valid)
        {
            Assert.True(await Invoke(() => WhisperModelSize.Base, fixture.Root, http,
                sha1: size => Convert.ToHexString(SHA256.HashData(Bytes(size)))));
            Assert.Equal(1, handler.Requests);
        }
    }

    [Theory]
    [InlineData(WhisperModelSize.Base, "ggml-base.bin", 100L * 1024 * 1024, 147_951_465L,
        "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe",
        "465707469ff3a37a2b9b8d8f89f2f99de7299dac")]
    [InlineData(WhisperModelSize.Small, "ggml-small.bin", 300L * 1024 * 1024, 487_601_967L,
        "1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b",
        "55356645c2b361a969dfd0ef2c5a50d530afd8d5")]
    public void Public_descriptors_pin_the_same_model_files_and_leave_installed_validation_unchanged(
        WhisperModelSize size, string filename, long minimum, long maximum, string sha256, string installedSha1)
    {
        const string revision = "5359861c739e955e79d9a303bcbc70fb988958b1";
        Assert.Equal(filename, ModelManager.GetWhisperFileName(size));
        Assert.Equal($"https://huggingface.co/ggerganov/whisper.cpp/resolve/{revision}/{filename}",
            Descriptor<string>("GetWhisperDownloadUrl", size));
        Assert.Equal(minimum, Descriptor<long>("GetWhisperMinimumBytes", size));
        Assert.Equal(maximum, Descriptor<long>("GetWhisperMaximumDownloadBytes", size));
        Assert.Equal(sha256, Descriptor<string>("GetWhisperDownloadSha256", size), ignoreCase: true);
        Assert.Equal(installedSha1, Descriptor<string>("GetWhisperSha1", size), ignoreCase: true);
    }

    [Theory]
    [InlineData("corrupt")]
    [InlineData("truncated")]
    [InlineData("http_failure")]
    [InlineData("stream_failure")]
    [InlineData("declared_truncation")]
    [InlineData("declared_overrun")]
    [InlineData("declared_oversize")]
    [InlineData("unknown_length_oversize")]
    public async Task Failed_download_preserves_previous_destination_and_cleans_only_owned_partial(string failure)
    {
        using var fixture = new Fixture();
        var destination = fixture.PathFor(WhisperModelSize.Base);
        var previous = "previous complete model fixture"u8.ToArray();
        await File.WriteAllBytesAsync(destination, previous);
        var handler = new Handler((_, _) => failure switch
        {
            "corrupt" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[BaseBytes.Length]) },
            "truncated" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(BaseBytes[..5]) },
            "http_failure" => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            "declared_truncation" => ResponseWithDeclaredLength(BaseBytes, BaseBytes.Length + 1),
            "declared_overrun" => ResponseWithDeclaredLength(BaseBytes, BaseBytes.Length - 1),
            "declared_oversize" => ResponseWithDeclaredLength(new byte[BaseBytes.Length + 1], BaseBytes.Length + 1),
            "unknown_length_oversize" => Response(new UnknownLengthStream(new byte[BaseBytes.Length + 1])),
            _ => Response(new ThrowingStream()),
        });
        using var http = new HttpClient(handler);

        Assert.False(await Invoke(() => WhisperModelSize.Base, fixture.Root, http));
        Assert.Equal(previous, await File.ReadAllBytesAsync(destination));
        Assert.Equal(["ggml-base.bin"], fixture.Files());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_or_existing_deadline_reaches_stalled_body_and_releases_download_gate(bool deadline)
    {
        using var fixture = new Fixture();
        using var stream = new HeldStream(BaseBytes);
        var handler = new Handler((_, _) => Response(stream));
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
        using var cancellation = new CancellationTokenSource();
        var first = Invoke(() => WhisperModelSize.Base, fixture.Root, http,
            token: cancellation.Token, timeout: deadline ? TimeSpan.FromMilliseconds(150) : TimeSpan.FromMinutes(30));
        await stream.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if (!deadline) cancellation.Cancel();

        Assert.False(await first.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(stream.LastReadToken.CanBeCanceled);
        Assert.True(stream.LastReadToken.IsCancellationRequested);
        Assert.Empty(fixture.Files());
        var retryHandler = new Handler((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(BaseBytes) });
        using var retryHttp = new HttpClient(retryHandler);
        Assert.True(await Invoke(() => WhisperModelSize.Base, fixture.Root, retryHttp));
        Assert.Equal(BaseBytes, await File.ReadAllBytesAsync(fixture.PathFor(WhisperModelSize.Base)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task Matching_hash_cannot_hide_a_declared_length_mismatch_inside_valid_descriptor_bounds(int difference)
    {
        using var fixture = new Fixture();
        var destination = fixture.PathFor(WhisperModelSize.Base);
        var previous = "previous complete model fixture"u8.ToArray();
        await File.WriteAllBytesAsync(destination, previous);
        var handler = new Handler((_, _) => ResponseWithDeclaredLength(BaseBytes, BaseBytes.Length + difference));
        using var http = new HttpClient(handler);

        Assert.False(await Invoke(() => WhisperModelSize.Base, fixture.Root, http,
            minimum: _ => 1, maximum: _ => BaseBytes.Length + 1));
        Assert.Equal(previous, await File.ReadAllBytesAsync(destination));
        Assert.Equal(["ggml-base.bin"], fixture.Files());
    }

    [Fact]
    public async Task Cancelling_a_waiter_does_not_cancel_or_delete_the_download_owners_work()
    {
        using var fixture = new Fixture();
        using var stream = new HeldStream(BaseBytes);
        var handler = new Handler((_, _) => Response(stream));
        using var http = new HttpClient(handler);
        var first = Invoke(() => WhisperModelSize.Base, fixture.Root, http);
        await stream.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var cancellation = new CancellationTokenSource();
        var waiter = Invoke(() => WhisperModelSize.Base, fixture.Root, http, token: cancellation.Token);
        cancellation.Cancel();

        Assert.False(await waiter.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(first.IsCompleted);
        Assert.Equal(1, handler.Requests);
        stream.Release();
        Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(BaseBytes, await File.ReadAllBytesAsync(fixture.PathFor(WhisperModelSize.Base)));
        Assert.Equal(["ggml-base.bin"], fixture.Files());
    }

    private static Task<bool> Invoke(
        Func<WhisperModelSize> selected, string root, HttpClient http,
        Func<WhisperModelSize, string> downloadUrl = null,
        Func<WhisperModelSize, string> sha1 = null,
        CancellationToken token = default, TimeSpan? timeout = null,
        Func<WhisperModelSize, long> minimum = null,
        Func<WhisperModelSize, long> maximum = null)
    {
        var method = typeof(ModelManager).GetMethod("DownloadWhisperModelAsync",
            BindingFlags.NonPublic | BindingFlags.Static, binder: null,
            [typeof(Func<WhisperModelSize>), typeof(string), typeof(HttpClient),
                typeof(Func<WhisperModelSize, string>), typeof(Func<WhisperModelSize, long>),
                typeof(Func<WhisperModelSize, string>), typeof(Func<WhisperModelSize, long>),
                typeof(Action<string>), typeof(CancellationToken), typeof(TimeSpan)],
            modifiers: null);
        Assert.NotNull(method); // Red until the public facade can share an injectable offline core.
        return (Task<bool>)method.Invoke(null,
            [selected, root, http, downloadUrl ?? Url,
                minimum ?? (size => Bytes(size).Length), sha1 ?? (size => Digest(Bytes(size))),
                maximum ?? (size => Bytes(size).Length),
                null, token, timeout ?? TimeSpan.FromMinutes(30)]);
    }

    private static byte[] Bytes(WhisperModelSize size) => size == WhisperModelSize.Base ? BaseBytes : SmallBytes;
    private static T Descriptor<T>(string name, WhisperModelSize size)
    {
        var method = typeof(ModelManager).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static,
            binder: null, [typeof(WhisperModelSize)], modifiers: null);
        Assert.NotNull(method);
        return (T)method.Invoke(null, [size]);
    }

    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA1.HashData(bytes));
    private static string Url(WhisperModelSize size) => $"https://model-fixture.invalid/{ModelManager.GetWhisperFileName(size)}";
    private static HttpResponseMessage Response(Stream stream) => new(HttpStatusCode.OK) { Content = new StreamContent(stream) };
    private static HttpResponseMessage ResponseWithDeclaredLength(byte[] bytes, long declared)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentLength = declared;
        return response;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        private readonly string _name = $"kwt-whisper-boundary-{Guid.NewGuid():N}";
        internal string Root { get; }
        internal Fixture() { Root = Path.GetFullPath(Path.Combine(_parent, _name)); Directory.CreateDirectory(Root); }
        internal string PathFor(WhisperModelSize size) => Path.Combine(Root, ModelManager.GetWhisperFileName(size));
        internal string[] Files() => Directory.GetFiles(Root).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        public void Dispose()
        {
            Assert.Equal(_parent, Path.GetDirectoryName(Root), ignoreCase: true);
            Assert.Equal(_name, Path.GetFileName(Root));
            Assert.True(_name.StartsWith("kwt-whisper-boundary-", StringComparison.Ordinal)
                && Guid.TryParseExact(_name["kwt-whisper-boundary-".Length..], "N", out _));
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
