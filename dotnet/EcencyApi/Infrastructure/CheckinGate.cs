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
/// NAT, a household). An address-keyed window makes them compete for a
/// single check-in slot. Keying on the address also buys nothing: a check-in
/// carries a signed code, so a caller can only check in as an account it
/// controls.</item>
/// <item>The window is fixed, not sliding. Only a forwarded check-in stores a
/// timestamp. Refreshing it on an absorbed repeat pushes the window past the
/// caller's next scheduled check-in, which is then absorbed too, leaving a
/// steady poller with no way out.</item>
/// <item>The window stays comfortably below both the client poll interval and
/// the backend's own per-account spacing. At or near the poll interval, which
/// of two consecutive polls survives comes down to arrival jitter; below the
/// backend's spacing, an absorbed repeat is provably one the backend would have
/// refused, so the gate can never cost an account a check-in.</item>
/// </list>
/// </summary>
public static class CheckinGate
{
    /// <summary>
    /// What the gate decided for one request. <see cref="StampToStore"/> is
    /// non-null exactly when the request is forwarded, which is what keeps
    /// "an absorbed repeat leaves the window alone" structural rather than a
    /// rule a caller has to remember.
    /// </summary>
    public readonly record struct Decision(string? StampToStore)
    {
        public bool Forward => StampToStore != null;
    }

    /// <summary>
    /// Decides one check-in against the account's last forwarded one.
    /// </summary>
    public static Decision Decide(string? recorded, long nowMs) =>
        IsWithinWindow(recorded, nowMs) ? new Decision(null) : new Decision(Stamp(nowMs));

    /// <summary>
    /// How long after a forwarded check-in a repeat for the same account is
    /// absorbed. Deliberately well under the client poll interval, so a steady
    /// poller is never a coin flip. Also under the backend's per-account spacing,
    /// so anything absorbed here would have been refused there.
    /// </summary>
    public const long WindowMs = 780_000;

    /// <summary>
    /// Derived from the window so the two cannot drift apart: an entry that
    /// outlives its window would only be read to conclude "expired" anyway.
    /// </summary>
    public const double CacheTtlSeconds = WindowMs / 1000d;

    /// <summary>Cache key for an account's last forwarded check-in.</summary>
    public static string CacheKey(string username) => "checkin:" + username;

    /// <summary>Serializes a timestamp for the cache; inverse of the parse in
    /// <see cref="IsWithinWindow"/>.</summary>
    public static string Stamp(long nowMs) => nowMs.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// True when <paramref name="recorded"/> is a timestamp this window still
    /// covers. Anything unreadable, absent or in the future is false: the gate
    /// fails open, because forwarding a repeat costs one upstream call the
    /// backend discards, while absorbing a real check-in costs the account its
    /// check-in.
    /// </summary>
    public static bool IsWithinWindow(string? recorded, long nowMs)
    {
        if (string.IsNullOrEmpty(recorded))
        {
            return false;
        }

        if (!double.TryParse(recorded, NumberStyles.Float, CultureInfo.InvariantCulture, out var recMs))
        {
            return false;
        }

        var age = nowMs - recMs;
        return age >= 0 && age < WindowMs;
    }
}
