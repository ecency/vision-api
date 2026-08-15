using System.Globalization;

namespace EcencyApi.Infrastructure;

/// <summary>
/// De-duplication window for check-in activities (<c>ty</c> 10) on
/// <c>/private-api/usr-activity</c>.
///
/// Clients poll check-in on a fixed interval a little over 15 minutes. The
/// points backend enforces its own minimum spacing <em>per account</em> and
/// refuses anything closer. This gate exists only to save the upstream call for
/// a repeat the backend would refuse anyway; it is not an authorization check
/// and it is not a rate limiter. Every rule below follows from that. Each one
/// has been wrong here before:
///
/// <list type="bullet">
/// <item>The window is keyed on the <em>account</em>, never on the caller's
/// address. Several accounts routinely share one address (NAT, carrier-grade
/// NAT, a household). An address-keyed window makes them compete for a single
/// check-in slot. Keying on the address also buys nothing: a check-in carries a
/// signed code, so a caller can only check in as an account it controls.</item>
/// <item>The window is anchored, not sliding. An absorbed repeat stores
/// nothing. Refreshing the anchor on one pushes the window past the caller's
/// next scheduled check-in, which is then absorbed too, leaving a steady poller
/// with no way out.</item>
/// <item>Only a check-in the backend will actually credit becomes the new
/// anchor. Between <see cref="WindowMs"/> and <see cref="AnchorAfterMs"/> a
/// check-in is forwarded but leaves the anchor alone, because the backend is
/// going to refuse it as too early. Anchoring on a refused attempt would move
/// the window under the caller's own schedule and cost it the next check-in,
/// which is the same failure in a different disguise.</item>
/// <item>The window stays comfortably below the client poll interval. At or
/// near it, which of two consecutive polls survives comes down to arrival
/// jitter.</item>
/// </list>
///
/// The two thresholds exist because two different clocks matter: the client
/// decides how often a check-in arrives, the backend decides how often one
/// counts. Every boundary case resolves toward forwarding. A needless forward
/// costs one upstream call that the backend discards; a needless absorb costs
/// an account its check-in and its streak. The caller cannot even tell,
/// because the gate answers 201.
/// </summary>
public static class CheckinGate
{
    /// <summary>
    /// What the gate decided for one request. <see cref="StampToStore"/> is
    /// non-null only when the request is forwarded <em>and</em> is far enough
    /// from the last anchor for the backend to credit it.
    /// </summary>
    public readonly record struct Decision(bool Forward, string? StampToStore);

    /// <summary>
    /// A repeat closer than this to the anchor is absorbed. Deliberately well
    /// under the client poll interval, so a steady poller is never a coin flip.
    /// Also under the backend's per-account spacing, so anything absorbed here
    /// would have been refused there.
    /// </summary>
    public const long WindowMs = 780_000;

    /// <summary>
    /// A forwarded check-in this far from the anchor moves it. Must be at least
    /// the backend's per-account spacing: too low and the gate anchors on an
    /// attempt the backend refused, too high and it merely forwards once more
    /// than it had to, which is the harmless direction.
    /// </summary>
    public const long AnchorAfterMs = 900_000;

    /// <summary>
    /// Derived from the thresholds so they cannot drift apart: an entry that
    /// outlives its own usefulness would only be read to conclude "expired".
    /// </summary>
    public const double CacheTtlSeconds = AnchorAfterMs / 1000d;

    /// <summary>Cache key for an account's anchor.</summary>
    public static string CacheKey(string username) => "checkin:" + username;

    /// <summary>Serializes a timestamp for the cache; inverse of the parse in
    /// <see cref="AgeMs"/>.</summary>
    public static string Stamp(long nowMs) => nowMs.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// How long ago <paramref name="recorded"/> was, or null when there is no
    /// usable anchor. Absent, unreadable and future timestamps all read as no
    /// anchor, which forwards the check-in and replaces the bad entry.
    /// </summary>
    public static double? AgeMs(string? recorded, long nowMs)
    {
        if (string.IsNullOrEmpty(recorded))
        {
            return null;
        }

        if (!double.TryParse(recorded, NumberStyles.Float, CultureInfo.InvariantCulture, out var recMs))
        {
            return null;
        }

        var age = nowMs - recMs;
        return age >= 0 ? age : null;
    }

