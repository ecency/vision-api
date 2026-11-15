# Differential parity harness

Proves the C# port returns responses identical to the Node reference across the
whole route surface.

- `mock_upstream.py` — a stub for every upstream (private API, search, chain
  RPC/esplora) that records each proxied request and returns deterministic
  bodies, so both services observe identical upstreams.
- `driver.py` — generates the request catalog from the C# route table
  (`EcencyApi/Handlers/Routes.cs`; `VAPI_ROUTES_CS` overrides it, `VAPI_INDEX_TSX`
  points it at a legacy Express `index.tsx`), fires it at a target, and diffs two
  runs. `python3 driver.py catalog` shows the current cases.

```bash
python3 driver.py catalog                    # inspect the generated cases
python3 driver.py run <name> <base_url>      # capture responses -> run-<name>.json
python3 driver.py diff <ref> <cand> [loose]  # compare; `loose` marks nondeterministic cases
```

`diff node csharp node2` compares C# against Node, using a second Node run
(`node2`) to auto-detect inherently nondeterministic cases (timestamped tokens,
random shuffles, live portfolio data) and compare those on status + content-type
only. Known intentional divergences are listed in `KNOWN_DIVERGENCES` in
`driver.py`. A clean run reports `REAL mismatches 0`.
