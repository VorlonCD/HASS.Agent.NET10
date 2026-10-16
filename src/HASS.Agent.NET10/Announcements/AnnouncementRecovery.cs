using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using HASS.Agent.Companion.Logging;
using NAudio.CoreAudioApi;

namespace HASS.Agent.Companion.Announcements;

/// <summary>Private, one-lease recovery journal and the short-lived restore watchdog.</summary>
internal static class AnnouncementRecovery
{
    private const string CommandName = "--recover-announcement";
    private const byte ReadySignal = 1;
    private const byte DoneSignal = 2;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = false };

    internal static bool TryHandleCommandLine(string[] args)
    {
        if (args.Length == 0 || !string.Equals(args[0], CommandName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // A malformed helper invocation is consumed too; it must never continue into the UI.
        if (args.Length != 2 || !Guid.TryParseExact(args[1], "D", out var leaseId) || leaseId == Guid.Empty)
        {
            return true;
        }

        try
        {
            RunWatchdogAsync(leaseId).GetAwaiter().GetResult();
        }
        catch
        {
            // The owner still has its in-process recovery path. A watchdog must fail closed
            // and never fall through to the normal application startup sequence.
        }

        return true;
    }

    internal static async Task<AnnouncementRecoveryWatchdog> StartWatchdogAsync(
        AnnouncementRecoveryJournal journal,
        FileLog log,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || !AnnouncementLeasePolicy.IsValidJournal(journal, DateTimeOffset.UtcNow))
        {
            throw new InvalidOperationException("The announcement recovery state is invalid.");
        }

        var pipe = new NamedPipeServerStream(
            PipeName(journal.LeaseId),
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        Process? process = null;
        try
        {
            Persist(journal);
            process = StartHelper(journal.LeaseId);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(HandshakeTimeout);
            await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);

            var ready = new byte[1];
            var read = await pipe.ReadAsync(ready, timeout.Token).ConfigureAwait(false);
            if (read != 1 || ready[0] != ReadySignal)
            {
                throw new InvalidOperationException("The announcement recovery helper did not become ready.");
            }

            log.Info("Announcement recovery watchdog is ready.");
            return new AnnouncementRecoveryWatchdog(journal.LeaseId, pipe, process, log);
        }
        catch
        {
            pipe.Dispose();
            if (process is not null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // The journal remains as a fail-safe if a helper could not be stopped.
                }

                process.Dispose();
            }

            Delete(journal.LeaseId);
            throw;
        }
    }

    internal static void Persist(AnnouncementRecoveryJournal journal)
    {
        if (!OperatingSystem.IsWindows() || !AnnouncementLeasePolicy.IsValidJournal(journal, DateTimeOffset.UtcNow))
        {
            throw new InvalidDataException("The announcement recovery state is invalid.");
        }
        if (DateTimeOffset.UtcNow >= journal.DeadlineUtc)
            throw new TimeoutException("The announcement recovery lease has expired.");

        var directory = EnsurePrivateDirectory();
        var path = JournalPath(directory, journal.LeaseId);
        var temporary = Path.Combine(directory, $"{journal.LeaseId:N}.{Guid.NewGuid():N}.tmp");
        try
        {
            var content = JsonSerializer.SerializeToUtf8Bytes(journal, JsonOptions);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            // The helper (or an AV scanner) can briefly hold the current journal
            // while this atomic replacement is attempted. Never mutate audio on a
            // stale journal: retry the move for a bounded interval, then fail closed.
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    if (DateTimeOffset.UtcNow >= journal.DeadlineUtc)
                        throw new TimeoutException("The announcement recovery lease has expired.");
                    File.Move(temporary, path, overwrite: true);
                    break;
                }
                catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 6)
                {
                    Thread.Sleep(25 * (attempt + 1));
                }
            }
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch
            {
                // The private directory will be cleaned up with the journal.
            }
        }
    }

    internal static AnnouncementRecoveryJournal? ReadValid(Guid leaseId)
    {
        if (!OperatingSystem.IsWindows() || leaseId == Guid.Empty)
        {
            return null;
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var path = JournalPath(EnsurePrivateDirectory(), leaseId);
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
                var journal = JsonSerializer.Deserialize<AnnouncementRecoveryJournal>(stream, JsonOptions);
                return journal is { } value
                    && value.LeaseId == leaseId
                    && AnnouncementLeasePolicy.IsValidJournal(value, DateTimeOffset.UtcNow)
                        ? value
                        : null;
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 2)
            {
                Thread.Sleep(25 * (attempt + 1));
            }
            catch { return null; }
        }

        return null;
    }

    internal static bool Delete(Guid leaseId)
    {
        if (!OperatingSystem.IsWindows() || leaseId == Guid.Empty)
        {
            return false;
        }

        try
        {
            var path = JournalPath(EnsurePrivateDirectory(), leaseId);
            File.Delete(path);
            return !File.Exists(path);
        }
        catch
        {
            // A retained journal is safer than claiming recovery succeeded.
            return false;
        }
    }

    private static async Task RunWatchdogAsync(Guid leaseId)
    {
        var initial = ReadValid(leaseId);
        if (initial is null)
        {
            return;
        }

        using var pipe = new NamedPipeClientStream(
            ".",
            PipeName(leaseId),
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var handshakeTimeout = new CancellationTokenSource(HandshakeTimeout);
        await pipe.ConnectAsync(handshakeTimeout.Token).ConfigureAwait(false);
        await pipe.WriteAsync(new[] { ReadySignal }, handshakeTimeout.Token).ConfigureAwait(false);
        await pipe.FlushAsync(handshakeTimeout.Token).ConfigureAwait(false);

        var ownerWait = WaitForOwnerExitAsync(initial.OwnerProcessId, initial.OwnerCreationTimeUtcTicks);
        Task<int>? signalWait = ReadSignalAsync(pipe, CancellationToken.None);
        while (true)
        {
            var latest = ReadValid(leaseId);
            if (latest is null)
            {
                return;
            }

            var remaining = latest.DeadlineUtc - DateTimeOffset.UtcNow;
            var deadlineWait = Task.Delay(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
            var completed = signalWait is null
                ? await Task.WhenAny(ownerWait, deadlineWait).ConfigureAwait(false)
                : await Task.WhenAny(ownerWait, deadlineWait, signalWait).ConfigureAwait(false);

            if (completed == signalWait && signalWait is not null)
            {
                var signal = await signalWait.ConfigureAwait(false);
                if (signal == 0)
                {
                    // The owner closed the pipe without a completed-restore signal.
                    // It has relinquished this lease, so recover immediately.
                    await RestoreCurrentJournalAsync(leaseId).ConfigureAwait(false);
                    return;
                }

                signalWait = ReadSignalAsync(pipe, CancellationToken.None);
                if (signal == DoneSignal && ReadValid(leaseId) is null)
                {
                    return;
                }

                continue;
            }

            if (completed == ownerWait || completed == deadlineWait)
            {
                await RestoreCurrentJournalAsync(leaseId).ConfigureAwait(false);
                return;
            }
        }
    }

    private static async Task RestoreCurrentJournalAsync(Guid leaseId)
    {
        var journal = ReadValid(leaseId);
        if (journal is null)
        {
            return;
        }

        var restored = RestoreCapturedState(journal);
        if (restored)
        {
            Delete(leaseId);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static bool RestoreCapturedState(AnnouncementRecoveryJournal journal)
    {
        var succeeded = true;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDevice(journal.EndpointId);
            using var endpointVolume = device.AudioEndpointVolume;

            var restoreMute = journal.EndpointMuted;
            foreach (var boundary in AnnouncementLeasePolicy.FindBoundaries(
                journal.ObservedAtUtc, DateTimeOffset.UtcNow, journal.QuietHoursPeriods, TimeZoneInfo.Local))
            {
                if (AnnouncementLeasePolicy.TryGetBoundaryTarget(
                    journal.QuietHoursEnabled, journal.QuietHoursForceUnmute, boundary.Boundary, out var boundaryMute))
                    restoreMute = boundaryMute;
            }

            // A hung owner may still be rendering. Silence the captured endpoint first,
            // then mute its private session before restoring any app or endpoint setting.
            if (journal.RestoreEndpointMute)
            {
                endpointVolume.Mute = true;
            }

            var manager = device.AudioSessionManager;
            var byId = new Dictionary<string, AudioSessionControl>(StringComparer.Ordinal);
            try
            {
                var sessions = manager.Sessions;
                for (var index = 0; index < sessions.Count; index++)
                {
                    var session = sessions[index];
                    try
                    {
                        var instanceId = WindowsAnnouncementAudioController.ReadSessionInstanceIdentifier(session);
                        if (byId.TryGetValue(instanceId, out var previous)) previous.Dispose();
                        byId[instanceId] = session;
                    }
                    catch
                    {
                        session.Dispose();
                    }
                }

                // The owner may still be hung at its deadline. Silence its private
                // playback session before any other application's mute is restored.
                if (byId.TryGetValue(journal.OwnSessionInstanceId, out var own))
                {
                    try { own.SimpleAudioVolume.Mute = true; }
                    catch { succeeded = false; }
                }

                if (journal.RestoreEndpointVolume)
                    endpointVolume.MasterVolumeLevelScalar = journal.EndpointVolume;

                foreach (var saved in journal.Sessions)
                {
                    if (saved.InstanceId == journal.OwnSessionInstanceId
                        || !byId.TryGetValue(saved.InstanceId, out var session))
                        continue;
                    try
                    {
                        if (saved.RestoreVolume)
                            session.SimpleAudioVolume.Volume = saved.Volume;
                        if (saved.RestoreMute)
                            session.SimpleAudioVolume.Mute = saved.Muted;
                    }
                    catch
                    {
                        succeeded = false;
                    }
                }
            }
            finally
            {
                foreach (var session in byId.Values)
                {
                    session.Dispose();
                }
                manager.Dispose();
            }

            if (journal.RestoreEndpointMute)
            {
                endpointVolume.Mute = restoreMute;
            }
        }
        catch
        {
            // In particular, never fall back to the new default endpoint if this one vanished.
            succeeded = false;
        }

        return succeeded;
    }

    private static async Task WaitForOwnerExitAsync(int processId, long creationTimeUtcTicks)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(processId);
            if (process.StartTime.ToUniversalTime().Ticks != creationTimeUtcTicks)
            {
                return;
            }

            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch
        {
            // A missing or replaced process is already gone for this journal.
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static async Task<int> ReadSignalAsync(NamedPipeClientStream pipe, CancellationToken cancellationToken)
    {
        var value = new byte[1];
        try
        {
            var count = await pipe.ReadAsync(value, cancellationToken).ConfigureAwait(false);
            return count == 1 ? value[0] : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static Process StartHelper(Guid leaseId)
    {
        var executable = Environment.ProcessPath ?? Application.ExecutablePath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            throw new InvalidOperationException("The announcement recovery helper could not find the application executable.");
        }

        var start = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var assemblyPath = Path.Combine(AppContext.BaseDirectory,
                typeof(AnnouncementRecovery).Assembly.GetName().Name + ".dll");
            if (!File.Exists(assemblyPath))
            {
                throw new InvalidOperationException("The announcement recovery helper could not find the application assembly.");
            }

            start.ArgumentList.Add(assemblyPath);
        }

        start.ArgumentList.Add(CommandName);
        start.ArgumentList.Add(leaseId.ToString("D"));
        return Process.Start(start) ?? throw new InvalidOperationException("The announcement recovery helper did not start.");
    }

    private static string EnsurePrivateDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new IOException("The private recovery directory is unavailable.");
        }

        var directory = Path.Combine(localAppData, "HASS.Agent.NET10", "AnnouncementRecovery");
        Directory.CreateDirectory(directory);
        var currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new UnauthorizedAccessException("The current user identity is unavailable.");
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        const InheritanceFlags inherited = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(currentUser);
        security.AddAccessRule(new FileSystemAccessRule(currentUser, FileSystemRights.FullControl, inherited, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, inherited, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(security);
        return directory;
    }

    private static string JournalPath(string directory, Guid leaseId) => Path.Combine(directory, $"{leaseId:D}.json");

    private static string PipeName(Guid leaseId) => $"HASSAgentAnnouncementRecovery-{leaseId:N}";

    internal sealed class AnnouncementRecoveryWatchdog : IAsyncDisposable
    {
        private readonly Guid _leaseId;
        private readonly NamedPipeServerStream _pipe;
        private Process? _process;
        private readonly FileLog _log;
        private int _completed;

        internal AnnouncementRecoveryWatchdog(Guid leaseId, NamedPipeServerStream pipe, Process process, FileLog log)
        {
            _leaseId = leaseId;
            _pipe = pipe;
            _process = process;
            _log = log;
        }

        public async Task CompleteAfterRestoreAsync()
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
            {
                return;
            }

            if (!Delete(_leaseId))
            {
                // The state was restored by the owner, so a stale journal must not
                // trigger another restore at the deadline. Stop this helper even if
                // a transient file lock prevented removing the journal.
                try
                {
                    if (_process is { HasExited: false })
                        _process.Kill(entireProcessTree: true);
                }
                catch { }
                await DisposeAsync().ConfigureAwait(false);
                throw new IOException("The announcement recovery journal could not be removed.");
            }
            try
            {
                await _pipe.WriteAsync(new[] { DoneSignal }).ConfigureAwait(false);
                await _pipe.FlushAsync().ConfigureAwait(false);
            }
            catch
            {
                // The helper also sees that the owner has exited and the journal is gone.
            }

            await DisposeAsync().ConfigureAwait(false);
        }

        public void LeaveForRecovery()
        {
            if (Interlocked.Exchange(ref _completed, 1) == 0)
            {
                _log.Warning("Announcement restoration was incomplete; the recovery watchdog remains active.");
                _pipe.Dispose();
                _process?.Dispose();
                _process = null;
            }
        }

        public ValueTask DisposeAsync()
        {
            _pipe.Dispose();
            _process?.Dispose();
            _process = null;
            return ValueTask.CompletedTask;
        }
    }
}
