using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using UrlShortener.Api;
using UrlShortener.Api.Options;
using Xunit;

namespace UrlShortener.Tests;

/// <summary>Spins the real API up in-process on a random local port and talks to it over HTTP.</summary>
public sealed class ApiHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    public HttpClient Client { get; }
    public FakeTimeProvider Time { get; }

    private ApiHost(WebApplication app, FakeTimeProvider time)
    {
        _app = app;
        Time = time;
        Client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri(app.Urls.First())
        };
    }

    public static async Task<ApiHost> StartAsync(Action<ShortenerOptions>? configure = null)
    {
        var time = new FakeTimeProvider();
        var app = AppHost.Build(new[] { "--urls", "http://127.0.0.1:0", "--Logging:LogLevel:Default=Warning", "--Logging:LogLevel:Microsoft=Warning" }, configure, time);
        await app.StartAsync();
        return new ApiHost(app, time);
    }

    public async Task<HttpResponseMessage> CreateAsync(object body, string? clientId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/urls") { Content = JsonContent.Create(body) };
        if (clientId is not null) request.Headers.Add("X-Client-Id", clientId);
        return await Client.SendAsync(request);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    public static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
}

public class ApiIntegrationTests
{
    [Fact]
    public async Task Health_endpoint_is_up()
    {
        await using var host = await ApiHost.StartAsync();
        var response = await host.Client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").First());
    }

    [Fact]
    public async Task Create_redirect_and_stats_flow()
    {
        await using var host = await ApiHost.StartAsync();

        var created = await host.CreateAsync(new { url = "https://example.com/some/long/path?x=1" }, "client-1");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await ApiHost.ReadJsonAsync(created);
        var code = body.GetProperty("code").GetString()!;
        Assert.Contains(code, body.GetProperty("shortUrl").GetString());

        for (var i = 0; i < 3; i++)
        {
            var redirect = await host.Client.GetAsync($"/{code}");
            Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
            Assert.Equal("https://example.com/some/long/path?x=1", redirect.Headers.Location!.ToString());
        }

        // clicks are recorded asynchronously: poll until the background processor has caught up
        long clicks = 0;
        for (var attempt = 0; attempt < 50 && clicks < 3; attempt++)
        {
            var stats = await ApiHost.ReadJsonAsync(await host.Client.GetAsync($"/api/urls/{code}/stats"));
            clicks = stats.GetProperty("totalClicks").GetInt64();
            if (clicks < 3) await Task.Delay(50);
        }
        Assert.Equal(3L, clicks);
    }

    [Fact]
    public async Task Repeated_create_for_same_client_is_idempotent()
    {
        await using var host = await ApiHost.StartAsync();
        var first = await host.CreateAsync(new { url = "https://example.com/a" }, "c1");
        var second = await host.CreateAsync(new { url = "https://example.com/a" }, "c1");

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal((await ApiHost.ReadJsonAsync(first)).GetProperty("code").GetString(),
            (await ApiHost.ReadJsonAsync(second)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Invalid_and_unsafe_urls_return_400()
    {
        await using var host = await ApiHost.StartAsync();
        foreach (var url in new[] { "not a url", "ftp://example.com", "http://127.0.0.1/admin", "http://169.254.169.254/" })
        {
            var response = await host.CreateAsync(new { url }, "c1");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public async Task Missing_body_returns_400()
    {
        await using var host = await ApiHost.StartAsync();
        var response = await host.Client.PostAsync("/api/urls", new StringContent("null", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Alias_conflict_returns_409()
    {
        await using var host = await ApiHost.StartAsync();
        Assert.Equal(HttpStatusCode.Created, (await host.CreateAsync(new { url = "https://example.com/a", alias = "promo" }, "c1")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await host.CreateAsync(new { url = "https://example.com/b", alias = "promo" }, "c2")).StatusCode);

        var redirect = await host.Client.GetAsync("/promo");
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
    }

    [Fact]
    public async Task Unknown_code_returns_404()
    {
        await using var host = await ApiHost.StartAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/nope123")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/api/urls/nope123/stats")).StatusCode);
    }

    [Fact]
    public async Task Expired_link_returns_410()
    {
        await using var host = await ApiHost.StartAsync();
        var created = await ApiHost.ReadJsonAsync(await host.CreateAsync(new { url = "https://example.com/a", expiresInMinutes = 5 }, "c1"));
        var code = created.GetProperty("code").GetString()!;

        Assert.Equal(HttpStatusCode.Redirect, (await host.Client.GetAsync($"/{code}")).StatusCode);
        host.Time.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(HttpStatusCode.Gone, (await host.Client.GetAsync($"/{code}")).StatusCode);
    }

    [Fact]
    public async Task Delete_is_owner_only_and_makes_link_gone()
    {
        await using var host = await ApiHost.StartAsync();
        var code = (await ApiHost.ReadJsonAsync(await host.CreateAsync(new { url = "https://example.com/a" }, "owner")))
            .GetProperty("code").GetString()!;

        var intruder = new HttpRequestMessage(HttpMethod.Delete, $"/api/urls/{code}");
        intruder.Headers.Add("X-Client-Id", "intruder");
        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.SendAsync(intruder)).StatusCode);

        var owner = new HttpRequestMessage(HttpMethod.Delete, $"/api/urls/{code}");
        owner.Headers.Add("X-Client-Id", "owner");
        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.SendAsync(owner)).StatusCode);

        Assert.Equal(HttpStatusCode.Gone, (await host.Client.GetAsync($"/{code}")).StatusCode);
    }

    [Fact]
    public async Task Rate_limit_returns_429_with_retry_after()
    {
        await using var host = await ApiHost.StartAsync(o => o.RateLimitPerWindow = 3);

        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.Created, (await host.CreateAsync(new { url = $"https://example.com/{i}" }, "busy")).StatusCode);

        var limited = await host.CreateAsync(new { url = "https://example.com/over" }, "busy");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.Contains("Retry-After"));

