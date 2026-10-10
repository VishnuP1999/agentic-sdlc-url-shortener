using UrlShortener.Api.Domain;

namespace UrlShortener.Api.Persistence;

/// <summary>Storage abstraction. The in-memory implementation can be replaced by SQL without touching the service.</summary>
public interface IUrlRepository
{
    long NextId();

    /// <summary>Atomically adds the link. Returns false when the code already exists.</summary>
    bool TryAdd(ShortLink link);

    ShortLink? Get(string code);

    /// <summary>Finds the latest link created by this client for this exact target (for idempotent create).</summary>
    ShortLink? FindByOwnerAndTarget(string ownerClientId, string targetUrl);

    /// <summary>Marks the link inactive. Returns false if it does not exist.</summary>
    bool Deactivate(string code);

    /// <summary>Compare-and-swap: replaces the stored link only if it still equals <paramref name="expected"/>.</summary>
    bool TryReplace(string code, ShortLink expected, ShortLink updated);
}
