namespace UrlShortener.Api.Options;

public sealed class ShortenerOptions
{
    /// <summary>Public base URL used to build short links, e.g. https://sho.rt. Falls back to the request host.</summary>
    public string? BaseUrl { get; set; }

    public int MaxUrlLength { get; set; } = 2048;
    public int MaxExpiryMinutes { get; set; } = 60 * 24 * 365;

    /// <summary>Create requests allowed per client per window.</summary>
    public int RateLimitPerWindow { get; set; } = 10;
    public int RateLimitWindowSeconds { get; set; } = 60;

    public int MaxRequestBodyBytes { get; set; } = 8 * 1024;
    public int ClickQueueCapacity { get; set; } = 10_000;
}
