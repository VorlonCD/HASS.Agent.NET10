namespace HASS.Agent.Companion.Announcements;

/// <summary>Serializes announcement preparation and playback while keeping a bounded priority queue.</summary>
internal sealed class AnnouncementCoordinator : IAsyncDisposable
{
    private const int MaximumWaitingRequests = 10;
    private const int RememberedRequestIds = 512;
    private static readonly TimeSpan MaximumRequestLifetime = TimeSpan.FromMinutes(2);

    private readonly object _gate = new();
    private readonly IAnnouncementMediaPreparer _mediaPreparer;
    private readonly IAnnouncementAudioController _audioController;
    private readonly Func<AnnouncementResult, Task> _resultCallback;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<PendingAnnouncement> _waiting = [];
    private readonly HashSet<string> _activeIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _completedIds = new(StringComparer.Ordinal);
    private readonly Queue<string> _completedIdOrder = new();

    private PendingAnnouncement? _current;
    private CancellationTokenSource? _currentManualCancellation;
    private Task? _workerTask;
    private TaskCompletionSource<bool>? _callbacksDrainedSource;
    private Task _callbacksDrained = Task.CompletedTask;
    private long _sequence;
    private int _pendingCallbacks;
    private bool _workerRunning;
    private bool _disposed;
    private bool _shutdownDisposed;

    public AnnouncementCoordinator(
        IAnnouncementMediaPreparer mediaPreparer,
        IAnnouncementAudioController audioController,
        Func<AnnouncementResult, Task> resultCallback,
        TimeProvider? timeProvider = null)
    {
        _mediaPreparer = mediaPreparer ?? throw new ArgumentNullException(nameof(mediaPreparer));
        _audioController = audioController ?? throw new ArgumentNullException(nameof(audioController));
        _resultCallback = resultCallback ?? throw new ArgumentNullException(nameof(resultCallback));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Returns false for duplicates, invalid identifiers, a full waiting queue, or a disposed coordinator.
    /// A new request that is immediately skipped, expired, or rejected by a full queue still gets one result callback.
    /// </summary>
    public bool Submit(AnnouncementRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Guid.TryParseExact(request.RequestId, "D", out var parsedId))
            return false;

        var requestKey = parsedId.ToString("D");
        var receiptTime = _timeProvider.GetUtcNow();
        var receiptDeadline = receiptTime + MaximumRequestLifetime;
        var deadline = request.ExpiresAt <= receiptDeadline ? request.ExpiresAt : receiptDeadline;
        var pending = new PendingAnnouncement(request, requestKey, deadline, Interlocked.Increment(ref _sequence));

        AnnouncementResult? immediateResult = null;
        if (deadline <= receiptTime)
        {
            immediateResult = Failed(request, "expired");
        }
        else if (request.Priority == AnnouncementPriority.Normal && TryGetNormalMuteResult(request) is { } muteResult)
        {
            immediateResult = muteResult;
        }

        lock (_gate)
        {
            if (_disposed || _activeIds.Contains(requestKey) || _completedIds.Contains(requestKey))
                return false;

            _activeIds.Add(requestKey);
            if (immediateResult is not null)
            {
                CompleteLocked(pending, immediateResult);
                return true;
            }

            if (_waiting.Count >= MaximumWaitingRequests)
            {
                CompleteLocked(pending, Failed(request, "queue_full"));
                return false;
            }

            _waiting.Add(pending);
            if (!_workerRunning)
                StartWorkerLocked();

            return true;
        }
    }

    /// <summary>Requests cancellation of the current request, without touching queued requests.</summary>
    public bool CancelCurrent()
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            if (_current is null || _currentManualCancellation is null)
                return false;

