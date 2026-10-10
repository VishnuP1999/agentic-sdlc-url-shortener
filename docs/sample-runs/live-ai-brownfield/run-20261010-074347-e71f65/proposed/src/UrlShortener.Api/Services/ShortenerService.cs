using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using UrlShortener.Api.Analytics;
using UrlShortener.Api.Domain;
using UrlShortener.Api.Options;
using UrlShortener.Api.Persistence;
using UrlShortener.Api.Validation;

namespace UrlShortener.Api.Services;

/// <summary>Core business logic: create, resolve (with expiry), deactivate, extend and stats.</summary>
public sealed class ShortenerService
{
    private const int MaxCodeCollisionRetries = 20;
    private const int MaxCasRetries = 10_000;

    private readonly IUrlRepository _repo;
    private readonly UrlValidator _urlValidator;
    private readonly ClickRecorder _clicks;
    private readonly AnalyticsStore _analytics;
    private readonly ShortenerOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger? _audit;

    public ShortenerService(
        IUrlRepository repo,
        UrlValidator urlValidator,
        ClickRecorder clicks,
        AnalyticsStore analytics,
        ShortenerOptions options,
        TimeProvider time,
        ILogger? auditLogger = null)
    {
        _repo = repo;
        _urlValidator = urlValidator;
        _clicks = clicks;
        _analytics = analytics;
        _options = options;
        _time = time;
        _audit = auditLogger;
    }

    public CreateResult Create(CreateLinkRequest request, string clientId)
    {
        var errors = new List<string>();

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

        if (errors.Count > 0) return new CreateResult(CreateStatus.Invalid, Errors: errors);

        var now = _time.GetUtcNow();

        // Idempotent create: same client + same target (and no custom alias) returns the existing live link.
        if (alias is null)
        {
            var existing = _repo.FindByOwnerAndTarget(clientId, url.NormalizedUrl!);
            if (existing is { IsActive: true } && !existing.IsExpired(now))
                return new CreateResult(CreateStatus.Existing, existing);
        }

        DateTimeOffset? expiresAt = request.ExpiresInMinutes is { } m ? now.AddMinutes(m) : null;

        if (alias is not null)
        {
            var link = new ShortLink(alias, url.NormalizedUrl!, now, expiresAt, true, clientId);
            return _repo.TryAdd(link)
                ? new CreateResult(CreateStatus.Created, link)
                : new CreateResult(CreateStatus.AliasTaken, Errors: new[] { "alias is already in use." });
        }

        for (var attempt = 0; attempt < MaxCodeCollisionRetries; attempt++)
        {
            var code = CodeGenerator.FromId(_repo.NextId());
            if (AliasValidator.IsReserved(code)) continue;

            var link = new ShortLink(code, url.NormalizedUrl!, now, expiresAt, true, clientId);
            if (_repo.TryAdd(link)) return new CreateResult(CreateStatus.Created, link);
            // the code collided with a custom alias: move on to the next id
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

    public DeactivateStatus Deactivate(string code, string clientId)
    {
        var link = _repo.Get(code);
        if (link is null) return DeactivateStatus.NotFound;
        if (!string.Equals(link.OwnerClientId, clientId, StringComparison.Ordinal)) return DeactivateStatus.Forbidden;
        _repo.Deactivate(code);
        return DeactivateStatus.Done;
    }

    /// <summary>
    /// Extends the expiry by <paramref name="additionalSeconds"/>. Check order: 404, 403, 400, 409.
    /// Expired (but active) links are revived from now. Uses compare-and-swap so concurrent extends lose no updates.
    /// </summary>
    public ExtendResult ExtendExpiry(string code, string clientId, long? additionalSeconds)
    {
        var maxSeconds = _options.MaxExpiryMinutes * 60L;

        for (var attempt = 0; attempt < MaxCasRetries; attempt++)
        {
            var link = _repo.Get(code);
            if (link is null) return Audit(ExtendStatus.NotFound, code, clientId, null, null, additionalSeconds);
            if (!string.Equals(link.OwnerClientId, clientId, StringComparison.Ordinal))
                return Audit(ExtendStatus.Forbidden, code, clientId, link.ExpiresAt, null, additionalSeconds);

            if (additionalSeconds is not { } add || add <= 0)
                return Audit(ExtendStatus.Invalid, code, clientId, link.ExpiresAt, null, additionalSeconds);
            if (add > maxSeconds)
                return Audit(ExtendStatus.TooLong, code, clientId, link.ExpiresAt, null, additionalSeconds);

            if (!link.IsActive) return Audit(ExtendStatus.Deactivated, code, clientId, link.ExpiresAt, null, additionalSeconds);
            if (link.ExpiresAt is not { } current)
                return Audit(ExtendStatus.NoExpiry, code, clientId, null, null, additionalSeconds);

            var now = _time.GetUtcNow();
            var baseTime = current > now ? current : now;
            var newExpiry = baseTime.AddSeconds(add);
            if ((newExpiry - now).TotalSeconds > maxSeconds)
                return Audit(ExtendStatus.TooLong, code, clientId, current, null, additionalSeconds);

            var updated = link with { ExpiresAt = newExpiry };
            if (_repo.TryReplace(code, link, updated))
            {
                return Audit(ExtendStatus.Done, code, clientId, current, newExpiry, additionalSeconds, updated, Remaining(newExpiry, now));
            }
            // lost the race: re-read and retry
        }

        throw new InvalidOperationException("Could not extend expiry due to contention.");
    }

    private ExtendResult Audit(ExtendStatus status, string code, string clientId, DateTimeOffset? previous,
        DateTimeOffset? next, long? add, ShortLink? link = null, long? remaining = null)
    {
        _audit?.LogInformation(
            "audit action={Action} at={At} code={Code} actor={ActorHash} outcome={Outcome} previousExpiresAt={Previous} newExpiresAt={New} additionalSeconds={Additional}",
            "expiry.extend", _time.GetUtcNow(), Sanitize(code), ActorHash(clientId), status.ToString(), previous, next, add);
        return new ExtendResult(status, link, remaining);
    }

    private static string Sanitize(string value)
    {
        var v = value.Length > 64 ? value[..64] : value;
        var sb = new StringBuilder(v.Length);
        foreach (var c in v) sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '?');
        return sb.ToString();
    }

    private static string ActorHash(string clientId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(clientId)))[..12];

    private static long Remaining(DateTimeOffset expiresAt, DateTimeOffset now)
    {
        var secs = (long)Math.Floor((expiresAt - now).TotalSeconds);
        return secs < 0 ? 0 : secs;
    }

    /// <summary>Link metadata, or null for unknown codes. Does not count as a click.</summary>
    public ShortLink? GetMetadata(string code) => _repo.Get(code);

    public LinkStats? GetStats(string code)
    {
        var link = _repo.Get(code);
        if (link is null) return null;
        var a = _analytics.Get(code);
        long? remaining = link.ExpiresAt is { } e ? Remaining(e, _time.GetUtcNow()) : null;
        return new LinkStats(link.Code, link.TargetUrl, link.CreatedAt, link.ExpiresAt, link.IsActive,
            a.TotalClicks, a.LastAccessedAt, a.DailyClicks, remaining);
    }
}
