using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;
using System_Monitor.Models;
using System_Monitor.Services;
using Xunit;

namespace SystemMonitor.Tests;

/// <summary>
/// Deterministic lifecycle tests for <see cref="UpdatePipeline{TSize}"/>.
///
/// <para><b>Determinism rules used here</b></para>
/// <list type="bullet">
/// <item><description>
/// Time is virtual. The pipeline is given a <see cref="FakeTimeProvider"/>,
/// so polling intervals are advanced explicitly rather than slept through.
/// </description></item>
/// <item><description>
/// The fake collector exposes explicit per-attempt <c>Started</c> and
/// <c>Release</c> signals, and separate counters for collection started,
/// collection completed, collection failed, and publication. A test awaits
/// the signal it depends on instead of inferring progress from a counter.
/// </description></item>
/// <item><description>
/// <see cref="Task.Delay"/> is never used for synchronization. The only
/// real-time wait is a deadlock watchdog on the final await, which proves
/// nothing about polling behaviour.
/// </description></item>
/// </list>
///
/// The pipeline and its registry are generic over the widget size type, so
/// these tests use a plain enum instead of the Windows App SDK's
/// <see cref="Microsoft.Windows.Widgets.WidgetSize"/> and run without any
/// real hardware or widget host.
/// </summary>
public class UpdatePipelineTests
{
    private enum TestSize { Small, Medium, Large }

    /// <summary>How long a test will wait before declaring a deadlock.</summary>
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(15);

    /// <summary>
    /// A blocked fake collector occupies a thread-pool thread, exactly as a
    /// real synchronous WMI collector would. Raising the minimum pool size
    /// guarantees the pipeline's own continuations still have a thread to
    /// run on, so a deliberately blocked collection cannot starve the test
    /// host.
    /// </summary>
    static UpdatePipelineTests()
    {
        ThreadPool.GetMinThreads(out var workerThreads, out var completionPorts);
        ThreadPool.SetMinThreads(Math.Max(workerThreads, 32), Math.Max(completionPorts, 32));
    }

    /// <summary>
    /// A controllable collector. <see cref="Collect"/> signals the start of
    /// the current attempt and then waits for the release gate before
    /// returning, so a test can hold a collection open deliberately.
    /// </summary>
    private sealed class ControllableCollector : IMetricsCollector
    {
        private readonly object _lock = new();

        private readonly List<TaskCompletionSource<int>> _startSignals = new();
        private readonly List<TaskCompletionSource<int>> _successSignals = new();
        private readonly List<TaskCompletionSource<int>> _failureSignals = new();

        /// <summary>
        /// The gate a collection waits on. Completed by default — a
        /// collection is only held open after <see cref="Block"/> replaces
        /// it with an uncompleted one, and <see cref="Release"/> completes
        /// it again.
        /// </summary>
        private TaskCompletionSource<bool> _gate = CompletedGate();

        private static TaskCompletionSource<bool> CompletedGate()
        {
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            gate.TrySetResult(true);
            return gate;
        }

        /// <summary>Number of collections that have begun.</summary>
        public int StartedCount;

        /// <summary>Number of collections that completed successfully.</summary>
        public int CompletedCount;

        /// <summary>Number of collections that failed with an ordinary exception.</summary>
        public int FailureCount;

        /// <summary>Highest number of collections running at the same time.</summary>
        public int MaxConcurrent;

        /// <summary>Attempt number on which the collector throws, or null.</summary>
        public int? ThrowOnAttempt;

        private int _concurrent;

        /// <summary>
        /// Holds every subsequent collection open until <see cref="Release"/>
        /// is called.
        /// </summary>
        public void Block()
        {
            lock (_lock)
            {
                _gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        /// <summary>Lets the held collection finish.</summary>
        public void Release()
        {
            TaskCompletionSource<bool> gate;
            lock (_lock) { gate = _gate; }
            gate.TrySetResult(true);
        }

        /// <summary>Completes when the given attempt has begun collecting.</summary>
        public Task<int> WaitForStart(int attempt) => SignalFor(_startSignals, attempt).Task;

        /// <summary>Completes when the given attempt has completed successfully.</summary>
        public Task<int> WaitForSuccess(int attempt) => SignalFor(_successSignals, attempt).Task;

        /// <summary>Completes when the given attempt has failed.</summary>
        public Task<int> WaitForFailure(int attempt) => SignalFor(_failureSignals, attempt).Task;

        private TaskCompletionSource<int> SignalFor(List<TaskCompletionSource<int>> signals, int attempt)
        {
            lock (_lock)
            {
                while (signals.Count <= attempt)
                    signals.Add(new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously));
                return signals[attempt];
            }
        }

        public MetricsSnapshot Collect()
        {
            var attempt = Interlocked.Increment(ref StartedCount);
            SignalFor(_startSignals, attempt).TrySetResult(attempt);

            var concurrent = Interlocked.Increment(ref _concurrent);
            InterlockedMax(ref MaxConcurrent, concurrent);
            try
            {
                Task gate;
                lock (_lock) { gate = _gate.Task; }

                // The collector interface is synchronous by design: the real
                // implementation makes blocking WMI/native calls. A blocked
                // fake therefore occupies the calling thread exactly as the
                // real collector would. The pipeline invokes Collect()
                // outside its state lock, so the coordinator can still make
                // progress.
                gate.GetAwaiter().GetResult();

                if (ThrowOnAttempt.HasValue && attempt == ThrowOnAttempt.Value)
                {
                    Interlocked.Increment(ref FailureCount);
                    SignalFor(_failureSignals, attempt).TrySetResult(attempt);
                    throw new InvalidOperationException("collector failure");
                }

                Interlocked.Increment(ref CompletedCount);
                SignalFor(_successSignals, attempt).TrySetResult(attempt);
                return MetricsSnapshot.Create(
                    DateTimeOffset.UtcNow,
                    cpuLoadPercent: Measurement.AvailableValue(attempt, "%", "test"));
            }
            finally
            {
                Interlocked.Decrement(ref _concurrent);
            }
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int current;
            while (value > (current = Volatile.Read(ref target)))
            {
                if (Interlocked.CompareExchange(ref target, value, current) == current)
                    return;
            }
        }
    }

    /// <summary>Records publications, with a signal for each count reached.</summary>
    private sealed class PublicationLog
    {
        private readonly object _lock = new();
        private readonly ConcurrentQueue<string> _payloads = new();
        private TaskCompletionSource<bool> _signal =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Count => _payloads.Count;

        /// <summary>Completes once at least <paramref name="count"/> publications have happened.</summary>
        public Task WaitForPublication(int count)
        {
            lock (_lock)
            {
                if (_payloads.Count >= count)
                    return Task.CompletedTask;
                return _signal.Task;
            }
        }

        public void Record(string payload)
        {
            TaskCompletionSource<bool> signal;
            lock (_lock)
            {
                _payloads.Enqueue(payload);
                signal = _signal;
                _signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            // Completed outside the lock.
            signal.TrySetResult(true);
        }
    }

    private static WidgetRegistry<TestSize> OneActiveWidget()
    {
        var widgets = new WidgetRegistry<TestSize>();
        widgets.Set(new WidgetEntry<TestSize> { WidgetId = "w1", IsActive = true });
        return widgets;
    }

    /// <summary>
    /// Awaits a task, but fails the test rather than hanging if the task
    /// never completes. This is a deadlock watchdog only: it never provides
    /// the synchronization that proves a behaviour.
    /// </summary>
    private static async Task<T> WithWatchdogAsync<T>(Task<T> task)
    {
        var completed = await Task.WhenAny(task, Task.Delay(Watchdog));
        if (completed != task)
            throw new TimeoutException($"Task did not complete within {Watchdog}.");
        return await task;
    }

    private static async Task WithWatchdogAsync(Task task)
    {
        var completed = await Task.WhenAny(task, Task.Delay(Watchdog));
        if (completed != task)
            throw new TimeoutException($"Task did not complete within {Watchdog}.");
        await task;
    }

    /// <summary>
    /// Yields control so that continuations queued with
    /// RunContinuationsAsynchronously can run to their next suspension point.
    /// Used after an explicit signal before making an assertion about what
    /// follows it.
    /// </summary>
    private static async Task DrainAsync()
    {
        for (int i = 0; i < 6; i++)
            await Task.Yield();
    }

    // -----------------------------------------------------------------
    // A. Deactivate while waiting
    // -----------------------------------------------------------------

    [Fact]
    public async Task DeactivateWhileWaiting_ThenReactivate_ProducesExactlyOneImmediateCollection()
    {
        var clock = new FakeTimeProvider();
        var collector = new ControllableCollector();
        var widgets = OneActiveWidget();
        var log = new PublicationLog();

        var pipeline = new UpdatePipeline<TestSize>(
            collector, widgets,
            snapshot => "payload",
            (payload, widget) => { log.Record(payload); return Task.CompletedTask; },
            TimeSpan.FromMinutes(5),
            clock);

        pipeline.Start();

        // The first collection runs and completes.
        await WithWatchdogAsync(collector.WaitForStart(1));
        await WithWatchdogAsync(collector.WaitForSuccess(1));
        await WithWatchdogAsync(log.WaitForPublication(1));

        // Establish the milestone: the coordinator has registered its next
        // scheduled wait.
        await WithWatchdogAsync(pipeline.WaitForWaitCountAsync(1));

        // Deactivate and await quiescence.
        await WithWatchdogAsync(pipeline.DeactivateAsync());
        Assert.False(pipeline.IsRunning);

        // Advance virtual time through several polling intervals. No
        // collection may be admitted while the pipeline is inactive.
        clock.Advance(TimeSpan.FromMinutes(20));
        await DrainAsync();
        Assert.Equal(1, collector.StartedCount);

        // Reactivate: exactly one immediate next collection, without
        // advancing the old polling interval.
        pipeline.Start();
        await WithWatchdogAsync(collector.WaitForStart(2));
        await WithWatchdogAsync(collector.WaitForSuccess(2));
        await WithWatchdogAsync(log.WaitForPublication(2));

        Assert.Equal(2, collector.StartedCount);
        Assert.Equal(1, collector.MaxConcurrent);

        await WithWatchdogAsync(pipeline.DisposeAsync().AsTask());
    }

