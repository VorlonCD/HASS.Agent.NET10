namespace HASS.Agent.Companion.Announcements;

internal enum QuietHoursBoundary { Start, End }

internal readonly record struct QuietHoursPeriod(int StartSeconds, int EndSeconds)
{
    public bool IsValid => StartSeconds is >= 0 and < 86_400 && EndSeconds is >= 0 and < 86_400 && StartSeconds != EndSeconds;
}

internal readonly record struct QuietHoursBoundaryOccurrence(DateTimeOffset AtUtc, QuietHoursBoundary Boundary);

internal sealed record AnnouncementJournalSession(
    string InstanceId,
    bool Muted,
    float Volume,
    bool RestoreMute,
    bool RestoreVolume);

internal sealed record AnnouncementRecoveryJournal(
    int Version,
    Guid LeaseId,
    string EndpointId,
    int OwnerProcessId,
    long OwnerCreationTimeUtcTicks,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset DeadlineUtc,
    AnnouncementPriority Priority,
    string OwnSessionInstanceId,
    bool RestoreEndpointMute,
    bool RestoreEndpointVolume,
    bool QuietHoursEnabled,
    bool QuietHoursForceUnmute,
    IReadOnlyList<QuietHoursPeriod> QuietHoursPeriods,
    DateTimeOffset ObservedAtUtc,
    bool EndpointMuted,
    float EndpointVolume,
    IReadOnlyList<AnnouncementJournalSession> Sessions)
{
    public const int CurrentVersion = 1;
    public static readonly TimeSpan MaximumDeadline = TimeSpan.FromSeconds(120);
}

/// <summary>Pure policy used by the Windows controller and its recovery journal.</summary>
internal static class AnnouncementLeasePolicy
{
    public static bool IsPriority(AnnouncementPriority priority) => priority is AnnouncementPriority.Important or AnnouncementPriority.Critical;

    public static bool WasChangedOutsideLease(bool observedValue, bool lastValueWrittenByLease) => observedValue != lastValueWrittenByLease;

    public static bool WasChangedOutsideLease(float observedValue, float lastValueWrittenByLease, float epsilon = 0.0005f) =>
        MathF.Abs(observedValue - lastValueWrittenByLease) > epsilon;

    public static bool TryGetBoundaryTarget(bool enabled, bool forceUnmute, QuietHoursBoundary boundary, out bool muted)
    {
        muted = false;
        if (!enabled)
        {
            return false;
        }

        if (boundary == QuietHoursBoundary.Start)
        {
            muted = true;
            return true;
        }

        if (forceUnmute)
        {
            // Return an effect even when the target was already false. The 08:00 boundary
            // is meaningful to restoration even if no endpoint notification was raised.
            muted = false;
            return true;
        }

        return false;
    }

    public static IReadOnlyList<QuietHoursBoundaryOccurrence> FindBoundaries(
        DateTimeOffset fromUtc,
        DateTimeOffset throughUtc,
        IReadOnlyList<QuietHoursPeriod> periods,
        TimeZoneInfo localTimeZone)
    {
        if (throughUtc <= fromUtc || periods.Count == 0)
        {
            return [];
        }

        var fromLocal = TimeZoneInfo.ConvertTime(fromUtc, localTimeZone).Date.AddDays(-1);
        var throughLocal = TimeZoneInfo.ConvertTime(throughUtc, localTimeZone).Date.AddDays(1);
        var occurrences = new List<QuietHoursBoundaryOccurrence>();
        for (var day = fromLocal; day <= throughLocal; day = day.AddDays(1))
        {
            foreach (var period in periods)
            {
                if (!period.IsValid)
                {
                    continue;
                }

                var endDay = period.EndSeconds <= period.StartSeconds ? day.AddDays(1) : day;
                AddBoundary(day, period.StartSeconds, QuietHoursBoundary.Start);
                AddBoundary(endDay, period.EndSeconds, QuietHoursBoundary.End);
            }
        }

        return occurrences.OrderBy(item => item.AtUtc).ToArray();

        void AddBoundary(DateTime day, int seconds, QuietHoursBoundary boundary)
        {
            var local = DateTime.SpecifyKind(day.Date.AddSeconds(seconds), DateTimeKind.Unspecified);
            // A configured boundary can fall in the skipped hour on a DST transition day.
            // Move to the first representable local minute rather than dropping the boundary.
            while (localTimeZone.IsInvalidTime(local))
            {
                local = local.AddMinutes(1);
            }

            var atUtc = new DateTimeOffset(local, localTimeZone.GetUtcOffset(local)).ToUniversalTime();
            if (atUtc > fromUtc && atUtc <= throughUtc)
            {
                occurrences.Add(new QuietHoursBoundaryOccurrence(atUtc, boundary));
            }
        }
    }

    public static bool IsValidJournal(AnnouncementRecoveryJournal? journal, DateTimeOffset nowUtc)
    {
        if (journal is null
            || journal.Version != AnnouncementRecoveryJournal.CurrentVersion
            || journal.LeaseId == Guid.Empty
            || string.IsNullOrWhiteSpace(journal.EndpointId)
            || journal.EndpointId.Length > 2_048
            || journal.OwnerProcessId <= 0
            || journal.OwnerCreationTimeUtcTicks <= 0
            || journal.CreatedAtUtc == default
            || journal.ObservedAtUtc == default
            || journal.CreatedAtUtc > nowUtc + TimeSpan.FromSeconds(5)
            || journal.ObservedAtUtc < journal.CreatedAtUtc
            || journal.ObservedAtUtc > nowUtc + TimeSpan.FromSeconds(5)
            || journal.DeadlineUtc <= journal.CreatedAtUtc
            || journal.DeadlineUtc > journal.CreatedAtUtc + AnnouncementRecoveryJournal.MaximumDeadline
            || !Enum.IsDefined(journal.Priority)
            || string.IsNullOrWhiteSpace(journal.OwnSessionInstanceId)
            || journal.OwnSessionInstanceId.Length > 4_096
            || !IsValidVolume(journal.EndpointVolume)
            || journal.QuietHoursPeriods is null
            || journal.QuietHoursPeriods.Count > 64
            || journal.QuietHoursPeriods.Any(period => !period.IsValid)
            || journal.Sessions is null
            || journal.Sessions.Count is 0 or > 4_096)
        {
            return false;
        }

        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        var ownSessionFound = false;
        foreach (var session in journal.Sessions)
        {
            if (session is null
                || string.IsNullOrWhiteSpace(session.InstanceId)
                || session.InstanceId.Length > 4_096
                || !identifiers.Add(session.InstanceId)
                || !IsValidVolume(session.Volume))
            {
                return false;
            }

            ownSessionFound |= string.Equals(session.InstanceId, journal.OwnSessionInstanceId, StringComparison.Ordinal);
        }

        return ownSessionFound;
    }

    public static bool IsValidVolume(float value) => float.IsFinite(value) && value is >= 0f and <= 1f;
}
