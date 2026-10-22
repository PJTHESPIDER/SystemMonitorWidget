using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System_Monitor.Models;

namespace System_Monitor.Services
{
    /// <summary>
    /// A single non-overlapping, cancellable update pipeline that replaces
    /// the independent sampling and publishing timers.
    ///
    /// Each cycle performs, in order:
    ///   1. Collect readings sequentially into one completed immutable snapshot.
    ///   2. Build one payload from that snapshot.
    ///   3. Publish it to every eligible (active) widget.
    ///   4. Wait for the next scheduled update without overlapping collection.
    ///
    /// <para><b>Lifecycle contract</b></para>
    ///
    /// <list type="bullet">
    /// <item><description>
    /// An <b>activation</b> (<see cref="Start"/>) creates a fresh generation
    /// with its own cancellation source. The generation is the unit of
    /// identity: every admitted cycle and every publication is tagged with
    /// it. A cancelled generation is never reused — reactivation always
    /// creates a new one.
    /// </description></item>
    /// <item><description>
    /// A <b>deactivation</b> (<see cref="Stop"/> / <see cref="DeactivateAsync"/>)
    /// transitions the pipeline to inactive through the <i>same</i> lock that
    /// admits a cycle. Once that transition happens no new cycle can be
    /// admitted, and the in-flight run is asked to cancel. An already-admitted
    /// collector is allowed to finish: the underlying hardware calls are
    /// synchronous and cannot be interrupted mid-flight.
    /// </description></item>
    /// <item><description>
    /// <b>Quiescence</b> (<see cref="DeactivateAsync"/>) completes only after
    /// the deactivated generation's admitted collection and publishing work
    /// has finished or unwound. It is implemented with a task completion
    /// signal, never by observing a stable counter or by sleeping.
    /// </description></item>
    /// <item><description>
    /// <b>Reactivation</b> wakes the coordinator immediately and requests an
    /// immediate sample; it never waits for an old polling delay to expire.
    /// If a previous collector has not finished, the new generation awaits it
    /// before admitting its own cycle, so at most one collection runs at a
    /// time across generation boundaries.
    /// </description></item>
    /// <item><description>
    /// <b>Disposal</b> (<see cref="DisposeAsync"/>) cancels, awaits the loop,
    /// and only then releases the cancellation sources. The caller disposes
    /// the collector/backend afterwards, so the backend is never disposed
    /// while a collection is still running.
    /// </description></item>
    /// </list>
    ///
    /// <para><b>Synchronization</b></para>
    /// <para>
    /// One coordinator owns all mutable lifecycle state, guarded by a single
    /// lock. The lock is never held across an await, and cancellation is
    /// never requested while the lock is held (cancelling a token runs
    /// registered callbacks synchronously). Cycle admission and the inactive
    /// transition mutate the same field under that lock, so a deactivation
    /// cannot race with a new cycle being admitted. Cancellation sources are
    /// never disposed while a cycle may still hold their token.
    /// </para>
    ///
    /// <para><b>Remaining blocking boundary</b></para>
    /// <para>
    /// <see cref="IMetricsCollector.Collect"/> is synchronous. The Windows
    /// performance counters (<c>GetSystemTimes</c>,
    /// <c>GlobalMemoryStatusEx</c>), WMI queries
    /// (<c>ManagementObjectSearcher</c>), <c>nvidia-smi</c> subprocess
    /// launches and the LibreHardwareMonitor sensor reads are all
    /// synchronous native/WMI calls. A cancellation request marks the
    /// token as cancelled and is observed at the next cycle boundary, but it
    /// cannot forcibly stop a native call that is already in progress. A
    /// deactivation therefore completes only once the in-flight collector
    /// returns on its own. This is a property of the platform, not of this
    /// class, and is not worked around here.
    /// </para>
    /// </summary>
    /// <typeparam name="TSize">
    /// The widget size type. Generic so the pipeline can be exercised by the
    /// test project without referencing the Windows App SDK.
    /// </typeparam>
    public sealed class UpdatePipeline<TSize> : IAsyncDisposable where TSize : struct
    {
        /// <summary>
        /// One activation. Owns the cancellation scope for every cycle and
        /// publication admitted under it, and the signal that reports when
        /// its work has unwound.
        /// </summary>
        private sealed class Generation
        {
            /// <summary>Monotonic identity of this activation.</summary>
            public long Id;

