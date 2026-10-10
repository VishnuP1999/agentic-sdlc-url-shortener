using Microsoft.Extensions.Options;
using UrlShortener.Api.Options;
using UrlShortener.Api.Reliability;
using UrlShortener.Api.Services;

namespace UrlShortener.Api.Endpoints;

public static class ShortLinkEndpoints
{
    private const int MaxClientIdLength = 64;

    public static void Map(WebApplication app)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

        var api = app.MapGroup("/api/urls");
        api.MapPost("", CreateAsync);
        api.MapGet("/{code}", GetMetadata);
        api.MapGet("/{code}/analytics", GetStats);
        api.MapGet("/{code}/stats", GetStats); // kept for backward compatibility
        api.MapDelete("/{code}", Deactivate);

        // Canonical redirect route; the single-segment form is kept for short URLs already handed out.
        app.MapGet("/r/{code}", Redirect);
        // Single-segment catch-all must be registered after the literal routes above.
        app.MapGet("/{code}", Redirect);
    }

    private static IResult CreateAsync(
        CreateLinkRequest? request,
        HttpContext http,
        ShortenerService service,
        FixedWindowRateLimiter limiter,
        IOptions<ShortenerOptions> options)
    {
        var clientId = GetClientId(http);

        if (!limiter.TryAcquire(clientId, out var retryAfter))
        {
            http.Response.Headers.RetryAfter = Math.Ceiling(retryAfter.TotalSeconds).ToString("0");
            return Results.Problem("Rate limit exceeded.", statusCode: StatusCodes.Status429TooManyRequests);
        }

        if (request is null)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["body"] = new[] { "request body is required." } });

        var result = service.Create(request, clientId);
        switch (result.Status)
        {
            case CreateStatus.Invalid:
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = result.Errors!.ToArray() });
            case CreateStatus.AliasTaken:
                return Results.Problem(result.Errors![0], statusCode: StatusCodes.Status409Conflict);
        }

        var link = result.Link!;
        var baseUrl = options.Value.BaseUrl?.TrimEnd('/') ?? $"{http.Request.Scheme}://{http.Request.Host}";
        var body = new CreateLinkResponse(link.Code, $"{baseUrl}/r/{link.Code}", link.TargetUrl, link.ExpiresAt,
            result.Status == CreateStatus.Created);

        return result.Status == CreateStatus.Created
            ? Results.Created($"/api/urls/{link.Code}", body)
            : Results.Ok(body);
    }

    private static IResult GetMetadata(string code, HttpContext http, ShortenerService service, IOptions<ShortenerOptions> options)
    {
        if (service.GetMetadata(code) is not { } link) return Results.NotFound();
        var baseUrl = options.Value.BaseUrl?.TrimEnd('/') ?? $"{http.Request.Scheme}://{http.Request.Host}";
        return Results.Ok(new LinkMetadataResponse(link.Code, $"{baseUrl}/r/{link.Code}", link.TargetUrl, link.CreatedAt,
            link.ExpiresAt, link.IsActive));
    }

    private static IResult Redirect(string code, ShortenerService service)
    {
        var result = service.Resolve(code);
        return result.Status switch
        {
            ResolveStatus.Redirect => Results.Redirect(result.TargetUrl!, permanent: false),
            ResolveStatus.Gone => Results.Problem("This link has expired or was deactivated.", statusCode: StatusCodes.Status410Gone),
            _ => Results.NotFound()
        };
    }

    private static IResult GetStats(string code, ShortenerService service) =>
        service.GetStats(code) is { } stats ? Results.Ok(stats) : Results.NotFound();

    private static IResult Deactivate(string code, HttpContext http, ShortenerService service) =>
        service.Deactivate(code, GetClientId(http)) switch
        {
            DeactivateStatus.Done => Results.NoContent(),
            DeactivateStatus.Forbidden => Results.Problem("Only the creator can deactivate this link.", statusCode: StatusCodes.Status403Forbidden),
            _ => Results.NotFound()
        };

    /// <summary>
    /// Placeholder identity: X-Client-Id header, else remote IP. Real authentication is out of scope for
    /// the prototype (documented limitation) - this value is spoofable.
    /// </summary>
    internal static string GetClientId(HttpContext http)
    {
        var header = http.Request.Headers["X-Client-Id"].ToString().Trim();
        if (header.Length > 0) return header.Length > MaxClientIdLength ? header[..MaxClientIdLength] : header;
        return http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}
