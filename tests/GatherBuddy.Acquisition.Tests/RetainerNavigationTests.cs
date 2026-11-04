using System.Numerics;
using GatherBuddy.Automation;
using GatherBuddy.Crafting;
using Xunit;

namespace GatherBuddy.Acquisition.Tests;

public class RetainerNavigationTests
{
    private sealed class Port : IRetainerWithdrawalPort
    {
        public bool Available { get; set; } = true;
        public bool Busy { get; set; }
        public bool MultiMode { get; set; }
        public bool Suppressed { get; set; }
        public bool PendingWork { get; set; }
        public bool Ready { get; set; } = true;
        public ulong CharacterId => 42;
        public bool InteractionClosed => true;
        public void SetSuppressed(bool value) => throw new Exception("Navigation must be read-only");
        public void CloseOwnedInteraction() => throw new Exception("Navigation cannot own bell interaction");
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    public void PendingExternalAndHandoffWorkPreventNavigation(bool pending, bool busy, bool multi, bool handoff)
    {
        var port = new Port { PendingWork = pending, Busy = busy, MultiMode = multi };
        Assert.False(RetainerNavigationAdmission.Check(port, handoff, false, out var reason));
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void BorrowedSuppressionAllowsNavigationWithoutWrites()
        => Assert.True(RetainerNavigationAdmission.Check(new Port { Suppressed = true, PendingWork = true }, false, false, out _));

    [Fact]
    public void UnavailableArDoesNotPreventNavigation()
        => Assert.True(RetainerNavigationAdmission.Check(new Port { Available = false, Busy = true, PendingWork = true }, false, false, out _));

    [Fact]
    public void ReadinessAndCleanupAreIndependentGates()
    {
        Assert.False(RetainerNavigationAdmission.Check(new Port { Ready = false }, false, false, out _));
        Assert.False(RetainerNavigationAdmission.Check(new Port(), false, true, out _));
    }

    [Fact]
    public void AdmissionIsReevaluatedAfterArReadinessChanges()
    {
        var port = new Port();
        Assert.True(RetainerNavigationAdmission.Check(port, false, false, out _));
        port.PendingWork = true;
        Assert.False(RetainerNavigationAdmission.Check(port, false, false, out _));
        port.PendingWork = false;
        Assert.True(RetainerNavigationAdmission.Check(port, false, false, out _));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.5f)]
    [InlineData(2f)]
    public void CandidateRingWithinHitboxPlusTwoPointFiveNotAtCenter(float hitboxRadius)
    {
        for (var i = 0; i < 8; ++i)
        {
            var point = RetainerBellGeometry.Approach(new(20, 0, 0), Vector3.Zero, hitboxRadius, i);
            Assert.True(RetainerBellGeometry.InRange(point, Vector3.Zero, hitboxRadius));
            Assert.Equal(hitboxRadius + 1.5f, point.Length(), 4);
        }
        Assert.False(RetainerBellGeometry.InRange(new(0, 3, 0), Vector3.Zero, hitboxRadius));
        Assert.False(RetainerBellGeometry.InRange(new(40, 0, 0), Vector3.Zero, hitboxRadius));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.5f)]
    [InlineData(2f)]
    public void StopBoundaryIsLiveHitboxPlusTwoPointFive(float hitboxRadius)
    {
        var range = hitboxRadius + 2.5f;
        Assert.Equal(range, RetainerBellGeometry.Range(hitboxRadius));
        Assert.True(RetainerBellGeometry.InRange(new(range, 0, 0), Vector3.Zero, hitboxRadius));
        Assert.False(RetainerBellGeometry.InRange(new(range + 0.01f, 0, 0), Vector3.Zero, hitboxRadius));
    }

    [Fact]
    public void StopPendingQuerySignalsActualCallerCancellationAndWaitsForSettlement()
    {
        var completion = new TaskCompletionSource<List<Vector3>>();
        CancellationToken token = default;
        var query = new RetainerPathQuery(t => { token = t; return completion.Task; });
        Assert.False(query.StopAndSettle());
        Assert.True(token.IsCancellationRequested);
        Assert.False(query.StopAndSettle());
        completion.SetResult([Vector3.One]); // A provider may complete despite cancellation.
        Assert.True(query.StopAndSettle());
        Assert.Null(query.TakeCompleted());
    }

    [Fact]
    public void CompletedQueryStillCannotEscapeCancellationOrBeTakenTwice()
    {
        var query = new RetainerPathQuery(_ => Task.FromResult(new List<Vector3> { Vector3.One }));
        Assert.Equal(Vector3.One, Assert.Single(query.TakeCompleted()!));
        Assert.Null(query.TakeCompleted());
        Assert.True(query.StopAndSettle());
        Assert.True(query.StopAndSettle());
        Assert.Null(query.TakeCompleted());
    }

    [Fact]
    public void FaultedAndCancelledQueriesSettleWithoutLaunchingMovement()
    {
        var failed = new RetainerPathQuery(_ => Task.FromException<List<Vector3>>(new Exception("mesh failed")));
        Assert.Throws<Exception>(() => failed.TakeCompleted());
        Assert.True(failed.StopAndSettle());
        var cancelled = new RetainerPathQuery(_ => Task.FromCanceled<List<Vector3>>(new CancellationToken(true)));
        Assert.True(cancelled.StopAndSettle());
        Assert.Null(cancelled.TakeCompleted());
    }

    [Fact]
    public void OnlyExactRemainingWaypointSuffixCanBeStopped()
    {
        Vector3[] submitted = [Vector3.Zero, Vector3.One, new(2, 2, 2)];
        Assert.True(RetainerPathQuery.MatchesRemaining(submitted, submitted));
        Assert.True(RetainerPathQuery.MatchesRemaining(submitted, submitted[1..]));
        Assert.False(RetainerPathQuery.MatchesRemaining(submitted, []));
        Assert.False(RetainerPathQuery.MatchesRemaining(submitted, [new(50, 0, 0), submitted[^1]]));
        Assert.False(RetainerPathQuery.MatchesRemaining(submitted, [submitted[0]]));
    }
}
