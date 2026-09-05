using System.Text.Json.Nodes;
using EcencyApi.Handlers;
using EcencyApi.Infrastructure;
using Microsoft.AspNetCore.Http;
using Xunit;
using static EcencyApi.Tests.CurationDeskTestSupport;

namespace EcencyApi.Tests;

/// <summary>
/// Handler-level behaviour of the desk routes: the shared secret on every
/// upstream call, the fail-closed 503, the validation memo, the byte memo with
/// its single flight and last-good fallback, and the Cache-Control rules.
/// </summary>
[Collection("curation-desk")]
public class CurationDeskAuthTests
{
    // ---- the token -----------------------------------------------------------

    [Fact]
    public async Task EveryUpstreamCallCarriesTheDeskToken()
    {
        var upstream = Install();

        foreach (var (name, handler, request, _) in PublicReads())
        {
            await handler(request());
            var call = Assert.Single(upstream.Calls);
            Assert.Equal(Token, call.Header(PrivateApi.DeskTokenHeader));
            Assert.Equal(HttpMethod.Get, call.Method);
            Assert.StartsWith("curation/desk/", call.Endpoint);
            Assert.Null(call.Payload);
            upstream.Calls.Clear();
            CurationDeskMemo.ResetForTests();
        }

        foreach (var (name, handler, body) in SignedWrites())
        {
            await handler(Post("/private-api/curation-desk/" + name, body));
            var call = Assert.Single(upstream.Calls);
            Assert.Equal(Token, call.Header(PrivateApi.DeskTokenHeader));
            Assert.Equal(HttpMethod.Post, call.Method);
            Assert.StartsWith("curation/desk/", call.Endpoint);
            Assert.Equal("alice", call.Payload!["username"]!.GetValue<string>());
            Assert.False(((JsonObject)call.Payload).ContainsKey("code"));
            upstream.Calls.Clear();
        }
    }

    [Fact]
    public void TheTokenComesFromItsOwnEnvironmentVariable()
    {
        // Not set in the test environment: the desk is switched off by default.
        Assert.Equal("", Config.DeskInternalToken);
    }

    // ---- fail closed ---------------------------------------------------------

    [Fact]
    public async Task WithoutTheTokenReadsAnswer503BeforeAnyUpstreamCall()
    {
        var upstream = Install(token: null);

        foreach (var (name, handler, request, _) in PublicReads())
        {
            var ctx = request();
            await handler(ctx);
            await Start(ctx);
            Assert.Equal(503, ctx.Response.StatusCode);
            Assert.Equal("curation desk not configured", Body(ctx));
            Assert.Null(CacheControl(ctx));
        }
        Assert.Empty(upstream.Calls);
    }

    [Fact]
    public async Task WithoutTheTokenWritesAnswer503BeforeValidatingAnything()
    {
        var upstream = Install(token: null);
        var validations = 0;
        PrivateApi.DeskValidateCode = _ => { validations++; return Task.FromResult<string?>("alice"); };

        foreach (var (name, handler, body) in SignedWrites())
        {
            // A dark desk answers the same to a signed body and to an anonymous
            // one, and neither costs a chain lookup: there is no work behind the
            // route to authorize.
            foreach (var payload in new[] { body, "{}" })
            {
                var ctx = Post("/private-api/curation-desk/" + name, payload);
                await handler(ctx);
                await Start(ctx);
                Assert.Equal(503, ctx.Response.StatusCode);
                Assert.Equal("curation desk not configured", Body(ctx));
                Assert.Null(CacheControl(ctx));
            }
        }
        Assert.Equal(0, validations);
        Assert.Empty(upstream.Calls);
    }

    [Fact]
    public async Task InvalidSignedCodesAre401WithTheRealValidator()
    {
        var upstream = Install();
        PrivateApi.DeskValidateCode = PrivateApi.ValidateCode;

        foreach (var (name, handler, _) in SignedWrites())
        {
            var empty = Post("/private-api/curation-desk/" + name, "{}");
            await handler(empty);
            Assert.Equal(401, empty.Response.StatusCode);

            // The parity probe: decodes to {"not":"valid"}, fails on structure
            // before any account lookup.
            var probe = Post("/private-api/curation-desk/" + name, "{\"code\":\"eyJub3QiOiJ2YWxpZCJ9\"}");
            await handler(probe);
            Assert.Equal(401, probe.Response.StatusCode);
            Assert.Equal("Unauthorized", Body(probe));
        }
        Assert.Empty(upstream.Calls);
    }

    [Fact]
    public async Task ARejectedPayloadIs400AndNeverReachesUpstream()
    {
        var upstream = Install();

        var mark = Post("/private-api/curation-desk/mark", "{\"code\":\"as:alice\",\"author\":\"bob\",\"permlink\":\"p\",\"state\":\"deleted\"}");
        await PrivateApi.CurationDeskMark(mark);
        Assert.Equal(400, mark.Response.StatusCode);
        Assert.Equal("invalid state", Body(mark));

        var meta = Post("/private-api/curation-desk/recommend-meta", "{\"code\":\"as:alice\",\"author\":\"bob\",\"permlink\":\"p\",\"trx_id\":\"nope\"}");
        await PrivateApi.CurationDeskRecommendMeta(meta);
        Assert.Equal(400, meta.Response.StatusCode);
        Assert.Equal("invalid trx_id", Body(meta));

        Assert.Empty(upstream.Calls);
    }

