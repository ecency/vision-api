namespace EcencyApi.Infrastructure;

/// <summary>
/// Bounded in-process cache of serialized responses, keyed by string, with a
/// per-entry TTL and least-recently-used eviction under a total-bytes budget.
///
/// Exists for the SSR RPC cache: the values are whole JSON payloads (feeds run
/// to hundreds of KB) that are served to many readers unchanged, so they are
/// stored once as UTF-8 bytes and written straight to the response. MemCache
/// is not used for these on purpose: it deep-clones a JsonNode on every set and
/// every get, has no size bound, and would force a JsJson.Stringify per hit.
///
/// Thread-safe via one lock; every operation is O(1) apart from eviction,
/// which removes as many tail entries as the budget requires.
/// </summary>
public sealed class BytesCache
{
    private sealed class Entry
    {
        public required byte[] Bytes;
        public required long ExpiresAtMs;
        public required LinkedListNode<string> Node;
    }

    private readonly Dictionary<string, Entry> _map = new();
    // Head = least recently used, tail = most recently used.
    private readonly LinkedList<string> _lru = new();
    private readonly object _lock = new();
    private long _bytes;

    public BytesCache(long budgetBytes)
    {
        Budget = Math.Max(0, budgetBytes);
    }

    public long Budget { get; }

    public long Bytes { get { lock (_lock) return _bytes; } }

    public int Count { get { lock (_lock) return _map.Count; } }

    private static long NowMs => Environment.TickCount64;

    /// <summary>Fresh entry or nothing; an expired entry is dropped on the way.</summary>
    public bool TryGet(string key, out byte[] bytes)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var entry))
            {
                if (entry.ExpiresAtMs > NowMs)
                {
                    _lru.Remove(entry.Node);
                    _lru.AddLast(entry.Node);
                    bytes = entry.Bytes;
                    return true;
                }
                RemoveLocked(key, entry);
            }
        }
        bytes = Array.Empty<byte>();
        return false;
    }

    /// <summary>
    /// Store a value for <paramref name="ttlMs"/>. A value larger than the whole
    /// budget is not stored (it would evict everything for one reader).
    /// </summary>
    public void Set(string key, byte[] bytes, int ttlMs)
    {
        if (ttlMs <= 0 || bytes.Length > Budget) return;
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                RemoveLocked(key, existing);
            }
            var node = _lru.AddLast(key);
            _map[key] = new Entry { Bytes = bytes, ExpiresAtMs = NowMs + ttlMs, Node = node };
            _bytes += bytes.Length;
            while (_bytes > Budget && _lru.First is { } oldest && oldest != node)
            {
                RemoveLocked(oldest.Value, _map[oldest.Value]);
            }
        }
    }

    private void RemoveLocked(string key, Entry entry)
    {
        _map.Remove(key);
        _lru.Remove(entry.Node);
        _bytes -= entry.Bytes.Length;
    }
}
