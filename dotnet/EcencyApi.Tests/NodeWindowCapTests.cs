using EcencyApi.Infrastructure;
using Xunit;

namespace EcencyApi.Tests;

/// <summary>
/// The per-node window cap (#128): a node that answers 429 once it has
/// accepted a fixed number of calls in a rolling minute is sent no more than
/// that number, instead of being overrun and parked over and over.
/// </summary>
public class NodeWindowCapTests
{
    private const int Window = 60_000;

    private static void Send(NodeHealthTracker t, int node, int count)
    {
        for (var i = 0; i < count; i++)
        {
            Assert.Equal(NodeHealthTracker.Admission.Admitted, t.Admit(node, allowRateLimitParked: false));
            t.RecordSuccess(node, 50, CallClass.Cheap);
            t.EndAttempt(node);
        }
    }

    /// <summary>One more call, answered 429. A probe is a last-resort send past
    /// a park or the cap; otherwise the call is admitted the ordinary way.</summary>
    private static void Refused(NodeHealthTracker t, int node, int? retryAfterMs = 0, bool probe = false)
    {
        Assert.Equal(NodeHealthTracker.Admission.Admitted, t.Admit(node, allowRateLimitParked: probe, out var sent));
        t.RecordRateLimited(node, retryAfterMs, sent);
        t.EndAttempt(node);
    }

    private static NodeHealthTracker.NodeView View(NodeHealthTracker t, int node) => t.Snapshot()[node];

    [Fact]
    public void ANodeThatNeverRateLimited_HasNoCap()
    {
        long now = 0;
        var t = new NodeHealthTracker(1, () => now);
        Send(t, 0, 5_000);
        Assert.Null(View(t, 0).WindowCap);
        Assert.Equal(5_000, View(t, 0).WindowSent);
    }

    [Fact]
    public void A429AfterAFullWindow_SetsTheCap_AndTheNodeIsSkippedUntilTheWindowMoves()
    {
        long now = 0;
        var t = new NodeHealthTracker(2, () => now);
        Send(t, 0, 600);
        now += 10;
        Refused(t, 0);
        Assert.Equal(600, View(t, 0).WindowCap);
        // The refused call is refunded: the node did not count it either.
        Assert.Equal(600, View(t, 0).WindowSent);

        now += 10; // the Retry-After park (0) is over; the window is still full
        Assert.Equal(NodeHealthTracker.Admission.AtCap, t.Admit(0, allowRateLimitParked: false));
        Assert.True(t.ThrottledForMs(0) > 0);

        now = Window + 1; // the first 600 have left the window
        Assert.Equal(NodeHealthTracker.Admission.Admitted, t.Admit(0, allowRateLimitParked: false));
    }

    [Fact]
    public void A429AfterFewCalls_IsABurstOrAnEpisode_AndSetsNoCap()
    {
        long now = 0;
        var t = new NodeHealthTracker(1, () => now);
        Send(t, 0, 99);
        Refused(t, 0);
        Assert.Null(View(t, 0).WindowCap);
    }

    [Fact]
    public void A429BelowTheCap_LowersIt()
    {
        long now = 0;
        var t = new NodeHealthTracker(1, () => now);
        Send(t, 0, 600);
        Refused(t, 0);
        Assert.Equal(600, View(t, 0).WindowCap);

        now += Window + 1; // a fresh window; the operator has halved the limit
        Send(t, 0, 300);
        Refused(t, 0);
        Assert.Equal(300, View(t, 0).WindowCap);
    }

    [Fact]
    public void A429WellBelowTheCap_HalvesItTowardsTheCountItCameAt()
    {
        long now = 0;
        var t = new NodeHealthTracker(1, () => now);
        Send(t, 0, 600);
        Refused(t, 0);
        Assert.Equal(600, View(t, 0).WindowCap);

        // The operator drops the limit to 50: each 429 inside the cap halves it,
        // never below the count the 429 came at.
        foreach (var expected in new[] { 300, 150, 75, 50 })
        {
            now += Window + 1;
            Send(t, 0, 50);
            Refused(t, 0);
            Assert.Equal(expected, View(t, 0).WindowCap);
        }
        // Settled: the next window's 51st call is no longer sent.
        now += Window + 1;
        Send(t, 0, 50);
        Assert.Equal(NodeHealthTracker.Admission.AtCap, t.Admit(0, allowRateLimitParked: false));
    }