    // ---- the validation memo -------------------------------------------------

    [Fact]
    public async Task ASuccessfulValidationIsRememberedWithinTheTtlAndForgottenAfter()
    {
        var upstream = Install();
        var validations = 0;
        PrivateApi.DeskValidateCode = _ => { validations++; return Task.FromResult<string?>("alice"); };
        // Wide enough that two back-to-back in-process calls cannot straddle it
        // on a loaded runner; the delay below is what expires it.
        PrivateApi.DeskAuthMemoSeconds = 2;
        var code = "memo-" + Guid.NewGuid().ToString("N");
        var body = "{\"code\":\"" + code + "\",\"author\":\"bob\",\"permlink\":\"p\"}";

        await PrivateApi.CurationDeskMarkClear(Post("/private-api/curation-desk/mark-clear", body));
        await PrivateApi.CurationDeskMarkClear(Post("/private-api/curation-desk/mark-clear", body));
        Assert.Equal(1, validations);
        Assert.Equal(2, upstream.Calls.Count);
        Assert.All(upstream.Calls, c => Assert.Equal("alice", c.Payload!["username"]!.GetValue<string>()));

        // A different code is a different memo entry.
        await PrivateApi.CurationDeskMarkClear(Post("/private-api/curation-desk/mark-clear", body.Replace(code, code + "x")));
        Assert.Equal(2, validations);

        await Task.Delay(2300);
        await PrivateApi.CurationDeskMarkClear(Post("/private-api/curation-desk/mark-clear", body));
        Assert.Equal(3, validations);
    }

    [Fact]
    public async Task AFailedValidationIsNeverRemembered()
    {
        var upstream = Install();
        var validations = 0;
        string? answer = null;
        PrivateApi.DeskValidateCode = _ => { validations++; return Task.FromResult(answer); };
        var code = "fail-" + Guid.NewGuid().ToString("N");
        var body = "{\"code\":\"" + code + "\",\"author\":\"bob\",\"permlink\":\"p\"}";

        for (var i = 0; i < 3; i++)
        {
            var ctx = Post("/private-api/curation-desk/mark-clear", body);
            await PrivateApi.CurationDeskMarkClear(ctx);
            Assert.Equal(401, ctx.Response.StatusCode);
        }
        Assert.Equal(3, validations);
        Assert.Empty(upstream.Calls);

        // Once the code validates it is remembered from that point, not before.
        answer = "alice";
        await PrivateApi.CurationDeskMarkClear(Post("/private-api/curation-desk/mark-clear", body));
        await PrivateApi.CurationDeskMarkClear(Post("/private-api/curation-desk/mark-clear", body));
        Assert.Equal(4, validations);
        Assert.Equal(2, upstream.Calls.Count);
    }

    [Fact]
    public async Task AMemoizedIdentityIsStillTheValidatedOneNotTheBodysUsername()
    {
        var upstream = Install();
        var body = "{\"code\":\"as:alice\",\"username\":\"victim\",\"author\":\"bob\",\"permlink\":\"p\"}";
        await PrivateApi.CurationDeskMarkClear(Post("/private-api/curation-desk/mark-clear", body));
        await PrivateApi.CurationDeskMarkClear(Post("/private-api/curation-desk/mark-clear", body));
        Assert.All(upstream.Calls, c => Assert.Equal("alice", c.Payload!["username"]!.GetValue<string>()));
    }

    // ---- client address ------------------------------------------------------

    [Fact]
    public async Task OnlyRecommendMetaForwardsTheProxySetClientAddress()
    {
        var upstream = Install();

        foreach (var (name, handler, body) in SignedWrites())
        {
            var ctx = Post("/private-api/curation-desk/" + name, body);
            ctx.Request.Headers["X-Real-IP"] = "198.51.100.7";
            ctx.Request.Headers["X-Forwarded-For"] = "203.0.113.9, 198.51.100.7";
            await handler(ctx);
            var call = Assert.Single(upstream.Calls);
            Assert.Equal(name == "recommend-meta" ? "198.51.100.7" : null, call.Header("X-Real-IP-V"));
            upstream.Calls.Clear();
        }

        // No proxy header: an empty value, never the forwarded-for chain.
        var bare = Post("/private-api/curation-desk/recommend-meta", "{\"code\":\"as:alice\",\"author\":\"bob\",\"permlink\":\"p\"}");
        bare.Request.Headers["X-Forwarded-For"] = "203.0.113.9";
        await PrivateApi.CurationDeskRecommendMeta(bare);
        Assert.Equal("", Assert.Single(upstream.Calls).Header("X-Real-IP-V"));
    }

