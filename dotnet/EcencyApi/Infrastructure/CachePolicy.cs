using System.Globalization;

namespace EcencyApi.Infrastructure;

/// <summary>
/// Cache-Control policies for the handful of private-API reads that are the same
/// for every visitor.
///
/// Most endpoints here are per-user or per-request and deliberately carry no
/// Cache-Control at all. The edge treats a missing Cache-Control as "do not
/// store", so a response only becomes reusable once a handler opts in — which is
/// why these policies are explicit per endpoint rather than a global default.
///
/// Only opt an endpoint in when its body depends on nothing but the URL: no
/// authenticated user, no request body, no per-caller variation. `public` here
/// means shared caches may store it, so getting that wrong would let one
/// visitor's response reach another.
/// </summary>
public static class CachePolicy
{
    /// <summary>
    /// Pro badge roster: one global list, no auth. It only moves when someone
    /// starts or stops being a Pro member, so a short fresh window plus a long
    /// revalidate window keeps badges correct without refetching per page view.
    /// </summary>
    public const string ProMembers = "public, max-age=600, stale-while-revalidate=3600";

    /// <summary>
    /// Announcements are a compile-time constant in this repo (Announcements.cs),
    /// so the body can only change on deploy.
    /// </summary>
    public const string Announcements = "public, max-age=1800, stale-while-revalidate=86400";

    /// <summary>
    /// Tips for a single post, keyed entirely by author/permlink and readable
    /// without auth. Tips arrive at any time, so the fresh window stays short;
    /// the revalidate window is what absorbs repeat reads within one visit.
    /// </summary>
    public const string PostTips = "public, max-age=60, stale-while-revalidate=600";

    /// <summary>
    /// Curation desk public reads. `max-age=0` makes browsers revalidate on every
    /// poll while `s-maxage` lets shared caches absorb the polling; the in-process
    /// memo of each route uses the same s-maxage as its TTL (see
    /// <see cref="SharedMaxAge"/>), so the two layers never disagree on freshness.
    /// A body served from that memo goes out through <see cref="Aged"/>, which
    /// hands the shared cache only what is left of the window rather than a fresh
    /// one, and a last-good body through <see cref="Stale"/>.
    /// The feed and the recommendations list move with every new post; status is
    /// the poll target and stays short; the roster changes when a curator is added
    /// or promoted, so it can stay put for minutes; a single post's recommenders
    /// are optimistic on the client and confirmed from here.
    /// </summary>
    public const string CurationDeskFeed = "public, max-age=0, s-maxage=30";
    public const string CurationDeskStatus = "public, max-age=0, s-maxage=15";
    public const string CurationDeskRoster = "public, max-age=0, s-maxage=600";
    public const string CurationDeskRecommendations = "public, max-age=0, s-maxage=30";
    public const string CurationDeskPost = "public, max-age=0, s-maxage=15";

    /// <summary>
    /// The `s-maxage` of a policy in seconds, or its `max-age` when it has no
    /// shared-cache directive. Handlers that memoize a response derive their TTL
    /// from this so the memo can never outlive what the policy promises.
    /// </summary>
    public static int SharedMaxAge(string policy)
    {
        var tokens = policy.Split(',', StringSplitOptions.TrimEntries);
        foreach (var name in new[] { "s-maxage=", "max-age=" })
        {
            foreach (var token in tokens)
            {
                if (token.StartsWith(name, StringComparison.Ordinal)
                    && int.TryParse(token.AsSpan(name.Length), out var seconds) && seconds >= 0)
                {
                    return seconds;
                }
            }
        }
        throw new ArgumentException("policy carries no max-age", nameof(policy));
    }

    /// <summary>
    /// Seconds a shared cache may keep a body that was served after the upstream
    /// failed. It is still an answer the backend gave, so it stays publicly
    /// cacheable rather than being refetched by every reader at once, but a
    /// recovered backend has to reach readers within a poll or two rather than at
    /// the end of a full window.
    /// </summary>
    public const int StaleSharedMaxAge = 5;

    /// <summary>
    /// The same policy with its shared window reduced to what is left of it after
    /// <paramref name="ageSeconds"/>.
    ///
    /// A memoized body is served for the rest of its TTL, not for a fresh one:
    /// without this a roster read from the memo a second before it lapses would
    /// license a shared cache to hold that body for another whole window, so the
    /// two layers together could serve one answer for nearly twice its TTL. The
    /// floor of one second keeps the response cacheable at all, since the memo is
    /// about to refill anyway.
    /// </summary>
    public static string Aged(string policy, int ageSeconds) =>
        ageSeconds <= 0 ? policy : WithSharedMaxAge(policy, Math.Max(1, SharedMaxAge(policy) - ageSeconds));

    /// <summary>
    /// The same policy cut down to <see cref="StaleSharedMaxAge"/>, for a body
    /// served because the upstream call failed. Never longer than the policy
    /// itself, so a route with an even shorter window keeps its own.
    /// </summary>
    public static string Stale(string policy) =>
        WithSharedMaxAge(policy, Math.Min(StaleSharedMaxAge, SharedMaxAge(policy)));

    /// <summary>
    /// Rewrite the `s-maxage` of a policy, leaving every other directive alone.
    /// A policy that carries none gains one: its shared window was its `max-age`
    /// (see <see cref="SharedMaxAge"/>), and this caps that without touching what
    /// browsers were told.
    /// </summary>
    private static string WithSharedMaxAge(string policy, int seconds)
    {
        var value = "s-maxage=" + seconds.ToString(CultureInfo.InvariantCulture);
        var tokens = policy.Split(',', StringSplitOptions.TrimEntries);
        var replaced = false;
        for (var i = 0; i < tokens.Length; i++)
        {
            if (tokens[i].StartsWith("s-maxage=", StringComparison.Ordinal))
            {
                tokens[i] = value;
                replaced = true;
            }
        }
        return string.Join(", ", replaced ? tokens : tokens.Append(value));
    }

    /// <summary>
    /// A policy applies only to a successful response. Handlers attach it before
    /// the upstream call resolves, and <see cref="Upstream.Pipe"/> can still turn
    /// a transport failure into 504/500 afterwards — caching either would pin an
    /// error in front of a healthy endpoint for the whole max-age.
    /// </summary>
    public static string? ForStatus(int status, string policy) => status == 200 ? policy : null;
}
