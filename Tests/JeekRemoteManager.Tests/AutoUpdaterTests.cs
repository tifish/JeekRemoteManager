using System.IO.Compression;
using System.Net;
using JeekTools;

namespace JeekRemoteManager.Tests;

public class AutoUpdaterTests
{
    [Fact]
    public async Task Probe_keeps_a_useful_partial_sample_at_its_deadline_and_uses_normal_get()
    {
        bool? usedRange = null;
        using var handler = new Handler(request =>
        {
            usedRange = request.Headers.Range is not null;
            return Response(new ChunkStream(new byte[300 * 1024], 64 * 1024, holdAtEnd: true));
        });
        var result = await GitHubMirrors.ProbeThroughputAsync("https://unit.test/package.zip", 0,
            2 * 1024 * 1024, TimeSpan.FromMilliseconds(200), default, new HttpClient(handler, false));

        Assert.False(usedRange);
        Assert.True(result.Ok);
        Assert.True(result.TimedOut);
        Assert.Equal(300 * 1024, result.Bytes);
        Assert.True(result.BytesPerSecond > 0);
    }

    [Fact]
    public async Task Probe_rejects_an_insufficient_partial_sample()
    {
        using var handler = new Handler(_ =>
            Response(new ChunkStream(new byte[16 * 1024], 16 * 1024, holdAtEnd: true)));
        var result = await GitHubMirrors.ProbeThroughputAsync("https://unit.test/package.zip", 0,
            2 * 1024 * 1024, TimeSpan.FromMilliseconds(100), default, new HttpClient(handler, false));

        Assert.False(result.Ok);
        Assert.True(result.TimedOut);
        Assert.Equal(16 * 1024, result.Bytes);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_reported_as_a_successful_probe()
    {
        using var handler = new Handler(_ => Response(new ChunkStream([], 1, holdAtEnd: true)));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            GitHubMirrors.ProbeThroughputAsync("https://unit.test/package.zip", 0,
                2 * 1024 * 1024, TimeSpan.FromSeconds(5), cts.Token, new HttpClient(handler, false)));
    }

    [Fact]
    public async Task All_slow_sources_retry_the_fastest_observed_source_and_stage_a_valid_package()
    {
        using var fixture = new DownloadFixture();
        var updater = fixture.CreateUpdater();
        Assert.Equal(UpdateCheckOutcome.Available, await updater.HasUpdateAsync());

        var staged = await updater.DownloadAndStageAsync(DownloadFixture.Urls);

        Assert.NotNull(staged);
        Assert.Equal(["first", "fastest", "last", "fastest"], fixture.Downloads);
        Assert.Equal(DownloadFixture.Urls[1], updater.DownloadUrl);
        Assert.Equal(fixture.Payload, await File.ReadAllBytesAsync(Path.Combine(staged, "TestApp.exe")));
        Assert.Equal("1000", await File.ReadAllTextAsync(updater.StagedVersionPath));
        Assert.False(File.Exists(updater.StagedZipPath));
    }

    [Fact]
    public async Task Http_failures_are_not_retried_as_slow_sources()
    {
        using var fixture = new DownloadFixture(httpFailures: true);
        var updater = fixture.CreateUpdater();
        await updater.HasUpdateAsync();

        Assert.Null(await updater.DownloadAndStageAsync(DownloadFixture.Urls));
        Assert.Equal(["first", "fastest", "last"], fixture.Downloads);
        Assert.Contains("HTTP 503", updater.FailureReason);
        Assert.False(Directory.Exists(updater.UpdateRoot));
    }

    [Fact]
    public async Task Cancellation_before_mirror_selection_does_not_start_a_download()
    {
        using var fixture = new DownloadFixture();
        var updater = fixture.CreateUpdater();
        await updater.HasUpdateAsync();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Null(await updater.DownloadAndStageAsync(cancellationToken: cts.Token));
        Assert.Empty(fixture.Downloads);
        Assert.Equal("download cancelled", updater.FailureReason);
    }

    [Fact]
    public async Task Cancellation_during_slow_retry_cleans_up_and_stops_trying_sources()
    {
        using var fixture = new DownloadFixture();
        var updater = fixture.CreateUpdater();
        await updater.HasUpdateAsync();
        using var cts = new CancellationTokenSource();

        var staged = await updater.DownloadAndStageAsync(DownloadFixture.Urls,
            new CancelProgress(cts), cts.Token);

        Assert.Null(staged);
        Assert.Equal(["first", "fastest", "last", "fastest"], fixture.Downloads);
        Assert.Equal("download cancelled", updater.FailureReason);
        Assert.False(Directory.Exists(updater.UpdateRoot));
    }

    private sealed class CancelProgress(CancellationTokenSource cts) : IProgress<UpdateDownloadProgress>
    {
        public void Report(UpdateDownloadProgress value) => cts.Cancel();
    }

    private sealed class DownloadFixture : IDisposable
    {
        public static readonly string[] Urls =
            ["https://unit.test/first", "https://unit.test/fastest", "https://unit.test/last"];
        private readonly string _root = Path.Combine(Path.GetTempPath(), "JrmUpdaterTests", Guid.NewGuid().ToString("N"));
        private readonly Handler _handler;
        public byte[] Payload { get; } = new byte[20 * 1024];
        public List<string> Downloads { get; } = [];

        public DownloadFixture(bool httpFailures = false)
        {
            using var archive = new MemoryStream();
            using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, leaveOpen: true))
            using (var entry = zip.CreateEntry("TestApp.exe", CompressionLevel.NoCompression).Open())
                entry.Write(Payload);
            var package = archive.ToArray();
            _handler = new Handler(request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("version.txt"))
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("1000") };
                var source = request.RequestUri.AbsolutePath.Trim('/');
                Downloads.Add(source);
                if (httpFailures)
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                var chunk = source switch { "first" => 512, "fastest" => 4096, _ => 128 };
                return Response(new ChunkStream(package, chunk, delayMilliseconds: 25));
            });
        }

        public AutoUpdater CreateUpdater() => new(new AutoUpdaterOptions
        {
            AppExeName = "TestApp.exe",
            ReleaseZipUrl = "https://unit.test/package.zip",
            VersionTxtUrl = "https://unit.test/version.txt",
            GetLocalVersion = () => 10,
            UpdateRoot = _root,
        }, () => new HttpClient(_handler, false), TimeSpan.FromMilliseconds(1));

        public void Dispose()
        {
            _handler.Dispose();
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
    }

    private static HttpResponseMessage Response(Stream body) => new(HttpStatusCode.OK)
    {
        Content = new StreamContent(body),
    };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(respond(request));
    }

    private sealed class ChunkStream(byte[] data, int chunkSize, bool holdAtEnd = false, int delayMilliseconds = 1)
        : MemoryStream(data)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (holdAtEnd && Position == Length)
                await Task.Delay(Timeout.Infinite, token);
            await Task.Delay(delayMilliseconds, token);
            return await base.ReadAsync(buffer[..Math.Min(buffer.Length, chunkSize)], token);
        }
    }
}
