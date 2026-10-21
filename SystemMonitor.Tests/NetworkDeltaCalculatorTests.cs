using System_Monitor.Services;
using Xunit;

namespace SystemMonitor.Tests;

public class NetworkDeltaCalculatorTests
{
    [Fact]
    public void NoBaseline_ReturnsNoValidDeltaAndEstablishesNothing()
    {
        var calc = new NetworkDeltaCalculator();
        var result = calc.Calculate("eth0", 1000, 500, DateTimeOffset.UtcNow, null);

        Assert.False(result.HasValidDelta);
        Assert.Equal(0.0, result.BytesReceivedPerSec);
        Assert.Equal(0.0, result.BytesSentPerSec);
    }

    [Fact]
    public void WithBaseline_ActualElapsedTimeUsed_ReturnsPerSecondRates()
    {
        var calc = new NetworkDeltaCalculator();
        var now = DateTimeOffset.UtcNow;
        var previous = new InterfaceSample("eth0", 1000, 500, now.AddSeconds(-10));

        var result = calc.Calculate("eth0", 6000, 2500, now, previous);

        Assert.True(result.HasValidDelta);
        Assert.Equal(500.0, result.BytesReceivedPerSec); // (6000-1000)/10
        Assert.Equal(200.0, result.BytesSentPerSec);    // (2500-500)/10
    }

    [Fact]
    public void CounterReset_RxLessThanBaseline_ReturnsNoValidDeltaNoNegativeRate()
    {
        var calc = new NetworkDeltaCalculator();
        var now = DateTimeOffset.UtcNow;
        var previous = new InterfaceSample("eth0", 1000, 500, now.AddSeconds(-5));

        // Current rx is less than baseline -> counter reset
        var result = calc.Calculate("eth0", 100, 600, now, previous);

        Assert.False(result.HasValidDelta);
        Assert.Equal(0.0, result.BytesReceivedPerSec);
        Assert.Equal(0.0, result.BytesSentPerSec);
    }

    [Fact]
    public void CounterReset_TxLessThanBaseline_ReturnsNoValidDeltaNoNegativeRate()
    {
        var calc = new NetworkDeltaCalculator();
        var now = DateTimeOffset.UtcNow;
        var previous = new InterfaceSample("eth0", 1000, 500, now.AddSeconds(-5));

        var result = calc.Calculate("eth0", 2000, 100, now, previous);

        Assert.False(result.HasValidDelta);
        Assert.Equal(0.0, result.BytesReceivedPerSec);
        Assert.Equal(0.0, result.BytesSentPerSec);
    }

    [Fact]
    public void ElapsedTimeZero_ReturnsNoValidDelta()
    {
        var calc = new NetworkDeltaCalculator();
        var now = DateTimeOffset.UtcNow;
        var previous = new InterfaceSample("eth0", 1000, 500, now);

        var result = calc.Calculate("eth0", 6000, 2500, now, previous);

        Assert.False(result.HasValidDelta);
    }

    [Fact]
    public void ElapsedTimeNegative_ReturnsNoValidDelta()
    {
        var calc = new NetworkDeltaCalculator();
        var now = DateTimeOffset.UtcNow;
        var previous = new InterfaceSample("eth0", 1000, 500, now.AddSeconds(10));

        var result = calc.Calculate("eth0", 6000, 2500, now, previous);

        Assert.False(result.HasValidDelta);
    }

    [Fact]
    public void NewInterfaceAfterRemoval_EstablishesFreshBaseline_NoSpike()
    {
        var calc = new NetworkDeltaCalculator();
        var now = DateTimeOffset.UtcNow;

        // First sample establishes baseline (no rate)
        var first = calc.Calculate("eth0", 1000, 500, now, null);
        Assert.False(first.HasValidDelta);

        // Interface disappears and reappears with lower counter (rebaseline)
        var reappeared = calc.Calculate("eth0", 100, 50, now.AddSeconds(5), null);
        Assert.False(reappeared.HasValidDelta);
        Assert.Equal(0.0, reappeared.BytesReceivedPerSec);
        Assert.Equal(0.0, reappeared.BytesSentPerSec);
    }

    [Fact]
    public void NormalSampling_ReturnsConsistentPositiveRates()
    {
        var calc = new NetworkDeltaCalculator();
        var now = DateTimeOffset.UtcNow;
        var previous = new InterfaceSample("eth0", 0, 0, now.AddSeconds(-10));

        var result = calc.Calculate("eth0", 10000, 5000, now, previous);

        Assert.True(result.HasValidDelta);
        Assert.Equal(1000.0, result.BytesReceivedPerSec);
        Assert.Equal(500.0, result.BytesSentPerSec);
    }
}
