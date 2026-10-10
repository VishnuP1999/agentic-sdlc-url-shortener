using Microsoft.Extensions.Options;
using UrlShortener.Api.Analytics;
using UrlShortener.Api.Endpoints;
using UrlShortener.Api.Options;
using UrlShortener.Api.Persistence;
using UrlShortener.Api.Reliability;
using UrlShortener.Api.Services;
using UrlShortener.Api.Validation;

namespace UrlShortener.Api;

/// <summary>Composition root. Kept separate from Program so tests can host the API in-process.</summary>
public static class AppHost
{
    public static WebApplication Build(string[] args, Action<ShortenerOptions>? configure = null, TimeProvider? time = null)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.Configure<ShortenerOptions>(builder.Configuration.GetSection("Shortener"));
        if (configure is not null) builder.Services.PostConfigure(configure);

        builder.WebHost.ConfigureKestrel((ctx, kestrel) =>
        {
            var opts = ctx.Configuration.GetSection("Shortener").Get<ShortenerOptions>() ?? new ShortenerOptions();
            configure?.Invoke(opts);
            kestrel.Limits.MaxRequestBodySize = opts.MaxRequestBodyBytes;
            kestrel.AddServerHeader = false;
        });

        builder.Services.AddProblemDetails();
        builder.Services.AddSingleton(time ?? TimeProvider.System);
        builder.Services.AddSingleton<IUrlRepository, InMemoryUrlRepository>();
        builder.Services.AddSingleton<AnalyticsStore>();
        builder.Services.AddSingleton(sp => new ClickRecorder(sp.GetRequiredService<IOptions<ShortenerOptions>>().Value.ClickQueueCapacity));
        builder.Services.AddHostedService<ClickProcessor>();
        builder.Services.AddSingleton(sp => new UrlValidator(sp.GetRequiredService<IOptions<ShortenerOptions>>().Value.MaxUrlLength));
        builder.Services.AddSingleton(sp =>
        {
            var o = sp.GetRequiredService<IOptions<ShortenerOptions>>().Value;
            return new FixedWindowRateLimiter(o.RateLimitPerWindow, TimeSpan.FromSeconds(o.RateLimitWindowSeconds), sp.GetRequiredService<TimeProvider>());
        });
        builder.Services.AddSingleton(sp => new ShortenerService(
            sp.GetRequiredService<IUrlRepository>(),
            sp.GetRequiredService<UrlValidator>(),
            sp.GetRequiredService<ClickRecorder>(),
            sp.GetRequiredService<AnalyticsStore>(),
            sp.GetRequiredService<IOptions<ShortenerOptions>>().Value,
            sp.GetRequiredService<TimeProvider>()));

        var app = builder.Build();

        app.UseExceptionHandler();
        app.Use(async (context, next) =>
        {
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Cache-Control"] = "no-store";
            await next();
        });

        ShortLinkEndpoints.Map(app);
        return app;
    }
}
