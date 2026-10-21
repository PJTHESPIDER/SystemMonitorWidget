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
}