    [Fact]
    public void ABurstOf429s_HalvesTheCapOnce()
    {
        long now = 0;
        var t = new NodeHealthTracker(1, () => now);
        Send(t, 0, 2_000);
        Refused(t, 0);
        now += Window + 1;
        Send(t, 0, 50);
        // Thirty calls already in flight when the node starts refusing: one
        // verdict, not thirty, or one episode would ratchet the cap to the floor.
        var inFlight = new List<NodeHealthTracker.SendTicket?>();
        for (var i = 0; i < 30; i++)
        {
            Assert.Equal(NodeHealthTracker.Admission.Admitted, t.Admit(0, allowRateLimitParked: false, out var sent));
            inFlight.Add(sent);
        }
        foreach (var sent in inFlight)
        {
            t.RecordRateLimited(0, 5_000, sent);
            t.EndAttempt(0);
        }
        Assert.Equal(1_000, View(t, 0).WindowCap);
    }

    [Theory]
    [InlineData(0)]     // Retry-After: 0
    [InlineData(null)]  // none: our own ladder
    public void ABurstOf429s_HalvesTheCapOnce_WhateverThePark(int? retryAfterMs)
    {
        long now = 0;
        var t = new NodeHealthTracker(1, () => now);
        Send(t, 0, 2_000);
        Refused(t, 0);
        now += Window + 1;
        Send(t, 0, 50);
        var inFlight = new List<NodeHealthTracker.SendTicket?>();
        for (var i = 0; i < 30; i++)
        {
            Assert.Equal(NodeHealthTracker.Admission.Admitted, t.Admit(0, allowRateLimitParked: false, out var sent));
            inFlight.Add(sent);
        }
        // The replies trickle in; with Retry-After: 0 no park is left between them.
        foreach (var sent in inFlight)
        {
            now += 5;
            t.RecordRateLimited(0, retryAfterMs, sent);
            t.EndAttempt(0);
        }
        Assert.Equal(1_000, View(t, 0).WindowCap);

        // A call sent after the cut (and after any park) and refused again is a
        // new verdict.
        now += 60_001;
        Refused(t, 0);
        Assert.Equal(500, View(t, 0).WindowCap);
    }

    [Fact]
    public void A429ToAProbe_AtALowCount_LeavesTheCap()
    {
        long now = 0;
        var t = new NodeHealthTracker(1, () => now);
        Send(t, 0, 600);
        Refused(t, 0, retryAfterMs: 30_000);
        now += Window + 1;
        // Still parked: a last-resort probe sent into the park is expected to be
        // refused, and says nothing new about the window.
        Send(t, 0, 0);
        Refused(t, 0, retryAfterMs: 30_000, probe: true);
        Assert.Equal(600, View(t, 0).WindowCap);
    }

    [Fact]
    public void A429RefundsItsOwnSend_NotTheNewestOne()
    {
        long now = 0;
        var t = new NodeHealthTracker(1, () => now);
        // An older call still in flight when newer ones are sent and answered.
        Assert.Equal(NodeHealthTracker.Admission.Admitted, t.Admit(0, allowRateLimitParked: false, out var older));
        now = 30_000;
        Send(t, 0, 150);
        t.RecordRateLimited(0, 0, older);
        t.EndAttempt(0);
        Assert.Equal(150, View(t, 0).WindowCap);
        // All 150 counted calls were sent at 30s, so the first slot frees at 90s,
        // not when the refused call from 0s would have left.
        Assert.Equal(Window, t.ThrottledForMs(0));
    }

    [Fact]
    public void A429WithoutASend_RefundsNothing()
    {
        long now = 0;
        var t = new NodeHealthTracker(1, () => now);
        Send(t, 0, 150);
        // A caller that never admitted (the Hive-Engine client) still parks.
        t.RecordRateLimited(0, 1_000);
        Assert.Equal(150, View(t, 0).WindowSent);
        Assert.Equal(150, View(t, 0).WindowCap);
    }

    [Fact]
    public void AnExtraSendOnOneAdmission_IsCountedAndRefundedOnItsOwn()
    {
        long now = 0;
        var t = new NodeHealthTracker(1, () => now);
        Assert.Equal(NodeHealthTracker.Admission.Admitted, t.Admit(0, allowRateLimitParked: false, out _));
        var fallback = t.TryCountExtraSend(0, pastCap: false);
        Assert.Equal(2, View(t, 0).WindowSent);
        t.RecordRateLimited(0, 0, fallback);
        t.EndAttempt(0);
        Assert.Equal(1, View(t, 0).WindowSent);
    }

