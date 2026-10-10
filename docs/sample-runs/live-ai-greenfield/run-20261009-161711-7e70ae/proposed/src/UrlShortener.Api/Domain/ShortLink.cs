namespace UrlShortener.Api.Domain;

/// <summary>A stored short link. Immutable; state changes create a modified copy.</summary>
public sealed record ShortLink(
    string Code,
    string TargetUrl,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    bool IsActive,
    string OwnerClientId,
    string? OwnerTokenHash = null)
{
    public bool IsExpired(DateTimeOffset now) => ExpiresAt is { } e && e <= now;
}

/// <summary>A single redirect that should be counted.</summary>
public readonly record struct ClickEvent(string Code, DateTimeOffset At);