        // another client is unaffected, and the window eventually resets
        Assert.Equal(HttpStatusCode.Created, (await host.CreateAsync(new { url = "https://example.com/x" }, "calm")).StatusCode);
        host.Time.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(HttpStatusCode.Created, (await host.CreateAsync(new { url = "https://example.com/y" }, "busy")).StatusCode);
    }

    [Fact]
    public async Task Oversized_body_is_rejected()
    {
        await using var host = await ApiHost.StartAsync(o => o.MaxRequestBodyBytes = 256);
        var big = new StringContent("{\"url\":\"https://example.com/" + new string('a', 1000) + "\"}", Encoding.UTF8, "application/json");
        var response = await host.Client.PostAsync("/api/urls", big);
        Assert.True(response.StatusCode is HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.BadRequest,
            $"unexpected status {(int)response.StatusCode}");
    }

    [Fact]
    public async Task Canonical_routes_redirect_return_metadata_and_analytics()
    {
        await using var host = await ApiHost.StartAsync();
        var created = await host.CreateAsync(new { url = "https://example.com/canonical" }, "c1");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await ApiHost.ReadJsonAsync(created);
        var code = body.GetProperty("code").GetString()!;
        Assert.Equal($"/api/urls/{code}", created.Headers.Location!.ToString());
        Assert.Contains($"/r/{code}", body.GetProperty("shortUrl").GetString());

        var redirect = await host.Client.GetAsync($"/r/{code}");
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        Assert.Equal("https://example.com/canonical", redirect.Headers.Location!.ToString());

        var metadata = await ApiHost.ReadJsonAsync(await host.Client.GetAsync($"/api/urls/{code}"));
        Assert.Equal("https://example.com/canonical", metadata.GetProperty("targetUrl").GetString());
        Assert.True(metadata.GetProperty("isActive").GetBoolean());
        Assert.False(metadata.TryGetProperty("ownerClientId", out _), "metadata must not expose the owner id");

        long clicks = 0;
        for (var attempt = 0; attempt < 50 && clicks < 1; attempt++)
        {
            var analytics = await ApiHost.ReadJsonAsync(await host.Client.GetAsync($"/api/urls/{code}/analytics"));
            clicks = analytics.GetProperty("totalClicks").GetInt64();
            if (clicks < 1) await Task.Delay(50);
        }
        Assert.Equal(1L, clicks);
    }

    [Fact]
    public async Task Canonical_routes_return_404_for_unknown_codes()
    {
        await using var host = await ApiHost.StartAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/r/nope123")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/api/urls/nope123")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/api/urls/nope123/analytics")).StatusCode);
    }

    [Fact]
    public async Task Concurrent_creates_never_collide()
    {
        await using var host = await ApiHost.StartAsync(o => o.RateLimitPerWindow = 10_000);
        var tasks = Enumerable.Range(0, 200).Select(async i =>
        {
            var body = await ApiHost.ReadJsonAsync(await host.CreateAsync(new { url = $"https://example.com/{i}" }, $"c{i}"));
            return body.GetProperty("code").GetString()!;
        });
        var codes = await Task.WhenAll(tasks);
        Assert.Equal(200, codes.Distinct().Count());
    }
}
