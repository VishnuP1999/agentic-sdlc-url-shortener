namespace UrlShortener.Api.Reliability;

/// <summary>Wraps a limiter so redirect limits are configured separately from creation limits.</summary>
public sealed class RedirectRateLimiter
{
    private readonly FixedWindowRateLimiter _inner;

    public RedirectRateLimiter(FixedWindowRateLimiter inner) => _inner = inner;

    public bool TryAcquire(string clientId, out TimeSpan retryAfter) => _inner.TryAcquire(clientId, out retryAfter);
}
