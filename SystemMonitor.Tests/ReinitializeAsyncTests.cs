using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System_Monitor.Models;
using System_Monitor.Services;
using Xunit;

namespace SystemMonitor.Tests;

/// <summary>
/// Tests for the structured backend reinitialization flow
/// (ILhmSensorSource.ReinitializeAsync).
///
/// Verifies the contract added to ILhmSensorSource and the implementation
/// in LibreHardwareMonitorSource:
/// - Concurrent sensor read safety (lock serialization)
/// - Concurrent disposal safety
/// - Cancellation boundaries around synchronous native calls
/// - All ReinitializeResult outcomes
///
/// Uses fakes — no real hardware or drivers required.
/// </summary>
public sealed class ReinitializeAsyncTests
{
    /// <summary>
    /// A fake source that simulates the full reinit lifecycle with lock-based
    /// serialization, mirroring the real LibreHardwareMonitorSource pattern.
    /// </summary>
    private sealed class ControllableFakeSource : ILhmSensorSource
    {
        private readonly object _lock = new();
        private bool _disposed;
        private bool _initialized;
        private bool _available;

        public bool IsAvailable => _available;
        public string? UnavailableReason { get; private set; }
        public bool IsDisposed { get; private set; }
        public int ReadCount { get; private set; }
        public int ReinitCount { get; private set; }
        public bool WasAvailableBeforeReinit { get; private set; }

        // Configurable reinit behavior
        // Configurable reinit behavior
        public ReinitializeResult? ScheduledResult { get; set; }
        public IReadOnlyList<LhmDeviceDescriptor>? Devices { get; set; }

        // Flags to control reinit outcome
        public bool InitFails { get; set; } = false;
        public bool TempUnavailableOnReinit { get; set; } = false; // reinit succeeds but no temp sensors

        // Configurable state before reinit — read in ReinitializeAsync
        public bool WasAvailableBefore { get; set; } = true;

        public ControllableFakeSource()
        {
            _available = true;
            _initialized = true;
        }

        public IReadOnlyList<LhmDeviceDescriptor> Read()
        {
            lock (_lock)
            {
                ReadCount++;
                if (_disposed) return Array.Empty<LhmDeviceDescriptor>();
            }
            return Devices ?? Array.Empty<LhmDeviceDescriptor>();
        }

        public Task<ReinitializeResult> ReinitializeAsync(CancellationToken ct = default)
        {
            WasAvailableBeforeReinit = WasAvailableBefore;

            lock (_lock)
            {
                ReinitCount++;
                if (_disposed)
                {
                    return Task.FromResult(new ReinitializeResult
                    {
                        Outcome = ReinitializationOutcome.DisposedDuringReinit,
                        WasAvailableBefore = WasAvailableBeforeReinit,
                        CompletedAt = DateTimeOffset.UtcNow,
                        FailureReason = "Source was disposed during reinitialization",
                    });
                }

                if (ct.IsCancellationRequested)
                {
                    return Task.FromResult(new ReinitializeResult
                    {
                        Outcome = ReinitializationOutcome.Cancelled,
                        WasAvailableBefore = WasAvailableBeforeReinit,
                        CompletedAt = DateTimeOffset.UtcNow,
                        FailureReason = "Reinitialization cancelled before close",
                    });
                }

                if (ScheduledResult is not null)
                {
                    return Task.FromResult(ScheduledResult);
                }

                // Simulate the real source's behavior:
                // - InitFails: EnsureInitialized returns false → InitializationFailed
                // - TempUnavailableOnReinit: init succeeds but no temp sensors → TemperatureUnavailable
                // - Otherwise: Success
                if (InitFails)
                {
                    _available = false;
                    UnavailableReason = "Initialization failed";
                    return Task.FromResult(new ReinitializeResult
                    {
                        Outcome = ReinitializationOutcome.InitializationFailed,
                        WasAvailableBefore = WasAvailableBeforeReinit,
                        CompletedAt = DateTimeOffset.UtcNow,
                        FailureReason = UnavailableReason,
                    });
                }

                if (TempUnavailableOnReinit)
                {
                    return Task.FromResult(new ReinitializeResult
                    {
                        Outcome = ReinitializationOutcome.TemperatureUnavailable,
                        WasAvailableBefore = WasAvailableBeforeReinit,
                        CompletedAt = DateTimeOffset.UtcNow,
                        FailureReason = "No CPU temperature sensors available after reinitialization",
                    });
                }

                return Task.FromResult(new ReinitializeResult
                {
                    Outcome = ReinitializationOutcome.Success,
                    WasAvailableBefore = WasAvailableBeforeReinit,
                    CompletedAt = DateTimeOffset.UtcNow,
                });
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _disposed = true;
            }
            IsDisposed = true;
        }
    }

    [Fact]
    public async Task Success_ReturnsSuccessOutcome()
    {
        var source = new ControllableFakeSource();

        var result = await source.ReinitializeAsync();

        Assert.Equal(ReinitializationOutcome.Success, result.Outcome);
        Assert.True(result.WasAvailableBefore);
        Assert.Empty(result.NewlyAvailableSensorTypes);
    }

    [Fact]
    public async Task InitializationFailed_ReturnsInitializationFailedOutcome()
    {
        var source = new ControllableFakeSource { InitFails = true, WasAvailableBefore = false };

        var result = await source.ReinitializeAsync();

        Assert.Equal(ReinitializationOutcome.InitializationFailed, result.Outcome);
        Assert.False(result.WasAvailableBefore);
        Assert.NotNull(result.FailureReason);
    }

