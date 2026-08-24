using EcencyApi.Infrastructure;
using Xunit;

namespace EcencyApi.Tests;

/// <summary>
/// The health tracker's per-call-class latency, driven directly with an
/// injected clock: latency is the only thing that splits by class, everything
/// that decides whether a node is answering at all stays node-wide.
/// </summary>
public class NodeCallClassTests
{
    private static (NodeHealthTracker Tracker, Action<long> Advance) Build(int nodes)
    {
        long now = 0;
        return (new NodeHealthTracker(nodes, () => now), ms => now += ms);
    }

    private static double? Ewma(NodeHealthTracker t, int node, CallClass cls) =>
        t.Snapshot()[node].Latency.First(l => l.Class == cls).EwmaMs;

    private static int Samples(NodeHealthTracker t, int node, CallClass cls) =>
        t.Snapshot()[node].Latency.First(l => l.Class == cls).Samples;

    [Fact]
    public void CallClassValues_StayContiguousFromZero()
    {
        // Each node holds one latency profile per class in an array indexed by the
        // enum value, on the ordering hot path. A gap, a negative member or a
        // renumbering would index outside that array, so the layout is pinned here
        // instead of trusted, and the check costs nothing.
        var values = Enum.GetValues<CallClass>().Select(v => (int)v).ToArray();
        Assert.Equal(Enumerable.Range(0, values.Length).ToArray(), values);
    }

    [Fact]
    public void EachClassKeepsItsOwnLatencyProfile()
    {
        var (t, _) = Build(1);
        for (var i = 0; i < 3; i++) t.RecordSuccess(0, 100, CallClass.Cheap);
        for (var i = 0; i < 3; i++) t.RecordSuccess(0, 1500, CallClass.Heavy);

        Assert.Equal(100, Ewma(t, 0, CallClass.Cheap));
        Assert.Equal(1500, Ewma(t, 0, CallClass.Heavy));
        Assert.Equal(3, Samples(t, 0, CallClass.Cheap));
        Assert.Equal(3, Samples(t, 0, CallClass.Heavy));
    }

    [Fact]
    public void ANodeQuickOnPointReadsAndSlowOnFeedQueries_LeadsOnlyTheCheapOrdering()
    {
        // The whole point of the split: node 0 wins the cheap ranking on its own
        // measurements and must NOT carry that win into the heavy ranking, where
        // it is slower than a node nothing is known about.
        var (t, _) = Build(2);
        for (var i = 0; i < 3; i++)
        {
            t.RecordSuccess(0, 100, CallClass.Cheap);
            t.RecordSuccess(0, 1500, CallClass.Heavy);
        }

        Assert.Equal(new[] { 0, 1 }, t.OrderedNodeIndices(CallClass.Cheap));
        Assert.Equal(new[] { 1, 0 }, t.OrderedNodeIndices(CallClass.Heavy));
    }

    [Fact]
    public void OneClassGoingStale_LeavesTheOtherProfileAlone()
    {
        // Staleness is per class. A class the traffic has moved away from
        // becoming unproven again is exploration, not a penalty: the node keeps
        // its other profile and all of its health.
        var (t, advance) = Build(2);
        advance(1_000); // a profile stamped at tick 0 reads as never stamped
        for (var i = 0; i < 3; i++)
        {
            t.RecordSuccess(0, 100, CallClass.Cheap);
            t.RecordSuccess(0, 1500, CallClass.Heavy);
        }

        advance(6 * 60_000);
        t.RecordSuccess(0, 120, CallClass.Cheap);

        Assert.Equal(1, Samples(t, 0, CallClass.Cheap));   // reset and re-learning
        Assert.Equal(120, Ewma(t, 0, CallClass.Cheap));
        Assert.Equal(3, Samples(t, 0, CallClass.Heavy));   // untouched
        Assert.Equal(1500, Ewma(t, 0, CallClass.Heavy));
        // ...but stale, so it no longer orders anything: node 0 scores the prior
        // for heavy, so config order breaks the tie with the untried node.
        Assert.Equal(new[] { 0, 1 }, t.OrderedNodeIndices(CallClass.Heavy));
    }

    [Fact]
    public void ATimeoutIsALatencySample_ForTheClassThatTimedOut()
    {
        // Floored above the unproven prior so a node that never answers a heavy
        // query cannot outrank nodes never tried for one. The cheap profile learns
        // nothing from it, because nothing cheap was measured.
        var (t, _) = Build(2);
        for (var i = 0; i < 3; i++) t.RecordFailure(0, 300, CallClass.Heavy, timedOut: true);

        Assert.True(Ewma(t, 0, CallClass.Heavy) > 1000);
        Assert.Equal(3, Samples(t, 0, CallClass.Heavy));
        Assert.Null(Ewma(t, 0, CallClass.Cheap));
        Assert.Equal(0, Samples(t, 0, CallClass.Cheap));
    }

    [Fact]
    public void AFailureParkedNode_IsSkippedForEveryClass()
    {
        // "Not answering" is not a per-class property: a parked node is out of
        // both orderings while any other node can take the call.
        var (t, _) = Build(2);
        for (var i = 0; i < 3; i++) t.RecordFailure(0, 300, CallClass.Heavy, timedOut: true);

        Assert.Equal(new[] { 1 }, t.OrderedNodeIndices(CallClass.Heavy));
        Assert.Equal(new[] { 1 }, t.OrderedNodeIndices(CallClass.Cheap));
    }

    [Fact]
    public void ARateLimitedNode_SortsLastForEveryClass()
    {
        var (t, _) = Build(2);
        for (var i = 0; i < 3; i++) t.RecordSuccess(0, 10, CallClass.Cheap);
        for (var i = 0; i < 3; i++) t.RecordSuccess(0, 10, CallClass.Heavy);
        t.RecordRateLimited(0, 5_000);

        Assert.Equal(new[] { 1, 0 }, t.OrderedNodeIndices(CallClass.Cheap));
        Assert.Equal(new[] { 1, 0 }, t.OrderedNodeIndices(CallClass.Heavy));
    }

    [Fact]
    public void ARecentFailureOnOneClass_DemotesTheNodeForBoth()
    {
        // Deliberate. It is also the narrow scope of the split: only the latency
        // score is per class. A node that just failed is a node that just failed,
        // whatever the call was, so it sorts behind clean nodes for everything.
        var (t, _) = Build(2);
        for (var i = 0; i < 3; i++) t.RecordSuccess(0, 10, CallClass.Cheap);
        t.RecordFailure(0, 50, CallClass.Heavy);

        Assert.Equal(new[] { 1, 0 }, t.OrderedNodeIndices(CallClass.Cheap));
        Assert.Equal(new[] { 1, 0 }, t.OrderedNodeIndices(CallClass.Heavy));
    }

    [Fact]
    public void ASuccessOnOneClass_ClearsNodeWideFailureState()
    {
        var (t, _) = Build(2);
        for (var i = 0; i < 3; i++) t.RecordFailure(0, 300, CallClass.Heavy, timedOut: true);
        Assert.Equal(new[] { 1 }, t.OrderedNodeIndices(CallClass.Cheap));

        t.RecordSuccess(0, 20, CallClass.Cheap);

        Assert.Equal(2, t.OrderedNodeIndices(CallClass.Cheap).Count);
        Assert.Equal(0, t.Snapshot()[0].FailureParkedForMs);
    }
}