    [Fact]
    public void A429AboveTheCap_DoesNotRaiseIt()
    {
        long now = 0;
        var t = new NodeHealthTracker(1, () => now);
        Send(t, 0, 300);
        Refused(t, 0);
        now += 10;
        // Last-resort probes go past the cap; a 429 there says the cap was right.
        for (var i = 0; i < 200; i++)
        {
            Assert.Equal(NodeHealthTracker.Admission.Admitted, t.Admit(0, allowRateLimitParked: true));
            t.RecordSuccess(0, 50, CallClass.Cheap);
            t.EndAttempt(0);
        }
        Refused(t, 0, probe: true);
        Assert.Equal(300, View(t, 0).WindowCap);
    }

    [Fact]
    public void ANodeHeldAtItsCapForAWindowWithoutA429_GetsAHigherCap()
    {
        long now = 0;
        var t = new NodeHealthTracker(1, () => now);
        Send(t, 0, 400);
        Refused(t, 0);
        Assert.Equal(400, View(t, 0).WindowCap);

        // Steady demand: the node is met at its cap every few seconds.
        for (now = 5_000; now < Window; now += 5_000)
        {
            Assert.Equal(NodeHealthTracker.Admission.AtCap, t.Admit(0, allowRateLimitParked: false));
        }
        // The window rolls over and refills at once: still held at the cap.
        now = Window;
        Send(t, 0, 400);
        Assert.Equal(NodeHealthTracker.Admission.AtCap, t.Admit(0, allowRateLimitParked: false));
        Assert.Equal(400, View(t, 0).WindowCap);

        // A whole window since it was first held there, no 429: the operator may
        // have raised the limit, so try half as much again.
        now = Window + 5_000;
        Assert.Equal(NodeHealthTracker.Admission.Admitted, t.Admit(0, allowRateLimitParked: false));
        t.EndAttempt(0);
        Assert.Equal(600, View(t, 0).WindowCap);
    }

    [Fact]
    public void AnIdleNodesCap_DoesNotGrow()
    {
        long now = 0;
        var t = new NodeHealthTracker(1, () => now);
        Send(t, 0, 400);
        Refused(t, 0);
        now += 10 * Window; // nothing sent: the cap was never tested
        Send(t, 0, 10);
        Assert.Equal(400, View(t, 0).WindowCap);
    }

    [Fact]
    public void ANodeThatLeftItsCap_StartsAFreshWindowBeforeItsCapGrows()
    {
        long now = 0;
        var t = new NodeHealthTracker(1, () => now);
        Send(t, 0, 400);
        Refused(t, 0);
        now += 10;
        Assert.Equal(NodeHealthTracker.Admission.AtCap, t.Admit(0, allowRateLimitParked: false));

        // Traffic falls: the node sits under its cap for minutes.
        now += 3 * Window;
        Send(t, 0, 10);
        now += 6 * Window;
        Send(t, 0, 400);
        // At the cap again, but only just: not a window spent there.
        Assert.Equal(NodeHealthTracker.Admission.AtCap, t.Admit(0, allowRateLimitParked: false));
        Assert.Equal(400, View(t, 0).WindowCap);
    }

    [Fact]
    public void ALastResortProbe_IsAdmittedPastTheCap()
    {
        long now = 0;
        var t = new NodeHealthTracker(1, () => now);
        Send(t, 0, 600);
        Refused(t, 0);
        now += 10;
        Assert.Equal(NodeHealthTracker.Admission.AtCap, t.Admit(0, allowRateLimitParked: false));
        Assert.Equal(NodeHealthTracker.Admission.Admitted, t.Admit(0, allowRateLimitParked: true));
    }

    [Fact]
    public void RetryAfter_ParksForItsValue_AndDoesNotAdvanceTheEscalation()
    {
        long now = 0;
        var t = new NodeHealthTracker(1, () => now);
        t.RecordRateLimited(0, 3_000);
        Assert.Equal(3_000, t.RateLimitedForMs(0));
        now += 3_001;
        t.RecordRateLimited(0, 3_000);
        now += 3_001;
        t.RecordRateLimited(0, 3_000);
        now += 3_001;
        // Header-less: the first step of the ladder, not a later one.
        t.RecordRateLimited(0, null);
        Assert.Equal(10_000, t.RateLimitedForMs(0));
    }

    [Fact]
    public void AnAtCapNode_IsOfferedAsAThrottledCandidate_FreeingWhenItsOldestCallLeaves()
    {
        long now = 0;
        var t = new NodeHealthTracker(1, () => now);
        Send(t, 0, 300); // at t=0
        now = 30_000;
        Send(t, 0, 300); // at t=30s
        Refused(t, 0);
        // The first slot frees when the calls sent at t=0 leave the window.
        Assert.Equal(Window - 30_000, t.ThrottledForMs(0));
    }
}