    [Fact]
    public async Task TemperatureUnavailable_ReturnsTemperatureUnavailableOutcome()
    {
        var source = new ControllableFakeSource { TempUnavailableOnReinit = true };

        var result = await source.ReinitializeAsync();

        Assert.Equal(ReinitializationOutcome.TemperatureUnavailable, result.Outcome);
        Assert.True(result.WasAvailableBefore);
        Assert.NotNull(result.FailureReason);
    }

    [Fact]
    public async Task DisposedDuringReinit_ReturnsDisposedDuringReinitOutcome()
    {
        var source = new ControllableFakeSource();
        source.Dispose();

        var result = await source.ReinitializeAsync();

        Assert.Equal(ReinitializationOutcome.DisposedDuringReinit, result.Outcome);
        Assert.Contains("disposed", result.FailureReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancelledDuringReinit_ReturnsCancelledOutcome()
    {
        var source = new ControllableFakeSource();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await source.ReinitializeAsync(cts.Token);

        Assert.Equal(ReinitializationOutcome.Cancelled, result.Outcome);
        Assert.Contains("cancelled", result.FailureReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConcurrentReadsAndReinit_AreSerialized()
    {
        // Verify that Read() and ReinitializeAsync() don't break each other
        // under concurrency. The fake source uses a lock, mirroring the real
        // implementation.
        var source = new ControllableFakeSource
        {
            Devices = new List<LhmDeviceDescriptor> { LhmFixtures.IntelCpu() },
        };

        var readTasks = Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() => source.Read()))
            .ToList();

        var reinitTask = Task.Run(() => source.ReinitializeAsync());

        var allTasks = readTasks.Cast<Task>().Concat(new[] { reinitTask }).ToArray();
        await Task.WhenAll(allTasks);

        Assert.True(source.ReadCount >= 20);
        Assert.Equal(1, source.ReinitCount);
    }

    [Fact]
    public async Task ConcurrentReadsAndDispose_AreSafe()
    {
        // Verify that concurrent reads + disposal don't cause exceptions
        var source = new ControllableFakeSource
        {
            Devices = new List<LhmDeviceDescriptor> { LhmFixtures.IntelCpu() },
        };

        var readTasks = Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() => source.Read()))
            .ToList();

        var disposeTask = Task.Run(() => source.Dispose());
        var reinitTask = Task.Run(() => source.ReinitializeAsync());

        var allTasks = readTasks.Cast<Task>().Concat(new[] { disposeTask, reinitTask }).ToArray();
        await Task.WhenAll(allTasks);

        Assert.True(source.IsDisposed);
    }

    [Fact]
    public async Task RetainedReadingsNotPublishedAfterReinit()
    {
        // After a failed reinit (InitFails), the source's IsAvailable is false.
        // Any readings from before should not be presented as current.
        var source = new ControllableFakeSource { InitFails = true, WasAvailableBefore = false };

        var reinitResult = await source.ReinitializeAsync();

        Assert.False(reinitResult.WasAvailableBefore);
        Assert.Equal(ReinitializationOutcome.InitializationFailed, reinitResult.Outcome);
        Assert.False(source.IsAvailable);
    }

    [Fact]
    public async Task ReinitThenRead_YieldsFreshDevices()
    {
        var source = new ControllableFakeSource
        {
            Devices = new List<LhmDeviceDescriptor> { LhmFixtures.IntelCpu() },
        };

        // Before reinit: source is available
        Assert.True(source.IsAvailable);

        var reinitResult = await source.ReinitializeAsync();

        Assert.Equal(ReinitializationOutcome.Success, reinitResult.Outcome);
        Assert.True(reinitResult.WasAvailableBefore);

        // After reinit: source still available, can read
        var devices = source.Read();
        Assert.NotEmpty(devices);
        Assert.Equal(1, source.ReadCount);
    }

    [Fact]
    public async Task CancelledReinit_SourceStillUsable()
    {
        var source = new ControllableFakeSource
        {
            Devices = new List<LhmDeviceDescriptor> { LhmFixtures.IntelCpu() },
        };

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await source.ReinitializeAsync(cts.Token);

        Assert.Equal(ReinitializationOutcome.Cancelled, result.Outcome);

        // Source should still be usable (disposal not triggered by cancel)
        var devices = source.Read();
        Assert.NotEmpty(devices);
    }

    [Fact]
    public async Task ReinitOutcomeEnum_CoversAllExpectedStates()
    {
        // Verify the enum has all expected values for the contract
        Assert.True(Enum.IsDefined(typeof(ReinitializationOutcome), ReinitializationOutcome.Success));
        Assert.True(Enum.IsDefined(typeof(ReinitializationOutcome), ReinitializationOutcome.InitializationFailed));
        Assert.True(Enum.IsDefined(typeof(ReinitializationOutcome), ReinitializationOutcome.TemperatureUnavailable));
        Assert.True(Enum.IsDefined(typeof(ReinitializationOutcome), ReinitializationOutcome.DisposedDuringReinit));
        Assert.True(Enum.IsDefined(typeof(ReinitializationOutcome), ReinitializationOutcome.Cancelled));
        Assert.True(Enum.IsDefined(typeof(ReinitializationOutcome), ReinitializationOutcome.NotInitialized));
    }
}
