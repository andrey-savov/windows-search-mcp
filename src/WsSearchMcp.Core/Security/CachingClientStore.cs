namespace WsSearchMcp.Security;

/// <summary>
/// Caches <see cref="IClientStore.List"/> for a short time so per-request lookups are cheap while
/// out-of-process changes (the admin tool revoking a client) still take effect within seconds,
/// without restarting the server.
/// </summary>
public sealed class CachingClientStore : IClientStore
{
    private readonly IClientStore _inner;
    private readonly TimeSpan _ttl;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private IReadOnlyList<ClientRecord>? _cache;
    private long _loadedAt;

    public CachingClientStore(IClientStore inner, TimeSpan ttl, TimeProvider? time = null)
    {
        _inner = inner;
        _ttl = ttl;
        _time = time ?? TimeProvider.System;
    }

    public IReadOnlyList<ClientRecord> List()
    {
        lock (_gate)
        {
            if (_cache is null || _time.GetElapsedTime(_loadedAt) > _ttl)
            {
                _cache = _inner.List();
                _loadedAt = _time.GetTimestamp();
            }
            return _cache;
        }
    }

    public void Add(ClientRecord record)
    {
        _inner.Add(record);
        Invalidate();
    }

    public bool Remove(string name)
    {
        var removed = _inner.Remove(name);
        Invalidate();
        return removed;
    }

    private void Invalidate()
    {
        lock (_gate) _cache = null;
    }
}
