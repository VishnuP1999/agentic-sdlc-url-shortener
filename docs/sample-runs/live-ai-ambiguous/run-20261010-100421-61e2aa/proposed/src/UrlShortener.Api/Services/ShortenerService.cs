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

        if (request.ExpiresAt is { } at)
        {
            if (request.ExpiresInMinutes is not null)
                errors.Add("specify either expiresAt or expiresInMinutes, not both.");
            else if (at <= now)
                errors.Add("expiresAt must be in the future.");
            else if (at > now.AddMinutes(_options.MaxExpiryMinutes))
                errors.Add($"expiresAt must be within {_options.MaxExpiryMinutes} minutes from now.");
        }

        if (errors.Count > 0) return new CreateResult(CreateStatus.Invalid, Errors: errors);

        // Idempotent create: same client + same target (and no custom alias) returns the existing live link.
        if (alias is null)
        {
            var existing = _repo.FindByOwnerAndTarget(clientId, url.NormalizedUrl!);
            if (existing is { IsActive: true } && !existing.IsExpired(now))
                return new CreateResult(CreateStatus.Existing, existing);
        }

        DateTimeOffset? expiresAt = request.ExpiresInMinutes is { } m ? now.AddMinutes(m) : request.ExpiresAt?.ToUniversalTime();

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

    public bool IsExpired(ShortLink link) => link.IsExpired(_time.GetUtcNow());

    public DeactivateStatus Deactivate(string code, string clientId)
    {
        var link = _repo.Get(code);
        if (link is null) return DeactivateStatus.NotFound;
        if (!string.Equals(link.OwnerClientId, clientId, StringComparison.Ordinal)) return DeactivateStatus.Forbidden;
        _repo.Deactivate(code);
        return DeactivateStatus.Done;
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
