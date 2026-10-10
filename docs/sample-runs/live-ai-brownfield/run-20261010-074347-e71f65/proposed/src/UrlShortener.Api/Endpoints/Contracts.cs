namespace UrlShortener.Api.Endpoints;

/// <summary>Response contracts (public API shape).</summary>
public sealed record CreateLinkResponse(string Code, string ShortUrl, string TargetUrl, DateTimeOffset? ExpiresAt, bool Created);

/// <summary>Link metadata without analytics (GET /api/urls/{code}). The owner id is deliberately not exposed.</summary>
public sealed record LinkMetadataResponse(
    string Code, string ShortUrl, string TargetUrl, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, bool IsActive);

/// <summary>Request body of the extend endpoint (parsed manually so malformed input yields 400).</summary>
public sealed record ExtendExpiryRequest(long? AdditionalSeconds);

public sealed record ExtendExpiryResponse(string Code, DateTimeOffset? ExpiresAt, long? RemainingSeconds);
