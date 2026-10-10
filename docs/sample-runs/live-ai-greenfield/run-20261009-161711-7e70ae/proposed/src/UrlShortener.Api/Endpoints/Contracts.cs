namespace UrlShortener.Api.Endpoints;

/// <summary>Response contracts (public API shape).</summary>
public sealed record CreateLinkResponse(string Code, string ShortUrl, string TargetUrl, DateTimeOffset? ExpiresAt, bool Created, string? OwnerToken = null);

/// <summary>Link metadata without analytics (GET /api/urls/{code}). Owner id and token are deliberately not exposed.</summary>
public sealed record LinkMetadataResponse(
    string Code, string ShortUrl, string TargetUrl, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, bool IsActive, bool IsExpired = false);
