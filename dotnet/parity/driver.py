#!/usr/bin/env python3
"""Differential parity driver: fire an identical request catalog at the Node
reference and the C# candidate, then diff responses + recorded upstream traffic.

Usage:
  driver.py catalog                 -> print the generated catalog
  driver.py run <name> <base_url>   -> run catalog against a target, save results
  driver.py diff <run_a> <run_b>    -> compare two result files (auto-loose on
                                       cases where two same-target runs differ)
"""
import os
import json
import re
import sys
import time
import urllib.parse
import urllib.request
import urllib.error
from pathlib import Path

HERE = Path(__file__).parent
# The Express route table drives the catalog. Defaults to the TS source two
# levels up (repo/src/server/index.tsx); override with VAPI_INDEX_TSX.
INDEX_TSX = Path(os.environ.get(
    "VAPI_INDEX_TSX", HERE.parent.parent / "src" / "server" / "index.tsx"))
MOCK = os.environ.get("VAPI_MOCK_URL", "http://127.0.0.1:15999")

# ---------------------------------------------------------------- catalog ---

PARAM_VALUES = {
    "username": "good-karma",
    "chain": "eth",
    "address": "0x1111111111111111111111111111111111111111",
    "duration": "week",
    "fiat": "usd",
    "token": "hive",
}

# Route-specific populated bodies (minimal variant is always {}).
BODIES = {
    "/search-api/search": {"q": "ecency", "sort": "newest", "hide_low": "0",
                           "votes": 0, "include_nsfw": 0, "since": ""},
    "/search-api/search-follower": {"q": "e", "following": "good-karma"},
    "/search-api/search-following": {"q": "e", "follower": "good-karma"},
    "/search-api/search-account": {"q": "ecency", "limit": 5, "random": 0},
    "/search-api/search-tag": {"q": "hive", "limit": 0, "random": 1},
    "/search-api/search-path": {"q": "@good-karma/about"},
    "/search-api/similar": {"author": "good-karma", "permlink": "about",
                            "tags": [], "since": None},
    "/auth-api/hs-token-refresh": {"code": "bm90LWEtdG9rZW4"},
    "/auth-api/hs-token-create": {"username": "alice",
                                  "password": "P5JRFhxvW9zZ1QqWzSp6ZoPhq6yGKPrM",
                                  "app": "ecency.app"},
    "/private-api/comment-history": {"author": "good-karma", "permlink": "about",
                                     "onlyMeta": ""},
    "/private-api/points": {"username": "good-karma"},
    "/private-api/point-list": {"username": "good-karma", "type": 0},
    "/private-api/account-create": {"username": "newuser123", "email": "x@example.com",
                                    "referral": "", "captchaToken": "tok"},
    "/private-api/account-create-friend": {"username": "newuser123",
                                           "email": "x@example.com", "friend": True},
    "/private-api/subscribe": {"email": "x@example.com"},
    "/private-api/notifications": {"code": "invalid", "filter": "", "since": 0,
                                   "limit": 50, "user": "good-karma"},
    "/private-api/report": {"type": "content", "data": {"a": 1}},
    "/private-api/request-delete": {"code": "invalid"},
    "/private-api/post-reblogs": {"author": "good-karma", "permlink": "about"},
    "/private-api/post-reblog-count": {"author": "good-karma", "permlink": "about"},
    "/private-api/post-tips": {"author": "good-karma", "permlink": "about"},
    "/private-api/wallets-add": {"code": "invalid", "username": "x",
                                 "token": "BTC", "address": "bc1qxyz", "meta": {}},
    "/private-api/wallets-chkuser": {"username": "good-karma"},
    "/private-api/engine-api": {"jsonrpc": "2.0", "method": "find",
                                "params": {"contract": "tokens", "table": "balances",
                                           "query": {"account": "good-karma"}}, "id": 1},
    "/private-api/stripe-create-intent": {"code": "invalid", "amount": 4.99,
                                          "currency": "usd", "gift_recipient": " Bob ",
                                          "gift_message": "hi"},
    "/private-api/stripe-order-status": {"code": "invalid", "order_id": "ord_1"},
    "/private-api/streak-freeze/buy": {"code": "invalid", "idempotency_key": "  "},
    "/wallet-api/portfolio": {"username": "good-karma", "currency": "usd"},
    "/wallet-api/portfolio-v2": {"username": "good-karma", "currency": "usd"},
    "/private-api/rpc/eth": {"jsonrpc": "2.0", "method": "eth_getBalance",
                             "params": ["0x1111111111111111111111111111111111111111", "latest"],
                             "id": 1},
}