            /// <summary>
            /// Cancellation scope for this activation. Cancelled when the
            /// activation is deactivated. Never reused for a later
            /// activation.
            /// </summary>
            public CancellationTokenSource Cts = null!;

            /// <summary>
            /// Completes when every collection and publication admitted under
            /// this generation has finished or unwound.
            /// </summary>
            public TaskCompletionSource<bool> Unwound =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private readonly IMetricsCollector _collector;
        private readonly WidgetRegistry<TSize> _widgets;
        private readonly Func<MetricsSnapshot, string> _buildPayload;
        private readonly Func<string, WidgetEntry<TSize>, Task> _publish;
        private readonly TimeSpan _interval;
        private readonly TimeProvider _timeProvider;

        // ------------------------------------------------------------------
        // Coordinator state. Every field below is guarded by _stateLock.
        // ------------------------------------------------------------------
        private readonly object _stateLock = new();

        /// <summary>
        /// The generation currently allowed to admit cycles, or null when the
        /// pipeline is inactive. Admission and the inactive transition both
        /// mutate this under <see cref="_stateLock"/>.
        /// </summary>
        private Generation? _current;

        /// <summary>Every generation ever created, for disposal.</summary>
        private readonly List<Generation> _generations = new();

        /// <summary>Monotonic counter used to assign generation identities.</summary>
        private long _generationCounter;

        /// <summary>
        /// Completion task of the currently admitted cycle, or null when no
        /// cycle is in flight. Acts as the global non-overlap barrier: a new
        /// cycle awaits the previous one to unwind before it is admitted.
        /// </summary>
        private Task? _inFlightCycle;

        /// <summary>
        /// The generation that admitted the currently in-flight cycle, or
        /// null when no cycle is in flight.
        ///
        /// <para>
        /// This is what makes the drain boundary survive a synchronous
        /// deactivation. <see cref="Stop"/> retires the active generation by
        /// clearing <see cref="_current"/>, so a later
        /// <see cref="DeactivateAsync"/> no longer sees it. The retired
        /// generation's collection may still be running, and this field is
        /// what identifies the generation that actually holds the work.
        /// </para>
        /// </summary>
        private Generation? _inFlightGeneration;

        /// <summary>
        /// Set when a refresh is requested and not yet satisfied by an
        /// admitted collection.
        /// </summary>
        private bool _refreshPending;

        /// <summary>True once the pipeline has been disposed.</summary>
        private bool _disposed;

        /// <summary>
        /// Pulsed whenever lifecycle state changes, so the waiters are woken
        /// promptly. Replaced under <see cref="_stateLock"/>; a waiter
        /// captures the current instance under the lock and awaits it outside.
        /// </summary>
        private TaskCompletionSource<bool> _stateChanged =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Signalled when an activation occurs while the coordinator is idle,
        /// so it can admit the new generation without waiting for a polling
        /// delay.
        /// </summary>
        private TaskCompletionSource<bool>? _activationSignal;

        /// <summary>Number of scheduled waits the coordinator has registered.</summary>
        private int _waitCounter;

        /// <summary>Completed and replaced every time <see cref="_waitCounter"/> grows.</summary>
        private TaskCompletionSource<bool> _waitSignal =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly Task _loopTask;
        private readonly CancellationTokenSource _lifetimeCts = new();

        // ------------------------------------------------------------------
        // Observability
        // ------------------------------------------------------------------

