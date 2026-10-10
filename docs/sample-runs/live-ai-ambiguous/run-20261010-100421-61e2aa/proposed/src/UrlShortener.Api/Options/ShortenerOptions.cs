namespace UrlShortener.Api.Options;

public sealed class ShortenerOptions
{
    /// <summary>Public base URL used to build short links, e.g. https://sho.rt. Falls back to the request host.</summary>
    public string? BaseUrl { get; set; }

    public int MaxUrlLength { get; set; } = 2048;
    public int MaxExpiryMinutes { get; set; } = 60 * 24 * 365;

    /// <summary>Create requests allowed per client per window.</summary>
    public int RateLimitPerWindow { get; set; } = 30;
    public int RateLimitWindowSeconds { get; set; } = 60;

    /// <summary>Redirect requests allowed per client IP per redirect window.</summary>
    public int RedirectRateLimitPerWindow { get; set; } = 600;
    public int RedirectRateLimitWindowSeconds { get; set; } = 60;

    public int MaxRequestBodyBytes { get; set; } = 8 * 1024;
    public int ClickQueueCapacity { get; set; } = 10_000;

    /// <summary>Malicious domains; a host equal to or a subdomain of an entry is rejected.</summary>
    public string[] Blocklist { get; set; } = Array.Empty<string>();

    /// <summary>Resolve hostnames at creation and reject private results.</summary>
    public bool DnsResolutionEnabled { get; set; } = true;
    public int DnsTimeoutMilliseconds { get; set; } = 2000;
}