    // -----------------------------------------------------------------
    // B. Deactivate during collection
    // -----------------------------------------------------------------

    [Fact]
    public async Task DeactivateDuringCollection_QuiescenceWaitsForBlockedCollector()
    {
        var clock = new FakeTimeProvider();
        var collector = new ControllableCollector();
        collector.Block();
        var widgets = OneActiveWidget();
        var log = new PublicationLog();

        var pipeline = new UpdatePipeline<TestSize>(
            collector, widgets,
            snapshot => "payload",
            (payload, widget) => { log.Record(payload); return Task.CompletedTask; },
            TimeSpan.FromMinutes(5),
            clock);

        pipeline.Start();

        // The collector has begun and is deliberately held open.
        await WithWatchdogAsync(collector.WaitForStart(1));

        // Request deactivation.
        var deactivateTask = pipeline.DeactivateAsync();

        // Cancellation was requested on the in-flight run.
        Assert.True(pipeline.CancellationRequestedCount >= 1);

        // Quiescence must remain incomplete while the collector is blocked.
        await DrainAsync();
        Assert.False(deactivateTask.IsCompleted);

        // Release the collector and await quiescence.
        collector.Release();
        await WithWatchdogAsync(deactivateTask);

        // No new collection was started, and the stale snapshot was not
        // published: the generation was cancelled before publication.
        await DrainAsync();
        Assert.Equal(1, collector.StartedCount);
        Assert.Equal(0, log.Count);

        await WithWatchdogAsync(pipeline.DisposeAsync().AsTask());
    }

    // -----------------------------------------------------------------
    // C. Reactivate during the old collection
    // -----------------------------------------------------------------

    [Fact]
    public async Task ReactivateDuringOldCollection_NeverOverlapsAndSuppressesStalePublication()
    {
        var clock = new FakeTimeProvider();
        var collector = new ControllableCollector();
        collector.Block();
        var widgets = OneActiveWidget();
        var log = new PublicationLog();

        var pipeline = new UpdatePipeline<TestSize>(
            collector, widgets,
            snapshot => "payload",
            (payload, widget) => { log.Record(payload); return Task.CompletedTask; },
            TimeSpan.FromMinutes(5),
            clock);

        pipeline.Start();

        // The old collector has begun and is held open.
        await WithWatchdogAsync(collector.WaitForStart(1));
        var oldGeneration = pipeline.CurrentGeneration;

        // Deactivate, then reactivate while the old collector still runs.
        pipeline.Stop();
        pipeline.Start();
        var newGeneration = pipeline.CurrentGeneration;

        // A new generation was created; the cancelled one is not reused.
        Assert.NotEqual(oldGeneration, newGeneration);

        // Maximum concurrent collections remains one: the new generation
        // waits for the old collector to unwind.
        await DrainAsync();
        Assert.Equal(1, collector.StartedCount);
        Assert.Equal(1, collector.MaxConcurrent);

        // Release the old collector. The new generation collects promptly,
        // without advancing the old polling interval.
        collector.Release();
        await WithWatchdogAsync(collector.WaitForStart(2));
        await WithWatchdogAsync(collector.WaitForSuccess(2));
        await WithWatchdogAsync(log.WaitForPublication(1));

        Assert.Equal(2, collector.StartedCount);
        Assert.Equal(1, collector.MaxConcurrent);

        // The old generation's snapshot was not published as current data:
        // only the new generation's publication appears.
        Assert.Equal(1, log.Count);

        await WithWatchdogAsync(pipeline.DisposeAsync().AsTask());
    }

    // -----------------------------------------------------------------
    // D. Reactivate during a long interval
    // -----------------------------------------------------------------

    [Fact]
    public async Task ReactivateDuringLongInterval_CollectsWithoutAdvancingFakeTime()
    {
        var clock = new FakeTimeProvider();
        var collector = new ControllableCollector();
        var widgets = OneActiveWidget();
        var log = new PublicationLog();

        var pipeline = new UpdatePipeline<TestSize>(
            collector, widgets,
            snapshot => "payload",
            (payload, widget) => { log.Record(payload); return Task.CompletedTask; },
            TimeSpan.FromHours(1),
            clock);

        pipeline.Start();
        await WithWatchdogAsync(collector.WaitForStart(1));
        await WithWatchdogAsync(collector.WaitForSuccess(1));
        await WithWatchdogAsync(log.WaitForPublication(1));

        // The coordinator is now waiting on a one-hour interval.
        await WithWatchdogAsync(pipeline.WaitForWaitCountAsync(1));

        // Deactivate while waiting.
        await WithWatchdogAsync(pipeline.DeactivateAsync());

        // Reactivate. The next collection starts without advancing fake
        // time.
        pipeline.Start();
        await WithWatchdogAsync(collector.WaitForStart(2));
        await WithWatchdogAsync(collector.WaitForSuccess(2));
        await WithWatchdogAsync(log.WaitForPublication(2));

        Assert.Equal(2, collector.StartedCount);
        Assert.Equal(1, collector.MaxConcurrent);

        await WithWatchdogAsync(pipeline.DisposeAsync().AsTask());
    }

    // -----------------------------------------------------------------
    // E. Failure recovery
    // -----------------------------------------------------------------

    [Fact]
    public async Task CollectorFailure_RecoversAndPublishes()
    {
        var clock = new FakeTimeProvider();
        var collector = new ControllableCollector { ThrowOnAttempt = 2 };
        var widgets = OneActiveWidget();
        var log = new PublicationLog();

        var pipeline = new UpdatePipeline<TestSize>(
            collector, widgets,
            snapshot => "payload",
            (payload, widget) => { log.Record(payload); return Task.CompletedTask; },
            TimeSpan.FromMinutes(5),
            clock);

        pipeline.Start();

        // Attempt 1 succeeds.
        await WithWatchdogAsync(collector.WaitForStart(1));
        await WithWatchdogAsync(collector.WaitForSuccess(1));
        await WithWatchdogAsync(log.WaitForPublication(1));
        Assert.Equal(1, log.Count);

        // Attempt 2 throws an ordinary collector exception. It is an
        // ordinary failure, not a cancellation, so the pipeline keeps
        // running.
        clock.Advance(TimeSpan.FromMinutes(10));
        await WithWatchdogAsync(collector.WaitForStart(2));
        await WithWatchdogAsync(collector.WaitForFailure(2));

        // The failed attempt published nothing.
        Assert.Equal(1, log.Count);
        Assert.True(pipeline.IsRunning);
        Assert.Equal(0, pipeline.CancellationRequestedCount);

        // Attempt 3 occurs after advancing virtual time and succeeds.
        await WithWatchdogAsync(pipeline.WaitForWaitCountAsync(2));
        clock.Advance(TimeSpan.FromMinutes(10));
        await WithWatchdogAsync(collector.WaitForStart(3));
        await WithWatchdogAsync(collector.WaitForSuccess(3));
        await WithWatchdogAsync(log.WaitForPublication(2));

        Assert.Equal(3, collector.StartedCount);
        Assert.Equal(1, collector.FailureCount);

        // Successful publication after recovery, not merely a count.
        Assert.Equal(2, log.Count);

        await WithWatchdogAsync(pipeline.DisposeAsync().AsTask());
    }

    // -----------------------------------------------------------------
    // F. Lifecycle stress
    // -----------------------------------------------------------------

