using HASS.Agent.Companion.Announcements;

namespace HASS.Agent.Companion.Tests;

public sealed class AnnouncementWindowsPolicyTests
{
    [Fact]
    public void Overnight_quiet_period_produces_start_and_end_boundaries()
    {
        var from = new DateTimeOffset(2026, 10, 14, 21, 59, 0, TimeSpan.Zero);
        var through = new DateTimeOffset(2026, 10, 15, 8, 1, 0, TimeSpan.Zero);
        var period = new QuietHoursPeriod(22 * 60 * 60, 8 * 60 * 60);

        var boundaries = AnnouncementLeasePolicy.FindBoundaries(from, through, [period], TimeZoneInfo.Utc);

        Assert.Collection(
            boundaries,
            item => Assert.Equal(new DateTimeOffset(2026, 10, 14, 22, 0, 0, TimeSpan.Zero), item.AtUtc),
            item => Assert.Equal(new DateTimeOffset(2026, 10, 15, 8, 0, 0, TimeSpan.Zero), item.AtUtc));
        Assert.Equal(QuietHoursBoundary.Start, boundaries[0].Boundary);
        Assert.Equal(QuietHoursBoundary.End, boundaries[1].Boundary);
    }

    [Fact]
    public void Force_unmute_boundary_is_applied_even_when_the_target_is_already_unmuted()
    {
        Assert.True(AnnouncementLeasePolicy.TryGetBoundaryTarget(
            enabled: true,
            forceUnmute: true,
            QuietHoursBoundary.End,
            out var muted));
        Assert.False(muted);
    }

    [Fact]
    public void Recovery_applies_only_quiet_boundaries_after_the_last_observation()
    {
        var observedAt = new DateTimeOffset(2026, 10, 14, 23, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 10, 15, 8, 0, 0, TimeSpan.Zero);
        var boundaries = AnnouncementLeasePolicy.FindBoundaries(
            observedAt,
            now,
            [new QuietHoursPeriod(22 * 60 * 60, 8 * 60 * 60)],
            TimeZoneInfo.Utc);

        Assert.Single(boundaries);
        Assert.Equal(QuietHoursBoundary.End, boundaries[0].Boundary);
        Assert.True(AnnouncementLeasePolicy.TryGetBoundaryTarget(true, true, boundaries[0].Boundary, out var muted));
        Assert.False(muted);
    }

    [Fact]
    public void Invalid_or_overlong_recovery_journals_are_rejected()
    {
        var now = DateTimeOffset.UtcNow;
        var sessionId = "session-instance";
        var valid = new AnnouncementRecoveryJournal(
            AnnouncementRecoveryJournal.CurrentVersion,
            Guid.NewGuid(),
            "{endpoint-id}",
            123,
            DateTime.UtcNow.Ticks,
            now,
            now + TimeSpan.FromSeconds(120),
            AnnouncementPriority.Critical,
            sessionId,
            RestoreEndpointMute: true,
            RestoreEndpointVolume: true,
            QuietHoursEnabled: true,
            QuietHoursForceUnmute: true,
            [new QuietHoursPeriod(79_200, 28_800)],
            now,
            EndpointMuted: false,
            EndpointVolume: 1f,
            [new AnnouncementJournalSession(sessionId, false, 1f, RestoreMute: true, RestoreVolume: true)]);

        Assert.True(AnnouncementLeasePolicy.IsValidJournal(valid, now));
        Assert.False(AnnouncementLeasePolicy.IsValidJournal(valid with { EndpointVolume = float.NaN }, now));
        Assert.False(AnnouncementLeasePolicy.IsValidJournal(valid with { DeadlineUtc = now + TimeSpan.FromSeconds(126) }, now));
        Assert.False(AnnouncementLeasePolicy.IsValidJournal(valid with
        {
            Sessions = [valid.Sessions[0], valid.Sessions[0] with { }]
        }, now));
    }
}
