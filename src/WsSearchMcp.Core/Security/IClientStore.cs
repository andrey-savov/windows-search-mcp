namespace WsSearchMcp.Security;

/// <summary>Storage of client public keys. Revoked clients are removed, so they never appear.</summary>
public interface IClientStore
{
    IReadOnlyList<ClientRecord> List();
    void Add(ClientRecord record);

    /// <summary>Removes the client with the given name (case-insensitive). Returns false if none.</summary>
    bool Remove(string name);
}

public static class ClientStoreExtensions
{
    public static ClientRecord? FindByKid(this IClientStore store, string kid) =>
        store.List().FirstOrDefault(c => string.Equals(c.Kid, kid, StringComparison.OrdinalIgnoreCase));

    public static ClientRecord? FindByName(this IClientStore store, string name) =>
        store.List().FirstOrDefault(c => ClientNames.Equal(c.Name, name));
}
