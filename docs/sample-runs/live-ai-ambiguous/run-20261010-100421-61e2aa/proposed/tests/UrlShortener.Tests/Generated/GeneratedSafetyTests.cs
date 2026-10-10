using System.Net;
using UrlShortener.Tests;
using Xunit;

namespace UrlShortener.Tests.Generated;

public class GeneratedSafetyTests
{
    [Fact]
    public async Task Create_valid_url_and_read_metadata()
    {
        await using var host = await ApiHost.StartAsync(o => o.DnsResolutionEnabled = false);
        var resp = await host.CreateAsync(new { url = "https://good.example/page" });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var json = await ApiHost.ReadJsonAsync(resp);
        var code = json.GetProperty("code").GetString()!;
        Assert.Contains("good.example", json.GetProperty("targetUrl").GetString());

        var meta = await host.Client.GetAsync("/api/urls/" + code);
        Assert.Equal(HttpStatusCode.OK, meta.StatusCode);
        var m = await ApiHost.ReadJsonAsync(meta);
        Assert.True(m.TryGetProperty("createdAt", out _));
        Assert.True(m.TryGetProperty("expiresAt", out _));

        var unknown = await host.Client.GetAsync("/api/urls/nope999");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task Create_rejects_missing_malformed_and_past_expiry()
    {
        await using var host = await ApiHost.StartAsync(o => o.DnsResolutionEnabled = false);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.CreateAsync(new { })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.CreateAsync(new { url = "not a url" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await host.CreateAsync(new { url = "https://good.example/", expiresAt = "2020-01-01T00:00:00Z" })).StatusCode);
    }

    [Fact]
    public async Task Create_rejects_bad_schemes_and_credentials()
    {
        await using var host = await ApiHost.StartAsync(o => o.DnsResolutionEnabled = false);
        foreach (var url in new[] { "ftp://good.example/f", "javascript:alert(1)", "file:///etc/passwd", "http://user:pass@good.example/" })
            Assert.Equal(HttpStatusCode.BadRequest, (await host.CreateAsync(new { url })).StatusCode);
    }

    [Fact]
    public async Task Create_rejects_private_and_loopback_targets()
    {
        await using var host = await ApiHost.StartAsync(o => { o.DnsResolutionEnabled = false; o.RateLimitPerWindow = 1000; });
        var targets = new[]
        {
            "http://localhost/", "http://127.0.0.1/", "http://[::1]/", "http://10.1.2.3/", "http://172.16.0.1/",
            "http://172.31.255.254/", "http://192.168.1.1/", "http://169.254.169.254/"
        };
        foreach (var url in targets)
            Assert.Equal(HttpStatusCode.BadRequest, (await host.CreateAsync(new { url })).StatusCode);
    }

    [Fact]
    public async Task Create_rejects_blocklisted_host_and_subdomain_only()
    {
        await using var host = await ApiHost.StartAsync(o => { o.DnsResolutionEnabled = false; o.Blocklist = new[] { "evil.example" }; });
        Assert.Equal(HttpStatusCode.BadRequest, (await host.CreateAsync(new { url = "https://evil.example/x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.CreateAsync(new { url = "https://a.b.evil.example/x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await host.CreateAsync(new { url = "https://notevil.example/x" })).StatusCode);
    }
}