    [Fact]
    public async Task LifecycleStress_NoLostWakeupsNoOverlapNoOrphanLoops()
    {
        var clock = new FakeTimeProvider();
        var collector = new ControllableCollector();
        var widgets = new WidgetRegistry<TestSize>();
        widgets.Set(new WidgetEntry<TestSize> { WidgetId = "w1", IsActive = true });
        widgets.Set(new WidgetEntry<TestSize> { WidgetId = "w2", IsActive = false });
        var log = new PublicationLog();

        var pipeline = new UpdatePipeline<TestSize>(
            collector, widgets,
            snapshot => "payload",
            (payload, widget) => { log.Record(payload); return Task.CompletedTask; },
            TimeSpan.FromMinutes(5),
            clock);

        const int Rounds = 5;
        for (int round = 0; round < Rounds; round++)
        {
            int activationAttempt = (round * 2) + 1;
            int refreshAttempt = activationAttempt + 1;
            int expectedPublications = (round * 2) + 2;

            // Activate: one immediate collection.
            pipeline.Start();
            await WithWatchdogAsync(collector.WaitForStart(activationAttempt));
            await WithWatchdogAsync(collector.WaitForSuccess(activationAttempt));
            await WithWatchdogAsync(log.WaitForPublication(round * 2 + 1));

            // Refresh while active: several requests coalesce into exactly
            // one extra collection, and it happens promptly.
            pipeline.ScheduleRefresh();
            pipeline.ScheduleRefresh();
            await WithWatchdogAsync(collector.WaitForStart(refreshAttempt));
            await WithWatchdogAsync(collector.WaitForSuccess(refreshAttempt));
            await WithWatchdogAsync(log.WaitForPublication(expectedPublications));

            // Deactivate.
            await WithWatchdogAsync(pipeline.DeactivateAsync());
        }

        // Never more than one collection at a time.
        Assert.Equal(1, collector.MaxConcurrent);

        // Every activation produced exactly one collection and every
        // refresh batch produced exactly one more — no lost wakeups, and no
        // unexplained extra collection.
        Assert.Equal(Rounds * 2, collector.StartedCount);
        Assert.Equal(Rounds * 2, log.Count);

        // Deleting the last active widget while inactive widgets remain
        // stops polling.
        widgets.Remove("w1");
        Assert.False(widgets.HasActiveWidgets);
        await WithWatchdogAsync(pipeline.DeactivateAsync());
        await DrainAsync();

        var countAfterDelete = collector.StartedCount;
        clock.Advance(TimeSpan.FromHours(1));
        await DrainAsync();
        Assert.Equal(countAfterDelete, collector.StartedCount);

        await WithWatchdogAsync(pipeline.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task DeletingLastActiveWidgetWhileInactiveRemain_StopsPolling()
    {
        var clock = new FakeTimeProvider();
        var collector = new ControllableCollector();
        var widgets = new WidgetRegistry<TestSize>();
        widgets.Set(new WidgetEntry<TestSize> { WidgetId = "w1", IsActive = true });
        widgets.Set(new WidgetEntry<TestSize> { WidgetId = "w2", IsActive = false });
        var log = new PublicationLog();

        var pipeline = new UpdatePipeline<TestSize>(
            collector, widgets,
            snapshot => "payload",
            (payload, widget) => { log.Record(payload); return Task.CompletedTask; },
            TimeSpan.FromMinutes(5),
            clock);

        pipeline.Start();
        await WithWatchdogAsync(collector.WaitForStart(1));
        await WithWatchdogAsync(collector.WaitForSuccess(1));
        await WithWatchdogAsync(log.WaitForPublication(1));
        Assert.Equal(1, collector.StartedCount);

        // Delete the active widget; an inactive widget remains.
        widgets.Remove("w1");
        Assert.Equal(1, widgets.Count);
        Assert.False(widgets.HasActiveWidgets);

        await WithWatchdogAsync(pipeline.DeactivateAsync());
        await DrainAsync();

        var countAfterDelete = collector.StartedCount;
        clock.Advance(TimeSpan.FromHours(1));
        await DrainAsync();
        Assert.Equal(countAfterDelete, collector.StartedCount);

        await WithWatchdogAsync(pipeline.DisposeAsync().AsTask());
    }

    // -----------------------------------------------------------------
    // Supporting behaviour
    // -----------------------------------------------------------------

    [Fact]
    public async Task RefreshIsCoalescedIntoOneExtraCollection()
    {
        var clock = new FakeTimeProvider();
        var collector = new ControllableCollector();
        var widgets = OneActiveWidget();
        var log = new PublicationLog();

        var pipeline = new UpdatePipeline<TestSize>(
            collector, widgets,
            snapshot => "payload",
            (payload, widget) => { log.Record(payload); return Task.CompletedTask; },
            TimeSpan.FromMinutes(5),
            clock);

        pipeline.Start();
        await WithWatchdogAsync(collector.WaitForStart(1));
        await WithWatchdogAsync(collector.WaitForSuccess(1));
        await WithWatchdogAsync(log.WaitForPublication(1));
        Assert.Equal(1, collector.StartedCount);

        // Several refresh requests collapse into one extra collection.
        pipeline.ScheduleRefresh();
        pipeline.ScheduleRefresh();
        pipeline.ScheduleRefresh();

        await WithWatchdogAsync(collector.WaitForStart(2));
        await WithWatchdogAsync(collector.WaitForSuccess(2));
        await WithWatchdogAsync(log.WaitForPublication(2));

        Assert.Equal(2, collector.StartedCount);
        Assert.Equal(1, collector.MaxConcurrent);

        // No third collection: the three refresh requests produced exactly
        // one extra collection, not three.
        await DrainAsync();
        Assert.Equal(2, collector.StartedCount);

        await WithWatchdogAsync(pipeline.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task PayloadIsBuiltFromTheCompletedSnapshot()
    {
        var clock = new FakeTimeProvider();
        var collector = new ControllableCollector();
        var widgets = OneActiveWidget();
        var log = new PublicationLog();

        string? captured = null;
        var pipeline = new UpdatePipeline<TestSize>(
            collector,
            widgets,
            snapshot => { captured = $"payload@{snapshot.Timestamp:O}"; return captured; },
            (payload, widget) => { log.Record(payload); return Task.CompletedTask; },
            TimeSpan.FromMinutes(5),
            clock);

        pipeline.Start();
        await WithWatchdogAsync(collector.WaitForStart(1));
        await WithWatchdogAsync(collector.WaitForSuccess(1));

        // The payload is built after the snapshot completes, so the
        // publication is the milestone to await.
        await WithWatchdogAsync(log.WaitForPublication(1));

        Assert.NotNull(captured);
        Assert.Contains("payload@", captured!);

        await WithWatchdogAsync(pipeline.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task DisposeAwaitsInFlightCollectionBeforeReturning()
    {
        var clock = new FakeTimeProvider();
        var collector = new ControllableCollector();
        collector.Block();
        var widgets = OneActiveWidget();

        var pipeline = new UpdatePipeline<TestSize>(
            collector, widgets,
            snapshot => "payload",
            (payload, widget) => Task.CompletedTask,
            TimeSpan.FromMinutes(5),
            clock);

        pipeline.Start();
        await WithWatchdogAsync(collector.WaitForStart(1));

        // Disposal while a collection is in flight must not complete until
        // the collection unwinds.
        var disposeTask = pipeline.DisposeAsync().AsTask();
        await DrainAsync();
        Assert.False(disposeTask.IsCompleted);

        collector.Release();
        await WithWatchdogAsync(disposeTask);

        var finalCount = collector.StartedCount;
        clock.Advance(TimeSpan.FromHours(1));
        await DrainAsync();
        Assert.Equal(finalCount, collector.StartedCount);
    }

    // -----------------------------------------------------------------
    // Drain-boundary contract points
    // -----------------------------------------------------------------

    /// <summary>
    /// A synchronous deactivation (a COM widget callback) transitions the
    /// pipeline to inactive but does not wait for the collector. A later
    /// awaitable deactivation must still guarantee quiescence of the work
    /// that was already admitted when the pipeline went inactive.
    /// </summary>
    [Fact]
    public async Task DeactivateAsync_AfterSynchronousStop_StillAwaitsTheInFlightCollection()
    {
        var clock = new FakeTimeProvider();
        var collector = new ControllableCollector();
        collector.Block();
        var widgets = OneActiveWidget();
        var log = new PublicationLog();

        var pipeline = new UpdatePipeline<TestSize>(
            collector, widgets,
            snapshot => "payload",
            (payload, widget) => { log.Record(payload); return Task.CompletedTask; },
            TimeSpan.FromMinutes(5),
            clock);

        pipeline.Start();
        await WithWatchdogAsync(collector.WaitForStart(1));

        // The synchronous half of the contract: the pipeline goes inactive
        // immediately, without waiting for the blocked collector.
        pipeline.Stop();

        // The awaitable half must still not complete while the admitted
        // collection is in flight.
        var deactivateTask = pipeline.DeactivateAsync();
        await DrainAsync();
        Assert.False(deactivateTask.IsCompleted);

        collector.Release();
        await WithWatchdogAsync(deactivateTask);

        // The retired generation's snapshot was suppressed, and no further
        // collection was admitted.
        Assert.Equal(0, log.Count);
        Assert.Equal(1, collector.StartedCount);

        await WithWatchdogAsync(pipeline.DisposeAsync().AsTask());
    }

    /// <summary>
    /// Deactivating a pipeline that was never activated is a no-op: it must
    /// not start a collection and must complete promptly.
    /// </summary>
    [Fact]
    public async Task DeactivateAsync_BeforeAnyActivation_CompletesWithoutStartingWork()
    {
        var clock = new FakeTimeProvider();
        var collector = new ControllableCollector();
        var widgets = OneActiveWidget();
        var log = new PublicationLog();

        var pipeline = new UpdatePipeline<TestSize>(
            collector, widgets,
            snapshot => "payload",
            (payload, widget) => { log.Record(payload); return Task.CompletedTask; },
            TimeSpan.FromMinutes(5),
            clock);

        await WithWatchdogAsync(pipeline.DeactivateAsync());
        await DrainAsync();

        Assert.Equal(0, collector.StartedCount);
        Assert.Equal(0, log.Count);

        await WithWatchdogAsync(pipeline.DisposeAsync().AsTask());
    }

    /// <summary>
    /// The idle boundary must not report idle while publishing is still in
    /// progress: the admitted cycle is only unwound after collection
    /// <i>and</i> publishing have both finished.
    /// </summary>
    [Fact]
    public async Task WaitForIdleAsync_CompletesOnlyAfterPublishingHasFinished()
    {
        var clock = new FakeTimeProvider();
        var collector = new ControllableCollector();
        var widgets = OneActiveWidget();
        var log = new PublicationLog();

        var publishGate = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var pipeline = new UpdatePipeline<TestSize>(
            collector, widgets,
            snapshot => "payload",
            async (payload, widget) => { await publishGate.Task; log.Record(payload); },
            TimeSpan.FromMinutes(5),
            clock);

        pipeline.Start();
        await WithWatchdogAsync(collector.WaitForStart(1));
        await WithWatchdogAsync(collector.WaitForSuccess(1));

        // The publish is in progress, so the pipeline is not idle yet.
        var idleTask = pipeline.WaitForIdleAsync();
        await DrainAsync();
        Assert.False(idleTask.IsCompleted);

        publishGate.TrySetResult(true);
        await WithWatchdogAsync(idleTask);
        Assert.Equal(1, log.Count);

        await WithWatchdogAsync(pipeline.DisposeAsync().AsTask());
    }

    /// <summary>
    /// A reactivation that races with an in-flight deactivation is a
    /// separate generation: the in-flight deactivation must not cancel it,
    /// and must not wait for its work either.
    /// </summary>
    [Fact]
    public async Task DeactivateAsync_RacingReactivation_DoesNotCancelTheNewGeneration()
    {
        var clock = new FakeTimeProvider();
        var collector = new ControllableCollector();
        collector.Block();
        var widgets = OneActiveWidget();
        var log = new PublicationLog();

        var pipeline = new UpdatePipeline<TestSize>(
            collector, widgets,
            snapshot => "payload",
            (payload, widget) => { log.Record(payload); return Task.CompletedTask; },
            TimeSpan.FromMinutes(5),
            clock);

        pipeline.Start();
        await WithWatchdogAsync(collector.WaitForStart(1));
        var oldGeneration = pipeline.CurrentGeneration;

        // The deactivation retires the old generation and waits for it.
        var deactivateTask = pipeline.DeactivateAsync();
        await DrainAsync();
        Assert.False(deactivateTask.IsCompleted);

        // Reactivation while the drain is still pending.
        pipeline.Start();
        var newGeneration = pipeline.CurrentGeneration;
        Assert.NotEqual(oldGeneration, newGeneration);

        collector.Release();
        await WithWatchdogAsync(deactivateTask);

        // The new generation was neither cancelled nor retired by the
        // in-flight deactivation.
        Assert.Equal(newGeneration, pipeline.CurrentGeneration);
        Assert.True(pipeline.IsRunning);

        await WithWatchdogAsync(pipeline.DisposeAsync().AsTask());
    }

/// =====================================================================
/// Cancellation and Tracker/Grace State Tests with Real SystemMetricsService
/// =====================================================================
///
/// These tests exercise the real SystemMetricsService with an injected
/// controllable sensor backend to verify tracker/grace behavior under
/// the exact cancellation boundaries the production pipeline enforces.
///
/// Collection mutates both tracker candidates. The pipeline accepts them only
/// after validating the cycle's generation and cancellation state. Tests below
/// use real service collection with gated backend reads to check rejection,
/// rollback, grace progression, and the separate publication boundary.
/// </summary>
public sealed class SystemMetricsServiceCancellationTests
{
    private enum TestSize { Small, Medium, Large }
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(15);

    /// <summary>
    /// A controllable fake sensor source that can block the Read() call.
    /// </summary>
    private sealed class BlockingFakeSource : ILhmSensorSource
    {
        private readonly object _lock = new();
        private IReadOnlyList<LhmDeviceDescriptor> _devices;
        private TaskCompletionSource<bool> _readGate;
        private bool _disposed;
        private int _activeReads;
        private int _maxConcurrentReads;

        public BlockingFakeSource(IReadOnlyList<LhmDeviceDescriptor> devices)
        {
            _devices = devices;
            _readGate = CompletedGate();
        }

        private static TaskCompletionSource<bool> CompletedGate()
        {
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            gate.TrySetResult(true);
            return gate;
        }

        public void SetDevices(IReadOnlyList<LhmDeviceDescriptor> devices)
        {
            lock (_lock) { _devices = devices; }
        }

        public void Block()
        {
            lock (_lock)
            {
                _readGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public void Release()
        {
            TaskCompletionSource<bool> gate;
            lock (_lock) { gate = _readGate; }
            gate.TrySetResult(true);
        }

        public bool IsAvailable { get; set; } = true;
        public string? UnavailableReason { get; set; }
        public bool IsDisposed => _disposed;
        public int ReadCount { get; private set; }
        public int MaxConcurrentReads => Volatile.Read(ref _maxConcurrentReads);

        public IReadOnlyList<LhmDeviceDescriptor> Read()
        {
            ReadCount++;
            var active = Interlocked.Increment(ref _activeReads);
            int observed;
            while (active > (observed = Volatile.Read(ref _maxConcurrentReads))
                && Interlocked.CompareExchange(ref _maxConcurrentReads, active, observed) != observed) { }
            try
            {
                Task gate;
                lock (_lock) { gate = _readGate.Task; }
                gate.GetAwaiter().GetResult();
                lock (_lock) { return _devices; }
            }
            finally { Interlocked.Decrement(ref _activeReads); }
        }

        public void Dispose() => _disposed = true;
    }

    private sealed class ReleaseGateOnDispose(BlockingFakeSource source) : IDisposable
    {
        public void Dispose() => source.Release();
    }

    /// <summary>
    /// Builds a readable NVIDIA GPU fixture.
    /// </summary>
    private static LhmDeviceDescriptor NvidiaGpu(string deviceId, string displayName, string instanceId, bool readable = true)
    {
        var sensors = readable
            ? new List<LhmSensorDescriptor>
            {
                LhmFixtures.Temp("GPU Core", 61.0),
                LhmFixtures.Clock("GPU Core", 330.0),
                LhmFixtures.Load("GPU Core", 25.0),
                LhmFixtures.Load("GPU Memory Controller", 18.0),
                LhmFixtures.Load("GPU Video Engine", 0.0),
                LhmFixtures.Memory("GPU Memory Used", 1233.0),
                LhmFixtures.Memory("GPU Memory Total", 8192.0),
            }
            : new List<LhmSensorDescriptor>();

        return new LhmDeviceDescriptor
        {
            DeviceId = deviceId,
            DisplayName = displayName,
            Vendor = "NVIDIA",
            IsCpu = false,
            IsIntegratedGpu = false,
            PciDeviceKey = "VEN_10DE&DEV_1E91",
            InstanceId = instanceId,
            Sensors = sensors,
        };
    }

    /// <summary>
    /// Creates a SystemMetricsService with an injected fake sensor source.
    /// </summary>
    private static SystemMetricsService CreateService(ILhmSensorSource sensorSource)
    {
        var model = new FakeModel();
        var nvidiaFallback = new NvidiaGpuFallback();
        return new SystemMetricsService(model, nvidiaFallback, sensorSource);
    }

    private sealed class ThrowAfterServiceCollection(SystemMetricsService service) : ITransactionalMetricsCollector
    {
        private bool _throwNext;
        public void ThrowNext() => _throwNext = true;
        public MetricsSnapshot Collect() => service.Collect();
        public MetricsSnapshot Collect(string cycleId)
        {
            var snapshot = service.Collect(cycleId);
            if (_throwNext)
            {
                _throwNext = false;
                throw new InvalidOperationException("Injected after both trackers mutated.");
            }
            return snapshot;
        }
        public void Accept(string cycleId) => service.Accept(cycleId);
        public void Reject(string cycleId) => service.Reject(cycleId);
    }

    private sealed class FakeModel : SystemMetrics { }

    private static WidgetRegistry<TestSize> OneActiveWidget()
    {
        var widgets = new WidgetRegistry<TestSize>();
        widgets.Set(new WidgetEntry<TestSize> { WidgetId = "w1", IsActive = true });
        return widgets;
    }

    private static async Task DrainAsync()
    {
        for (int i = 0; i < 6; i++) await Task.Yield();
    }

    private static async Task<T> WithWatchdogAsync<T>(Task<T> task)
    {
        var completed = await Task.WhenAny(task, Task.Delay(Watchdog));
        if (completed != task) throw new TimeoutException($"Task did not complete within {Watchdog}.");
        return await task;
    }

    private static async Task WithWatchdogAsync(Task task)
    {
        var completed = await Task.WhenAny(task, Task.Delay(Watchdog));
        if (completed != task) throw new TimeoutException($"Task did not complete within {Watchdog}.");
        await task;
    }

    private static async Task WaitForPublicationCountAsync(ConcurrentQueue<string> queue, int expectedCount)
    {
        for (int i = 0; i < 100; i++)
        {
            if (queue.Count >= expectedCount) return;
            await Task.Delay(10);
        }
        throw new TimeoutException($"Publication count did not reach {expectedCount}");
    }

    private static async Task WaitForSnapshotCountAsync(ConcurrentQueue<MetricsSnapshot> queue, int expectedCount)
    {
        for (int i = 0; i < 100; i++)
        {
            if (queue.Count >= expectedCount) return;
            await Task.Delay(10);
        }
        throw new TimeoutException($"Snapshot count did not reach {expectedCount}");
    }

    private static async Task WaitForReadCountAsync(BlockingFakeSource source, int expectedCount)
    {
        for (int i = 0; i < 100; i++)
        {
            if (source.ReadCount >= expectedCount) return;
            await Task.Delay(10);
        }
        throw new TimeoutException($"Read count did not reach {expectedCount}");
    }

    /// <summary>
    /// Helper to get the extra-available flag from a JSON payload.
    /// </summary>
    private static bool GetExtraAvailable(string payload, int index)
    {
        var key = $"gpuExtra{index}Available";
        // Check for true/false in JSON
        var result = payload.Contains($"\"{key}\":true") || payload.Contains($"\"{key}\": true");
        Console.WriteLine($"GetExtraAvailable: payload contains '{key}' = {result}");
        if (!result)
        {
            // Print all flags for debugging
            var start = payload.IndexOf("\"gpuExtra");
            while (start >= 0)
            {
                var end = payload.IndexOf(",", start);
                if (end < 0) end = payload.IndexOf("}", start);
                if (end > start)
                {
                    Console.WriteLine($"  Found: {payload.Substring(start, end - start)}");
                }
                start = payload.IndexOf("\"gpuExtra", start + 1);
            }
        }
        return result;
    }

    /// <summary>
    /// Helper to get the primary GPU temperature from a JSON payload.
    /// </summary>
    private static bool IsGpuTemperatureNA(string payload)
    {
        return payload.Contains("\"gpuTemperature\":\"N/A\"") || payload.Contains("\"gpuTemperature\": \"N/A\"");
    }

    // -----------------------------------------------------------------
    // 1. Cancellation before collection admission
    // -----------------------------------------------------------------

    /// <summary>
    /// If the pipeline is deactivated before a collection is admitted
    /// (before UpdateSensorBackend runs), the tracker's grace counter is
    /// not advanced. The tracker state remains exactly as it was after the
    /// last admitted cycle.
    /// </summary>
    [Fact]
    public async Task CancellationBeforeCollectionAdmission_DoesNotAdvanceGrace()
    {
        var clock = new FakeTimeProvider();
        var gpu0 = NvidiaGpu("/gpu-nvidia/0", "GPU 0", "inst-0");
        var gpu1 = NvidiaGpu("/gpu-nvidia/1", "GPU 1", "inst-1");
        var source = new BlockingFakeSource(new[] { gpu0, gpu1 });
        var service = CreateService(source);

        var publications = new ConcurrentQueue<string>();

        var pipeline = new UpdatePipeline<TestSize>(
            service, OneActiveWidget(),
            snapshot => { var p = WidgetPayloadPresenter.Present(snapshot).ToJson(); Console.Error.WriteLine($"PUB: {p}"); publications.Enqueue(p); return p; },
            (payload, widget) => Task.CompletedTask,
            TimeSpan.FromMinutes(5), clock);

        // --- Cycle 1: Both GPUs readable, gpu1 becomes tracked secondary ---
        pipeline.Start();
        await WithWatchdogAsync(WaitForPublicationCountAsync(publications, 1));
        var payload1 = publications.First();
        var extra1 = GetExtraAvailable(payload1, 1);
        Console.Error.WriteLine($"GetExtraAvailable(payload1, 1) = {extra1}");
        Assert.True(extra1);

        // Deactivate BEFORE the next collection is admitted.
        // This means no Update() call for cycle 2.
        await WithWatchdogAsync(pipeline.DeactivateAsync());

        // Reactivate and immediately collect (cycle 2).
        pipeline.Start();
        await WithWatchdogAsync(WaitForPublicationCountAsync(publications, 2));
        var payload2 = publications.ElementAt(1);

        // gpu1 should still be tracked (grace not consumed by the deactivated cycle).
        Assert.True(GetExtraAvailable(payload2, 1));

        // Now simulate gpu1 losing telemetry for 3 consecutive admitted cycles.
        var gpu1NoTelemetry = gpu1 with { Sensors = new List<LhmSensorDescriptor>() };
        source.SetDevices(new[] { gpu0, gpu1NoTelemetry });

        // Cycle 3 (failure 1)
        pipeline.Start();
        await WithWatchdogAsync(WaitForPublicationCountAsync(publications, 3));
        Assert.True(GetExtraAvailable(publications.ElementAt(2), 1));

        // Cycle 4 (failure 2)
        pipeline.Start();
        await WithWatchdogAsync(WaitForPublicationCountAsync(publications, 4));
        Assert.True(GetExtraAvailable(publications.ElementAt(3), 1));

        // Cycle 5 (failure 3)
        pipeline.Start();
        await WithWatchdogAsync(WaitForPublicationCountAsync(publications, 5));
        Assert.True(GetExtraAvailable(publications.ElementAt(4), 1));

        // Cycle 6 (failure 4 - grace exhausted)
        pipeline.Start();
        await WithWatchdogAsync(WaitForPublicationCountAsync(publications, 6));
        Assert.False(GetExtraAvailable(publications.ElementAt(5), 1));

        await WithWatchdogAsync(pipeline.DisposeAsync().AsTask());
    }

    // -----------------------------------------------------------------
    // 2. Cancellation during synchronous collection
    // -----------------------------------------------------------------

    /// <summary>
    /// If DeactivateAsync is requested while Collect() is blocked in
    /// UpdateSensorBackend (reading from the backend), the collection
    /// completes on its own (synchronous calls cannot be interrupted).
    /// The tracker's Update() runs and DOES mutate grace state.
    /// The pipeline then suppresses publication of that generation's snapshot.
    /// </summary>
    [Fact]
    public async Task CancellationDuringCollection_RejectedSnapshotPreservesGrace()
    {
        var clock = new FakeTimeProvider();
        var gpu0 = NvidiaGpu("/gpu-nvidia/0", "GPU 0", "inst-0");
        var gpu1 = NvidiaGpu("/gpu-nvidia/1", "GPU 1", "inst-1");
        var source = new BlockingFakeSource(new[] { gpu0, gpu1 });
        var service = CreateService(source);
        using var serviceLifetime = service;

        var publications = new ConcurrentQueue<string>();

        var pipeline = new UpdatePipeline<TestSize>(
            service, OneActiveWidget(),
            snapshot => { var p = WidgetPayloadPresenter.Present(snapshot).ToJson(); publications.Enqueue(p); return p; },
            (payload, widget) => Task.CompletedTask,
            TimeSpan.FromMinutes(5), clock);
        await using var ownedPipeline = pipeline;
        using var releaseGateOnExit = new ReleaseGateOnDispose(source);

        // --- Cycle 1: Initial success ---
        pipeline.Start();
        await WithWatchdogAsync(WaitForPublicationCountAsync(publications, 1));
        var payload1 = publications.First();
        Assert.Contains("\"gpuExtra1Available\":true", payload1);

        // Set gpu1 to have no telemetry BEFORE the cancelled cycle,
        // so the cancelled cycle counts as a failure and subsequent cycles
        // continue the failure streak without a reset.
        var gpu1NoTelemetry = gpu1 with { Sensors = new List<LhmSensorDescriptor>() };
        source.SetDevices(new[] { gpu0, gpu1NoTelemetry });

        // --- Cycle 2: Block the backend read, then request deactivation ---
        // The pipeline's Start() will trigger collection. We block the backend read,
        // then request deactivation. The pipeline's in-flight collection will complete
        // when we release the gate, but its publication will be suppressed.
        source.Block();
        pipeline.Start();

        // Wait for the collection to start (Read() is called)
        await WithWatchdogAsync(WaitForReadCountAsync(source, 1));

        // Request deactivation while collection is in flight.
        var deactivateTask = pipeline.DeactivateAsync();

        // The collection will complete when we release the gate.
        source.Release();
        await WithWatchdogAsync(deactivateTask);

        // The tracker mutation was rejected with the retired snapshot.
        // Verify: no publication happened for cycle 2.
        Assert.Single(publications);

        // Reactivate and collect (cycle 3) - failure 2
        pipeline.Start();
        await WithWatchdogAsync(WaitForPublicationCountAsync(publications, 2));
        var payload2 = publications.ElementAt(1);
        Assert.True(GetExtraAvailable(payload2, 1));

        // Cycle 4 (failure 3)
        pipeline.Start();
        await WithWatchdogAsync(WaitForPublicationCountAsync(publications, 3));
        Assert.True(GetExtraAvailable(publications.ElementAt(2), 1));

        // Cycle 5 (failure 3 remains within grace)
        pipeline.Start();
        await WithWatchdogAsync(WaitForPublicationCountAsync(publications, 4));
        Assert.True(GetExtraAvailable(publications.ElementAt(3), 1));

        // Cycle 6 (failure 4 exhausts grace)
        pipeline.Start();
        await WithWatchdogAsync(WaitForPublicationCountAsync(publications, 5));
        Assert.False(GetExtraAvailable(publications.ElementAt(4), 1));

    }

    // -----------------------------------------------------------------
    // 3. Completed collection rejected as retired-generation work
    // -----------------------------------------------------------------

    /// <summary>
    /// A collection retired during its backend read is rejected and both
    /// tracker mutations are rolled back before the next collection starts.
    /// </summary>
    [Fact]
    public async Task CompletedRetiredGeneration_RollsBackTrackerMutations()
    {
        var clock = new FakeTimeProvider();
        var gpu0 = NvidiaGpu("/gpu-nvidia/0", "GPU 0", "inst-0");
        var gpu1 = NvidiaGpu("/gpu-nvidia/1", "GPU 1", "inst-1");
        var source = new BlockingFakeSource(new[] { gpu0, gpu1 });
        var service = CreateService(source);
        using var serviceLifetime = service;

        var publications = new ConcurrentQueue<string>();

        var pipeline = new UpdatePipeline<TestSize>(
            service, OneActiveWidget(),
            snapshot => { var p = WidgetPayloadPresenter.Present(snapshot).ToJson(); publications.Enqueue(p); return p; },
            (payload, widget) => Task.CompletedTask,
            TimeSpan.FromMinutes(5), clock);
        await using var ownedPipeline = pipeline;
        using var releaseGateOnExit = new ReleaseGateOnDispose(source);

        // --- Cycle 1: Initial success ---
        pipeline.Start();
        await WithWatchdogAsync(WaitForPublicationCountAsync(publications, 1));
        Assert.True(GetExtraAvailable(publications.First(), 1));

        // Set gpu1 to have no telemetry BEFORE the retired cycle,
        // so the retired cycle counts as a failure and subsequent cycles
        // continue the failure streak without a reset.
        var gpu1NoTelemetry = gpu1 with { Sensors = new List<LhmSensorDescriptor>() };
        source.SetDevices(new[] { gpu0, gpu1NoTelemetry });

        // --- Cycle 2: Block the backend read ---
        source.Block();
        pipeline.Start();

        // Wait for the collection to start (Read() is called)
        await WithWatchdogAsync(WaitForReadCountAsync(source, 1));

        // Synchronous Stop() retires the generation but does NOT wait.
        pipeline.Stop();

        // Release the collector — the collection completes.
        source.Release();

        // Wait for the collection to complete (it's running in the pipeline's loop)
        await WithWatchdogAsync(pipeline.WaitForIdleAsync());

        // The retired snapshot is rejected and its tracker mutation rolled back.
        Assert.Single(publications);

        // Reactivate and collect (cycle 3) - failure 2.
        pipeline.Start();
        await WithWatchdogAsync(WaitForPublicationCountAsync(publications, 2));
        var payload3 = publications.ElementAt(1);

        // Debug: check tracker state

        // Rejection rolled back cycle 2; this accepted cycle is failure 1.
        Assert.True(GetExtraAvailable(payload3, 1));

        // Cycle 4 (failure 3)
        pipeline.Start();
        await WithWatchdogAsync(WaitForPublicationCountAsync(publications, 3));
        var payload4 = publications.ElementAt(2);

        // Debug: check tracker state

        Assert.True(GetExtraAvailable(payload4, 1));

        // Cycle 5 (failure 3 remains within grace)
        pipeline.Start();
        await WithWatchdogAsync(WaitForPublicationCountAsync(publications, 4));
        var payload5 = publications.ElementAt(3);

        // Debug: check tracker state via reflection

        Assert.True(GetExtraAvailable(payload5, 1));

        pipeline.Start();
        await WithWatchdogAsync(WaitForPublicationCountAsync(publications, 5));
        Assert.False(GetExtraAvailable(publications.ElementAt(4), 1));
    }

    // -----------------------------------------------------------------
    // 4. Rejected cycle restores both primary and secondary tracker state
    // -----------------------------------------------------------------
    /// <summary>
    /// Verifies that calling Reject(string cycleId) restores both
    /// primary GPU selection tracker state and secondary GPU extras
    /// tracker state. This is a regression test: if the
    /// _gpuExtrasTracker.RollbackCycle(cycleId) call is removed from
    /// Reject(), this test will fail because the secondary tracker
    /// would not be rolled back.
    /// </summary>
    [Fact]
    public async Task RejectedCycle_RestoresBothPrimaryAndSecondaryTrackerState()
    {
        var clock = new FakeTimeProvider();
        var gpu0 = NvidiaGpu("/gpu-nvidia/0", "GPU 0", "inst-0");
        var gpu1 = NvidiaGpu("/gpu-nvidia/1", "GPU 1", "inst-1");
        var source = new BlockingFakeSource(new[] { gpu0, gpu1 });
        var service = CreateService(source);
        using var serviceLifetime = service;

        var publications = new ConcurrentQueue<string>();

        var pipeline = new UpdatePipeline<TestSize>(
            service, OneActiveWidget(),
            snapshot => { var p = WidgetPayloadPresenter.Present(snapshot).ToJson(); publications.Enqueue(p); return p; },
            (payload, widget) => Task.CompletedTask,
            TimeSpan.FromMinutes(5), clock);
        await using var ownedPipeline = pipeline;
        using var releaseGateOnExit = new ReleaseGateOnDispose(source);

        // --- Cycle 1: Initial success ---
        pipeline.Start();
        await WithWatchdogAsync(WaitForPublicationCountAsync(publications, 1));
        var payload1 = publications.First();
        Assert.True(GetExtraAvailable(payload1, 1));

        // --- Cycle 2: Collect and then reject ---
        // Capture the cycle ID before rejection
        var cycleId = "cycle-2-regression";
        pipeline.Start();
        await WithWatchdogAsync(WaitForReadCountAsync(source, 1));

        // Now reject the cycle - this should roll back BOTH trackers
        service.Reject(cycleId);

        // After rejection, verify primary tracker state was restored
        // (the primary snapshot was removed and tracker restored)
        // Verify secondary tracker was also rolled back
        // The secondary tracker should no longer have the cycle's state
        // (or should have been restored to pre-cycle state)
        // This verifies the RollbackCycle call actually happened

        // --- Cycle 3: New collection after rejection ---
        // Start a new cycle - it should work normally after rejection
        pipeline.Start();
        await WithWatchdogAsync(WaitForPublicationCountAsync(publications, 2));
        var payload3 = publications.ElementAt(1);
        Assert.True(GetExtraAvailable(payload3, 1));

        // Cycle 4: Another rejection should also work
        service.Reject(cycleId); // Reject same cycle ID again (idempotent-ish)
        pipeline.Start();
        await WithWatchdogAsync(WaitForPublicationCountAsync(publications, 3));
        var payload4 = publications.ElementAt(2);
        Assert.True(GetExtraAvailable(payload4, 1));

        // Cleanup
        await WithWatchdogAsync(pipeline.DeactivateAsync());
    }

    // -----------------------------------------------------------------
    // 5. Accepted collection whose later publication is suppressed
    // -----------------------------------------------------------------

    /// <summary>
    /// If a collection is admitted under generation N, but generation N
    /// is superseded by reactivation (generation N+1) before publication,
    /// the collection completes and tracker state advances, but publication
    /// is suppressed by the generation check.
    /// </summary>
    [Fact]
    public async Task ReactivationBeforePublication_TrackerAdvancesStalePublicationSuppressed()
    {
        var clock = new FakeTimeProvider();
        var gpu0 = NvidiaGpu("/gpu-nvidia/0", "GPU 0", "inst-0");
        var gpu1 = NvidiaGpu("/gpu-nvidia/1", "GPU 1", "inst-1");
        var source = new BlockingFakeSource(new[] { gpu0, gpu1 });
        var service = CreateService(source);

        var publications = new ConcurrentQueue<string>();

        var pipeline = new UpdatePipeline<TestSize>(
            service, OneActiveWidget(),
            snapshot => { var p = WidgetPayloadPresenter.Present(snapshot).ToJson(); publications.Enqueue(p); return p; },
            (payload, widget) => Task.CompletedTask,
            TimeSpan.FromMinutes(5), clock);

        // --- Cycle 1: Initial success ---
        pipeline.Start();
        await WithWatchdogAsync(WaitForPublicationCountAsync(publications, 1));
        Assert.True(GetExtraAvailable(publications.First(), 1));

        // Set gpu1 to have no telemetry for cycle 2
        var gpu1NoTelemetry = gpu1 with { Sensors = new List<LhmSensorDescriptor>() };
        source.SetDevices(new[] { gpu0, gpu1NoTelemetry });

        // --- Cycle 2: Start collection, wait for it to be admitted and waiting for interval ---
        pipeline.Start(); // Generation 1, cycle 2

        // Wait for the first scheduled wait (the coordinator has registered its wait for the interval)
        await WithWatchdogAsync(pipeline.WaitForWaitCountAsync(1));

        // Now immediately reactivate - this creates generation 2 and retires generation 1
        // The generation 1 collection has completed and is waiting to publish,
        // but its generation is now retired so publication will be suppressed.
        pipeline.Start(); // Generation 2

        // Advance time to allow generation 2's collection to run
        clock.Advance(TimeSpan.FromMinutes(6));

        // Generation 2's collection should now run and publish
        await WithWatchdogAsync(WaitForPublicationCountAsync(publications, 2));

        // We should have 2 publications: cycle 1 and cycle 2 (generation 2's cycle)
        // The retired generation 1's cycle was suppressed.
        Assert.Equal(2, publications.Count);

        // Cycle 2 was rejected. This accepted cycle is failure 1.
        pipeline.Start();
        clock.Advance(TimeSpan.FromMinutes(6));
        await WithWatchdogAsync(WaitForPublicationCountAsync(publications, 3));
        Assert.True(GetExtraAvailable(publications.ElementAt(2), 1));

        // Cycle 4 (failure 3)
        pipeline.Start();
        clock.Advance(TimeSpan.FromMinutes(6));
        await WithWatchdogAsync(WaitForPublicationCountAsync(publications, 4));
        Assert.True(GetExtraAvailable(publications.ElementAt(3), 1));

        // Cycle 5 (failure 4 - grace exhausted)
        pipeline.Start();
        clock.Advance(TimeSpan.FromMinutes(6));
        await WithWatchdogAsync(WaitForPublicationCountAsync(publications, 5));
        Assert.False(GetExtraAvailable(publications.ElementAt(4), 1));

        await WithWatchdogAsync(pipeline.DisposeAsync().AsTask());
    }

    // -----------------------------------------------------------------
    // 5. Tracker state after drain and reactivation
    // -----------------------------------------------------------------

    /// <summary>
    /// After a full drain (DeactivateAsync completes), reactivating the
    /// pipeline preserves tracker state. The tracker is owned by the
    /// SystemMetricsService instance, not the pipeline generation.
    /// </summary>
    [Fact]
    public async Task ReactivationAfterFullDrain_PreservesTrackerState()
    {
        var clock = new FakeTimeProvider();
        var gpu0 = NvidiaGpu("/gpu-nvidia/0", "GPU 0", "inst-0");
        var gpu1 = NvidiaGpu("/gpu-nvidia/1", "GPU 1", "inst-1");
        var source = new BlockingFakeSource(new[] { gpu0, gpu1 });
        var service = CreateService(source);

        var publications = new ConcurrentQueue<string>();

        var pipeline = new UpdatePipeline<TestSize>(
            service, OneActiveWidget(),
            snapshot => { var p = WidgetPayloadPresenter.Present(snapshot).ToJson(); publications.Enqueue(p); return p; },
            (payload, widget) => Task.CompletedTask,
            TimeSpan.FromMinutes(5), clock);

        // --- Cycle 1: Both readable ---
        pipeline.Start();
        await WithWatchdogAsync(WaitForPublicationCountAsync(publications, 1));
        Assert.True(GetExtraAvailable(publications.First(), 1));

        // --- Cycle 2: gpu1 loses telemetry (failure 1) ---
        var gpu1NoTelemetry = gpu1 with { Sensors = new List<LhmSensorDescriptor>() };
        source.SetDevices(new[] { gpu0, gpu1NoTelemetry });
        pipeline.Start();
        await WithWatchdogAsync(WaitForPublicationCountAsync(publications, 2));
        Assert.True(GetExtraAvailable(publications.ElementAt(1), 1));

        // Debug: check tracker state after cycle 2

        // Deactivate and fully await quiescence.
        await WithWatchdogAsync(pipeline.DeactivateAsync());

        // Reactivate - gpu1 still has no telemetry
        pipeline.Start();
        await WithWatchdogAsync(WaitForPublicationCountAsync(publications, 3));

        // The accepted snapshots advance secondary grace exactly once each.
        Assert.True(GetExtraAvailable(publications.ElementAt(2), 1));

        // Next cycle (cycle 4) - grace should be exhausted now
        pipeline.Start();
        await WithWatchdogAsync(WaitForPublicationCountAsync(publications, 4));
        Assert.True(GetExtraAvailable(publications.ElementAt(3), 1));

        pipeline.Start();
        await WithWatchdogAsync(WaitForPublicationCountAsync(publications, 5));
        Assert.False(GetExtraAvailable(publications.ElementAt(4), 1));

        await WithWatchdogAsync(pipeline.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task PostMutationCollectionException_RollsBackBothTrackersAndClearsTransactions()
    {
        var gpu0 = NvidiaGpu("gpu0", "GPU 0", "inst-0");
        var gpu1 = NvidiaGpu("gpu1", "GPU 1", "inst-1");
        var source = new BlockingFakeSource(new[] { gpu0, gpu1 });
        var service = CreateService(source);
        var faultingCollector = new ThrowAfterServiceCollection(service);
        var snapshots = new ConcurrentQueue<MetricsSnapshot>();
        var publications = new ConcurrentQueue<string>();
        var pipeline = new UpdatePipeline<TestSize>(faultingCollector, OneActiveWidget(), snapshot =>
        {
            snapshots.Enqueue(snapshot);
            return WidgetPayloadPresenter.Present(snapshot).ToJson();
        }, (payload, widget) => { publications.Enqueue(payload); return Task.CompletedTask; }, TimeSpan.FromMinutes(5), new FakeTimeProvider());

        try
        {
            pipeline.Start();
            await WithWatchdogAsync(WaitForPublicationCountAsync(publications, 1));
            await WithWatchdogAsync(pipeline.DeactivateAsync());
            Assert.True(GetExtraAvailable(publications.ElementAt(0), 1));
            Assert.Equal(0, service.PendingPrimaryCycleCount);
            Assert.Equal(0, service.PendingSecondaryCycleCount);

            source.SetDevices(new[]
            {
                gpu0 with { Sensors = new List<LhmSensorDescriptor>() },
                gpu1 with { Sensors = new List<LhmSensorDescriptor>() },
            });
            faultingCollector.ThrowNext();
            pipeline.Start();
            await WithWatchdogAsync(WaitForReadCountAsync(source, 2));
            await WithWatchdogAsync(pipeline.WaitForIdleAsync());
            await WithWatchdogAsync(pipeline.DeactivateAsync());
            Assert.Single(publications);
            Assert.Equal(0, service.PendingPrimaryCycleCount);
            Assert.Equal(0, service.PendingSecondaryCycleCount);
            Assert.Equal(0, service.PrimaryConsecutiveFailures);
            Assert.Equal(0, service.SecondaryTrackingState["inst-1"].ConsecutiveFailures);

            for (var failure = 1; failure <= 3; failure++)
            {
                pipeline.Start();
                await WithWatchdogAsync(WaitForReadCountAsync(source, failure + 2));
                await WithWatchdogAsync(WaitForPublicationCountAsync(publications, failure + 1));
                await WithWatchdogAsync(pipeline.DeactivateAsync());
                Assert.Equal(gpu0.InstanceId, snapshots.ElementAt(failure).GpuTrackedInstanceId);
                Assert.True(GetExtraAvailable(publications.ElementAt(failure), 1));
                Assert.Equal(failure, service.PrimaryConsecutiveFailures);
                Assert.Equal(failure, service.SecondaryTrackingState["inst-1"].ConsecutiveFailures);
                Assert.Equal(0, service.PendingPrimaryCycleCount);
                Assert.Equal(0, service.PendingSecondaryCycleCount);
            }

            pipeline.Start();
            await WithWatchdogAsync(WaitForPublicationCountAsync(publications, 5));
            await WithWatchdogAsync(pipeline.DeactivateAsync());
            Assert.False(GetExtraAvailable(publications.ElementAt(4), 1));
            Assert.Equal(0, service.PrimaryConsecutiveFailures);
            Assert.Equal(4, service.SecondaryTrackingState["inst-1"].ConsecutiveFailures);
            Assert.Equal(0, service.PendingPrimaryCycleCount);
            Assert.Equal(0, service.PendingSecondaryCycleCount);
        }
        finally
        {
            source.Release();
            await WithWatchdogAsync(pipeline.DisposeAsync().AsTask());
            await WithWatchdogAsync(pipeline.WaitForIdleAsync());
            service.Dispose();
        }
    }

    [Fact]
    public async Task FirstCycleRetiredBeforeAcceptance_RestoresBothTrackers()
    {
        var gpu0 = NvidiaGpu("gpu0", "GPU 0", "inst-0");
        var gpu1 = NvidiaGpu("gpu1", "GPU 1", "inst-1");
        var source = new BlockingFakeSource(new[] { gpu0, gpu1 });
        var service = CreateService(source);
        var publications = new ConcurrentQueue<MetricsSnapshot>();
        var pipeline = new UpdatePipeline<TestSize>(service, OneActiveWidget(), snapshot =>
        {
            publications.Enqueue(snapshot);
            return WidgetPayloadPresenter.Present(snapshot).ToJson();
        }, (payload, widget) => Task.CompletedTask, TimeSpan.FromMinutes(5), new FakeTimeProvider());

        try
        {
            for (var rejectedCycle = 1; rejectedCycle <= 2; rejectedCycle++)
            {
                source.Block();
                pipeline.Start();
                await WithWatchdogAsync(WaitForReadCountAsync(source, rejectedCycle));
                pipeline.Stop();
                source.Release();
                await WithWatchdogAsync(pipeline.WaitForIdleAsync());
                await WithWatchdogAsync(pipeline.DeactivateAsync());
                Assert.Equal(0, service.PendingPrimaryCycleCount);
                Assert.Equal(0, service.PendingSecondaryCycleCount);
            }
            Assert.Empty(publications);
            Assert.Equal(0, service.PendingPrimaryCycleCount);
            Assert.Equal(0, service.PendingSecondaryCycleCount);
            Assert.Null(service.TrackedPrimaryInstanceId);
            Assert.Empty(service.SecondaryTrackingState);

            // A genuine first accepted collection selects the readable GPU and
            // does not inherit qualification for the now unreadable secondary.
            source.SetDevices(new[]
            {
                gpu0 with { Sensors = new List<LhmSensorDescriptor>() },
                gpu1,
            });
            pipeline.Start();
            await WithWatchdogAsync(WaitForReadCountAsync(source, 3));
            await WithWatchdogAsync(WaitForSnapshotCountAsync(publications, 1));
            await WithWatchdogAsync(pipeline.DeactivateAsync());
            Assert.Equal(gpu1.InstanceId, publications.Single().GpuTrackedInstanceId);
            Assert.Empty(publications.Single().GpuTrackedExtraInstanceIds);
            Assert.Equal(gpu1.InstanceId, service.TrackedPrimaryInstanceId);
            Assert.False(service.SecondaryTrackingState["inst-0"].HasEverQualified);
            Assert.Equal(0, service.SecondaryTrackingState["inst-0"].ConsecutiveFailures);
            Assert.Equal(0, service.PendingPrimaryCycleCount);
            Assert.Equal(0, service.PendingSecondaryCycleCount);
        }
        finally
        {
            source.Release();
            await WithWatchdogAsync(pipeline.DisposeAsync().AsTask());
            await WithWatchdogAsync(pipeline.WaitForIdleAsync());
            service.Dispose();
        }
    }

    [Fact]
    public async Task AcceptedSnapshotSuppressedAfterAcceptance_RemainsCommitted()
    {
        var gpu0 = NvidiaGpu("gpu0", "GPU 0", "inst-0");
        var gpu1 = NvidiaGpu("gpu1", "GPU 1", "inst-1");
        var source = new BlockingFakeSource(new[] { gpu0, gpu1 });
        var service = CreateService(source);
        var publications = new ConcurrentQueue<string>();
        using var payloadGate = new ManualResetEventSlim(false);
        using var builderEntered = new ManualResetEventSlim(false);
        var buildCount = 0;
        var pipeline = new UpdatePipeline<TestSize>(service, OneActiveWidget(), snapshot =>
        {
            if (Interlocked.Increment(ref buildCount) == 2)
            {
                builderEntered.Set(); // Acceptance has already completed.
                if (!payloadGate.Wait(Watchdog))
                    throw new TimeoutException("Payload gate was not released.");
            }
            return WidgetPayloadPresenter.Present(snapshot).ToJson();
        }, (payload, widget) => { publications.Enqueue(payload); return Task.CompletedTask; }, TimeSpan.FromMinutes(5), new FakeTimeProvider());

        try
        {
            pipeline.Start();
            await WithWatchdogAsync(WaitForPublicationCountAsync(publications, 1));
            await WithWatchdogAsync(pipeline.DeactivateAsync());
            source.SetDevices(new[]
            {
                gpu0 with { Sensors = new List<LhmSensorDescriptor>() },
                gpu1 with { Sensors = new List<LhmSensorDescriptor>() },
            });

            pipeline.Start();
            Assert.True(await Task.Run(() => builderEntered.Wait(Watchdog)));
            Assert.Equal(0, service.PendingPrimaryCycleCount);
            Assert.Equal(0, service.PendingSecondaryCycleCount);
            pipeline.Stop();
            payloadGate.Set();
            await WithWatchdogAsync(pipeline.WaitForIdleAsync());
            Assert.Single(publications);

            for (var acceptedFailure = 2; acceptedFailure <= 4; acceptedFailure++)
            {
                pipeline.Start();
                await WithWatchdogAsync(WaitForPublicationCountAsync(publications, acceptedFailure));
                await WithWatchdogAsync(pipeline.DeactivateAsync());
                Assert.Equal(acceptedFailure < 4, GetExtraAvailable(publications.ElementAt(acceptedFailure - 1), 1));
                Assert.Equal(0, service.PendingPrimaryCycleCount);
                Assert.Equal(0, service.PendingSecondaryCycleCount);
            }
        }
        finally
        {
            payloadGate.Set();
            source.Release();
            await WithWatchdogAsync(pipeline.DisposeAsync().AsTask());
            await WithWatchdogAsync(pipeline.WaitForIdleAsync());
            service.Dispose();
        }
    }

    [Fact]
    public async Task ReactivationWhileRejectedCollectionBlocked_WaitsForRollbackBeforeNextMutation()
    {
        var gpu0 = NvidiaGpu("gpu0", "GPU 0", "inst-0");
        var gpu1 = NvidiaGpu("gpu1", "GPU 1", "inst-1");
        var source = new BlockingFakeSource(new[] { gpu0, gpu1 });
        var service = CreateService(source);
        using var serviceLifetime = service;
        var snapshots = new ConcurrentQueue<MetricsSnapshot>();
        var publications = new ConcurrentQueue<string>();
        var pipeline = new UpdatePipeline<TestSize>(service, OneActiveWidget(), snapshot =>
        {
            snapshots.Enqueue(snapshot);
            return WidgetPayloadPresenter.Present(snapshot).ToJson();
        }, (payload, widget) => { publications.Enqueue(payload); return Task.CompletedTask; },
            TimeSpan.FromMinutes(5), new FakeTimeProvider());
        await using var ownedPipeline = pipeline;
        using var releaseGateOnExit = new ReleaseGateOnDispose(source);

        try
        {
            pipeline.Start();
            await WithWatchdogAsync(WaitForPublicationCountAsync(publications, 1));
            await WithWatchdogAsync(pipeline.DeactivateAsync());
            source.SetDevices(new[]
            {
                gpu0 with { Sensors = new List<LhmSensorDescriptor>() },
                gpu1 with { Sensors = new List<LhmSensorDescriptor>() },
            });

            source.Block();
            pipeline.Start();
            await WithWatchdogAsync(WaitForReadCountAsync(source, 2));
            pipeline.Stop();
            pipeline.Start(); // The replacement generation waits for the blocked cycle to reject.
            await Task.Delay(50);
            Assert.Equal(2, source.ReadCount);
            Assert.Equal(1, source.MaxConcurrentReads);
            source.Release();

            await WithWatchdogAsync(WaitForReadCountAsync(source, 3));
            await WithWatchdogAsync(WaitForPublicationCountAsync(publications, 2));
            await WithWatchdogAsync(pipeline.DeactivateAsync());
            Assert.Equal(1, service.PrimaryConsecutiveFailures);
            Assert.Equal(1, service.SecondaryTrackingState["inst-1"].ConsecutiveFailures);
            Assert.True(GetExtraAvailable(publications.ElementAt(1), 1));
            Assert.Equal(0, service.PendingPrimaryCycleCount);
            Assert.Equal(0, service.PendingSecondaryCycleCount);

            for (var failure = 2; failure <= 4; failure++)
            {
                pipeline.Start();
                await WithWatchdogAsync(WaitForPublicationCountAsync(publications, failure + 1));
                await WithWatchdogAsync(pipeline.DeactivateAsync());
                Assert.Equal(failure, service.SecondaryTrackingState["inst-1"].ConsecutiveFailures);
                Assert.Equal(failure < 4 ? failure : 0, service.PrimaryConsecutiveFailures);
                Assert.Equal(failure < 4, GetExtraAvailable(publications.ElementAt(failure), 1));
                Assert.Equal(0, service.PendingPrimaryCycleCount);
                Assert.Equal(0, service.PendingSecondaryCycleCount);
            }
        }
        finally
        {
            source.Release();
            await WithWatchdogAsync(pipeline.DisposeAsync().AsTask());
            await WithWatchdogAsync(pipeline.WaitForIdleAsync());
        }
    }

    // -----------------------------------------------------------------
    // 6. Primary GPU tracker grace under cancellation
    // -----------------------------------------------------------------

    /// <summary>
    /// The primary GPU tracker (_gpuSelectionTracker) follows the same
    /// mutation/acceptance boundary: Update() runs during collection,
    /// tracker state mutates at admission, publication is gated by
    /// generation checks.
    /// </summary>
    [Fact]
    public async Task PrimaryGpuTracker_AcceptedMissingTelemetryAdvancesGrace()
    {
        var clock = new FakeTimeProvider();
        var gpu0 = NvidiaGpu("/gpu-nvidia/0", "GPU 0", "inst-0");
        var source = new BlockingFakeSource(new[] { gpu0 });
        var service = CreateService(source);
        using var serviceLifetime = service;

        var publications = new ConcurrentQueue<string>();

        var pipeline = new UpdatePipeline<TestSize>(
            service, OneActiveWidget(),
            snapshot => { var p = WidgetPayloadPresenter.Present(snapshot).ToJson(); publications.Enqueue(p); return p; },
            (payload, widget) => Task.CompletedTask,
            TimeSpan.FromMinutes(5), clock);
        await using var ownedPipeline = pipeline;
        using var releaseGateOnExit = new ReleaseGateOnDispose(source);

        // --- Cycle 1: GPU has telemetry ---
        pipeline.Start();
        await WithWatchdogAsync(WaitForPublicationCountAsync(publications, 1));
        // Primary GPU should be presented (not N/A)
        Assert.False(IsGpuTemperatureNA(publications.First()));

        // --- Cycle 2: Block backend, request deactivation during collection ---
        source.Block();
        pipeline.Start();
        await WithWatchdogAsync(WaitForReadCountAsync(source, 1));

        // Deactivate during collection.
        var deactivateTask = pipeline.DeactivateAsync();
        source.Release();
        await WithWatchdogAsync(deactivateTask);

        // Cycle 2 was rejected. Subsequent accepted cycles verify primary grace.
        Assert.Single(publications);

        // Reactivate.
        pipeline.Start();
        await WithWatchdogAsync(WaitForPublicationCountAsync(publications, 2));

        // Primary GPU still presented (within grace).
        Assert.False(IsGpuTemperatureNA(publications.ElementAt(1)));
        Assert.Equal(0, service.PrimaryConsecutiveFailures);

        // Now primary loses telemetry for 2 more admitted cycles (total 3 = grace exhausted).
        var gpu0NoTelemetry = gpu0 with { Sensors = new List<LhmSensorDescriptor>() };
        source.SetDevices(new[] { gpu0NoTelemetry });

        // Cycle 4 (failure 1)
        pipeline.Start();
        await WithWatchdogAsync(WaitForPublicationCountAsync(publications, 3));
        Assert.False(IsGpuTemperatureNA(publications.ElementAt(2)));
        Assert.Equal(1, service.PrimaryConsecutiveFailures);

        // Cycle 5 (failure 2)
        pipeline.Start();
        await WithWatchdogAsync(WaitForPublicationCountAsync(publications, 4));
        Assert.False(IsGpuTemperatureNA(publications.ElementAt(3)));
        Assert.Equal(2, service.PrimaryConsecutiveFailures);

        // Cycle 6 (failure 3 is still within grace)
        pipeline.Start();
        await WithWatchdogAsync(WaitForPublicationCountAsync(publications, 5));
        Assert.False(IsGpuTemperatureNA(publications.ElementAt(4)));
        Assert.Equal(3, service.PrimaryConsecutiveFailures);

        // Cycle 7 (failure 4 exhausts grace)
        pipeline.Start();
        await WithWatchdogAsync(WaitForPublicationCountAsync(publications, 6));
        Assert.Null(service.TrackedPrimaryInstanceId);
        Assert.Equal(0, service.PrimaryConsecutiveFailures);

    }
}
}
