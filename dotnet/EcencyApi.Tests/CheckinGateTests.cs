using EcencyApi.Infrastructure;
using Xunit;

namespace EcencyApi.Tests;

/// <summary>
/// The check-in gate is the one place in this service that can silently decide a
/// user action never happened: it answers 201 and drops the request. Every rule
/// it depends on is pinned here, because the failure mode is invisible: the
/// client is told the check-in landed.
/// </summary>
public class CheckinGateTests
{
    /// <summary>
    /// The web client's check-in poll interval (<c>1000 * 60 * 15 + 8</c> in
    /// vision-next's <c>user-activity-recorder.tsx</c>). The gate has to let a
    /// caller polling at this rate through every single time.
    /// </summary>
    private const long ClientPollIntervalMs = 1000 * 60 * 15 + 8;

    /// <summary>
    /// Conservative lower bound on the points backend's own per-account minimum
    /// spacing, which is a little under 15 minutes. The exact value belongs to
    /// that service; the gate only needs to stay below it, so that anything it
    /// absorbs is something the backend would have refused anyway.
    /// </summary>
    private const long BackendMinSpacingLowerBoundMs = 870_000;

    [Fact]
    public void TheWindowClosesWellBeforeAClientPollsAgain()
    {
        // The regression this guards: the window used to sit 8 ms below the poll
        // interval, so whether a legitimate check-in survived came down to whether
        // its arrival delay happened to be longer than the previous one's.
        Assert.True(CheckinGate.WindowMs < ClientPollIntervalMs);
        Assert.True(ClientPollIntervalMs - CheckinGate.WindowMs >= 60_000,
            "the gap between the window and the poll interval must be far larger than arrival jitter");
    }

    [Fact]
    public void TheWindowNeverOutlastsTheBackendsOwnSpacing()
    {
        // Keeps the gate strictly weaker than the rule it fronts, so absorbing a
        // request can never cost an account a check-in it would otherwise have got.
        Assert.True(CheckinGate.WindowMs <= BackendMinSpacingLowerBoundMs);
    }

    [Fact]
    public void ACachedEntryOutlivesItsOwnWindow()
    {
        // If the entry expired first, the window would end early and silently.
        Assert.True(CheckinGate.CacheTtlSeconds * 1000 >= CheckinGate.WindowMs);
    }

    [Fact]
    public void EachAccountGetsItsOwnNamespacedWindow()
    {
        // Accounts sharing one network address must not share a check-in slot.
        // CacheKey takes nothing but the username, which is what makes that true;
        // the namespace keeps it clear of the other users of this cache.
        Assert.NotEqual(CheckinGate.CacheKey("alice"), CheckinGate.CacheKey("bob"));
        Assert.StartsWith("checkin:", CheckinGate.CacheKey("alice"));
    }

    [Fact]
    public void AFirstCheckinIsAlwaysForwarded()
    {
        Assert.False(CheckinGate.IsWithinWindow(null, 1_000_000));
        Assert.False(CheckinGate.IsWithinWindow("", 1_000_000));
    }

    [Fact]
    public void ARepeatInsideTheWindowIsAbsorbed()
    {
        var stamp = CheckinGate.Stamp(1_000_000);

        Assert.True(CheckinGate.IsWithinWindow(stamp, 1_000_000));
        Assert.True(CheckinGate.IsWithinWindow(stamp, 1_000_000 + CheckinGate.WindowMs - 1));
    }

    [Fact]
    public void TheWindowEndsExactlyWhereItSays()
    {
        var stamp = CheckinGate.Stamp(1_000_000);

        Assert.False(CheckinGate.IsWithinWindow(stamp, 1_000_000 + CheckinGate.WindowMs));
        Assert.False(CheckinGate.IsWithinWindow(stamp, 1_000_000 + CheckinGate.WindowMs + 1));
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("NaN")]
    [InlineData(" ")]
    public void AnUnreadableStampFailsOpen(string stored)
    {
        // Forwarding a repeat costs one upstream call the backend discards.
        // Absorbing a real check-in costs the account its check-in and its streak.
        Assert.False(CheckinGate.IsWithinWindow(stored, 1_000_000));
    }

    [Fact]
    public void AStampFromTheFutureFailsOpen()
    {
        var stamp = CheckinGate.Stamp(2_000_000);

        Assert.False(CheckinGate.IsWithinWindow(stamp, 1_000_000));
    }

    [Fact]
    public void AbsorbedRepeatsDoNotDisplaceASteadyPoller()
    {
        // Mirrors the handler loop: a forwarded check-in stores its timestamp, an
        // absorbed one stores nothing. A second check-in source for the same
        // account sits between the polls, either a second tab or the ping a page
        // load fires on mount.
        //
        // While an absorbed request also refreshed the window, that extra source
        // moved the window mid-cycle, the next scheduled poll landed inside it and
        // was absorbed, that absorption moved the window again, so the account
        // never got another check-in through until its page reloaded. Anchoring the
        // window to the last *forwarded* check-in is what breaks that loop.
        const long extraSourceOffsetMs = 420_000;

        string? stored = null;
        var pollsForwarded = 0;

        for (var poll = 0; poll < 20; poll++)
        {
            var pollAt = poll * ClientPollIntervalMs;

            var pollDecision = CheckinGate.Decide(stored, pollAt);
            stored = pollDecision.StampToStore ?? stored;
            if (pollDecision.Forward)
            {
                pollsForwarded++;
            }

            var extraDecision = CheckinGate.Decide(stored, pollAt + extraSourceOffsetMs);
            stored = extraDecision.StampToStore ?? stored;
        }

        Assert.Equal(20, pollsForwarded);
    }

    [Fact]
    public void AnAbsorbedRepeatStoresNothing()
    {
        // The structural half of the rule above: the gate cannot hand a caller a
        // timestamp to store for a request it just absorbed.
        var stamp = CheckinGate.Stamp(1_000_000);
        var decision = CheckinGate.Decide(stamp, 1_000_000 + CheckinGate.WindowMs - 1);

        Assert.False(decision.Forward);
        Assert.Null(decision.StampToStore);
    }

    [Fact]
    public void AForwardedCheckinStoresItsOwnArrival()
    {
        var decision = CheckinGate.Decide(null, 1_000_000);

        Assert.True(decision.Forward);
        Assert.Equal(CheckinGate.Stamp(1_000_000), decision.StampToStore);
    }

    [Fact]
    public void ABurstFromOneAccountStillCollapsesToOneUpstreamCall()
    {
        // The gate still has to do its job: repeated check-ins inside one window
        // must cost exactly one upstream call.
        string? stored = null;
        var forwarded = 0;

        for (var i = 0; i < 10; i++)
        {
            var decision = CheckinGate.Decide(stored, i * 30_000L);
            stored = decision.StampToStore ?? stored;
            if (decision.Forward)
            {
                forwarded++;
            }
        }

        Assert.Equal(1, forwarded);
    }
}
