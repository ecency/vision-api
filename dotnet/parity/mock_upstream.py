#!/usr/bin/env python3
"""Mock upstream for vapi parity testing.

Serves as PRIVATE_API_ADDR (/api/...), SEARCH_API_ADDR (/search/...) and the
chain RPC/esplora pools. Every request is recorded to a JSONL log keyed by an
X-Parity-Marker propagated... (markers actually ride inside bodies/queries since
the proxies don't forward custom headers) — pairing is done by (path, body hash).
Responses are deterministic echoes so both proxies see identical upstreams.
"""
import json
import os
import re
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlparse, parse_qsl

LOG_PATH = os.environ.get(
    "VAPI_UPSTREAM_LOG", str(Path(__file__).resolve().parent / "upstream-log.jsonl"))
LOCK = threading.Lock()


def record(entry):
    with LOCK:
        with open(LOG_PATH, "a") as f:
            f.write(json.dumps(entry, sort_keys=True) + "\n")


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *args):
        pass

    def _handle(self):
        parsed = urlparse(self.path)
        path = parsed.path
        query = sorted(parse_qsl(parsed.query, keep_blank_values=True))
        length = int(self.headers.get("Content-Length") or 0)
        raw_body = self.rfile.read(length) if length else b""
        try:
            body = json.loads(raw_body) if raw_body else None
        except ValueError:
            body = {"__raw__": raw_body.decode("utf-8", "replace")}

        # Headers that matter for parity (auth/content negotiation); skip
        # transport noise (host, content-length, connection, user-agent...).
        interesting = {}
        for name in ("Authorization", "Content-Type", "Accept"):
            v = self.headers.get(name)
            if v:
                interesting[name.lower()] = v
        for name, v in self.headers.items():
            if name.lower().startswith("x-") and name.lower() != "x-parity-client":
                interesting[name.lower()] = v

        client = self.headers.get("X-Parity-Client") or "unknown"
        record({
            "client": client,
            "method": self.command,
            "path": path,
            "query": query,
            "headers": interesting,
            "body": body,
        })

        status, payload = self.route(path, query, body)
        data = json.dumps(payload).encode() if not isinstance(payload, (bytes, str)) else (
            payload.encode() if isinstance(payload, str) else payload)
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def route(self, path, query, body):
        # Canned shapes for endpoints whose responses the proxy post-processes.
        if re.match(r"^/api/promoted-posts", path):
            return 200, [
                {"author": f"user{i}", "permlink": f"post-{i}",
                 "post_data": {"author": f"user{i}", "permlink": f"post-{i}", "title": f"T{i}"}}
                for i in range(24)
            ]
        if path.endswith("/eth-rpc") or path.endswith("/bnb-rpc"):
            return 200, {"jsonrpc": "2.0", "id": 1, "result": "0x2386f26fc10000"}
        if path.endswith("/sol-rpc"):
            return 200, {"jsonrpc": "2.0", "id": 1,
                         "result": {"context": {"slot": 12345}, "value": 2039280}}
        if "/esplora/address/" in path:
            return 200, {
                "address": path.rsplit("/", 1)[-1],
                "chain_stats": {"funded_txo_sum": 700000, "spent_txo_sum": 100000,
                                "funded_txo_count": 3, "spent_txo_count": 1, "tx_count": 4},
                "mempool_stats": {"funded_txo_sum": 5000, "spent_txo_sum": 0,
                                  "funded_txo_count": 1, "spent_txo_count": 0, "tx_count": 1},
            }
        # error-shape probes
        if path == "/api/parity-404":
            return 404, {"error": "not found"}
        if path == "/api/parity-500":
            return 500, {"error": "boom"}
        if path == "/api/parity-nonjson":
            return 200, "plain text, not json"
        if path == "/api/parity-number":
            return 200, 42
        # generic deterministic echo
        return 200, {"ok": True, "echo": {"method": self.command, "path": path,
                                          "query": query, "body": body}}

    do_GET = _handle
    do_POST = _handle
    do_PUT = _handle
    do_DELETE = _handle


if __name__ == "__main__":
    open(LOG_PATH, "w").close()
    ThreadingHTTPServer.request_queue_size = 256
    server = ThreadingHTTPServer(("127.0.0.1", 15999), Handler)
    print("mock upstream on 127.0.0.1:15999")
    server.serve_forever()