    [Fact]
    public async Task RecommendMetaAcceptsABodyWithoutATrxId()
    {
        var upstream = Install();
        upstream.Answer = _ => Task.FromResult(JsonResponse(202, "{\"ok\":true}"));
        var ctx = Post("/private-api/curation-desk/recommend-meta", "{\"code\":\"as:alice\",\"author\":\"bob\",\"permlink\":\"p\"}");
        await PrivateApi.CurationDeskRecommendMeta(ctx);
        Assert.Equal(202, ctx.Response.StatusCode);
        Assert.Equal("{\"ok\":true}", Body(ctx));
        Assert.Equal("curation/desk/recommendations/meta", Assert.Single(upstream.Calls).Endpoint);
    }

    // ---- Cache-Control -------------------------------------------------------

    [Fact]
    public async Task ReadsCarryTheirPolicyOnlyOnA200()
    {
        var upstream = Install();

        foreach (var (name, handler, request, policy) in PublicReads())
        {
            var ok = request();
            await handler(ok);
            await Start(ok);
            Assert.Equal(200, ok.Response.StatusCode);
            Assert.Equal(policy, CacheControl(ok));
            // Filled by this request, so the whole window and no age spent yet.
            Assert.Null(Age(ok));
            Assert.StartsWith("application/json", ok.Response.ContentType);

            CurationDeskMemo.ResetForTests();
            upstream.Answer = _ => Task.FromResult(JsonResponse(404, "{\"error\":\"not found\"}"));
            var missing = request();
            await handler(missing);
            await Start(missing);
            Assert.Equal(404, missing.Response.StatusCode);
            Assert.Null(CacheControl(missing));
            Assert.Null(Age(missing));
            Assert.Equal("{\"error\":\"not found\"}", Body(missing));

            upstream.Answer = _ => throw new UpstreamTimeoutException("u", new TimeoutException());
            CurationDeskMemo.ResetForTests();
            var timeout = request();
            await handler(timeout);
            await Start(timeout);
            Assert.Equal(504, timeout.Response.StatusCode);
            Assert.Equal("Upstream Timeout", Body(timeout));
            Assert.Null(CacheControl(timeout));
            Assert.Null(Age(timeout));

            upstream.Answer = _ => Task.FromResult(JsonResponse(200, "{}"));
            CurationDeskMemo.ResetForTests();
        }
    }

    [Fact]
    public async Task WritesAreNeverCacheable()
    {
        Install();
        foreach (var (name, handler, body) in SignedWrites())
        {
            var ctx = Post("/private-api/curation-desk/" + name, body);
            await handler(ctx);
            await Start(ctx);
            Assert.Equal(200, ctx.Response.StatusCode);
            Assert.Equal("no-store", CacheControl(ctx));
        }
    }

    [Fact]
    public async Task AMemoHitAdvertisesOnlyWhatIsLeftOfTheSharedWindow()
    {
        var upstream = Install();
        var clock = UseTestClock();
        const string body = "{\"curators\":[{\"username\":\"alice\"}]}";
        upstream.Answer = _ => Task.FromResult(JsonResponse(200, body));

        var fill = Get("/private-api/curation-desk/roster");
        await PrivateApi.CurationDeskRoster(fill);
        await Start(fill);
        Assert.Equal(CachePolicy.CurationDeskRoster, CacheControl(fill));
        Assert.Null(Age(fill));

        // 590 s into the roster's 600 s window. Sending the whole window again
        // here would let a shared cache hold this body for another 600 s on top
        // of the 590 it has already lived in the memo.
        clock.Advance(TimeSpan.FromSeconds(590));
        var hit = Get("/private-api/curation-desk/roster");
        await PrivateApi.CurationDeskRoster(hit);
        await Start(hit);
        Assert.Equal(200, hit.Response.StatusCode);
        Assert.Equal(body, Body(hit));
        Assert.Equal("public, max-age=0, s-maxage=10", CacheControl(hit));
        Assert.Null(Age(hit));

        // Both readers were answered from one upstream call.
        Assert.Single(upstream.Calls);
    }

    [Fact]
    public async Task AMemoHitAtTheEndOfItsWindowStaysCacheableForOneSecond()
    {
        var upstream = Install();
        var clock = UseTestClock();
        upstream.Answer = _ => Task.FromResult(JsonResponse(200, "{\"behind_seconds\":3}"));
        await PrivateApi.CurationDeskStatus(Get("/private-api/curation-desk/status"));

        // Past the 15 s the status policy promises. The memo entry is about to
        // lapse and refill, so the floor keeps this answer cacheable rather than
        // sending s-maxage=0 to every reader in the last moment of a window.
        clock.Advance(TimeSpan.FromSeconds(20));
        var hit = Get("/private-api/curation-desk/status");
        await PrivateApi.CurationDeskStatus(hit);
        await Start(hit);
        Assert.Equal("public, max-age=0, s-maxage=1", CacheControl(hit));
        Assert.Null(Age(hit));
        Assert.Single(upstream.Calls);
    }

