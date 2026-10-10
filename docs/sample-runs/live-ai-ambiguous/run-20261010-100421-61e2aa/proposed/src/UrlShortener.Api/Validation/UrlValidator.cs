using System.Net;
using System.Net.Sockets;

namespace UrlShortener.Api.Validation;

public sealed record UrlValidationResult(bool IsValid, string? Error, string? NormalizedUrl)
{
    public static UrlValidationResult Fail(string error) => new(false, error, null);
    public static UrlValidationResult Ok(string normalized) => new(true, null, normalized);
}

/// <summary>
/// Validates target URLs: http/https only, no credentials, no loopback/private/link-local hosts,
/// no blocklisted domains (suffix match) and hostnames resolving to private addresses are rejected.
/// DNS lookups time out and then are not treated as a rejection (creation-time check only;
/// DNS rebinding later is possible, the service never fetches targets server-side).
/// </summary>
public sealed class UrlValidator
{
    private readonly int _maxLength;
    private readonly string[] _blocklist;
    private readonly bool _resolveDns;
    private readonly TimeSpan _dnsTimeout;

    public UrlValidator(int maxLength, IEnumerable<string>? blocklist = null, bool resolveDns = false, TimeSpan? dnsTimeout = null)
    {
        _maxLength = maxLength;
        _blocklist = (blocklist ?? Array.Empty<string>())
            .Where(b => !string.IsNullOrWhiteSpace(b))
            .Select(b => b.Trim().TrimStart('.').TrimEnd('.').ToLowerInvariant())
            .ToArray();
        _resolveDns = resolveDns;
        _dnsTimeout = dnsTimeout ?? TimeSpan.FromSeconds(2);
    }

    public UrlValidationResult Validate(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return UrlValidationResult.Fail("url is required.");
        url = url.Trim();
        if (url.Length > _maxLength) return UrlValidationResult.Fail($"url must be at most {_maxLength} characters.");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return UrlValidationResult.Fail("url must be an absolute URL.");
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return UrlValidationResult.Fail("only http and https URLs are allowed.");
        if (!string.IsNullOrEmpty(uri.UserInfo)) return UrlValidationResult.Fail("URLs containing credentials are not allowed.");
        var host = uri.DnsSafeHost;
        if (IsBlockedHost(host)) return UrlValidationResult.Fail("target host is not allowed.");
        if (IsBlocklisted(host)) return UrlValidationResult.Fail("target host is on the blocklist.");
        if (_resolveDns && ResolvesToPrivate(host)) return UrlValidationResult.Fail("target host resolves to a private address.");
        return UrlValidationResult.Ok(uri.AbsoluteUri);
    }

    private bool IsBlocklisted(string host)
    {
        host = host.TrimEnd('.').ToLowerInvariant();
        foreach (var b in _blocklist)
            if (host == b || host.EndsWith("." + b, StringComparison.Ordinal)) return true;
        return false;
    }

    private bool ResolvesToPrivate(string host)
    {
        if (IPAddress.TryParse(host.Trim('[', ']'), out _)) return false; // literal already checked
        try
        {
            var addresses = Dns.GetHostAddressesAsync(host).WaitAsync(_dnsTimeout).GetAwaiter().GetResult();
            foreach (var a in addresses)
            {
                var ip = a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a;
                if (IsPrivateOrReserved(ip)) return true;
            }
        }
        catch (Exception)
        {
            // unresolvable or timed out: not treated as private
        }
        return false;
    }

    public static bool IsBlockedHost(string host)
    {
        host = host.TrimEnd('.').ToLowerInvariant();
        if (host.Length == 0) return true;
        if (host == "localhost" || host.EndsWith(".localhost") || host.EndsWith(".local") || host.EndsWith(".internal"))
            return true;

        if (IPAddress.TryParse(host.Trim('[', ']'), out var ip))
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
            return b[0] == 0
                || b[0] == 10
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || b[0] >= 224;
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast
                || (b[0] & 0xFE) == 0xFC
                || ip.Equals(IPAddress.IPv6Any);
        }
        return true;
    }
}
