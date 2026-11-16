using GatherBuddy.Crafting;
using GatherBuddy.Vulcan;
using Xunit;

namespace GatherBuddy.Compatibility.Tests;

public class RaphaelAdaptiveSeedCacheTests
{
    private static RaphaelSolveRequest Request => new(5618, 100, 5953, 5490, 664, true, false);

    [Fact]
    public void PublicationOwnsActionsAndLookupNeverReadsMutableList()
    {
        var request = Request;
        var actions = new List<uint> { (uint)VulcanSkill.MuscleMemory, (uint)VulcanSkill.Groundwork };
        var entry = new CachedRaphaelSolution(request.GetKey(), request) { ActionIds = actions };
        var cache = new RaphaelAdaptiveSeedCache();
        cache.Publish(entry, actions);
        actions.Clear();
        entry.ActionIds = null!; // Lookup must not even enumerate the live list.
        Assert.True(cache.TryGet(request, entry, out var seed, out _));
        Assert.Equal(new[] { VulcanSkill.MuscleMemory, VulcanSkill.Groundwork }, seed!.Actions);
        Assert.Null(entry.ActionIds);
        Assert.False(entry.IsFailed);
        Assert.Throws<NotSupportedException>(() => ((IList<VulcanSkill>)seed.Actions).Clear());
    }

    [Fact]
    public void LookupRequiresExactRequestAndCurrentPublicationOwner()
    {
        var request = Request;
        var entry = new CachedRaphaelSolution(request.GetKey(), request);
        var cache = new RaphaelAdaptiveSeedCache();
        cache.Publish(entry, new[] { (uint)VulcanSkill.Groundwork });
        Assert.False(cache.TryGet(request, null, out _, out _));
        Assert.False(cache.TryGet(request, new(request.GetKey(), request), out _, out _));
        foreach (var changed in new[] { request with { InitialQuality = 1 }, request with { CP = 663 },
                     request with { Specialist = true }, request with { ValidationContext = "different" } })
            Assert.False(cache.TryGet(changed, entry, out _, out _));
        entry.IsFailed = true;
        Assert.False(cache.TryGet(request, entry, out _, out _));
        entry.IsFailed = false;
        entry.Request = request with { InitialQuality = 1 };
        Assert.False(cache.TryGet(request, entry, out _, out _));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(257, false)]
    [InlineData(1, true)]
    public void FailedEmptyAndOversizedPublicationsRemainUnavailable(int count, bool failed)
    {
        var request = Request;
        var entry = new CachedRaphaelSolution(request.GetKey(), request) { IsFailed = failed };
        var cache = new RaphaelAdaptiveSeedCache();
        cache.Publish(entry, Enumerable.Repeat((uint)VulcanSkill.Groundwork, count).ToArray());
        Assert.False(cache.TryGet(request, entry, out var seed, out _));
        Assert.Null(seed);
    }

    [Fact]
    public void UnsupportedRemovalAndClearCannotReturnOldPublication()
    {
        var request = Request;
        var entry = new CachedRaphaelSolution(request.GetKey(), request);
        var cache = new RaphaelAdaptiveSeedCache();
        cache.Publish(entry, new[] { (uint)VulcanSkill.HastyTouch });
        Assert.False(cache.TryGet(request, entry, out _, out _));
        cache.Publish(entry, new[] { (uint)VulcanSkill.Groundwork });
        Assert.True(cache.TryGet(request, entry, out _, out _));
        cache.Remove(request.GetKey());
        Assert.False(cache.TryGet(request, entry, out _, out _));
        cache.Publish(entry, new[] { (uint)VulcanSkill.Groundwork });
        cache.Clear();
        Assert.False(cache.TryGet(request, entry, out _, out _));
    }
}
