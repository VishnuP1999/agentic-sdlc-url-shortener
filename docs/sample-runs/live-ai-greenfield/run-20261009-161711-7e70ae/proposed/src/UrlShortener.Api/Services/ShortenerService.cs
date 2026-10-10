using System.Security.Cryptography;
using System.Text;
using UrlShortener.Api.Analytics;
using UrlShortener.Api.Domain;
using UrlShortener.Api.Options;
using UrlShortener.Api.Persistence;
using UrlShortener.Api.Validation;

namespace UrlShortener.Api.Services;

/// <summary>Core business logic: create, resolve (with expiry), deactivate and stats.</summary>
public sealed class ShortenerService
{
    private const int MaxCodeCollisionRetries = 20;

    private readonly IUrlRepository _repo;
    private readonly UrlValidator _urlValidator;
    private readonly ClickRecorder _clicks;
    private readonly AnalyticsStore _analytics;
    private readonly ShortenerOptions _options;
    private readonly TimeProvider _time;

    public ShortenerService(
        IUrlRepository repo,
        UrlValidator urlValidator,
        ClickRecorder clicks,
        AnalyticsStore analytics,
        ShortenerOptions options,
        TimeProvider time)
    {
        _repo = repo;
        _urlValidator = urlValidator;
        _clicks = clicks;
        _analytics = analytics;
        _options = options;
        _time = time;
    }

    internal static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public CreateResult Create(CreateLinkRequest request, string clientId)
    {
        var errors = new List<string>();
        var now = _time.GetUtcNow();

        var url = _urlValidator.Validate(request.Url);
        if (!url.IsValid) errors.Add(url.Error!);

        string? alias = string.IsNullOrWhiteSpace(request.Alias) ? null : request.Alias.Trim();
        if (alias is not null)
        {
            var aliasError = AliasValidator.Validate(alias);
            if (aliasError is not null) errors.Add(aliasError);
        }

        if (request.ExpiresInMinutes is { } minutes && (minutes < 1 || minutes > _options.MaxExpiryMinutes))
            errors.Add($"expiresInMinutes must be between 1 and {_options.MaxExpiryMinutes}.");

        if (request.ExpiresAt is { } abs && abs <= now)
            errors.Add("expiresAt must be in the future.");

        if (errors.Count > 0) return new CreateResult(CreateStatus.Invalid, Errors: errors);

        // Idempotent create: same client + same target (no alias, no explicit expiry) returns the existing live link.
        // The plaintext owner token is not recoverable (only its hash is stored), so none is returned here.
        if (alias is null && request.ExpiresAt is null)
        {
            var existing = _repo.FindByOwnerAndTarget(clientId, url.NormalizedUrl!);
            if (existing is { IsActive: true } && !existing.IsExpired(now))
                return new CreateResult(CreateStatus.Existing, existing);
        }

        DateTimeOffset? expiresAt = request.ExpiresAt?.ToUniversalTime()
            ?? (request.ExpiresInMinutes is { } m ? now.AddMinutes(m) : null);

        var token = NewToken();
        var hash = HashToken(token);

        if (alias is not null)
        {
            var link = new ShortLink(alias, url.NormalizedUrl!, now, expiresAt, true, clientId, hash);
            return _repo.TryAdd(link)
                ? new CreateResult(CreateStatus.Created, link, OwnerToken: token)
                : new CreateResult(CreateStatus.AliasTaken, Errors: new[] { "alias is already in use." });
        }

        for (var attempt = 0; attempt < MaxCodeCollisionRetries; attempt++)
        {
            var code = CodeGenerator.NewRandom();
            if (AliasValidator.IsReserved(code)) continue;

            var link = new ShortLink(code, url.NormalizedUrl!, now, expiresAt, true, clientId, hash);
            if (_repo.TryAdd(link)) return new CreateResult(CreateStatus.Created, link, OwnerToken: token);
        }

        throw new InvalidOperationException("Could not allocate a unique short code.");
    }

    public ResolveResult Resolve(string code)
    {
        var link = _repo.Get(code);
        if (link is null) return new ResolveResult(ResolveStatus.NotFound);

        var now = _time.GetUtcNow();
        if (!link.IsActive || link.IsExpired(now)) return new ResolveResult(ResolveStatus.Gone);

        _clicks.Enqueue(new ClickEvent(code, now));
        return new ResolveResult(ResolveStatus.Redirect, link.TargetUrl);
    }

    /// <summary>Legacy deactivation by client id.</summary>
    public DeactivateStatus Deactivate(string code, string clientId)
    {
        var link = _repo.Get(code);
        if (link is null) return DeactivateStatus.NotFound;
        if (!string.Equals(link.OwnerClientId, clientId, StringComparison.Ordinal)) return DeactivateStatus.Forbidden;
        _repo.Deactivate(code);
        return DeactivateStatus.Done;
    }

    /// <summary>Deactivation by bearer owner token; falls back to an explicit legacy client id when no token is given.</summary>
    public DeactivateStatus Deactivate(string code, string? bearerToken, string? legacyClientId)
    {
        var link = _repo.Get(code);
        if (link is null) return DeactivateStatus.NotFound;

        if (!string.IsNullOrEmpty(bearerToken))
        {
            if (link.OwnerTokenHash is null) return DeactivateStatus.Forbidden;
            var a = Encoding.UTF8.GetBytes(HashToken(bearerToken));
            var b = Encoding.UTF8.GetBytes(link.OwnerTokenHash);
            if (!CryptographicOperations.FixedTimeEquals(a, b)) return DeactivateStatus.Forbidden;
            _repo.Deactivate(code);
            return DeactivateStatus.Done;
        }

        if (string.IsNullOrEmpty(legacyClientId)) return DeactivateStatus.Unauthorized;
        return Deactivate(code, legacyClientId);
    }

    /// <summary>Link metadata, or null for unknown codes. Does not count as a click.</summary>
    public ShortLink? GetMetadata(string code) => _repo.Get(code);

    public LinkStats? GetStats(string code)
    {
        var link = _repo.Get(code);
        if (link is null) return null;
        var a = _analytics.Get(code);
        return new LinkStats(link.Code, link.TargetUrl, link.CreatedAt, link.ExpiresAt, link.IsActive,
            a.TotalClicks, a.LastAccessedAt, a.DailyClicks);
    }
}
