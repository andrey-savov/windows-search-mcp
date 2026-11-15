namespace WsSearchMcp.Security;

/// <summary>Non-persistent store, used by tests.</summary>
public sealed class InMemoryClientStore : IClientStore
{
    private readonly List<ClientRecord> _items = new();
    private readonly object _gate = new();

    public IReadOnlyList<ClientRecord> List()
    {
        lock (_gate) return _items.ToList();
    }

    public void Add(ClientRecord record)
    {
        lock (_gate) _items.Add(record);
    }

    public bool Remove(string name)
    {
        lock (_gate) return _items.RemoveAll(c => ClientNames.Equal(c.Name, name)) > 0;
    }
}
