namespace HASS.Agent.Companion.Announcements;

internal enum AnnouncementPriority { Normal, Important, Critical }

internal sealed record AnnouncementRequest(
    string RequestId, string SpeechUrl, string? ChimeUrl,
    AnnouncementPriority Priority, DateTimeOffset ExpiresAt);

internal sealed record AnnouncementResult(string RequestId, string Status, string? Reason = null);

internal sealed class PreparedAnnouncement(string speechPath, string? chimePath, string? warning = null, Action? cleanup = null) : IDisposable
{
    public string SpeechPath { get; } = speechPath;
    public string? ChimePath { get; } = chimePath;
    public string? Warning { get; } = warning;
    public void Dispose() => cleanup?.Invoke();
}

internal interface IAnnouncementMediaPreparer
{
    Task<PreparedAnnouncement> PrepareAsync(AnnouncementRequest request, CancellationToken cancellationToken);
}

internal interface IAnnouncementAudioController
{
    // Null means that the Windows state could not be read. During an override,
    // this is the desired restoration state, not the temporarily unmuted state.
    bool? GetEffectiveMuted();
    Task<IAnnouncementAudioLease> AcquireAsync(AnnouncementPriority priority, CancellationToken cancellationToken);
}

internal interface IAnnouncementAudioLease : IAsyncDisposable
{
    Task PlayAsync(string filePath, CancellationToken cancellationToken);
}

internal sealed class AnnouncementSuppressedException() : Exception("muted");
