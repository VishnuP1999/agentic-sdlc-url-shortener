using System.Net;
using System.Net.Sockets;

namespace UrlShortener.Api.Validation;

public sealed record UrlValidationResult(bool IsValid, string? Error, string? NormalizedUrl)
{
    public static UrlValidationResult Fail(string error) => new(false, error, null);
    public static UrlValidationResult Ok(string normalized) => new(true, null, normalized);
}

/// <summary>
/// Validates target URLs. Only http/https are allowed, credentials in the URL are rejected, and
/// hosts that point at loopback / private / link-local networks are blocked so the shortener
/// cannot be used to launder links to internal resources or cloud metadata endpoints.
/// Note: hostnames are not resolved (no DNS lookup), so DNS-rebinding is out of scope; the service
/// never fetches targets server-side.
/// </summary>
public sealed class UrlValidator
{
    private readonly int _maxLength;

    public UrlValidator(int maxLength) => _maxLength = maxLength;

    public UrlValidationResult Validate(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return UrlValidationResult.Fail("url is required.");
        url = url.Trim();
        if (url.Length > _maxLength) return UrlValidationResult.Fail($"url must be at most {_maxLength} characters.");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return UrlValidationResult.Fail("url must be an absolute URL.");
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return UrlValidationResult.Fail("only http and https URLs are allowed.");
        if (!string.IsNullOrEmpty(uri.UserInfo)) return UrlValidationResult.Fail("URLs containing credentials are not allowed.");
        if (IsBlockedHost(uri.DnsSafeHost)) return UrlValidationResult.Fail("target host is not allowed.");
        return UrlValidationResult.Ok(uri.AbsoluteUri);
    }

    public static bool IsBlockedHost(string host)
    {
        host = host.TrimEnd('.').ToLowerInvariant();
        if (host.Length == 0) return true;
        if (host == "localhost" || host.EndsWith(".localhost") || host.EndsWith(".local") || host.EndsWith(".internal"))
            return true;

        if (IPAddress.TryParse(host, out var ip))
        {
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            return IsPrivateOrReserved(ip);
        }
        return false;
    }

    private static bool IsPrivateOrReserved(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        var b = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            return b[0] == 0                                   // 0.0.0.0/8
                || b[0] == 10                                  // 10.0.0.0/8
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)  // 100.64.0.0/10 CGNAT
                || (b[0] == 169 && b[1] == 254)                // 169.254.0.0/16 link-local / cloud metadata
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)   // 172.16.0.0/12
                || (b[0] == 192 && b[1] == 168)                // 192.168.0.0/16
                || b[0] >= 224;                                // multicast / reserved
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast
                || (b[0] & 0xFE) == 0xFC                       // fc00::/7 unique local
                || ip.Equals(IPAddress.IPv6Any);
        }
        return true;
    }
}