    /// <summary>True when the anchor still covers <paramref name="nowMs"/>, so a
    /// check-in arriving then is absorbed.</summary>
    public static bool IsWithinWindow(string? recorded, long nowMs) =>
        AgeMs(recorded, nowMs) is { } age && age < WindowMs;

    /// <summary>Decides one check-in against the account's current anchor.</summary>
    public static Decision Decide(string? recorded, long nowMs)
    {
        var age = AgeMs(recorded, nowMs);

        if (age is { } covered && covered < WindowMs)
        {
            return new Decision(false, null);
        }

        var anchors = age is not { } gap || gap >= AnchorAfterMs;
        return new Decision(true, anchors ? Stamp(nowMs) : null);
    }

    /// <summary>
    /// Reads the anchor, decides against it, then stores the new one as a single
    /// step.
    ///
    /// The three have to happen together. Two check-ins for one account can
    /// arrive in the same instant (two tabs opening at once both fire the ping
    /// their page load schedules). If both read the anchor before either writes,
    /// both are forwarded, which is the burst this gate exists to collapse.
    /// Serializing them makes a concurrent duplicate behave exactly like a
    /// sequential one: the second reads the anchor the first just wrote
    /// and is absorbed. That stays correct in the direction this gate cares
    /// about, because a check-in milliseconds behind another is one the backend
    /// refuses regardless.
    ///
    /// Striped rather than one global lock so unrelated accounts never queue
    /// behind each other. The lock covers in-memory work only. It is never held
    /// across an await.
    /// </summary>
    public static Decision DecideAndReserve(string username, long nowMs)
    {
        var key = CacheKey(username);

        lock (StripeFor(key))
        {
            string? recorded = null;
            try
            {
                recorded = MemCache.Get<string>(key);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                Console.Error.WriteLine("Cache get failed.");
            }

            var decision = Decide(recorded, nowMs);

            if (decision.StampToStore != null)
            {
                try
                {
                    MemCache.Set(key, decision.StampToStore, CacheTtlSeconds);
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine(e);
                    Console.Error.WriteLine("Cache set failed.");
                }
            }

            return decision;
        }
    }

    /// <summary>
    /// Gives up an anchor whose check-in never reached the backend.
    ///
    /// The anchor is reserved before the upstream call, because that is what
    /// closes the burst race. If the call then fails to deliver, holding the
    /// anchor would absorb the account's next attempt on the strength of a
    /// check-in that never happened, which is the failure this whole gate is
    /// being fixed for. Releasing puts the account back where it started.
    ///
    /// Only an anchor still holding <paramref name="stamp"/> is removed, so this
    /// can never discard one a later check-in established.
    /// </summary>
    public static void Release(string username, string stamp)
    {
        var key = CacheKey(username);

        lock (StripeFor(key))
        {
            try
            {
                if (MemCache.Get<string>(key) == stamp)
                {
                    MemCache.Del(key);
                }
            }
            catch
            {
                // Deliberately silent as well as swallowed. This runs after the
                // response has been written, so letting it escape would raise an
                // error the client can no longer be told about. A cache throwing
                // here is already saying so from the read and the write on the way
                // in. A request handler should not be adding logging of its own.
            }
        }
    }

    private const int StripeCount = 64;

    private static readonly object[] Stripes =
        Enumerable.Range(0, StripeCount).Select(_ => new object()).ToArray();

    private static object StripeFor(string key) =>
        Stripes[(uint)StringComparer.Ordinal.GetHashCode(key) % StripeCount];
}