    [Fact]
    public async Task TheLastGoodBodyCarriesTheShortWindowAndNoAgeHeader()
    {
        var upstream = Install();
        var clock = UseTestClock();
        const string body = "{\"curators\":[{\"username\":\"alice\"}]}";
        upstream.Answer = _ => Task.FromResult(JsonResponse(200, body));
        await PrivateApi.CurationDeskRoster(Get("/private-api/curation-desk/roster"));

        // The fresh entry lapses (simulated) and the backend stops answering, so
        // the next read falls back to the last good body, now minutes old.
        CurationDeskMemo.Fresh = new BytesCache(CurationDeskMemo.BudgetBytes);
        clock.Advance(TimeSpan.FromSeconds(120));
        upstream.Answer = _ => throw new UpstreamTimeoutException("u", new TimeoutException());

        var stale = Get("/private-api/curation-desk/roster");
        await PrivateApi.CurationDeskRoster(stale);
        await Start(stale);
        Assert.Equal(200, stale.Response.StatusCode);
        Assert.Equal(body, Body(stale));

        // Never the route's own window: a backend that recovers has to reach
        // readers within a poll or two, not ten minutes later.
        Assert.Equal("public, max-age=0, s-maxage=5", CacheControl(stale));
        Assert.Equal(CachePolicy.Stale(CachePolicy.CurationDeskRoster), CacheControl(stale));
        Assert.Null(Age(stale));
    }

    [Fact]
    public void EachPolicySharedMaxAgeIsTheMemoTtl()
    {
        Assert.Equal(30, CachePolicy.SharedMaxAge(CachePolicy.CurationDeskFeed));
        Assert.Equal(15, CachePolicy.SharedMaxAge(CachePolicy.CurationDeskStatus));
        Assert.Equal(600, CachePolicy.SharedMaxAge(CachePolicy.CurationDeskRoster));
        Assert.Equal(30, CachePolicy.SharedMaxAge(CachePolicy.CurationDeskRecommendations));
        Assert.Equal(15, CachePolicy.SharedMaxAge(CachePolicy.CurationDeskPost));
        Assert.Equal(60, CachePolicy.SharedMaxAge(CachePolicy.CurationDeskRecommender));
    }

    // ---- the byte memo -------------------------------------------------------

    [Fact]
    public async Task ASecondReadWithinTheTtlIsServedFromTheMemoAsBytes()
    {
        var upstream = Install();
        const string body = "{\"items\":[{\"post_id\":1}],\"feed_version\":\"v1\"}";
        upstream.Answer = _ => Task.FromResult(JsonResponse(200, body));

        var first = Get("/private-api/curation-desk/feed", "limit=10&sort=queue");
        await PrivateApi.CurationDeskFeed(first);
        var second = Get("/private-api/curation-desk/feed", "sort=queue&limit=10&x=1");
        await PrivateApi.CurationDeskFeed(second);

        Assert.Single(upstream.Calls);
        Assert.Equal("curation/desk/feed?limit=10&sort=queue", upstream.Calls[0].Endpoint);
        Assert.Equal(body, Body(first));
        Assert.Equal(body, Body(second));

        // Stored as the bytes that were served, keyed by the normalized endpoint.
        Assert.True(CurationDeskMemo.TryGetFresh("curation/desk/feed?limit=10&sort=queue",
            out var stored, out var storedType, out var storedAge));
        Assert.IsType<byte[]>(stored);
        Assert.Equal(body, System.Text.Encoding.UTF8.GetString(stored));
        Assert.Equal("application/json; charset=utf-8", storedType);
        Assert.Equal(0, storedAge);
        Assert.False(CurationDeskMemo.Fresh.TryGet("curation/desk/feed", out _));
    }

    [Fact]
    public async Task DifferentQuestionsAreDifferentMemoEntries()
    {
        var upstream = Install();
        await PrivateApi.CurationDeskFeed(Get("/private-api/curation-desk/feed", "sort=queue"));
        await PrivateApi.CurationDeskFeed(Get("/private-api/curation-desk/feed", "sort=unique"));
        await PrivateApi.CurationDeskFeed(Get("/private-api/curation-desk/feed", "window=full"));
        Assert.Equal(3, upstream.Calls.Count);
        Assert.Equal(3, CurationDeskMemo.Fresh.Count);
    }

    [Fact]
    public async Task ConcurrentReadsOfOneKeyMakeOneUpstreamCall()
    {
        var upstream = Install();
        var release = new TaskCompletionSource<UpstreamResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        upstream.Answer = _ => release.Task;

        var requests = Enumerable.Range(0, 8).Select(_ => Get("/private-api/curation-desk/status")).ToArray();
        var pending = requests.Select(r => PrivateApi.CurationDeskStatus(r)).ToArray();

        // Give every request time to reach the gate before the fill completes.
        await Task.Delay(100);
        Assert.Single(upstream.Calls);
        release.SetResult(JsonResponse(200, "{\"behind_seconds\":3}"));
        await Task.WhenAll(pending);

        Assert.Single(upstream.Calls);
        Assert.All(requests, r =>
        {
            Assert.Equal(200, r.Response.StatusCode);
            Assert.Equal("{\"behind_seconds\":3}", Body(r));
        });
    }