        /// <summary>
        /// The number of times cancellation has been requested on a
        /// generation. A test uses this to confirm that a deactivation asked
        /// the in-flight run to stop.
        /// </summary>
        public int CancellationRequestedCount { get; private set; }

        /// <summary>The identity of the current activation, or 0 when inactive.</summary>
        public long CurrentGeneration
        {
            get { lock (_stateLock) { return _current?.Id ?? 0; } }
        }

        /// <summary>True while the pipeline is permitted to collect.</summary>
        public bool IsRunning
        {
            get { lock (_stateLock) { return _current is not null; } }
        }

        /// <summary>Number of admitted cycles currently in flight (0 or 1).</summary>
        public int CyclesInFlight
        {
            get { lock (_stateLock) { return _inFlightCycle is null ? 0 : 1; } }
        }

        /// <summary>
        /// The number of scheduled waits the coordinator has registered. A
        /// test uses this with <see cref="WaitForWaitCountAsync"/> to
        /// establish the milestone "the coordinator is now waiting for its
        /// next scheduled update" without relying on wall-clock timing.
        /// </summary>
        public int WaitCount
        {
            get { lock (_stateLock) { return _waitCounter; } }
        }

        /// <summary>
        /// Completes once the coordinator has registered at least
        /// <paramref name="count"/> scheduled waits. Used by tests as an
        /// explicit milestone; the returned task never relies on a timeout to
        /// make progress.
        /// </summary>
        public Task WaitForWaitCountAsync(int count)
        {
            lock (_stateLock)
            {
                if (_waitCounter >= count)
                    return Task.CompletedTask;
                return _waitSignal.Task;
            }
        }

        public UpdatePipeline(
            IMetricsCollector collector,
            WidgetRegistry<TSize> widgets,
            Func<MetricsSnapshot, string> buildPayload,
            Func<string, WidgetEntry<TSize>, Task> publish,
            TimeSpan interval)
            : this(collector, widgets, buildPayload, publish, interval, TimeProvider.System)
        {
        }

        public UpdatePipeline(
            IMetricsCollector collector,
            WidgetRegistry<TSize> widgets,
            Func<MetricsSnapshot, string> buildPayload,
            Func<string, WidgetEntry<TSize>, Task> publish,
            TimeSpan interval,
            TimeProvider timeProvider)
        {
            _collector = collector ?? throw new ArgumentNullException(nameof(collector));
            _widgets = widgets ?? throw new ArgumentNullException(nameof(widgets));
            _buildPayload = buildPayload ?? throw new ArgumentNullException(nameof(buildPayload));
            _publish = publish ?? throw new ArgumentNullException(nameof(publish));
            _interval = interval;
            _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

            // The coordinator lives for the lifetime of the pipeline and
            // idles until the first activation.
            _loopTask = LoopAsync(_lifetimeCts.Token);
        }

        // ------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------

        /// <summary>
        /// Activates the pipeline and requests an immediate sample.
        ///
        /// If the pipeline is inactive, a new generation is created and the
        /// coordinator is woken promptly, so the new generation admits a
        /// cycle without waiting for a polling delay. If a generation is
        /// already active, a refresh is requested instead. A cancelled
        /// generation is never reused.
        /// </summary>
        public void Start()
        {
            TaskCompletionSource<bool>? activation = null;
            TaskCompletionSource<bool>? wake = null;

            lock (_stateLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, nameof(UpdatePipeline<TSize>));

                if (_current is not null)
                {
                    // Already active: request an immediate sample.
                    _refreshPending = true;
                    wake = _stateChanged;
                }
                else
                {
                    var generation = new Generation
                    {
                        Id = ++_generationCounter,
                        Cts = new CancellationTokenSource(),
                    };
                    _generations.Add(generation);
                    _current = generation;

                    // Wake the coordinator so it admits the new generation
                    // immediately rather than after a polling delay.
                    _activationSignal ??= new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    activation = _activationSignal;
                }
            }

