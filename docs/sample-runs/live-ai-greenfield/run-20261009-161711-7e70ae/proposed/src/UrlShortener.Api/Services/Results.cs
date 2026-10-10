using UrlShortener.Api.Domain;

namespace UrlShortener.Api.Services;

public enum CreateStatus { Created, Existing, Invalid, AliasTaken }

public sealed record CreateLinkRequest(string? Url, string? Alias = null, int? ExpiresInMinutes = null, DateTimeOffset? ExpiresAt = null);

public sealed record CreateResult(CreateStatus Status, ShortLink? Link = null, IReadOnlyList<string>? Errors = null, string? OwnerToken = null);

public enum ResolveStatus { Redirect, NotFound, Gone }

public sealed record ResolveResult(ResolveStatus Status, string? TargetUrl = null);

public enum DeactivateStatus { Done, NotFound, Forbidden, Unauthorized }

public sealed record LinkStats(
    string Code,
    string TargetUrl,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    bool IsActive,
    long TotalClicks,
    DateTimeOffset? LastAccessedAt,
    IReadOnlyDictionary<string, long> DailyClicks)
{
    public DateTimeOffset? LastClickAt => LastAccessedAt;
}
