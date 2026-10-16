using System.Diagnostics;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using HASS.Agent.Companion.Logging;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace HASS.Agent.Companion.Announcements;

/// <summary>Owns one bounded announcement on the captured multimedia render endpoint.</summary>
internal sealed class WindowsAnnouncementAudioController : IAnnouncementAudioController, IDisposable
{
    private readonly FileLog _log;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private Lease? _active;
    private bool _disposed;

    internal WindowsAnnouncementAudioController(FileLog log, string? journalRoot = null)
    {
        _log = log;
        // The recovery helper is a separate invocation of this executable and must use
        // the same protected, fixed journal location. The optional path is reserved for
        // tests and is deliberately rejected for production leases.
        if (journalRoot is not null)
            throw new ArgumentException("A custom recovery location is not supported.", nameof(journalRoot));
    }

    public bool? GetEffectiveMuted()
    {
        if (_active is { } lease)
            return lease.DesiredEndpointMute;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            using var volume = device.AudioEndpointVolume;
            return volume.Mute;
        }
        catch (Exception ex)
        {
            _log.Warning($"Unable to read announcement output mute: {ex.Message}");
            return null;
        }
    }

    public async Task<IAnnouncementAudioLease> AcquireAsync(AnnouncementPriority priority, CancellationToken cancellationToken)
    {
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        Lease? lease = null;
        try
        {
            if (_disposed) throw new ObjectDisposedException(nameof(WindowsAnnouncementAudioController));
            lease = new Lease(this, priority, _log);
            await lease.StartAsync(cancellationToken).ConfigureAwait(false);
            _active = lease;
            return lease;
        }
        catch
        {
            if (lease is not null) await lease.DisposeAsync().ConfigureAwait(false);
            else _serial.Release();
            throw;
        }
    }

    public void Dispose() => _disposed = true;

    internal static string ReadSessionInstanceIdentifier(AudioSessionControl session) =>
        session.GetSessionInstanceIdentifier;

    private sealed class Lease : IAnnouncementAudioLease
    {
        private readonly WindowsAnnouncementAudioController _owner;
        private readonly AnnouncementPriority _priority;
        private readonly FileLog _log;
        private readonly object _gate = new();
        private readonly Guid _sessionGuid = Guid.NewGuid();
        private readonly Guid _endpointEventContext = Guid.NewGuid();
        private readonly CancellationTokenSource _expired = new();
        private readonly Dictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);
        private readonly ConcurrentQueue<IAudioSessionControl> _pendingSessions = new();
        private readonly DateTimeOffset _created = DateTimeOffset.UtcNow;
        private readonly DateTimeOffset _deadline;
        private MMDeviceEnumerator? _enumerator;
        private MMDevice? _device;
        private AudioEndpointVolume? _endpoint;
        private AudioSessionManager? _manager;
        private AudioClient? _client;
        private AudioRenderClient? _render;
        private AnnouncementRecovery.AnnouncementRecoveryWatchdog? _watchdog;
        private System.Threading.Timer? _monitor;
        private AnnouncementRecoveryJournal? _journal;
        private string? _ownId;
        private bool _desiredMute;
        private float _desiredVolume;
        private bool _lastWrittenMute;
        private float _lastWrittenVolume;
        private bool _started;
        private bool _disposed;

        public bool DesiredEndpointMute { get { lock (_gate) return _desiredMute; } }

        internal Lease(WindowsAnnouncementAudioController owner, AnnouncementPriority priority, FileLog log)
        {
            _owner = owner;
            _priority = priority;
            _log = log;
            _deadline = _created + AnnouncementRecoveryJournal.MaximumDeadline;
        }

        internal async Task StartAsync(CancellationToken cancellationToken)
        {
            _enumerator = new MMDeviceEnumerator();
            _device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _endpoint = _device.AudioEndpointVolume;
            _manager = _device.AudioSessionManager;
            _client = _device.AudioClient;
            // Initialize creates a uniquely identified session while the endpoint is still
            // untouched. We require a verified instance ID before enabling any override.
            _client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.None,
                2_000_000, 0, _client.MixFormat, _sessionGuid);
            _render = _client.AudioRenderClient;
            _ownId = FindOwnSessionId();
            using (var own = FindSession(_ownId!)
                ?? throw new InvalidOperationException("The announcement audio session disappeared during capture."))
            {
                _sessions.Add(_ownId!, new SessionState(
                    _ownId!, own.SimpleAudioVolume.Mute, own.SimpleAudioVolume.Volume));
            }
            _desiredMute = _endpoint.Mute;
            _desiredVolume = _endpoint.MasterVolumeLevelScalar;
            _lastWrittenMute = _desiredMute;
            _lastWrittenVolume = _desiredVolume;

            var quiet = ReadQuietHours();
            using var process = Process.GetCurrentProcess();
            _journal = new AnnouncementRecoveryJournal(
                AnnouncementRecoveryJournal.CurrentVersion, Guid.NewGuid(), _device.ID,
                process.Id, process.StartTime.ToUniversalTime().Ticks, _created, _deadline,
                _priority, _ownId!, true, true, quiet.Enabled, quiet.ForceUnmute,
                quiet.Periods, DateTimeOffset.UtcNow, _desiredMute, _desiredVolume,
                [new AnnouncementJournalSession(_ownId!, _sessions[_ownId!].DesiredMute,
                    _sessions[_ownId!].DesiredVolume, false, false)]);
            // Register before enumeration so a newly created app session is included.
            _manager.OnSessionCreated += SessionCreated;
            _endpoint.NotificationGuid = _endpointEventContext;
            _endpoint.OnVolumeNotification += EndpointChanged;
            lock (_gate)
            {
                // The subscription is active now. This second read closes the gap
                // between the placeholder journal and notification registration.
                ApplyQuietBoundaries();
                _desiredMute = _endpoint.Mute;
                _desiredVolume = _endpoint.MasterVolumeLevelScalar;
                _lastWrittenMute = _desiredMute;
                _lastWrittenVolume = _desiredVolume;
                if (_priority == AnnouncementPriority.Normal && _desiredMute)
                    throw new AnnouncementSuppressedException();
                PersistDesired();
                ReconcileSessions();
                DrainPendingSessions();
            }
            _watchdog = await AnnouncementRecovery.StartWatchdogAsync(_journal, _log, cancellationToken)
                .ConfigureAwait(false);
            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _expired.Token.ThrowIfCancellationRequested();
                CheckDeadline();
                // A session-created callback can race the helper's initial write.
                // Commit the complete current snapshot before the first mute.
                ApplyQuietBoundaries();
                PersistDesired();
                _started = true;
                ReconcileSessions();
                ApplyOverride();
                VerifyOverride();
                _expired.Token.ThrowIfCancellationRequested();
            }
            _monitor = new System.Threading.Timer(_ => Monitor(), null, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));
        }

        private string FindOwnSessionId()
        {
            var matches = new List<string>();
            _manager!.RefreshSessions();
            var sessions = _manager.Sessions;
            for (var i = 0; i < sessions.Count; i++)
            {
                using var session = sessions[i];
                var id = ReadSessionInstanceIdentifier(session);
                var logicalId = session.GetSessionIdentifier;
                if ((id.Contains(_sessionGuid.ToString("D"), StringComparison.OrdinalIgnoreCase) ||
                     logicalId.Contains(_sessionGuid.ToString("D"), StringComparison.OrdinalIgnoreCase)) &&
                    session.GetProcessID == (uint)Environment.ProcessId)
                    matches.Add(id);
            }
            if (matches.Count != 1)
                throw new InvalidOperationException("The dedicated announcement audio session could not be verified.");
            return matches[0];
        }

        private void SessionCreated(object sender, IAudioSessionControl raw)
        {
            // Queue before taking _gate. Startup may hold it across enumeration;
            // ApplyOverride drains the queue before it unmutes the endpoint.
            _pendingSessions.Enqueue(raw);
            try
            {
                lock (_gate)
                {
                    if (_disposed || _journal is null) { DrainPendingSessions(capture: false); return; }
                    CheckDeadline();
                    DrainPendingSessions();
                }
            }
            catch (Exception ex) { _log.Warning($"Announcement session capture failed: {ex.Message}"); CancelPlayback(); }
        }

        private void EndpointChanged(AudioVolumeNotificationData data)
        {
            if (data.EventContext == _endpointEventContext) return;
            try
            {
                lock (_gate)
                {
                    if (_disposed || _journal is null) return;
                    CheckDeadline();
                    ApplyQuietBoundaries();
                    _desiredMute = data.Muted;
                    _desiredVolume = data.MasterVolume;
                    PersistDesired();
                    if (_started) ApplyOverride();
                }
            }
            catch (Exception ex) { _log.Warning($"Announcement endpoint update failed: {ex.Message}"); CancelPlayback(); }
        }

        private void CaptureSession(AudioSessionControl session)
        {
            CheckDeadline();
            var id = ReadSessionInstanceIdentifier(session);
            if (string.IsNullOrWhiteSpace(id) || _sessions.ContainsKey(id)) return;
            var simple = session.SimpleAudioVolume;
            var state = new SessionState(id, simple.Mute, simple.Volume);
            _sessions.Add(id, state);
            PersistDesired(); // durable before muting even a newly created session
            if (id == _ownId) return;
            if (AnnouncementLeasePolicy.IsPriority(_priority) && _started)
            {
                CheckDeadline();
                simple.Mute = true;
                state.WroteMute = true;
            }
        }

        private void DrainPendingSessions(bool capture = true)
        {
            while (_pendingSessions.TryDequeue(out var raw))
            {
                using var session = new AudioSessionControl(raw);
                if (capture) CaptureSession(session);
            }
        }

        private void ReconcileSessions()
        {
            CheckDeadline();
            _manager!.RefreshSessions();
            var collection = _manager.Sessions;
            for (var i = 0; i < collection.Count; i++)
            {
                using var session = collection[i];
                var id = ReadSessionInstanceIdentifier(session);
                CaptureSession(session);
                if (_started && id != _ownId && _sessions.TryGetValue(id, out var state)
                    && AnnouncementLeasePolicy.IsPriority(_priority))
                {
                    CheckDeadline();
                    var simple = session.SimpleAudioVolume;
                    if (!simple.Mute) { state.DesiredMute = false; PersistDesired(); simple.Mute = true; state.WroteMute = true; }
                    if (AnnouncementLeasePolicy.WasChangedOutsideLease(simple.Volume, state.DesiredVolume))
                    { state.DesiredVolume = simple.Volume; PersistDesired(); }
                }
            }
        }

        private void ApplyOverride()
        {
            CheckDeadline();
            var own = FindSession(_ownId!);
            if (own is null) throw new InvalidOperationException("The announcement session disappeared.");
            using (own)
            {
                CheckDeadline();
                if (own.SimpleAudioVolume.Mute) own.SimpleAudioVolume.Mute = false;
                CheckDeadline();
                if (own.SimpleAudioVolume.Volume != 1f) own.SimpleAudioVolume.Volume = 1f;
            }
            if (AnnouncementLeasePolicy.IsPriority(_priority))
            {
                foreach (var state in _sessions.Values.Where(s => s.Id != _ownId))
                {
                    CheckDeadline();
                    using var session = FindSession(state.Id);
                    if (session is null) continue;
                    if (!session.SimpleAudioVolume.Mute)
                    { state.DesiredMute = false; PersistDesired(); session.SimpleAudioVolume.Mute = true; state.WroteMute = true; }
                }
                DrainPendingSessions();
            }
            else if (_desiredMute) throw new AnnouncementSuppressedException();
            CheckDeadline();
            if (Math.Abs(_endpoint!.MasterVolumeLevelScalar - 1f) > .0005f)
                _endpoint.MasterVolumeLevelScalar = 1f;
            _lastWrittenVolume = 1f;
            if (AnnouncementLeasePolicy.IsPriority(_priority))
            {
                CheckDeadline();
                if (_endpoint.Mute) _endpoint.Mute = false;
                _lastWrittenMute = false;
            }
        }

        private void VerifyOverride()
        {
            DrainPendingSessions();
            if (_endpoint!.Mute || Math.Abs(_endpoint.MasterVolumeLevelScalar - 1f) > .001f)
                throw new InvalidOperationException("The announcement endpoint override could not be verified.");
            using var own = FindSession(_ownId!);
            if (own is null || own.SimpleAudioVolume.Mute || Math.Abs(own.SimpleAudioVolume.Volume - 1f) > .001f)
                throw new InvalidOperationException("The announcement session override could not be verified.");
            if (!AnnouncementLeasePolicy.IsPriority(_priority)) return;
            foreach (var state in _sessions.Values.Where(s => s.Id != _ownId))
            {
                using var session = FindSession(state.Id);
                if (session is not null && !session.SimpleAudioVolume.Mute)
                    throw new InvalidOperationException("Another audio session was not muted.");
            }
        }

        private AudioSessionControl? FindSession(string id)
        {
            var collection = _manager!.Sessions;
            for (var i = 0; i < collection.Count; i++)
            {
                var session = collection[i];
                if (ReadSessionInstanceIdentifier(session) == id) return session;
                session.Dispose();
            }
            return null;
        }

        private void Monitor()
        {
            try
            {
                lock (_gate)
                {
                    if (_disposed || !_started) return;
                    CheckDeadline();
                    using (var currentDefault = _enumerator!.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
                    {
                        if (!string.Equals(currentDefault.ID, _device!.ID, StringComparison.Ordinal))
                            throw new InvalidOperationException("The default multimedia output changed during announcement playback.");
                    }
                    ApplyQuietBoundaries();
                    var observedMute = _endpoint!.Mute;
                    var observedVolume = _endpoint.MasterVolumeLevelScalar;
                    if (observedMute != _lastWrittenMute || AnnouncementLeasePolicy.WasChangedOutsideLease(observedVolume, _lastWrittenVolume))
                    { _desiredMute = observedMute; _desiredVolume = observedVolume; PersistDesired(); }
                    ReconcileSessions();
                    ApplyOverride();
                    VerifyOverride();
                }
            }
            catch (Exception ex)
            {
                _log.Warning($"Announcement audio enforcement stopped: {ex.Message}");
                CancelPlayback();
                if (DateTimeOffset.UtcNow >= _deadline)
                    lock (_gate) _started = false;
            }
        }

        private void CancelPlayback()
        {
            try { _expired.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        private void CheckDeadline()
        {
            if (DateTimeOffset.UtcNow >= _deadline) { _expired.Cancel(); throw new TimeoutException("Announcement audio lease expired."); }
        }

        private void ApplyQuietBoundaries()
        {
            var journal = _journal;
            if (journal is null) return;
            var now = DateTimeOffset.UtcNow;
            foreach (var boundary in AnnouncementLeasePolicy.FindBoundaries(journal.ObservedAtUtc, now, journal.QuietHoursPeriods, TimeZoneInfo.Local))
                if (AnnouncementLeasePolicy.TryGetBoundaryTarget(journal.QuietHoursEnabled, journal.QuietHoursForceUnmute, boundary.Boundary, out var muted))
                    _desiredMute = muted;
            _journal = journal with { ObservedAtUtc = now };
        }

        private void PersistDesired()
        {
            if (_journal is null) return;
            CheckDeadline();
            // Any journal write advances ObservedAtUtc. Account for a scheduled
            // boundary first, including writes triggered by a new app session.
            ApplyQuietBoundaries();
            _journal = _journal with
            {
                ObservedAtUtc = DateTimeOffset.UtcNow,
                EndpointMuted = _desiredMute,
                EndpointVolume = _desiredVolume,
                Sessions = _sessions.Values.Select(s => new AnnouncementJournalSession(s.Id, s.DesiredMute,
                    s.DesiredVolume, AnnouncementLeasePolicy.IsPriority(_priority) && s.Id != _ownId, false)).ToArray()
            };
            AnnouncementRecovery.Persist(_journal);
        }

        public async Task PlayAsync(string filePath, CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _expired.Token);
            lock (_gate) { CheckDeadline(); VerifyOverride(); }
            using var reader = new MediaFoundationReader(filePath);
            using var resampler = new MediaFoundationResampler(reader, _client!.MixFormat);
            var blockAlign = _client.MixFormat.BlockAlign;
            var buffer = new byte[_client.BufferSize * blockAlign];
            var started = false;
            try
            {
                while (true)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    int available;
                    lock (_gate) { CheckDeadline(); available = _client.BufferSize - _client.CurrentPadding; }
                    if (available > 0)
                    {
                        var read = resampler.Read(buffer, 0, available * blockAlign);
                        var frames = read / blockAlign;
                        if (frames == 0) break;
                        lock (_gate)
                        {
                            linked.Token.ThrowIfCancellationRequested();
                            CheckDeadline();
                            var pointer = _render!.GetBuffer(frames);
                            Marshal.Copy(buffer, 0, pointer, frames * blockAlign);
                            _render.ReleaseBuffer(frames, AudioClientBufferFlags.None);
                            if (!started) { _client.Start(); started = true; }
                        }
                    }
                    else await Task.Delay(10, linked.Token).ConfigureAwait(false);
                }
                // Completion is based on WASAPI buffer drain, rather than media duration.
                while (true)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    lock (_gate) { CheckDeadline(); if (_client.CurrentPadding == 0) break; }
                    await Task.Delay(10, linked.Token).ConfigureAwait(false);
                }
            }
            finally
            {
                lock (_gate) { if (started) { _client.Stop(); _client.Reset(); } }
            }
        }

        public async ValueTask DisposeAsync()
        {
            lock (_gate) { if (_disposed) return; _disposed = true; _monitor?.Dispose(); }
            bool restored = false;
            try
            {
                lock (_gate)
                {
                    if (_journal is not null && _watchdog is not null && DateTimeOffset.UtcNow < _deadline)
                    {
                        // Capture the last external mixer changes before restoration.
                        ApplyQuietBoundaries();
                        ReconcileSessions();
                        if (_endpoint!.Mute != _lastWrittenMute) _desiredMute = _endpoint.Mute;
                        if (AnnouncementLeasePolicy.WasChangedOutsideLease(_endpoint.MasterVolumeLevelScalar, _lastWrittenVolume))
                            _desiredVolume = _endpoint.MasterVolumeLevelScalar;
                        PersistDesired();
                        if (_desiredMute) _endpoint.Mute = true;
                        using (var own = FindSession(_ownId!)) { if (own is not null) own.SimpleAudioVolume.Mute = true; }
                        foreach (var state in _sessions.Values.Where(s =>
                            AnnouncementLeasePolicy.IsPriority(_priority) && s.Id != _ownId))
                        {
                            CheckDeadline();
                            using var session = FindSession(state.Id);
                            if (session is null) continue;
                            session.SimpleAudioVolume.Mute = state.DesiredMute;
                        }
                        CheckDeadline();
                        _endpoint.MasterVolumeLevelScalar = _desiredVolume;
                        CheckDeadline();
                        _endpoint.Mute = _desiredMute;
                        restored = true;
                    }
                }
                if (restored) await _watchdog!.CompleteAfterRestoreAsync().ConfigureAwait(false);
                else if (_watchdog is not null)
                {
                    _watchdog.LeaveForRecovery();
                    throw new InvalidOperationException("Announcement audio restoration did not complete.");
                }
            }
            catch (Exception ex)
            {
                _log.Warning($"Announcement audio restoration failed: {ex.Message}");
                _watchdog?.LeaveForRecovery();
                throw;
            }
            finally
            {
                try { if (_manager is not null) _manager.OnSessionCreated -= SessionCreated; } catch { }
                try { if (_endpoint is not null) _endpoint.OnVolumeNotification -= EndpointChanged; } catch { }
                try { _render?.Dispose(); } catch { }
                try { _client?.Dispose(); } catch { }
                try { _manager?.Dispose(); } catch { }
                try { _endpoint?.Dispose(); } catch { }
                try { _device?.Dispose(); } catch { }
                try { _enumerator?.Dispose(); } catch { }
                _expired.Dispose();
                _owner._active = null;
                _owner._serial.Release();
            }
        }

        private static (bool Enabled, bool ForceUnmute, QuietHoursPeriod[] Periods) ReadQuietHours()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\lx-systems\WinMute", writable: false);
                var enabled = Convert.ToInt32(key?.GetValue("QuietHoursEnabled") ?? 0) == 1;
                var force = Convert.ToInt32(key?.GetValue("QuietHoursForceUnmute") ?? 0) == 1;
                using var times = key?.OpenSubKey("QuietHourTimes", writable: false);
                var start = Convert.ToInt32(times?.GetValue("Start0") ?? -1);
                var end = Convert.ToInt32(times?.GetValue("End0") ?? -1);
                var period = new QuietHoursPeriod(start, end);
                return (enabled, force, period.IsValid ? [period] : []);
            }
            catch { return (false, false, []); }
        }

        private sealed class SessionState(string id, bool muted, float volume)
        {
            public string Id { get; } = id;
            public bool DesiredMute { get; set; } = muted;
            public float DesiredVolume { get; set; } = volume;
            public bool WroteMute { get; set; }
        }
    }
}