            // Completed outside the lock so no continuation runs while the
            // coordinator state is locked.
            activation?.TrySetResult(true);
            wake?.TrySetResult(true);
        }

        /// <summary>
        /// Requests a pause. This is the synchronous half of the
        /// deactivation contract and is safe to call from a synchronous
        /// widget callback: it transitions the pipeline to inactive through
        /// the same lock that admits a cycle, and asks the in-flight run to
        /// cancel. It does not wait for the collector to finish — use
        /// <see cref="DeactivateAsync"/> for that.
        /// </summary>
        public void Stop()
        {
            RetireCurrentGeneration();
            PulseState();
        }

        /// <summary>
        /// Retires the currently active generation: the inactive transition
        /// and the request to cancel the in-flight run.
        ///
        /// <para>
        /// The inactive transition happens under the same lock that admits a
        /// cycle, so no new cycle can be admitted after it. Cancellation is
        /// requested outside the lock, because cancelling a token runs
        /// registered callbacks synchronously and those callbacks must not
        /// run while the coordinator state is locked.
        /// </para>
        ///
        /// <para>
        /// This is shared by <see cref="Stop"/> and
        /// <see cref="DeactivateAsync"/>, so a synchronous stop followed by
        /// an awaitable deactivation does not request cancellation twice and
        /// does not lose the drain boundary.
        /// </para>
        /// </summary>
        private void RetireCurrentGeneration()
        {
            RetireCurrentGeneration(out _);
        }