            cancellation = _currentManualCancellation;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The worker completed between observing the current request and cancelling it.
        }

        return true;
    }

    public async ValueTask DisposeAsync()
    {
        List<Task> queuedCallbacks = [];
        Task? worker;
        var cancelShutdown = false;
        lock (_gate)
        {
            if (!_disposed)
            {
                _disposed = true;
                cancelShutdown = true;
                foreach (var pending in _waiting.OrderByDescending(item => item.Request.Priority).ThenBy(item => item.Sequence).ToArray())
                    queuedCallbacks.Add(CompleteLocked(pending, new AnnouncementResult(pending.Request.RequestId, "cancelled", "disposed")));

                _waiting.Clear();
            }

            worker = _workerTask;
        }

        if (cancelShutdown)
        {
            try
            {
                _shutdown.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Another DisposeAsync call completed the lifetime cleanup.
            }
        }

        if (worker is not null)
            await worker.ConfigureAwait(false);

        if (queuedCallbacks.Count > 0)
            await Task.WhenAll(queuedCallbacks).ConfigureAwait(false);

        Task callbacksDrained;
        var disposeShutdown = false;
        lock (_gate)
        {
            callbacksDrained = _callbacksDrained;
            if (!_shutdownDisposed)
            {
                _shutdownDisposed = true;
                disposeShutdown = true;
            }
        }

        await callbacksDrained.ConfigureAwait(false);
        if (disposeShutdown)
            _shutdown.Dispose();
    }

    private void StartWorkerLocked()
    {
        _workerRunning = true;
        _workerTask = Task.Run(ProcessQueueAsync);
    }

    private async Task ProcessQueueAsync()
    {
        while (true)
        {
            PendingAnnouncement pending;
            CancellationTokenSource manualCancellation;
            lock (_gate)
            {
                if (_disposed || _waiting.Count == 0)
                {
                    _workerRunning = false;
                    _workerTask = null;
                    return;
                }

                var index = FindNextIndexLocked();
                pending = _waiting[index];
                _waiting.RemoveAt(index);
                manualCancellation = new CancellationTokenSource();
                _current = pending;
                _currentManualCancellation = manualCancellation;
            }

            AnnouncementResult result;
            try
            {
                result = await ProcessOneAsync(pending, manualCancellation.Token).ConfigureAwait(false);
            }
            catch
            {
                result = Failed(pending.Request, "playback_failed");
            }

            Task callbackTask;
            lock (_gate)
            {
                if (ReferenceEquals(_current, pending))
                {
                    _current = null;
                    _currentManualCancellation = null;
                }

                callbackTask = CompleteLocked(pending, result);
            }

            manualCancellation.Dispose();
            await callbackTask.ConfigureAwait(false);
        }
    }

    private async Task<AnnouncementResult> ProcessOneAsync(PendingAnnouncement pending, CancellationToken manualCancellation)
    {
        using var expiryCancellation = new CancellationTokenSource();
        var remaining = pending.Deadline - _timeProvider.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
            return Failed(pending.Request, "expired");

        using var expiryTimer = _timeProvider.CreateTimer(
            static state =>
            {
                try
                {
                    ((CancellationTokenSource)state!).Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // Timer completion can race request cleanup.
                }
            }, expiryCancellation, remaining, Timeout.InfiniteTimeSpan);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            manualCancellation, expiryCancellation.Token, _shutdown.Token);

        PreparedAnnouncement? prepared = null;
        IAnnouncementAudioLease? lease = null;
        var failureReason = "media_prepare_failed";
        var result = Failed(pending.Request, failureReason);
        var leaseDisposalFailed = false;

        try
        {
            linkedCancellation.Token.ThrowIfCancellationRequested();
            var initialMuteResult = pending.Request.Priority == AnnouncementPriority.Normal
                ? TryGetNormalMuteResult(pending.Request)
                : null;
            if (initialMuteResult is not null)
                result = initialMuteResult;
            else
            {
                prepared = await _mediaPreparer.PrepareAsync(pending.Request, linkedCancellation.Token).ConfigureAwait(false);
                linkedCancellation.Token.ThrowIfCancellationRequested();

                var acquireMuteResult = pending.Request.Priority == AnnouncementPriority.Normal
                    ? TryGetNormalMuteResult(pending.Request)
                    : null;
                if (acquireMuteResult is not null)
                    result = acquireMuteResult;
                else
                {
                    failureReason = "audio_acquire_failed";
                    lease = await _audioController.AcquireAsync(pending.Request.Priority, linkedCancellation.Token).ConfigureAwait(false);
                    linkedCancellation.Token.ThrowIfCancellationRequested();

                    var hasWarning = string.Equals(prepared.Warning, "chime_unavailable", StringComparison.Ordinal);
                    if (prepared.ChimePath is not null)
                    {
                        try
                        {
                            await lease.PlayAsync(prepared.ChimePath, linkedCancellation.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch
                        {
                            hasWarning = true;
                        }
                    }

                    failureReason = "playback_failed";
                    await lease.PlayAsync(prepared.SpeechPath, linkedCancellation.Token).ConfigureAwait(false);
                    linkedCancellation.Token.ThrowIfCancellationRequested();
                    result = new AnnouncementResult(pending.Request.RequestId, "completed", hasWarning ? "chime_unavailable" : null);
                }
            }
        }
        catch (AnnouncementSuppressedException)
        {
            result = pending.Request.Priority == AnnouncementPriority.Normal
                ? SkippedMuted(pending.Request, "muted")
                : Failed(pending.Request, "audio_acquire_failed");
        }
        catch (OperationCanceledException)
        {
            result = GetCancellationResult(pending, expiryCancellation, manualCancellation);
        }
        catch
        {
            result = Failed(pending.Request, failureReason);
        }
        finally
        {
            if (lease is not null)
            {
                try
                {
                    await lease.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    leaseDisposalFailed = true;
                }
            }

            if (prepared is not null)
            {
                try
                {
                    prepared.Dispose();
                }
                catch
                {
                    // A cleanup failure must not prevent lease disposal or terminal delivery.
                }
            }
        }

        if (leaseDisposalFailed)
            return Failed(pending.Request, "playback_failed");

        if (result.Status == "completed" && linkedCancellation.IsCancellationRequested)
            return GetCancellationResult(pending, expiryCancellation, manualCancellation);

        return result;
    }

    private AnnouncementResult GetCancellationResult(
        PendingAnnouncement pending,
        CancellationTokenSource expiryCancellation,
        CancellationToken manualCancellation)
    {
        if (expiryCancellation.IsCancellationRequested || _timeProvider.GetUtcNow() >= pending.Deadline)
            return Failed(pending.Request, "expired");
        if (manualCancellation.IsCancellationRequested)
            return new AnnouncementResult(pending.Request.RequestId, "cancelled", "cancelled");
        if (_shutdown.IsCancellationRequested)
            return new AnnouncementResult(pending.Request.RequestId, "cancelled", "disposed");

        return Failed(pending.Request, "playback_failed");
    }

    private AnnouncementResult? TryGetNormalMuteResult(AnnouncementRequest request)
    {
        bool? muted;
        try
        {
            muted = _audioController.GetEffectiveMuted();
        }
        catch
        {
            muted = null;
        }

        return muted switch
        {
            false => null,
            true => SkippedMuted(request, "muted"),
            null => Failed(request, "mute_state_unknown")
        };
    }

    private int FindNextIndexLocked()
    {
        var bestIndex = 0;
        for (var index = 1; index < _waiting.Count; index++)
        {
            var candidate = _waiting[index];
            var best = _waiting[bestIndex];
            if (candidate.Request.Priority > best.Request.Priority ||
                (candidate.Request.Priority == best.Request.Priority && candidate.Sequence < best.Sequence))
            {
                bestIndex = index;
            }
        }

        return bestIndex;
    }

    private Task CompleteLocked(PendingAnnouncement pending, AnnouncementResult result)
    {
        _activeIds.Remove(pending.RequestKey);
        RememberCompletedLocked(pending.RequestKey);
        return QueueCallbackLocked(result);
    }

    private Task QueueCallbackLocked(AnnouncementResult result)
    {
        if (_pendingCallbacks++ == 0)
        {
            _callbacksDrainedSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _callbacksDrained = _callbacksDrainedSource.Task;
        }

        return DeliverResultAsync(result);
    }

    private async Task DeliverResultAsync(AnnouncementResult result)
    {
        await Task.Yield();
        try
        {
            await _resultCallback(result).ConfigureAwait(false);
        }
        catch
        {
            // The callback is an external transport. It cannot change the single terminal result.
        }
        finally
        {
            TaskCompletionSource<bool>? drained = null;
            lock (_gate)
            {
                if (--_pendingCallbacks == 0)
                    drained = _callbacksDrainedSource;
            }

            drained?.TrySetResult(true);
        }
    }

    private void RememberCompletedLocked(string requestKey)
    {
        if (!_completedIds.Add(requestKey))
            return;

        _completedIdOrder.Enqueue(requestKey);
        while (_completedIdOrder.Count > RememberedRequestIds)
            _completedIds.Remove(_completedIdOrder.Dequeue());
    }

    private static AnnouncementResult Failed(AnnouncementRequest request, string reason) =>
        new(request.RequestId, "failed", reason);

    private static AnnouncementResult SkippedMuted(AnnouncementRequest request, string reason) =>
        new(request.RequestId, "skipped_muted", reason);

    private sealed record PendingAnnouncement(
        AnnouncementRequest Request,
        string RequestKey,
        DateTimeOffset Deadline,
        long Sequence);
}