QUERIES = {
    "/private-api/leaderboard/week": "?dummy=1",
    "/private-api/waves/feed": "?host=ecency.waves&limit=20&page=2",
    "/private-api/waves/shorts": "?host=leothreads&limit=0",
    "/private-api/waves/tags": "?host=ecency.waves",
    "/private-api/waves/account": "?host=ecency.waves&username=good-karma",
    "/private-api/waves/following": "?host=ecency.waves&username=good-karma",
    "/private-api/waves/trending/tags": "?host=ecency.waves&hours=24",
    "/private-api/waves/trending/authors": "?host=ecency.waves",
    "/private-api/promoted-entries": "?limit=10&short_content=1",
    "/private-api/engine-chart-api": "?symbol=LEO&interval=daily",
    "/private-api/engine-account-history": "?account=good-karma&symbol=LEO&limit=20",
    "/private-api/market-data/usd/hive": "",
    "/private-api/pub-notifications/good-karma": "",
}

AUTH_CODE_PROBE = {"code": "eyJub3QiOiJ2YWxpZCJ9"}  # decodes to {"not":"valid"} -> structure fail


def load_routes():
    """Parse .get/.post route lines out of index.tsx."""
    src = INDEX_TSX.read_text()
    routes = []
    for m in re.finditer(r'\.(get|post)\("(\^?[^"]+?)\$?",', src):
        method, path = m.group(1).upper(), m.group(2).lstrip("^")
        if path in ("*",):
            continue
        # substitute :params (incl. regex-constrained ones)
        concrete = re.sub(r":(\w+)(\([^)]*\))?", lambda p: PARAM_VALUES.get(p.group(1), "x"), path)
        routes.append({"method": method, "path": concrete, "template": path})
    return routes


def build_catalog():
    cases = []
    seen = set()
    for r in load_routes():
        key = (r["method"], r["path"])
        if key in seen:
            continue
        seen.add(key)
        base = r["path"] + QUERIES.get(r["path"], "")
        if r["method"] == "POST":
            cases.append({"id": f"{r['path']}::min", "method": "POST", "url": base, "body": {}})
            body = BODIES.get(r["path"], {"code": "invalid-code", "probe": True})
            cases.append({"id": f"{r['path']}::pop", "method": "POST", "url": base, "body": body})
            if "code" in json.dumps(body):
                cases.append({"id": f"{r['path']}::badcode", "method": "POST", "url": base,
                              "body": {**body, **AUTH_CODE_PROBE}})
        else:
            cases.append({"id": f"{r['path']}::get", "method": "GET", "url": base, "body": None})
    # error-shape probes through the generic proxy path (upstream status/body forwarding)
    cases.append({"id": "fallback::root", "method": "GET", "url": "/", "body": None})
    cases.append({"id": "fallback::deep", "method": "GET", "url": "/no/such/route", "body": None})
    cases.append({"id": "health::get", "method": "GET", "url": "/healthcheck.json", "body": None})
    cases.append({"id": "badjson::post", "method": "POST", "url": "/private-api/points",
                  "body": None, "raw_body": '{"broken": '})
    return cases


# -------------------------------------------------------------------- run ---

def mark(case_id, client):
    try:
        urllib.request.urlopen(f"{MOCK}/__marker__?id={urllib.parse.quote(case_id)}&client={client}", timeout=5)
    except Exception:
        pass


