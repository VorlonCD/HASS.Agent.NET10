using System.Collections.Concurrent;
using HASS.Agent.Companion.Announcements;

namespace HASS.Agent.Companion.Tests;

public sealed class AnnouncementCoordinatorTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Prepares_before_acquiring_and_disposes_the_lease_before_terminal_callback()
    {
        var events = new ConcurrentQueue<string>();
        var callback = NewSignal<AnnouncementResult>();
        var preparer = new FakePreparer((request, _) =>
        {
            events.Enqueue("prepare");
            return Task.FromResult(Prepared(request));
        });
        var audio = new FakeAudioController(() => false, (_, _) =>
        {
            events.Enqueue("acquire");
            return Task.FromResult<IAnnouncementAudioLease>(new FakeLease(
                (path, _) =>
                {
                    events.Enqueue("play");
                    return Task.CompletedTask;
                },
                () =>
                {
                    events.Enqueue("dispose");
                    return ValueTask.CompletedTask;
                }));
        });

        await using var coordinator = new AnnouncementCoordinator(preparer, audio, result =>
        {
            events.Enqueue("callback");
            callback.TrySetResult(result);
            return Task.CompletedTask;
        });

        Assert.True(coordinator.Submit(Request()));
        Assert.Equal("completed", (await callback.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(["prepare", "acquire", "play", "dispose", "callback"], events.ToArray());
    }

    [Fact]
    public async Task Normal_requests_skip_when_muted_or_mute_state_is_unknown_at_submission()
    {
        var results = new ConcurrentDictionary<string, TaskCompletionSource<AnnouncementResult>>();
        var muted = true;
        var preparer = new FakePreparer((request, _) => Task.FromResult(Prepared(request)));
        var audio = new FakeAudioController(() => muted ? true : null);
        await using var coordinator = new AnnouncementCoordinator(preparer, audio, result =>
        {
            results[result.RequestId].TrySetResult(result);
            return Task.CompletedTask;
        });

        var mutedRequest = Request();
        var unknownRequest = Request();
        results[mutedRequest.RequestId] = NewSignal<AnnouncementResult>();
        results[unknownRequest.RequestId] = NewSignal<AnnouncementResult>();

        Assert.True(coordinator.Submit(mutedRequest));
        muted = false;
        Assert.True(coordinator.Submit(unknownRequest));

        var mutedResult = await results[mutedRequest.RequestId].Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        var unknownResult = await results[unknownRequest.RequestId].Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.Equal(("skipped_muted", "muted"), (mutedResult.Status, mutedResult.Reason));
        Assert.Equal(("failed", "mute_state_unknown"), (unknownResult.Status, unknownResult.Reason));
        Assert.Equal(0, preparer.PreparationCount);
        Assert.Equal(0, audio.AcquireCount);
    }

    [Fact]
    public async Task Normal_request_rechecks_mute_after_preparation_and_cleans_prepared_media()
    {
        var prepareStarted = NewSignal<bool>();
        var releasePreparation = NewSignal<bool>();
        var callback = NewSignal<AnnouncementResult>();
        var cleaned = false;
        var readCount = 0;
        var preparer = new FakePreparer(async (request, cancellationToken) =>
        {
            prepareStarted.TrySetResult(true);
            using var linkedCancellation = LinkWithTestCancellation(cancellationToken);
            await releasePreparation.Task.WaitAsync(linkedCancellation.Token);
            return new PreparedAnnouncement(request.RequestId, null, cleanup: () => cleaned = true);
        });
        var audio = new FakeAudioController(() => Interlocked.Increment(ref readCount) <= 2 ? false : true);
        await using var coordinator = new AnnouncementCoordinator(preparer, audio, result =>
        {
            callback.TrySetResult(result);
            return Task.CompletedTask;
        });

        Assert.True(coordinator.Submit(Request()));
        await prepareStarted.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        releasePreparation.TrySetResult(true);

        var result = await callback.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.Equal(("skipped_muted", "muted"), (result.Status, result.Reason));
        Assert.Equal(0, audio.AcquireCount);
        Assert.True(cleaned);
    }

    [Fact]
    public async Task Waiting_queue_is_bounded_deduplicated_and_ordered_by_priority_then_fifo()
    {
        var firstId = Guid.NewGuid().ToString("D");
        var firstPlayStarted = NewSignal<bool>();
        var releaseFirst = NewSignal<bool>();
        var played = new ConcurrentQueue<string>();
        var callbackCounts = new ConcurrentDictionary<string, int>();
        var completionSignals = new ConcurrentDictionary<string, TaskCompletionSource<AnnouncementResult>>();
        var activePlays = 0;
        var maximumActivePlays = 0;
        var preparer = new FakePreparer((request, _) => Task.FromResult(Prepared(request)));
        var audio = new FakeAudioController(() => false, (_, _) => Task.FromResult<IAnnouncementAudioLease>(new FakeLease(
            async (path, cancellationToken) =>
            {
                var active = Interlocked.Increment(ref activePlays);
                UpdateMaximum(ref maximumActivePlays, active);
                try
                {
                    var requestId = path["speech:".Length..];
                    played.Enqueue(requestId);
                    if (requestId == firstId)
                    {
                        firstPlayStarted.TrySetResult(true);
                        using var linkedCancellation = LinkWithTestCancellation(cancellationToken);
                        await releaseFirst.Task.WaitAsync(linkedCancellation.Token);
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref activePlays);
                }
            })));
        await using var coordinator = new AnnouncementCoordinator(preparer, audio, result =>
        {
            callbackCounts.AddOrUpdate(result.RequestId, 1, (_, count) => count + 1);
            completionSignals[result.RequestId].TrySetResult(result);
            return Task.CompletedTask;
        });

        var first = Request(id: firstId);
        completionSignals[firstId] = NewSignal<AnnouncementResult>();
        Assert.True(coordinator.Submit(first));
        await firstPlayStarted.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        var queued = new[]
        {
            Request(AnnouncementPriority.Normal),
            Request(AnnouncementPriority.Important),
            Request(AnnouncementPriority.Critical),
            Request(AnnouncementPriority.Important),
            Request(AnnouncementPriority.Normal),
            Request(AnnouncementPriority.Critical),
            Request(AnnouncementPriority.Normal),
            Request(AnnouncementPriority.Important),
            Request(AnnouncementPriority.Normal),
            Request(AnnouncementPriority.Important)
        };
        foreach (var request in queued)
        {
            completionSignals[request.RequestId] = NewSignal<AnnouncementResult>();
            Assert.True(coordinator.Submit(request));
        }

        Assert.False(coordinator.Submit(queued[0]));
        var overflow = Request();
        var overflowResult = NewSignal<AnnouncementResult>();
        completionSignals[overflow.RequestId] = overflowResult;
        Assert.False(coordinator.Submit(overflow));
        var overflowTerminal = await overflowResult.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.Equal(("failed", "queue_full"), (overflowTerminal.Status, overflowTerminal.Reason));

        releaseFirst.TrySetResult(true);
        foreach (var request in queued.Append(first))
            await completionSignals[request.RequestId].Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);

        var expected = new[]
        {
            firstId,
            queued[2].RequestId, queued[5].RequestId,
            queued[1].RequestId, queued[3].RequestId, queued[7].RequestId, queued[9].RequestId,
            queued[0].RequestId, queued[4].RequestId, queued[6].RequestId, queued[8].RequestId
        };
        Assert.Equal(expected, played.ToArray());
        Assert.Equal(1, maximumActivePlays);
        Assert.All(callbackCounts.Values, count => Assert.Equal(1, count));
    }

    [Fact]
    public async Task Manual_cancel_and_deadline_expiry_have_distinct_terminal_results_and_release_the_lease()
    {
        var cancelStarted = NewSignal<bool>();
        var cancelResult = NewSignal<AnnouncementResult>();
        var cancelLease = new FakeLease(async (_, token) =>
        {
            cancelStarted.TrySetResult(true);
            using var linkedCancellation = LinkWithTestCancellation(token);
            await Task.Delay(Timeout.InfiniteTimeSpan, linkedCancellation.Token);
        });
        await using (var coordinator = new AnnouncementCoordinator(
            new FakePreparer((request, _) => Task.FromResult(Prepared(request))),
            new FakeAudioController(() => false, (_, _) => Task.FromResult<IAnnouncementAudioLease>(cancelLease)),
            result => { cancelResult.TrySetResult(result); return Task.CompletedTask; }))
        {
            Assert.True(coordinator.Submit(Request()));
            await cancelStarted.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
            Assert.True(coordinator.CancelCurrent());
            var result = await cancelResult.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
            Assert.Equal(("cancelled", "cancelled"), (result.Status, result.Reason));
            Assert.Equal(1, cancelLease.DisposeCount);
        }

        var expiryStarted = NewSignal<bool>();
        var expiryResult = NewSignal<AnnouncementResult>();
        var expiryLease = new FakeLease(async (_, token) =>
        {
            expiryStarted.TrySetResult(true);
            using var linkedCancellation = LinkWithTestCancellation(token);
            await Task.Delay(Timeout.InfiniteTimeSpan, linkedCancellation.Token);
        });
        await using (var coordinator = new AnnouncementCoordinator(
            new FakePreparer((request, _) => Task.FromResult(Prepared(request))),
            new FakeAudioController(() => false, (_, _) => Task.FromResult<IAnnouncementAudioLease>(expiryLease)),
            result => { expiryResult.TrySetResult(result); return Task.CompletedTask; }))
        {
            Assert.True(coordinator.Submit(Request(expiresAt: DateTimeOffset.UtcNow.AddMilliseconds(250))));
            await expiryStarted.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
            var result = await expiryResult.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
            Assert.Equal(("failed", "expired"), (result.Status, result.Reason));
            Assert.Equal(1, expiryLease.DisposeCount);
        }
    }

    [Fact]
    public async Task Chime_playback_failure_warns_and_continues_to_speech()
    {
        var callback = NewSignal<AnnouncementResult>();
        var played = new ConcurrentQueue<string>();
        var lease = new FakeLease((path, _) =>
        {
            played.Enqueue(path);
            if (path.EndsWith(":chime", StringComparison.Ordinal))
                throw new IOException("private URL must not escape");
            return Task.CompletedTask;
        });
        await using var coordinator = new AnnouncementCoordinator(
            new FakePreparer((request, _) => Task.FromResult(Prepared(request, hasChime: true))),
            new FakeAudioController(() => false, (_, _) => Task.FromResult<IAnnouncementAudioLease>(lease)),
            result => { callback.TrySetResult(result); return Task.CompletedTask; });

        Assert.True(coordinator.Submit(Request(chime: "https://ha.local/chime.mp3")));
        var result = await callback.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.Equal(("completed", "chime_unavailable"), (result.Status, result.Reason));
        Assert.Equal(2, played.Count);
        Assert.StartsWith("speech:", played.Last());
        Assert.Equal(1, lease.DisposeCount);
    }

    [Fact]
    public async Task Stop_accepted_during_restoration_is_reported_after_cleanup()
    {
        var restoring = NewSignal<bool>();
        var release = NewSignal<bool>();
        var terminal = NewSignal<AnnouncementResult>();
        var lease = new FakeLease((_, _) => Task.CompletedTask, async () =>
        {
            restoring.TrySetResult(true);
            await release.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        });
        await using var coordinator = new AnnouncementCoordinator(
            new FakePreparer((request, _) => Task.FromResult(Prepared(request))),
            new FakeAudioController(() => false, (_, _) => Task.FromResult<IAnnouncementAudioLease>(lease)),
            result => { terminal.TrySetResult(result); return Task.CompletedTask; });
        Assert.True(coordinator.Submit(Request()));
        await restoring.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.True(coordinator.CancelCurrent());
        Assert.False(terminal.Task.IsCompleted);
        release.TrySetResult(true);
        var result = await terminal.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.Equal("cancelled", result.Status);
        Assert.Equal(1, lease.DisposeCount);
    }

    [Fact]
    public async Task Lease_restore_failure_produces_one_safe_failure_after_disposal()
    {
        var callbackCount = 0;
        var callback = NewSignal<AnnouncementResult>();
        var lease = new FakeLease((_, _) => Task.CompletedTask, () =>
        {
            throw new IOException("private failure detail");
        });
        await using var coordinator = new AnnouncementCoordinator(
            new FakePreparer((request, _) => Task.FromResult(Prepared(request))),
            new FakeAudioController(() => false, (_, _) => Task.FromResult<IAnnouncementAudioLease>(lease)),
            result =>
            {
                Interlocked.Increment(ref callbackCount);
                callback.TrySetResult(result);
                return Task.CompletedTask;
            });

        Assert.True(coordinator.Submit(Request()));
        var result = await callback.Task.WaitAsync(TestTimeout, TestContext.Current.CancellationToken);
        Assert.Equal(("failed", "playback_failed"), (result.Status, result.Reason));
        await coordinator.DisposeAsync();
        Assert.Equal(1, callbackCount);
        Assert.Equal(1, lease.DisposeCount);
    }

    private static AnnouncementRequest Request(
        AnnouncementPriority priority = AnnouncementPriority.Normal,
        string? id = null,
        string? chime = null,
        DateTimeOffset? expiresAt = null) => new(
            id ?? Guid.NewGuid().ToString("D"),
            "https://ha.local/speech.mp3",
            chime,
            priority,
            expiresAt ?? DateTimeOffset.UtcNow.AddMinutes(1));

    private static PreparedAnnouncement Prepared(AnnouncementRequest request, bool hasChime = false) => new(
        "speech:" + request.RequestId,
        hasChime ? "request:" + request.RequestId + ":chime" : null);

    private static TaskCompletionSource<T> NewSignal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static CancellationTokenSource LinkWithTestCancellation(CancellationToken cancellationToken) =>
        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, TestContext.Current.CancellationToken);

    private static void UpdateMaximum(ref int target, int value)
    {
        while (true)
        {
            var current = Volatile.Read(ref target);
            if (current >= value || Interlocked.CompareExchange(ref target, value, current) == current)
                return;
        }
    }

    private sealed class FakePreparer(Func<AnnouncementRequest, CancellationToken, Task<PreparedAnnouncement>> prepare) : IAnnouncementMediaPreparer
    {
        public int PreparationCount;

        public Task<PreparedAnnouncement> PrepareAsync(AnnouncementRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref PreparationCount);
            return prepare(request, cancellationToken);
        }
    }

    private sealed class FakeAudioController(
        Func<bool?> getMuted,
        Func<AnnouncementPriority, CancellationToken, Task<IAnnouncementAudioLease>>? acquire = null) : IAnnouncementAudioController
    {
        public int AcquireCount;

        public bool? GetEffectiveMuted() => getMuted();

        public Task<IAnnouncementAudioLease> AcquireAsync(AnnouncementPriority priority, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref AcquireCount);
            return acquire is null
                ? Task.FromResult<IAnnouncementAudioLease>(new FakeLease((_, _) => Task.CompletedTask))
                : acquire(priority, cancellationToken);
        }
    }

    private sealed class FakeLease(
        Func<string, CancellationToken, Task> play,
        Func<ValueTask>? dispose = null) : IAnnouncementAudioLease
    {
        private int _disposeCount;
        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public Task PlayAsync(string filePath, CancellationToken cancellationToken) => play(filePath, cancellationToken);

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            return dispose?.Invoke() ?? ValueTask.CompletedTask;
        }
    }
}
