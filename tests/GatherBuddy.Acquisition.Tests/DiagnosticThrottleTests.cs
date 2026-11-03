using GatherBuddy.Utility;
using Xunit;

namespace GatherBuddy.Acquisition.Tests;

public sealed class DiagnosticThrottleTests
{
    private sealed class Clock : TimeProvider
    {
        public long Timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Timestamp;
        public void Advance(TimeSpan elapsed) => Timestamp += elapsed.Ticks;
    }

    [Fact]
    public void FirstOccurrenceEmitsAndRepeatsAreCountedUntilWindowExpires()
    {
        var clock = new Clock();
        var throttle = new DiagnosticThrottle<string>(TimeSpan.FromSeconds(5), clock: clock);
        Assert.True(throttle.TryLog("npc", "fallback", out var count));
        Assert.Equal(0, count);
        for (var i = 0; i < 10; i++)
            Assert.False(throttle.TryLog("npc", "fallback", out _));
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.True(throttle.TryLog("npc", "fallback", out count));
        Assert.Equal(10, count);
        Assert.False(throttle.TryLog("npc", "fallback", out _));
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.True(throttle.TryLog("npc", "fallback", out count));
        Assert.Equal(1, count);
    }

    [Fact]
    public void SuppressedCallsDoNotExtendWindow()
    {
        var clock = new Clock();
        var throttle = new DiagnosticThrottle<int>(TimeSpan.FromSeconds(5), clock: clock);
        Assert.True(throttle.TryLog(1, "state", out _));
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.False(throttle.TryLog(1, "state", out _));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(throttle.TryLog(1, "state", out var count));
        Assert.Equal(1, count);
    }

    [Fact]
    public void AlternatingCategoriesAndNpcsHaveIndependentWindows()
    {
        var throttle = new DiagnosticThrottle<(int Npc, string Category)>(TimeSpan.FromSeconds(5));
        var keys = new[] { (1, "Staging"), (1, "FinalApproach"), (1, "PointOnFloor"), (2, "Staging") };
        foreach (var key in keys) Assert.True(throttle.TryLog(key, "fallback", out _));
        for (var frame = 0; frame < 100; frame++)
            foreach (var key in keys) Assert.False(throttle.TryLog(key, "fallback", out _));
        Assert.Equal(keys.Length, throttle.Count);
    }

    [Fact]
    public void StateChangesEmitImmediatelyAndResetWindow()
    {
        var clock = new Clock();
        var throttle = new DiagnosticThrottle<int>(TimeSpan.FromSeconds(5), clock: clock);
        Assert.True(throttle.TryLog(1, "fallback", out _));
        Assert.False(throttle.TryLog(1, "fallback", out _));
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.True(throttle.TryLog(1, "selected", out var count));
        Assert.Equal(1, count);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(throttle.TryLog(1, "selected", out _));
        Assert.True(throttle.TryLog(1, "fallback", out count));
        Assert.Equal(1, count);
    }

    [Fact]
    public void CapacityEvictsLeastRecentlyUsedKeyIncludingSuppressedAccesses()
    {
        var throttle = new DiagnosticThrottle<int>(TimeSpan.MaxValue, capacity: 2);
        Assert.True(throttle.TryLog(1, "state", out _));
        Assert.True(throttle.TryLog(2, "state", out _));
        Assert.False(throttle.TryLog(1, "state", out _));
        Assert.True(throttle.TryLog(3, "state", out _));
        Assert.False(throttle.TryLog(1, "state", out _));
        Assert.True(throttle.TryLog(2, "state", out _));
        Assert.Equal(2, throttle.Count);
    }

    [Fact]
    public void ForgetAndClearAllowFreshDiagnostics()
    {
        var throttle = new DiagnosticThrottle<int>(TimeSpan.MaxValue);
        Assert.True(throttle.TryLog(1, "state", out _));
        Assert.True(throttle.TryLog(2, "state", out _));
        throttle.Forget(1);
        throttle.Forget(99);
        Assert.Equal(1, throttle.Count);
        Assert.True(throttle.TryLog(1, "state", out var count));
        Assert.Equal(0, count);
        throttle.Clear();
        Assert.Equal(0, throttle.Count);
        Assert.True(throttle.TryLog(2, "state", out _));
    }

    [Fact]
    public void BackwardClockReadmitsRatherThanSuppressingIndefinitely()
    {
        var clock = new Clock { Timestamp = TimeSpan.TicksPerSecond * 10 };
        var throttle = new DiagnosticThrottle<int>(TimeSpan.FromSeconds(5), clock: clock);
        Assert.True(throttle.TryLog(1, "state", out _));
        Assert.False(throttle.TryLog(1, "state", out _));
        clock.Advance(TimeSpan.FromSeconds(-1));
        Assert.True(throttle.TryLog(1, "state", out var count));
        Assert.Equal(1, count);
        Assert.False(throttle.TryLog(1, "state", out _));
    }

    [Fact]
    public void ConcurrentMissingPathReportsOnceAndRearmsAfterRecovery()
    {
        var throttle = new DiagnosticThrottle<string>(TimeSpan.MaxValue, capacity: 8,
            comparer: StringComparer.OrdinalIgnoreCase);
        var emitted = 0;
        Parallel.For(0, 1000, i =>
        {
            if (throttle.TryLog(i % 2 == 0 ? "C:/Plugin/raphael-cli.exe" : "c:/plugin/RAPHAEL-CLI.EXE", "Missing", out _))
                Interlocked.Increment(ref emitted);
        });
        Assert.Equal(1, emitted);
        Assert.Equal(1, throttle.Count);
        Assert.True(throttle.TryLog("C:/Other/raphael-cli.exe", "Missing", out _));
        throttle.Forget("c:/plugin/raphael-cli.exe");
        Assert.True(throttle.TryLog("C:/Plugin/raphael-cli.exe", "Missing", out _));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(1, 0)]
    [InlineData(1, -1)]
    public void InvalidBoundsAreRejected(int seconds, int capacity)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new DiagnosticThrottle<int>(TimeSpan.FromSeconds(seconds), capacity));

    [Fact]
    public void SuffixOnlyFormatsNonzeroCounts()
    {
        Assert.Equal(string.Empty, DiagnosticThrottle.Suffix(0));
        Assert.Equal(" (suppressed 42 earlier repeats)", DiagnosticThrottle.Suffix(42));
    }
}
