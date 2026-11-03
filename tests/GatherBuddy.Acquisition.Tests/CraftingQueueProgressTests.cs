using GatherBuddy.Crafting;
using Xunit;

namespace GatherBuddy.Acquisition.Tests;

public sealed class CraftingQueueProgressTests
{
    private static CraftingQueueProgress Progress(int processed, params bool[] skips)
        => CraftingQueueProgress.Capture(skips, processed, static skipped => skipped);

    [Fact]
    public void NewQueueHasZeroConfirmedInsteadOfShowingFirstCraftCompleted()
    {
        var progress = Progress(0, false, false, false);
        Assert.Equal(0, progress.ConfirmedCrafts);
        Assert.Equal(3, progress.RemainingCrafts);
        Assert.Equal(3, progress.RequestedCrafts);
    }

    [Fact]
    public void PendingAndProcessedExplicitSkipsNeverCountAsConfirmed()
    {
        var progress = Progress(2, true, false, true, false);
        Assert.Equal(1, progress.ConfirmedCrafts);
        Assert.Equal(2, progress.ExplicitSkips);
        Assert.Equal(1, progress.RemainingCrafts);
        Assert.Equal(2, progress.RequestedCrafts);
        Assert.Equal(4, progress.TotalEntries);
    }

    [Fact]
    public void DeferredWorkStaysRemainingAfterIndependentProgress()
    {
        var progress = Progress(1, false, false, false);
        Assert.Equal(1, progress.ConfirmedCrafts);
        Assert.Equal(2, progress.RemainingCrafts);
    }

    [Fact]
    public void UnconfirmedAttemptsDoNotChangePresentation()
    {
        var before = Progress(0, false, false);
        // Preparation, native progress callbacks, and unsuccessful output do not advance the processor index.
        var after = Progress(0, false, false);
        Assert.Equal(before, after);
        Assert.Equal(0, after.ConfirmedCrafts);
    }

    [Fact]
    public void ConfirmedMultiOutputCraftCountsOnceRatherThanPerProducedItem()
    {
        var confirmed = CraftReadiness.ConfirmedCrafts(10, 13, 3, 1);
        var progress = Progress(confirmed, false, false);
        Assert.Equal(1, progress.ConfirmedCrafts);
        Assert.Equal(1, progress.RemainingCrafts);
    }

    [Fact]
    public void PartialQuickSynthesisLeavesUnconfirmedEntriesRemaining()
    {
        var confirmed = CraftReadiness.ConfirmedCrafts(0, 6, 3, 3);
        var progress = Progress(confirmed, false, false, false);
        Assert.Equal(2, progress.ConfirmedCrafts);
        Assert.Equal(1, progress.RemainingCrafts);
    }

    [Fact]
    public void FinishedQueueStillSeparatesExplicitExclusions()
    {
        var progress = Progress(3, false, true, false);
        Assert.Equal(2, progress.ConfirmedCrafts);
        Assert.Equal(1, progress.ExplicitSkips);
        Assert.Equal(0, progress.RemainingCrafts);
        Assert.Equal(progress.RequestedCrafts, progress.ConfirmedCrafts);
    }

    [Fact]
    public void EmptyQueueAndAllSkippedQueueHaveNoInventedSuccess()
    {
        Assert.Equal(new CraftingQueueProgress(0, 0, 0, 0), Progress(0));
        Assert.Equal(new CraftingQueueProgress(2, 0, 2, 0), Progress(0, true, true));
        Assert.Equal(0, Progress(0, true, true).RequestedCrafts);
    }

    [Fact]
    public void ProgressPartitionsQueueForEveryValidIndex()
    {
        bool[] skips = [true, false, false, true, false];
        for (var processed = 0; processed <= skips.Length; processed++)
        {
            var progress = Progress(processed, skips);
            Assert.Equal(progress.TotalEntries, progress.ConfirmedCrafts + progress.ExplicitSkips + progress.RemainingCrafts);
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void InvalidProcessedIndicesAreNotSilentlyMisreported(int processed)
        => Assert.Throws<ArgumentOutOfRangeException>(() => Progress(processed, false, false));
}