def run(name, base):
    results = {}
    cases = build_catalog()
    for c in cases:
        mark(c["id"], name)
        url = base + c["url"]
        data = None
        headers = {}
        if c["method"] == "POST":
            raw = c.get("raw_body")
            data = raw.encode() if raw is not None else json.dumps(c["body"]).encode()
            headers["Content-Type"] = "application/json"
        t0 = time.time()
        status, ctype, body = -1, "", ""
        # Retry transport-level failures (connection refused / reset under rapid
        # sequential load) — these are harness noise, not a parity signal. HTTP
        # error statuses are real responses and are NOT retried.
        for attempt in range(4):
            req = urllib.request.Request(url, data=data, method=c["method"], headers=headers)
            try:
                with urllib.request.urlopen(req, timeout=30) as resp:
                    body = resp.read().decode("utf-8", "replace")
                    status, ctype = resp.status, resp.headers.get("Content-Type", "")
                break
            except urllib.error.HTTPError as e:
                body = e.read().decode("utf-8", "replace")
                status, ctype = e.code, e.headers.get("Content-Type", "")
                break
            except Exception as e:
                body, status, ctype = f"__transport_error__: {e}", -1, ""
                time.sleep(0.25 * (attempt + 1))
        results[c["id"]] = {
            "status": status,
            "contentType": ctype.split(";")[0].strip(),
            "body": body,
            "ms": round((time.time() - t0) * 1000),
        }
    out = HERE / f"run-{name}.json"
    out.write_text(json.dumps(results, indent=1, sort_keys=True))
    print(f"{name}: {len(results)} cases -> {out}")


# ------------------------------------------------------------------- diff ---

def norm_body(text):
    try:
        return json.loads(text)
    except ValueError:
        return text


# Cases where the C# port intentionally differs from Node (Node bugs the port fixes).
KNOWN_DIVERGENCES = {
    "/auth-api/hs-token-refresh::min":
        "Node hangs the socket (unhandled promise rejection: code.replace on undefined) "
        "when `code` is missing; the C# port returns 401 Unauthorized instead.",
    "/private-api/request-delete::min":
        "Old route table sent request-delete to the report handler (400 for the mobile "
        "payload); it now returns the account-deletion acknowledgment stub (200).",
    "/private-api/request-delete::pop":
        "Same request-delete rerouting as ::min.",
    "/private-api/request-delete::badcode":
        "Same request-delete rerouting as ::min.",
}


def diff(a_name, b_name, loose_name=None):
    a = json.loads((HERE / f"run-{a_name}.json").read_text())
    b = json.loads((HERE / f"run-{b_name}.json").read_text())
    loose = set()
    if loose_name:
        l = json.loads((HERE / f"run-{loose_name}.json").read_text())
        for k in a:
            if k in l and (a[k]["status"], norm_body(a[k]["body"])) != (l[k]["status"], norm_body(l[k]["body"])):
                loose.add(k)
    mismatches = []
    known = []
    for k in sorted(set(a) | set(b)):
        if k in KNOWN_DIVERGENCES:
            known.append({"case": k, "reason": KNOWN_DIVERGENCES[k]})
            continue
        ra, rb = a.get(k), b.get(k)
        if ra is None or rb is None:
            mismatches.append({"case": k, "kind": "missing", "a": bool(ra), "b": bool(rb)})
            continue
        if k in loose:
            if ra["status"] != rb["status"] or ra["contentType"] != rb["contentType"]:
                mismatches.append({"case": k, "kind": "loose-status",
                                   "a": [ra["status"], ra["contentType"]],
                                   "b": [rb["status"], rb["contentType"]]})
            continue
        if ra["status"] != rb["status"]:
            mismatches.append({"case": k, "kind": "status", "a": ra["status"], "b": rb["status"],
                               "abody": ra["body"][:200], "bbody": rb["body"][:200]})
            continue
        if ra["contentType"] != rb["contentType"]:
            mismatches.append({"case": k, "kind": "content-type",
                               "a": ra["contentType"], "b": rb["contentType"]})
            continue
        if norm_body(ra["body"]) != norm_body(rb["body"]):
            mismatches.append({"case": k, "kind": "body",
                               "a": ra["body"][:300], "b": rb["body"][:300]})
    print(json.dumps({"cases": len(set(a) | set(b)), "loose": sorted(loose),
                      "knownDivergences": known, "mismatches": mismatches}, indent=1))
    return 1 if mismatches else 0


if __name__ == "__main__":
    cmd = sys.argv[1]
    if cmd == "catalog":
        print(json.dumps(build_catalog(), indent=1))
    elif cmd == "run":
        run(sys.argv[2], sys.argv[3])
    elif cmd == "diff":
        sys.exit(diff(sys.argv[2], sys.argv[3], sys.argv[4] if len(sys.argv) > 4 else None))