    [Fact]
    public async Task AKeysGateIsKeptWhileAnyReaderStillHoldsIt()
    {
        Install();
        const string key = "curation/desk/feed?limit=10";

        // Two readers take the key's gate. The second stands for a request that
        // has been handed the gate and has not reached its wait yet.
        var first = CurationDeskMemo.GateFor(key);
        var late = CurationDeskMemo.GateFor(key);
        Assert.Same(first, late);

        // The first reader fills and hands the gate back.
        Assert.True(await first.Semaphore.WaitAsync(TimeSpan.Zero));
        first.Semaphore.Release();
        CurationDeskMemo.ReleaseGate(key, first);

        // A third reader arrives after that. It must land on the gate the late
        // reader is about to wait on, or the two of them fill one key at once
        // and the fill that finishes second stores its answer over the first.
        var newcomer = CurationDeskMemo.GateFor(key);
        Assert.Same(late, newcomer);
        Assert.True(await newcomer.Semaphore.WaitAsync(TimeSpan.Zero));
        Assert.False(await late.Semaphore.WaitAsync(TimeSpan.Zero));

        newcomer.Semaphore.Release();
        CurationDeskMemo.ReleaseGate(key, newcomer);
        Assert.Equal(1, CurationDeskMemo.GateCount);

        // With the last reader gone the entry is dropped, so a scan over many
        // distinct keys leaves no semaphore per key behind.
        CurationDeskMemo.ReleaseGate(key, late);
        Assert.Equal(0, CurationDeskMemo.GateCount);
        var afterwards = CurationDeskMemo.GateFor(key);
        Assert.NotSame(late, afterwards);
        CurationDeskMemo.ReleaseGate(key, afterwards);
        Assert.Equal(0, CurationDeskMemo.GateCount);
    }

