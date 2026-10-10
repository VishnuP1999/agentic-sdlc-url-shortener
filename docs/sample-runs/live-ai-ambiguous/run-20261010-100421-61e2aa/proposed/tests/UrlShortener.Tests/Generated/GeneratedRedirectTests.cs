using System.Net;
using UrlShortener.Tests;
using Xunit;

namespace UrlShortener.Tests.Generated;

public class GeneratedRedirectTests
{
    private static async Task<string> NewLink(ApiHost host, string url, object? extra = null, string? client = null)
    {
        var resp = extra is null ? await host.CreateAsync(new { url }, client) : await host.CreateAsync(extra, client);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return (await ApiHost.ReadJsonAsync(resp)).GetProperty("code").GetString()!;
    }

    private static async Task<long> Clicks(ApiHost host, string code)
    {
        var r = await host.Client.GetAsync($"/api/urls/{code}/analytics");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await ApiHost.ReadJsonAsync(r)).GetProperty("totalClicks").GetInt64();
    }

    private static async Task<bool> WaitForClicks(ApiHost host, string code, long expected)
    {
        for (var i = 0; i < 200; i++)
        {
            if (await Clicks(host, code) == expected) return true;
            await Task.Delay(25);
        }
        return false;
    }

    [Fact]
    public async Task Redirect_returns_302_for_both_routes_and_404_for_unknown()
    {
        await using var host = await ApiHost.StartAsync(o => o.DnsResolutionEnabled = false);
        var code = await NewLink(host, "https://good.example/page");
        var r = await host.Client.GetAsync("/r/" + code);
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("https://good.example/page", r.Headers.Location!.ToString());
        var legacy = await host.Client.GetAsync("/" + code);
        Assert.Equal(HttpStatusCode.Redirect, legacy.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/r/unknown99")).StatusCode);
    }

    [Fact]
    public async Task Tiny_queue_never_fails_redirects()
    {
        await using var host = await ApiHost.StartAsync(o => { o.DnsResolutionEnabled = false; o.ClickQueueCapacity = 1; o.RedirectRateLimitPerWindow = 10000; });
        var code = await NewLink(host, "https://good.example/q");
        for (var i = 0; i < 100; i++)
            Assert.Equal(HttpStatusCode.Redirect, (await host.Client.GetAsync("/r/" + code)).StatusCode);
    }

    [Fact]
    public async Task Analytics_counts_successful_redirects_only()
    {
        await using var host = await ApiHost.StartAsync(o => o.DnsResolutionEnabled = false);
        var code = await NewLink(host, "https://good.example/a", new { url = "https://good.example/a", expiresInMinutes = 5 });
        for (var i = 0; i < 3; i++) await host.Client.GetAsync("/r/" + code);
        Assert.True(await WaitForClicks(host, code, 3), "expected 3 clicks");

        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/r/unknown99")).StatusCode);
        host.Time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(HttpStatusCode.Gone, (await host.Client.GetAsync("/r/" + code)).StatusCode);
        await Task.Delay(150);
        Assert.Equal(3L, await Clicks(host, code));
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/api/urls/unknown99/analytics")).StatusCode);
    }

    [Fact]
    public async Task Expiry_redirects_before_and_gone_after()
    {
        await using var host = await ApiHost.StartAsync(o => o.DnsResolutionEnabled = false);
        var code = await NewLink(host, "https://good.example/e", new { url = "https://good.example/e", expiresInMinutes = 10 });
        host.Time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(HttpStatusCode.Redirect, (await host.Client.GetAsync("/r/" + code)).StatusCode);
        host.Time.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(HttpStatusCode.Gone, (await host.Client.GetAsync("/r/" + code)).StatusCode);
        var meta = await host.Client.GetAsync("/api/urls/" + code);
        Assert.Equal(HttpStatusCode.OK, meta.StatusCode);
        Assert.True((await ApiHost.ReadJsonAsync(meta)).GetProperty("isExpired").GetBoolean());
    }

    [Fact]
    public async Task Create_rate_limit_returns_429_and_isolates_clients()
    {
        await using var host = await ApiHost.StartAsync(o => { o.DnsResolutionEnabled = false; o.RateLimitPerWindow = 2; });
        Assert.Equal(HttpStatusCode.Created, (await host.CreateAsync(new { url = "https://good.example/1" }, "client-a")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await host.CreateAsync(new { url = "https://good.example/2" }, "client-a")).StatusCode);
        var limited = await host.CreateAsync(new { url = "https://good.example/3" }, "client-a");
        Assert.Equal((HttpStatusCode)429, limited.StatusCode);
        Assert.True(limited.Headers.Contains("Retry-After"), "Retry-After expected");
        Assert.Equal(HttpStatusCode.Created, (await host.CreateAsync(new { url = "https://good.example/4" }, "client-b")).StatusCode);
    }

    [Fact]
    public async Task Redirect_rate_limit_returns_429_with_retry_after()
    {
        await using var host = await ApiHost.StartAsync(o => { o.DnsResolutionEnabled = false; o.RedirectRateLimitPerWindow = 2; });
        var code = await NewLink(host, "https://good.example/r");
        Assert.Equal(HttpStatusCode.Redirect, (await host.Client.GetAsync("/r/" + code)).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await host.Client.GetAsync("/r/" + code)).StatusCode);
        var limited = await host.Client.GetAsync("/r/" + code);
        Assert.Equal((HttpStatusCode)429, limited.StatusCode);
        Assert.True(limited.Headers.Contains("Retry-After"), "Retry-After expected");
        Assert.True(await WaitForClicks(host, code, 2), "429 must not count as a click");
    }
}
