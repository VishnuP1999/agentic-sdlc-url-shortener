using UrlShortener.Api.Domain;

namespace UrlShortener.Api.Services;

public enum CreateStatus { Created, Existing, Invalid, AliasTaken }

public sealed record CreateLinkRequest(string? Url, string? Alias = null, int? ExpiresInMinutes = null);

public sealed record CreateResult(CreateStatus Status, ShortLink? Link = null, IReadOnlyList<string>? Errors = null);

public enum ResolveStatus { Redirect, NotFound, Gone }

public sealed record ResolveResult(ResolveStatus Status, string? TargetUrl = null);

public enum DeactivateStatus { Done, NotFound, Forbidden }

public enum ExtendStatus { Done, NotFound, Forbidden, Invalid, Deactivated, NoExpiry, TooLong }

public sealed record ExtendResult(ExtendStatus Status, ShortLink? Link = null, long? RemainingSeconds = null);

public sealed record LinkStats(
    string Code,
    string TargetUrl,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    bool IsActive,
    long TotalClicks,
    DateTimeOffset? LastAccessedAt,
    IReadOnlyDictionary<string, long> DailyClicks,
    long? RemainingSeconds = null);