    [Fact]
    public async Task ALateWaiterNeverFillsAKeyBesideTheReaderFillingIt()
    {
        var upstream = Install();
        const string key = "curation/desk/status";

        // A reader that has been handed the key's gate and has not waited on it
        // yet: the fill below must not be able to drop the gate under it.
        var late = CurationDeskMemo.GateFor(key);

        var firstFill = new TaskCompletionSource<UpstreamResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        upstream.Answer = _ => firstFill.Task;
        var first = Get("/private-api/curation-desk/status");
        var firstRequest = PrivateApi.CurationDeskStatus(first);
        await Task.Delay(100);
        Assert.Single(upstream.Calls);
        firstFill.SetResult(JsonResponse(200, "{\"behind_seconds\":1}"));
        await firstRequest.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("{\"behind_seconds\":1}", Body(first));

        // That entry lapses (simulated), so the next reader fills again.
        CurationDeskMemo.Fresh = new BytesCache(CurationDeskMemo.BudgetBytes);

        var secondFill = new TaskCompletionSource<UpstreamResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        upstream.Answer = _ => secondFill.Task;
        var second = Get("/private-api/curation-desk/status");
        var secondRequest = PrivateApi.CurationDeskStatus(second);
        await Task.Delay(100);
        Assert.Equal(2, upstream.Calls.Count);

        // The late reader reaches its wait now and must queue behind the fill in
        // flight instead of being admitted beside it.
        Assert.False(await late.Semaphore.WaitAsync(TimeSpan.Zero));

        secondFill.SetResult(JsonResponse(200, "{\"behind_seconds\":2}"));
        await secondRequest.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("{\"behind_seconds\":2}", Body(second));

        // Once admitted it finds the answer memoized, so the key was filled once
        // per reader that needed it and never twice at a time.
        Assert.True(await late.Semaphore.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.True(CurationDeskMemo.TryGetFresh(key, out var memoized, out _, out _));
        Assert.Equal("{\"behind_seconds\":2}", System.Text.Encoding.UTF8.GetString(memoized));
        late.Semaphore.Release();
        CurationDeskMemo.ReleaseGate(key, late);
        Assert.Equal(2, upstream.Calls.Count);
        Assert.Equal(0, CurationDeskMemo.GateCount);
    }

    [Fact]
    public async Task ASlowReaderDoesNotHoldTheGateOfItsKey()
    {
        var upstream = Install();
        var fill = new TaskCompletionSource<UpstreamResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        upstream.Answer = _ => fill.Task;

        // A reader whose socket never drains takes the gate and starts the fill.
        var slowBody = new BlockingBody();
        var slow = Get("/private-api/curation-desk/status");
        slow.Response.Body = slowBody;
        var slowRequest = PrivateApi.CurationDeskStatus(slow);
        await Task.Delay(100);
        Assert.Single(upstream.Calls);

        // A second reader of the same key arrives during the fill, so it is
        // queued on the gate rather than served from the memo.
        var fast = Get("/private-api/curation-desk/status");
        var fastRequest = PrivateApi.CurationDeskStatus(fast);
        await Task.Delay(100);
        Assert.False(fastRequest.IsCompleted);

        fill.SetResult(JsonResponse(200, "{\"behind_seconds\":3}"));
        await slowBody.WriteReached;

        // The fill is done and the slow reader is stuck in its write. The bound
        // is far under CurationDeskMemo.FillWait on purpose: waiting for the
        // gate to time out would also "answer", just seconds later.
        await fastRequest.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(200, fast.Response.StatusCode);
        Assert.Equal("{\"behind_seconds\":3}", Body(fast));
        Assert.False(slowRequest.IsCompleted);

        slowBody.Release();
        await slowRequest.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("{\"behind_seconds\":3}", slowBody.Text);
        Assert.Single(upstream.Calls);
    }

    [Fact]
    public async Task A200ThatIsNotAJsonBodyIsNeitherCachedNorMemoized()
    {
        var upstream = Install();
        upstream.Answer = _ => Task.FromResult(TextResponse(200, "<html>gateway login</html>"));

        var page = Get("/private-api/curation-desk/status");
        await PrivateApi.CurationDeskStatus(page);
        await Start(page);
        Assert.Equal(200, page.Response.StatusCode);
        Assert.Equal("<html>gateway login</html>", Body(page));
        Assert.StartsWith("text/html", page.Response.ContentType);
        Assert.Null(CacheControl(page));
        Assert.Equal(0, CurationDeskMemo.Fresh.Count);
        Assert.Equal(0, CurationDeskMemo.LastGood.Count);

        // A JSON body that is not an object or an array is the same case.
        upstream.Answer = _ => Task.FromResult(JsonResponse(200, "\"maintenance\""));
        var scalar = Get("/private-api/curation-desk/status");
        await PrivateApi.CurationDeskStatus(scalar);
        await Start(scalar);
        Assert.Equal(200, scalar.Response.StatusCode);
        Assert.Null(CacheControl(scalar));
        Assert.Equal(0, CurationDeskMemo.Fresh.Count);

        // And an answer the desk did give is preferred over that page, with the
        // route's policy on it because it is a body this service holds.
        CurationDeskMemo.ResetForTests();
        upstream.Answer = _ => Task.FromResult(JsonResponse(200, "{\"behind_seconds\":3}"));
        await PrivateApi.CurationDeskStatus(Get("/private-api/curation-desk/status"));
        CurationDeskMemo.Fresh = new BytesCache(CurationDeskMemo.BudgetBytes);

        upstream.Answer = _ => Task.FromResult(TextResponse(200, "<html>gateway login</html>"));
        var stale = Get("/private-api/curation-desk/status");
        await PrivateApi.CurationDeskStatus(stale);
        await Start(stale);
        Assert.Equal(200, stale.Response.StatusCode);
        Assert.Equal("{\"behind_seconds\":3}", Body(stale));
        Assert.StartsWith("application/json", stale.Response.ContentType);

        // A last-good body, so the short window rather than the route's own.
        Assert.Equal(CachePolicy.Stale(CachePolicy.CurationDeskStatus), CacheControl(stale));
    }

    [Fact]
    public async Task AJsonErrorBodyPassesThroughTheSameFenceAsAServedOne()
    {
        var upstream = Install();
        upstream.Answer = _ => Task.FromResult(JsonResponse(404,
            "{\"error\":\"unknown post\",\"excluded_reason\":\"abuser\",\"detail\":{\"set_by\":\"alice\"}}"));

        var ctx = Get("/private-api/curation-desk/post/good-karma/nope", "",
            new[] { ("author", "good-karma"), ("permlink", "nope") });
        await PrivateApi.CurationDeskPost(ctx);
        await Start(ctx);
        Assert.Equal(404, ctx.Response.StatusCode);
        Assert.Null(CacheControl(ctx));
        var body = Body(ctx);
        Assert.Contains("\"error\":\"unknown post\"", body);
        Assert.DoesNotContain("excluded_reason", body);
        Assert.DoesNotContain("set_by", body);
    }

    [Fact]
    public async Task AnUpstreamErrorAnswersWithTheLastGoodBody()
    {
        var upstream = Install();
        upstream.Answer = _ => Task.FromResult(JsonResponse(200, "{\"curators\":[{\"username\":\"alice\"}]}"));
        await PrivateApi.CurationDeskRoster(Get("/private-api/curation-desk/roster"));

        // The fresh entry lapses (simulated), the last-good one is still there.
        CurationDeskMemo.Fresh = new BytesCache(CurationDeskMemo.BudgetBytes);

        upstream.Answer = _ => throw new UpstreamTimeoutException("u", new TimeoutException());
        var stale = Get("/private-api/curation-desk/roster");
        await PrivateApi.CurationDeskRoster(stale);
        Assert.Equal(200, stale.Response.StatusCode);
        Assert.Equal("{\"curators\":[{\"username\":\"alice\"}]}", Body(stale));

        upstream.Answer = _ => Task.FromResult(TextResponse(502, "<html>bad gateway</html>"));
        var down = Get("/private-api/curation-desk/roster");
        await PrivateApi.CurationDeskRoster(down);
        Assert.Equal(200, down.Response.StatusCode);
        Assert.Equal("{\"curators\":[{\"username\":\"alice\"}]}", Body(down));

        // With nothing good to fall back on, the backend's answer passes through.
        CurationDeskMemo.ResetForTests();
        var bare = Get("/private-api/curation-desk/roster");
        await PrivateApi.CurationDeskRoster(bare);
        Assert.Equal(502, bare.Response.StatusCode);
        Assert.Equal("<html>bad gateway</html>", Body(bare));

        // Errors are never memoized: the next read tries upstream again.
        upstream.Answer = _ => Task.FromResult(JsonResponse(200, "{\"curators\":[]}"));
        var recovered = Get("/private-api/curation-desk/roster");
        await PrivateApi.CurationDeskRoster(recovered);
        Assert.Equal(200, recovered.Response.StatusCode);
        Assert.Equal("{\"curators\":[]}", Body(recovered));
    }

    [Fact]
    public async Task ANon200IsPipedThroughAndNotMemoized()
    {
        var upstream = Install();
        upstream.Answer = _ => Task.FromResult(JsonResponse(404, "{\"error\":\"unknown post\"}"));
        var ctx = Get("/private-api/curation-desk/post/good-karma/nope", "", new[] { ("author", "good-karma"), ("permlink", "nope") });
        await PrivateApi.CurationDeskPost(ctx);
        Assert.Equal(404, ctx.Response.StatusCode);
        Assert.Equal("{\"error\":\"unknown post\"}", Body(ctx));
        Assert.Equal(0, CurationDeskMemo.Fresh.Count);
        Assert.Equal(0, CurationDeskMemo.LastGood.Count);
        Assert.Equal("curation/desk/post/good-karma/nope", Assert.Single(upstream.Calls).Endpoint);
    }

    [Fact]
    public async Task AnInvalidPostPathIs400BeforeAnyUpstreamCall()
    {
        var upstream = Install();
        foreach (var (author, permlink) in MalformedPostPaths)
        {
            var ctx = Get("/private-api/curation-desk/post/x/y", "", new[] { ("author", author), ("permlink", permlink) });
            await PrivateApi.CurationDeskPost(ctx);
            await Start(ctx);
            Assert.Equal(400, ctx.Response.StatusCode);
            Assert.Equal("Invalid author or permlink", Body(ctx));
            Assert.Null(CacheControl(ctx));
        }
        Assert.Empty(upstream.Calls);
    }

    [Fact]
    public async Task WithoutTheTokenAMalformedPostPathIs503LikeEveryOtherRoute()
    {
        var upstream = Install(token: null);

        // While the desk is dark every route answers the same, so this one does
        // not single itself out by reporting on the path it was given.
        foreach (var (author, permlink) in MalformedPostPaths)
        {
            var ctx = Get("/private-api/curation-desk/post/x/y", "", new[] { ("author", author), ("permlink", permlink) });
            await PrivateApi.CurationDeskPost(ctx);
            await Start(ctx);
            Assert.Equal(503, ctx.Response.StatusCode);
            Assert.Equal("curation desk not configured", Body(ctx));
            Assert.Null(CacheControl(ctx));
        }
        Assert.Empty(upstream.Calls);
    }

    private static readonly (string Author, string Permlink)[] MalformedPostPaths =
    {
        ("..", "p"), ("good-karma", "a/b"), ("good-karma", "p?x=1"), ("x", "p"),
    };

    // ---- the recommender scorecard -------------------------------------------

    private static DefaultHttpContext RecommenderRequest(string username, string query = "") =>
        Get("/private-api/curation-desk/recommender/" + username, query, new[] { ("username", username) });

    private const string Scorecard =
        "{\"username\":\"good-karma\",\"window_days\":90,\"recommended\":12,\"curated\":9,"
        + "\"dismissed\":1,\"withdrawn\":0,\"precision\":0.75,\"trusted\":true,\"computed_at\":\"t\"}";

    [Fact]
    public async Task AScorecardIsPipedUnderTheTokenAndCachedForAMinute()
    {
        var upstream = Install();
        var clock = UseTestClock();
        upstream.Answer = _ => Task.FromResult(JsonResponse(200, Scorecard));

        var fill = RecommenderRequest("good-karma");
        await PrivateApi.CurationDeskRecommender(fill);
        await Start(fill);

        var call = Assert.Single(upstream.Calls);
        Assert.Equal("curation/desk/recommenders/good-karma", call.Endpoint);
        Assert.Equal(HttpMethod.Get, call.Method);
        Assert.Equal(Token, call.Header(PrivateApi.DeskTokenHeader));
        Assert.Equal(200, fill.Response.StatusCode);
        Assert.Equal(Scorecard, Body(fill));
        Assert.StartsWith("application/json", fill.Response.ContentType);
        Assert.Equal("public, max-age=0, s-maxage=60", CacheControl(fill));
        Assert.Equal(CachePolicy.CurationDeskRecommender, CacheControl(fill));
        Assert.Null(Age(fill));

        // 45 s into the minute the policy promises: the hit is offered only the
        // rest of that window; its age is not advertised a second time.
        clock.Advance(TimeSpan.FromSeconds(45));
        var hit = RecommenderRequest("good-karma");
        await PrivateApi.CurationDeskRecommender(hit);
        await Start(hit);
        Assert.Equal(200, hit.Response.StatusCode);
        Assert.Equal(Scorecard, Body(hit));
        Assert.Equal("public, max-age=0, s-maxage=15", CacheControl(hit));
        Assert.Null(Age(hit));
        Assert.Single(upstream.Calls);
    }

    [Fact]
    public async Task TheScorecardIsKeyedByTheNameAloneAndTakesNoQueryParameters()
    {
        var upstream = Install();
        upstream.Answer = _ => Task.FromResult(JsonResponse(200, Scorecard));

        await PrivateApi.CurationDeskRecommender(RecommenderRequest("good-karma"));
        await PrivateApi.CurationDeskRecommender(RecommenderRequest("good-karma", "window_days=7&limit=50"));

        // One question, one upstream call and one memo entry: a query string
        // cannot fork the key or reach the backend.
        var call = Assert.Single(upstream.Calls);
        Assert.Equal("curation/desk/recommenders/good-karma", call.Endpoint);
        Assert.True(CurationDeskMemo.TryGetFresh("curation/desk/recommenders/good-karma", out _, out _, out _));
        Assert.Equal(1, CurationDeskMemo.Fresh.Count);

        // A different name is a different entry.
        await PrivateApi.CurationDeskRecommender(RecommenderRequest("user.name"));
        Assert.Equal(2, upstream.Calls.Count);
        Assert.Equal("curation/desk/recommenders/user.name", upstream.Calls[1].Endpoint);
    }

    [Fact]
    public async Task AnInvalidRecommenderNameIs400BeforeAnyUpstreamCall()
    {
        var upstream = Install();
        foreach (var username in MalformedRecommenderNames)
        {
            var ctx = RecommenderRequest(username);
            await PrivateApi.CurationDeskRecommender(ctx);
            await Start(ctx);
            Assert.Equal(400, ctx.Response.StatusCode);
            Assert.Equal("Invalid username", Body(ctx));
            Assert.Null(CacheControl(ctx));
        }
        Assert.Empty(upstream.Calls);
    }

    [Fact]
    public async Task WithoutTheTokenAMalformedRecommenderNameIs503LikeEveryOtherRoute()
    {
        var upstream = Install(token: null);

        // The 503 is decided before the route value is read, so a dark desk does
        // not single this route out by reporting on the name it was given.
        foreach (var username in MalformedRecommenderNames)
        {
            var ctx = RecommenderRequest(username);
            await PrivateApi.CurationDeskRecommender(ctx);
            await Start(ctx);
            Assert.Equal(503, ctx.Response.StatusCode);
            Assert.Equal("curation desk not configured", Body(ctx));
            Assert.Null(CacheControl(ctx));
        }

        // Including a request that carries no route value at all.
        var bare = Get("/private-api/curation-desk/recommender/good-karma");
        await PrivateApi.CurationDeskRecommender(bare);
        await Start(bare);
        Assert.Equal(503, bare.Response.StatusCode);
        Assert.Equal("curation desk not configured", Body(bare));
        Assert.Empty(upstream.Calls);
    }

    [Fact]
    public async Task AScorecardGoesThroughTheSameFenceAsEveryOtherPublicBody()
    {
        var upstream = Install();
        upstream.Answer = _ => Task.FromResult(JsonResponse(200,
            "{\"username\":\"good-karma\",\"precision\":0.75,\"trusted\":true,\"ip_hash\":\"ab\","
            + "\"key_id\":3,\"note\":\"secret\",\"computed_at\":\"t\"}"));

        var ctx = RecommenderRequest("good-karma");
        await PrivateApi.CurationDeskRecommender(ctx);
        await Start(ctx);
        Assert.Equal(200, ctx.Response.StatusCode);
        var body = Body(ctx);
        Assert.Contains("\"precision\":0.75", body);
        Assert.Contains("\"computed_at\":\"t\"", body);
        Assert.DoesNotContain("ip_hash", body);
        Assert.DoesNotContain("key_id", body);
        Assert.DoesNotContain("note", body);

        // The memo holds the stripped bytes, so a hit cannot leak them either.
        var hit = RecommenderRequest("good-karma");
        await PrivateApi.CurationDeskRecommender(hit);
        Assert.Equal(body, Body(hit));
        Assert.Single(upstream.Calls);
    }

    private static readonly string[] MalformedRecommenderNames =
    {
        "..", "a%2Fb", "good-karma?x=1", "Good-Karma", new string('a', 17),
    };
}
