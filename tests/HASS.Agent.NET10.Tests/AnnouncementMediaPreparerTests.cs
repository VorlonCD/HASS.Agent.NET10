using System.Net;
using HASS.Agent.Companion.Announcements;

namespace HASS.Agent.Companion.Tests;

public sealed class AnnouncementMediaPreparerTests
{
    private static readonly Uri AllowedOrigin = new("https://ha.local:8123");

    [Fact]
    public async Task Downloads_speech_before_chime_and_removes_private_files_on_dispose()
    {
        var root = NewTempRoot();
        var requests = new List<string>();
        var handler = new DelegateHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            requests.Add(path);
            var bytes = path.EndsWith("speech.wav", StringComparison.Ordinal)
                ? new byte[] { 1, 2, 3 }
                : new byte[] { 4, 5 };
            return Task.FromResult(Ok(bytes));
        });
        using var preparer = new AnnouncementMediaPreparer([AllowedOrigin], handler, root);

        var prepared = await preparer.PrepareAsync(Request(
            "https://ha.local:8123/api/speech.wav?token=secret",
            "https://ha.local:8123/api/chime.wav?token=secret"), TestContext.Current.CancellationToken);

        Assert.Equal(["/api/speech.wav", "/api/chime.wav"], requests);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(prepared.SpeechPath, TestContext.Current.CancellationToken));
        Assert.Equal(new byte[] { 4, 5 }, await File.ReadAllBytesAsync(prepared.ChimePath!, TestContext.Current.CancellationToken));
        var requestDirectory = Path.GetDirectoryName(prepared.SpeechPath)!;
        Assert.StartsWith(Path.GetFullPath(root), Path.GetFullPath(requestDirectory), StringComparison.OrdinalIgnoreCase);

        prepared.Dispose();
        Assert.False(Directory.Exists(requestDirectory));
        DeleteTempRoot(root);
    }

    [Fact]
    public async Task Optional_chime_http_failure_keeps_prepared_speech_and_reports_fixed_warning()
    {
        var root = NewTempRoot();
        var handler = new DelegateHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("speech.mp3", StringComparison.Ordinal)
                ? Ok([7, 8, 9])
                : new HttpResponseMessage(HttpStatusCode.NotFound)));
        using var preparer = new AnnouncementMediaPreparer([AllowedOrigin], handler, root);

        var prepared = await preparer.PrepareAsync(Request(
            "https://ha.local:8123/speech.mp3",
            "https://ha.local:8123/missing.mp3"), TestContext.Current.CancellationToken);

        Assert.Equal("chime_unavailable", prepared.Warning);
        Assert.Null(prepared.ChimePath);
        Assert.True(File.Exists(prepared.SpeechPath));
        var requestDirectory = Path.GetDirectoryName(prepared.SpeechPath)!;
        prepared.Dispose();
        Assert.False(Directory.Exists(requestDirectory));
        DeleteTempRoot(root);
    }

    [Fact]
    public async Task Optional_chime_timeout_does_not_discard_speech_or_caller_cancellation()
    {
        var root = NewTempRoot();
        var handler = new DelegateHandler(async (request, cancellationToken) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("chime.mp3", StringComparison.Ordinal))
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Ok([1, 2, 3]);
        });
        using var preparer = new AnnouncementMediaPreparer([AllowedOrigin], handler, root, TimeSpan.FromMilliseconds(250));

        var prepared = await preparer.PrepareAsync(Request(
            "https://ha.local:8123/speech.mp3",
            "https://ha.local:8123/chime.mp3"), TestContext.Current.CancellationToken);

        Assert.Equal("chime_unavailable", prepared.Warning);
        Assert.Null(prepared.ChimePath);
        Assert.True(File.Exists(prepared.SpeechPath));
        var requestDirectory = Path.GetDirectoryName(prepared.SpeechPath)!;
        prepared.Dispose();
        Assert.False(Directory.Exists(requestDirectory));
        DeleteTempRoot(root);
    }

    [Theory]
    [InlineData("http://ha.local:8123/speech.mp3")]
    [InlineData("https://ha.local:8124/speech.mp3")]
    [InlineData("https://user:secret@ha.local:8123/speech.mp3")]
    [InlineData("https://other.local:8123/speech.mp3")]
    public async Task Rejects_unconfigured_scheme_host_port_or_userinfo_before_request(string url)
    {
        var root = NewTempRoot();
        var callCount = 0;
        using var preparer = new AnnouncementMediaPreparer([AllowedOrigin], new DelegateHandler((_, _) =>
        {
            Interlocked.Increment(ref callCount);
            return Task.FromResult(Ok([1]));
        }), root);

        await Assert.ThrowsAsync<HttpRequestException>(() => preparer.PrepareAsync(Request(url), TestContext.Current.CancellationToken));
        Assert.Equal(0, callCount);
        Assert.Empty(Directory.Exists(root) ? Directory.EnumerateFileSystemEntries(root) : []);
        DeleteTempRoot(root);
    }

    [Fact]
    public async Task Rejects_redirect_to_an_unconfigured_origin_without_contacting_it()
    {
        var root = NewTempRoot();
        var requested = new List<string>();
        var handler = new DelegateHandler((request, _) =>
        {
            requested.Add(request.RequestUri!.Host);
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri("https://outside.local:8123/speech.mp3");
            return Task.FromResult(response);
        });
        using var preparer = new AnnouncementMediaPreparer([AllowedOrigin], handler, root);

        await Assert.ThrowsAsync<HttpRequestException>(() => preparer.PrepareAsync(
            Request("https://ha.local:8123/start.mp3"), TestContext.Current.CancellationToken));

        Assert.Equal(["ha.local"], requested);
        Assert.Empty(Directory.Exists(root) ? Directory.EnumerateFileSystemEntries(root) : []);
        DeleteTempRoot(root);
    }

    [Theory]
    [InlineData(3, true)]
    [InlineData(4, false)]
    public async Task Follows_at_most_three_same_origin_redirects(int redirectCount, bool succeeds)
    {
        var root = NewTempRoot();
        var callCount = 0;
        var handler = new DelegateHandler((request, _) =>
        {
            var requestNumber = Interlocked.Increment(ref callCount);
            if (requestNumber <= redirectCount)
            {
                var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
                response.Headers.Location = new Uri($"/redirect-{requestNumber}.mp3", UriKind.Relative);
                return Task.FromResult(response);
            }

            return Task.FromResult(Ok([1, 2]));
        });
        using var preparer = new AnnouncementMediaPreparer([AllowedOrigin], handler, root);

        if (succeeds)
        {
            var prepared = await preparer.PrepareAsync(Request("https://ha.local:8123/start.mp3"), TestContext.Current.CancellationToken);
            Assert.Equal(4, callCount);
            prepared.Dispose();
        }
        else
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => preparer.PrepareAsync(
                Request("https://ha.local:8123/start.mp3"), TestContext.Current.CancellationToken));
            Assert.Equal(4, callCount);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }

        DeleteTempRoot(root);
    }

    [Fact]
    public async Task Rejects_media_over_ten_mebibytes_and_cleans_partial_work()
    {
        var root = NewTempRoot();
        var handler = new DelegateHandler((_, _) => Task.FromResult(Ok(new byte[AnnouncementMediaPreparer.MaximumMediaBytes + 1])));
        using var preparer = new AnnouncementMediaPreparer([AllowedOrigin], handler, root);

        await Assert.ThrowsAsync<InvalidDataException>(() => preparer.PrepareAsync(
            Request("https://ha.local:8123/large.mp3"), TestContext.Current.CancellationToken));

        Assert.Empty(Directory.Exists(root) ? Directory.EnumerateFileSystemEntries(root) : []);
        DeleteTempRoot(root);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_converted_to_an_optional_chime_warning()
    {
        var root = NewTempRoot();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var handler = new DelegateHandler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("chime.mp3", StringComparison.Ordinal))
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Ok([1, 2, 3]);
        });
        using var preparer = new AnnouncementMediaPreparer([AllowedOrigin], handler, root, TimeSpan.FromSeconds(2));
        var task = preparer.PrepareAsync(Request(
            "https://ha.local:8123/speech.mp3",
            "https://ha.local:8123/chime.mp3"), cancellation.Token);
        cancellation.CancelAfter(50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Empty(Directory.Exists(root) ? Directory.EnumerateFileSystemEntries(root) : []);
        DeleteTempRoot(root);
    }

    private static AnnouncementRequest Request(string speechUrl, string? chimeUrl = null) => new(
        Guid.NewGuid().ToString("D"), speechUrl, chimeUrl, AnnouncementPriority.Critical, DateTimeOffset.UtcNow.AddMinutes(1));

    private static HttpResponseMessage Ok(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    private static string NewTempRoot() => Path.Combine(Path.GetTempPath(), "hass-announcement-tests-" + Guid.NewGuid().ToString("N"));

    private static void DeleteTempRoot(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
