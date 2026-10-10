using System.Collections.Concurrent;
using UrlShortener.Api.Domain;

namespace UrlShortener.Api.Persistence;

public sealed class InMemoryUrlRepository : IUrlRepository
{
    private readonly ConcurrentDictionary<string, ShortLink> _links = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string Owner, string Target), string> _byOwnerAndTarget = new();
    private long _idCounter;

    public long NextId() => Interlocked.Increment(ref _idCounter);

    public bool TryAdd(ShortLink link)
    {
        if (!_links.TryAdd(link.Code, link)) return false;
        _byOwnerAndTarget[(link.OwnerClientId, link.TargetUrl)] = link.Code;
        return true;
    }

    public ShortLink? Get(string code) => _links.TryGetValue(code, out var link) ? link : null;

    public ShortLink? FindByOwnerAndTarget(string ownerClientId, string targetUrl) =>
        _byOwnerAndTarget.TryGetValue((ownerClientId, targetUrl), out var code) ? Get(code) : null;

    public bool Deactivate(string code)
    {
        while (_links.TryGetValue(code, out var current))
        {
            if (!current.IsActive) return true;
            if (_links.TryUpdate(code, current with { IsActive = false }, current)) return true;
        }
        return false;
    }

    public bool TryReplace(string code, ShortLink expected, ShortLink updated) =>
        _links.TryUpdate(code, updated, expected);
}