        /// <summary>
        /// Retires the currently active generation and reports the generation
        /// that holds admitted work, so the caller can await its drain.
        ///
        /// <para>
        /// Both values are captured under the <i>same</i> lock acquisition
        /// as the inactive transition. Capturing them separately would allow
        /// a reactivation to admit a cycle in between, and the caller would
        /// then await a generation it never retired and never cancelled.
        /// </para>
        /// </summary>
        private void RetireCurrentGeneration(out Generation? drainTarget)
        {
            Generation? generation;

            lock (_stateLock)
            {
                if (_disposed)
                {
                    drainTarget = null;
                    return;
                }

                generation = _current;

                // The inactive transition: no cycle may be admitted after
                // this point, because admission checks this same field
                // under this same lock.
                _current = null;
                _refreshPending = false;

                // The generation that holds admitted work. A generation is
                // recorded here only once it has admitted a cycle, which is
                // exactly when the coordinator's per-generation finally
                // block is guaranteed to complete its Unwound signal. A
                // generation that never admitted a cycle has no admitted
                // work to drain.
                drainTarget = _inFlightGeneration;
            }

            if (generation is null)
                return;

            CancellationRequestedCount++;
            try { generation.Cts.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        /// <summary>
        /// Requests a pause and awaits quiescence of the deactivated
        /// generation.
        ///
        /// <para><b>Guarantee</b>: when this method completes, every
        /// collection and every publication admitted by the generation that
        /// was active at the time of the call has finished or unwound. No new
        /// cycle is admitted after the inactive transition. The wait is a
        /// task-completion signal — not a counter observation and not a
        /// sleep — and this method never blocks the calling thread.</para>
        ///
        /// <para><b>After a synchronous stop</b>: if the pipeline was already
        /// deactivated through <see cref="Stop"/>, that call has already
        /// retired the active generation and requested its cancellation, so
        /// this method does not cancel it a second time. It still awaits the
        /// generation that actually holds admitted work — the one that
        /// admitted the in-flight cycle — so the drain boundary is not lost
        /// by the earlier synchronous transition.</para>
        ///
        /// <para><b>Reactivation race</b>: if a new activation is requested
        /// while this method is awaiting, that activation is a separate
        /// generation with its own lifecycle and is <i>not</i> cancelled by
        /// this call. This method still waits for the deactivated generation
        /// to unwind. The new generation does not admit its own cycle until
        /// the previous collection has unwound, so the non-overlap guarantee
        /// holds across the boundary.</para>
        /// </summary>
        public async Task DeactivateAsync(CancellationToken ct = default)
        {
            // Retire the active generation, if any, and capture the
            // generation that holds admitted work in the same lock
            // acquisition. This performs the inactive transition and
            // requests cancellation, exactly as Stop() does.
            RetireCurrentGeneration(out var generation);

            if (generation is not null)
            {
                // Waits for the generation's cycle loop to unwind.
                using var registration = ct.Register(
                    static s => ((TaskCompletionSource<bool>)s!).TrySetCanceled(),
                    generation.Unwound);
                await generation.Unwound.Task.ConfigureAwait(false);
            }

            PulseState();
        }

        /// <summary>
        /// Awaits until no collection or publishing work is in flight
        /// <summary>
        /// Awaits until no collection or publishing work is in flight
        /// anywhere in the pipeline. Does not change the running state. If a
        /// new cycle is admitted while waiting, this method keeps waiting.
        ///
        /// <para>
        /// The awaited task is the cycle's completion signal, which is
        /// completed only after <c>RunCycleAsync</c> has returned — that is,
        /// after collection <i>and</i> publishing have both finished. The
        /// field is cleared to null before the signal is completed, so a
        /// waiter that observes null has already seen the cycle unwind.
        /// </para>
        /// </summary>
        public async Task WaitForIdleAsync(CancellationToken ct = default)
        {
            while (true)
            {
                Task? inFlight;
                lock (_stateLock) { inFlight = _inFlightCycle; }
                if (inFlight is null)
                    return;

                try
                {
                    await inFlight.ConfigureAwait(false);
                }
                catch
                {
                    // Expected on cancellation or collector failure.
                }

                if (ct.IsCancellationRequested)
                    throw new OperationCanceledException(ct);
            }
        }

        /// <summary>
        /// Coalesces a refresh request. Several requests that arrive while a
        /// collection is running collapse into a single additional
        /// collection, and the request is not delayed by a pending polling
        /// interval. Concurrent collection is never allowed.
        /// </summary>
        public void ScheduleRefresh()
        {
            lock (_stateLock)
            {
                if (_disposed)
                    return;
                _refreshPending = true;
            }

            // Pulse outside the lock. The pulse completes the *current*
            // signal and installs a fresh one, so a waiter that captured the
            // old signal wakes exactly once and no waiter observes an
            // already-completed signal (which would spin the loop).
            PulseState();
        }

        /// <summary>
        /// Cancels the coordinator, awaits outstanding work, then releases the
        /// cancellation sources. The caller disposes the collector/backend
        /// after this returns, so the backend is never disposed while a
        /// collection is still running. Cancellation is expected control
        /// flow, not a collector failure.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            List<Generation> generations;
            TaskCompletionSource<bool>? activation;

            lock (_stateLock)
            {
                if (_disposed)
                    return;
                _disposed = true;
                _current = null;
                _refreshPending = false;
                generations = new List<Generation>(_generations);
                activation = _activationSignal;
                _activationSignal = null;
            }

            // Cancellation is requested outside the lock.
            foreach (var generation in generations)
            {
                try { generation.Cts.Cancel(); }
                catch (ObjectDisposedException) { }
            }

            _lifetimeCts.Cancel();
            activation?.TrySetResult(true);
            PulseState();

            try
            {
                await _loopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancellation is expected control flow on shutdown.
            }

            // The loop has exited, so no cycle can still be using these
            // sources. Only now are they disposed.
            foreach (var generation in generations)
            {
                generation.Unwound.TrySetResult(true);
                try { generation.Cts.Dispose(); }
                catch (ObjectDisposedException) { }
            }
            _generations.Clear();
            _lifetimeCts.Dispose();
        }

        /// <summary>
        /// Best-effort synchronous cancellation. The preferred shutdown path
        /// is <see cref="DisposeAsync"/>, which awaits the outstanding work.
        /// </summary>
        public void Dispose()
        {
            try { _lifetimeCts.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        // ------------------------------------------------------------------
        // Coordination helpers
        // ------------------------------------------------------------------

        /// <summary>
        /// Wakes the waiters. The current instance is completed and replaced
        /// under the lock so a waiter that captured the old instance is
        /// woken exactly once.
        /// </summary>
        private void PulseState()
        {
            TaskCompletionSource<bool> toSignal;
            lock (_stateLock)
            {
                toSignal = _stateChanged;
                _stateChanged = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            // Completed outside the lock so no continuation runs while the
            // coordinator state is locked.
            toSignal.TrySetResult(true);
        }

        // ------------------------------------------------------------------
        // Coordinator loop
        // ------------------------------------------------------------------

        private async Task LoopAsync(CancellationToken lifetimeToken)
        {
            try
            {
                while (!lifetimeToken.IsCancellationRequested)
                {
                    Generation? generation;
                    lock (_stateLock) { generation = _current; }

                    if (generation is null)
                    {
                        // Idle: wait for an activation. The wait is wakeable,
                        // so an activation does not have to wait for a polling
                        // delay to expire.
                        if (!await WaitForActivationAsync(lifetimeToken).ConfigureAwait(false))
                            break;
                        continue;
                    }

                    await RunGenerationAsync(generation, lifetimeToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
            {
                // Expected on shutdown.
            }
            finally
            {
                // Release anything still awaiting quiescence.
                List<Generation> generations;
                lock (_stateLock) { generations = new List<Generation>(_generations); }
                foreach (var generation in generations)
                {
                    generation.Unwound.TrySetResult(true);
                }
            }
        }

        /// <summary>
        /// Waits for an activation. Returns false when the pipeline is
        /// shutting down.
        /// </summary>
        private async Task<bool> WaitForActivationAsync(CancellationToken lifetimeToken)
        {
            TaskCompletionSource<bool> signal;
            lock (_stateLock)
            {
                // The check and the registration happen under the same lock
                // that Start() uses to publish the activation, so the signal
                // cannot be lost.
                if (_current is not null)
                    return true;

                _activationSignal ??= new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                signal = _activationSignal;
            }

            try
            {
                await signal.Task.WaitAsync(lifetimeToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            lock (_stateLock)
            {
                if (ReferenceEquals(_activationSignal, signal))
                    _activationSignal = null;
            }

            return true;
        }

        /// <summary>
        /// Runs the cycle loop for one generation. Returns when the
        /// generation is deactivated, cancelled, or superseded.
        /// </summary>
        private async Task RunGenerationAsync(Generation generation, CancellationToken lifetimeToken)
        {
            try
            {
                while (true)
                {
                    // Global non-overlap barrier. A previous generation's
                    // collector may still be running; wait for it to unwind
                    // before admitting this generation's cycle.
                    Task? previous;
                    lock (_stateLock) { previous = _inFlightCycle; }
                    if (previous is not null)
                    {
                        try { await previous.ConfigureAwait(false); }
                        catch { /* expected on cancellation or failure */ }
                    }

                    // Atomic admission. The inactive transition uses the
                    // same lock, so no cycle can be admitted after a
                    // deactivation.
                    var completion = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    lock (_stateLock)
                    {
                        if (_disposed
                            || lifetimeToken.IsCancellationRequested
                            || !ReferenceEquals(_current, generation)
                            || generation.Cts.IsCancellationRequested)
                        {
                            return;
                        }

                        _inFlightCycle = completion.Task;
                        _inFlightGeneration = generation;

                        // This collection satisfies a pending refresh.
                        _refreshPending = false;
                    }

                    // The collector runs *outside* the lock: it is a
                    // synchronous call that may block on native code, and
                    // holding the lock across it would stall the whole
                    // coordinator.
                    try
                    {
                        await RunCycleAsync(generation, lifetimeToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (generation.Cts.IsCancellationRequested)
                    {
                        // Cancellation during a cycle is expected control flow.
                    }
                    catch (Exception ex)
                    {
                        DiagnosticLog.Shared.Error(
                            "pipeline",
                            "cycle",
                            "failed",
                            detail: DiagnosticDetail.Describe(ex));
                    }
                    finally
                    {
                        lock (_stateLock)
                        {
                            if (ReferenceEquals(_inFlightCycle, completion.Task))
                            {
                                _inFlightCycle = null;
                                _inFlightGeneration = null;
                            }
                        }
                        completion.TrySetResult(true);
                    }

                    // Has this generation been deactivated or superseded?
                    lock (_stateLock)
                    {
                        if (_disposed
                            || lifetimeToken.IsCancellationRequested
                            || !ReferenceEquals(_current, generation)
                            || generation.Cts.IsCancellationRequested)
                        {
                            return;
                        }
                    }

                    // Wait for the next scheduled update. A lifecycle change
                    // (deactivation, reactivation) or a refresh wakes this
                    // wait promptly, so polling stops promptly and
                    // reactivation never waits out the old interval.
                    var waitResult = await WaitForNextTickAsync(generation, lifetimeToken)
                        .ConfigureAwait(false);

                    if (waitResult == WaitResult.Interrupted)
                    {
                        // Re-evaluate: a reactivation means a new generation
                        // owns the pipeline now, and the outer loop will pick
                        // it up and collect immediately.
                        lock (_stateLock)
                        {
                            if (!ReferenceEquals(_current, generation))
                                return;
                        }
                        continue;
                    }
                }
            }
            finally
            {
                // The generation has unwound: no collection and no
                // publishing work remains under it.
                generation.Unwound.TrySetResult(true);
            }
        }

        /// <summary>Outcome of waiting for the next scheduled update.</summary>
        private enum WaitResult { Elapsed, Interrupted }

        /// <summary>
        /// Waits for the next scheduled update, a refresh request, or the
        /// generation to be cancelled. Returns <see cref="WaitResult.Interrupted"/>
        /// when the wait was cut short by a lifecycle change.
        /// </summary>
        private async Task<WaitResult> WaitForNextTickAsync(
            Generation generation,
            CancellationToken lifetimeToken)
        {
            TaskCompletionSource<bool> signal;
            CancellationToken activationToken;

            lock (_stateLock)
            {
                if (_disposed || lifetimeToken.IsCancellationRequested)
                    return WaitResult.Elapsed;

                // A new activation owns the pipeline now: do not wait out the
                // old interval, collect again immediately.
                if (!ReferenceEquals(_current, generation))
                    return WaitResult.Interrupted;

                // A refresh was requested: honour it immediately rather than
                // waiting for the interval.
                if (_refreshPending)
                    return WaitResult.Interrupted;

                activationToken = generation.Cts.Token;
                signal = _stateChanged;

                // Milestone: the coordinator has registered its next
                // scheduled wait.
                _waitCounter++;
                _waitSignal.TrySetResult(true);
                _waitSignal = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            var delayTask = DelayAsync(_timeProvider, _interval, activationToken, lifetimeToken);
            var winner = await Task.WhenAny(delayTask, signal.Task).ConfigureAwait(false);

            if (ReferenceEquals(winner, delayTask))
            {
                try
                {
                    await delayTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // The generation was cancelled or the pipeline is
                    // shutting down; the caller re-evaluates the state.
                }
                return WaitResult.Elapsed;
            }

            // Woken early by a lifecycle change.
            return WaitResult.Interrupted;
        }

        /// <summary>
        /// Waits for a delay using the injected <see cref="TimeProvider"/> so
        /// that virtual time (FakeTimeProvider) controls scheduling in tests.
        /// The delay is cancelled when the activation is cancelled, so a
        /// deactivation stops the poll promptly.
        /// </summary>
        private static async Task DelayAsync(
            TimeProvider timeProvider,
            TimeSpan delay,
            CancellationToken activationToken,
            CancellationToken lifetimeToken)
        {
            if (delay <= TimeSpan.Zero)
            {
                await Task.Yield();
                return;
            }

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var timer = timeProvider.CreateTimer(
                static s => ((TaskCompletionSource<bool>)s!).TrySetResult(true),
                tcs,
                delay,
                Timeout.InfiniteTimeSpan);

            using var activationRegistration = activationToken.Register(
                static s => ((TaskCompletionSource<bool>)s!).TrySetCanceled(), tcs);
            using var lifetimeRegistration = lifetimeToken.Register(
                static s => ((TaskCompletionSource<bool>)s!).TrySetCanceled(), tcs);

            await tcs.Task.ConfigureAwait(false);
        }

        /// <summary>
        /// Runs one complete update cycle: collect → build → publish. The
        /// snapshot is tagged with the generation that admitted it; if that
        /// generation has since been deactivated or superseded, the result is
        /// suppressed so stale data is never published as current.
        /// </summary>
        private async Task RunCycleAsync(Generation generation, CancellationToken lifetimeToken)
        {
            var token = generation.Cts.Token;

            // 1. Collect readings synchronously into one immutable snapshot.
            //    Collectors are synchronous WMI/native calls and cannot be
            //    interrupted mid-flight; cancellation is cooperative and is
            //    observed by the loop before and after the cycle.
            MetricsSnapshot snapshot;
            try
            {
                snapshot = _collector.Collect();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Cancellation is expected control flow, not a failure.
                return;
            }
            catch (Exception ex)
            {
                // Ordinary collector failure: report it and let the next
                // cycle run.
                DiagnosticLog.Shared.Error(
                    "pipeline",
                    "collect",
                    "failed",
                    detail: DiagnosticDetail.Describe(ex));
                return;
            }

            // A snapshot collected under a cancelled or superseded
            // generation is stale and must not be published as current data.
            if (token.IsCancellationRequested || lifetimeToken.IsCancellationRequested)
                return;

            lock (_stateLock)
            {
                if (_disposed || !ReferenceEquals(_current, generation))
                    return;
            }

            // 2. Build one payload from the snapshot.
            string payload;
            try
            {
                payload = _buildPayload(snapshot);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Shared.Error(
                    "pipeline",
                    "build-payload",
                    "failed",
                    detail: DiagnosticDetail.Describe(ex));
                return;
            }

            // 3. Publish to the eligible (active) widgets. Publication
            //    admission is coordinated with deactivation under the same
            //    lock, so a deactivated generation publishes nothing that has
            //    not already been submitted.
            foreach (var widget in _widgets.GetActive())
            {
                lock (_stateLock)
                {
                    if (_disposed || !ReferenceEquals(_current, generation))
                        return;
                }

                // Re-check the widget: it may have been deactivated between
                // the snapshot above and now.
                if (!_widgets.TryGet(widget.WidgetId, out var current)
                    || current is null
                    || !current.IsActive)
                {
                    continue;
                }

                try
                {
                    await _publish(payload, widget).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // One widget's failure does not prevent publishing to
                    // the others. The widget's id is not logged: it is
                    // assigned by the host and identifies one instance of
                    // this widget on this machine.
                    DiagnosticLog.Shared.Error(
                        "pipeline",
                        "publish",
                        "failed",
                        detail: DiagnosticDetail.Describe(ex));
                }
            }

            // Boundary: a host update that has already been submitted to the
            // widget host cannot be retracted. Only updates that have not
            // been submitted yet are suppressed.
        }
    }
}
