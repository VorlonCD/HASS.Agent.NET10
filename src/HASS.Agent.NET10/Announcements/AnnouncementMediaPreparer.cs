using System.Buffers;
using System.Net;

namespace HASS.Agent.Companion.Announcements;

/// <summary>Downloads announcement media into short-lived files under the user's local data directory.</summary>
internal sealed class AnnouncementMediaPreparer : IAnnouncementMediaPreparer, IDisposable
{
    public const int MaximumMediaBytes = 10 * 1024 * 1024;
    private readonly TimeSpan _perMediaTimeout;

    private readonly HttpClient _httpClient;
    private readonly HashSet<Origin> _allowedOrigins;
    private readonly string _cacheRoot;
    private bool _disposed;

    public AnnouncementMediaPreparer(
        IEnumerable<Uri> allowedOrigins,
        HttpMessageHandler? handler = null,
        string? cacheRoot = null,
        TimeSpan? mediaTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(allowedOrigins);

        _allowedOrigins = new HashSet<Origin>();
        foreach (var origin in allowedOrigins)
        {
            if (!TryGetOrigin(origin, out var parsed))
                throw new ArgumentException("Allowed origins must be absolute HTTP or HTTPS origins without user information.", nameof(allowedOrigins));

            _allowedOrigins.Add(parsed);
        }

        _cacheRoot = Path.GetFullPath(cacheRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HASS.Agent.NET10", "Announcements"));
        _perMediaTimeout = mediaTimeout ?? TimeSpan.FromSeconds(30);
        if (_perMediaTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(mediaTimeout));

        HttpMessageHandler actualHandler;
        if (handler is null)
        {
            actualHandler = new HttpClientHandler { AllowAutoRedirect = false };
            _httpClient = new HttpClient(actualHandler, disposeHandler: true);
        }
        else
        {
            if (handler is HttpClientHandler httpClientHandler)
                httpClientHandler.AllowAutoRedirect = false;

            actualHandler = handler;
            _httpClient = new HttpClient(actualHandler, disposeHandler: false);
        }

        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
    }

    public async Task<PreparedAnnouncement> PrepareAsync(AnnouncementRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);

        string? directory = null;
        try
        {
            var speechUri = ParseAndValidateTarget(request.SpeechUrl);
            directory = Path.Combine(_cacheRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var speechPath = Path.Combine(directory, "speech" + GetSafeExtension(speechUri));
            await DownloadAsync(speechUri, speechPath, cancellationToken).ConfigureAwait(false);

            string? chimePath = null;
            string? warning = null;
            if (request.ChimeUrl is not null)
            {
                try
                {
                    var chimeUri = ParseAndValidateTarget(request.ChimeUrl);
                    var proposedPath = Path.Combine(directory, "chime" + GetSafeExtension(chimeUri));
                    await DownloadAsync(chimeUri, proposedPath, cancellationToken).ConfigureAwait(false);
                    chimePath = proposedPath;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    warning = "chime_unavailable";
                }
            }

            var ownedDirectory = directory;
            return new PreparedAnnouncement(speechPath, chimePath, warning, () => DeleteDirectoryBestEffort(ownedDirectory));
        }
        catch
        {
            if (directory is not null)
                DeleteDirectoryBestEffort(directory);

            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _httpClient.Dispose();
    }

    private async Task DownloadAsync(Uri initialUri, string destinationPath, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(_perMediaTimeout);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var token = linkedCancellation.Token;
        try
        {
            var currentUri = initialUri;
            var redirectsFollowed = 0;

            while (true)
            {
                ValidateTarget(currentUri);
                using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);

                if (IsRedirect(response.StatusCode))
                {
                    if (redirectsFollowed >= 3 || response.Headers.Location is null)
                        throw new HttpRequestException("Announcement media redirect is invalid.");

                    var nextUri = response.Headers.Location.IsAbsoluteUri
                        ? response.Headers.Location
                        : new Uri(currentUri, response.Headers.Location);
                    ValidateTarget(nextUri);
                    currentUri = nextUri;
                    redirectsFollowed++;
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException("Announcement media request failed.");

                if (response.Content.Headers.ContentLength is long contentLength && contentLength > MaximumMediaBytes)
                    throw new InvalidDataException("Announcement media exceeds the size limit.");

                await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                await using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    81920, FileOptions.Asynchronous | FileOptions.SequentialScan);

                var buffer = ArrayPool<byte>.Shared.Rent(81920);
                long total = 0;
                try
                {
                    while (true)
                    {
                        var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false);
                        if (read == 0)
                            break;

                        total += read;
                        if (total > MaximumMediaBytes)
                            throw new InvalidDataException("Announcement media exceeds the size limit.");

                        await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                if (total == 0)
                    throw new InvalidDataException("Announcement media is empty.");

                return;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new TimeoutException("Announcement media download timed out.");
        }
    }

    private Uri ParseAndValidateTarget(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new HttpRequestException("Announcement media URL is invalid.");

        ValidateTarget(uri);
        return uri;
    }

    private void ValidateTarget(Uri uri)
    {
        if (!TryGetOrigin(uri, out var origin) || !_allowedOrigins.Contains(origin))
            throw new HttpRequestException("Announcement media origin is not allowed.");
    }

    private static bool TryGetOrigin(Uri uri, out Origin origin)
    {
        origin = default;
        if (uri is null || !uri.IsAbsoluteUri ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length != 0)
        {
            return false;
        }

        origin = new Origin(uri.Scheme, uri.IdnHost, uri.Port);
        return true;
    }

    private static string GetSafeExtension(Uri uri)
    {
        var extension = Path.GetExtension(uri.AbsolutePath);
        if (extension.Length is < 2 or > 10)
            return ".audio";

        for (var i = 1; i < extension.Length; i++)
        {
            var character = extension[i];
            if (!char.IsAsciiLetterOrDigit(character))
                return ".audio";
        }

        return extension.ToLowerInvariant();
    }

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or
            HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static void DeleteDirectoryBestEffort(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Cleanup must not replace the terminal playback result.
        }
    }

    private readonly record struct Origin(string Scheme, string Host, int Port);
}
