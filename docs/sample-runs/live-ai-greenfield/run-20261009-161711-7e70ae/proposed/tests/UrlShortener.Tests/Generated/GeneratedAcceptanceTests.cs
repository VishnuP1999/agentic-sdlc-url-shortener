using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace UrlShortener.Tests.Generated;

public class GeneratedAcceptanceTests
{
    private static int _counter;
    private static string Cid() => "gen-" + Interlocked.Increment(ref _counter);

    private static async Task<(HttpResponseMessage Resp, JsonElement Json)> Create(ApiHost h, object body, string? cid = null)
    {
        var r = await h.CreateAsync(body, cid ?? Cid());
        JsonElement j = default;
        var text = await r.Content.ReadAsStringAsync();
        if (!string.IsNullOrWhiteSpace(text))
        {
            try { j = JsonDocument.Parse(text).RootElement.Clone(); } catch (JsonException) { }
        }
        return (r, j);
    }

    private static async Task<HttpResponseMessage> Delete(ApiHost h, string code, string? token)
    {
        var req = new HttpRequestMessage(HttpMethod.Delete, "/api/urls/" + code);
        if (token is not null) req.Headers.Add("Authorization", "Bearer " + token);
        return await h.Client.SendAsync(req);
    }

    private static async Task<long> WaitForClicks(ApiHost h, string code, long expected)
    {
        long total = -1;
        for (var i = 0; i < 250; i++)
        {
            var j = await ApiHost.ReadJsonAsync(await h.Client.GetAsync($"/api/urls/{code}/analytics"));
            total = j.GetProperty("totalClicks").GetInt64();
            if (total == expected) return total;
            await Task.Delay(20);
        }
        return total;
    }

    [Fact]
    public async Task Create_valid_returns_201_with_fields_and_unique_codes()
    {
        await using var h = await ApiHost.StartAsync();
        var (r1, j1) = await Create(h, new { url = "https://example.com/a" });
        var (r2, j2) = await Create(h, new { url = "https://example.com/b" });
        Assert.Equal(HttpStatusCode.Created, r1.StatusCode);
        var code = j1.GetProperty("code").GetString()!;
        Assert.Equal(7, code.Length);
        Assert.True(code.All(char.IsAsciiLetterOrDigit), "code must be alphanumeric");
        Assert.False(string.IsNullOrEmpty(j1.GetProperty("shortUrl").GetString()));
        Assert.True(j1.TryGetProperty("expiresAt", out var exp));
        Assert.Equal(JsonValueKind.Null, exp.ValueKind);
        Assert.False(string.IsNullOrEmpty(j1.GetProperty("ownerToken").GetString()));
        Assert.NotNull(r1.Headers.Location);
        Assert.True(r1.Headers.Location!.ToString().EndsWith("/api/urls/" + code), "Location must point to metadata");
        Assert.NotEqual(code, j2.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Created, r2.StatusCode);
    }

    [Fact]
    public async Task Create_missing_or_malformed_url_returns_400()
    {
        await using var h = await ApiHost.StartAsync();
        foreach (var body in new object[] { new { }, new { url = "" }, new { url = "not a url" }, new { url = "example.com" } })
        {
            var (r, _) = await Create(h, body);
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            Assert.Contains("json", r.Content.Headers.ContentType?.MediaType ?? "");
        }
    }

    [Fact]
    public async Task Alias_valid_duplicate_and_case_sensitive()
    {
        await using var h = await ApiHost.StartAsync();
        var (r1, j1) = await Create(h, new { url = "https://example.com/a", alias = "my-link" });
        Assert.Equal(HttpStatusCode.Created, r1.StatusCode);
        Assert.Equal("my-link", j1.GetProperty("code").GetString());
        var (r2, _) = await Create(h, new { url = "https://example.com/b", alias = "my-link" });
        Assert.Equal(HttpStatusCode.Conflict, r2.StatusCode);
        var (r3, j3) = await Create(h, new { url = "https://example.com/c", alias = "My-Link" });
        Assert.Equal(HttpStatusCode.Created, r3.StatusCode);
        Assert.Equal("My-Link", j3.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Alias_invalid_length_or_chars_returns_400()
    {
        await using var h = await ApiHost.StartAsync();
        foreach (var alias in new[] { "ab", new string('a', 33), "bad alias", "a$b", "caféx" })
        {
            var (r, _) = await Create(h, new { url = "https://example.com/a", alias });
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        }
        var (ok32, _) = await Create(h, new { url = "https://example.com/a", alias = new string('b', 32) });
        Assert.Equal(HttpStatusCode.Created, ok32.StatusCode);
    }

    [Fact]
    public async Task Expiry_past_rejected_and_future_expires_and_none_never_expires()
    {
        await using var h = await ApiHost.StartAsync();
        var now = h.Time.GetUtcNow();
        var (past, _) = await Create(h, new { url = "https://example.com/a", expiresAt = now.AddMinutes(-5) });
        Assert.Equal(HttpStatusCode.BadRequest, past.StatusCode);

        var (r, j) = await Create(h, new { url = "https://example.com/b", expiresAt = now.AddMinutes(10) });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var code = j.GetProperty("code").GetString()!;
        h.Time.Advance(TimeSpan.FromMinutes(9));
        Assert.Equal(HttpStatusCode.Found, (await h.Client.GetAsync("/r/" + code)).StatusCode);
        var m1 = await ApiHost.ReadJsonAsync(await h.Client.GetAsync("/api/urls/" + code));
        Assert.False(m1.GetProperty("isExpired").GetBoolean());
        h.Time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(HttpStatusCode.Gone, (await h.Client.GetAsync("/r/" + code)).StatusCode);
        var m2 = await ApiHost.ReadJsonAsync(await h.Client.GetAsync("/api/urls/" + code));
        Assert.True(m2.GetProperty("isExpired").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, m2.GetProperty("expiresAt").ValueKind);

        var (_, jn) = await Create(h, new { url = "https://example.com/c" });
        var ncode = jn.GetProperty("code").GetString()!;
        h.Time.Advance(TimeSpan.FromDays(3650));
        Assert.Equal(HttpStatusCode.Found, (await h.Client.GetAsync("/r/" + ncode)).StatusCode);
    }

    [Fact]
    public async Task Unsafe_schemes_userinfo_and_long_urls_rejected()
    {
        await using var h = await ApiHost.StartAsync();
        var urls = new[]
        {
            "javascript:alert(1)", "file:///etc/passwd", "ftp://example.com/x",
            "http://user:pass@example.com/", "https://example.com/" + new string('a', 2100)
        };
        foreach (var u in urls)
        {
            var (r, _) = await Create(h, new { url = u });
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        }
    }

    [Fact]
    public async Task Private_and_loopback_hosts_rejected()
    {
        await using var h = await ApiHost.StartAsync();
        var urls = new[]
        {
            "http://localhost/", "http://127.0.0.1/", "http://10.1.2.3/", "http://172.16.0.1/",
            "http://172.31.255.1/", "http://192.168.1.1/", "http://169.254.169.254/", "http://[::1]/", "http://[fd00::1]/"
        };
        foreach (var u in urls)
        {
            var (r, _) = await Create(h, new { url = u });
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        }
    }

    [Fact]
    public async Task Redirect_and_metadata_behaviour()
    {
        await using var h = await ApiHost.StartAsync();
        var (_, j) = await Create(h, new { url = "https://example.com/target?x=1" });
        var code = j.GetProperty("code").GetString()!;
        var token = j.GetProperty("ownerToken").GetString()!;
        var red = await h.Client.GetAsync("/r/" + code);
        Assert.Equal(HttpStatusCode.Found, red.StatusCode);
        Assert.Equal("https://example.com/target?x=1", red.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await h.Client.GetAsync("/r/zzzzzzz")).StatusCode);

        var metaResp = await h.Client.GetAsync("/api/urls/" + code);
        Assert.Equal(HttpStatusCode.OK, metaResp.StatusCode);
        var raw = await metaResp.Content.ReadAsStringAsync();
        var m = JsonDocument.Parse(raw).RootElement;
        Assert.Equal(code, m.GetProperty("code").GetString());
        Assert.Equal("https://example.com/target?x=1", m.GetProperty("targetUrl").GetString());
        Assert.True(m.TryGetProperty("createdAt", out _));
        Assert.True(m.TryGetProperty("expiresAt", out _));
        Assert.True(m.GetProperty("isActive").GetBoolean());
        Assert.False(m.TryGetProperty("ownerToken", out _));
        Assert.False(raw.Contains(token), "token must not leak");
        Assert.Equal(HttpStatusCode.NotFound, (await h.Client.GetAsync("/api/urls/zzzzzzz")).StatusCode);
    }

    [Fact]
    public async Task Analytics_counts_successful_redirects_only()
    {
        await using var h = await ApiHost.StartAsync();
        var (_, j) = await Create(h, new { url = "https://example.com/a" });
        var code = j.GetProperty("code").GetString()!;
        var token = j.GetProperty("ownerToken").GetString()!;
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Found, (await h.Client.GetAsync("/r/" + code)).StatusCode);
        Assert.Equal(5L, await WaitForClicks(h, code, 5));
        var a = await ApiHost.ReadJsonAsync(await h.Client.GetAsync($"/api/urls/{code}/analytics"));
        Assert.NotEqual(JsonValueKind.Null, a.GetProperty("lastClickAt").ValueKind);
        Assert.True(a.TryGetProperty("dailyClicks", out var daily));
        Assert.Equal("2026-01-01", daily.EnumerateObject().Single().Name);

        Assert.Equal(HttpStatusCode.NotFound, (await h.Client.GetAsync("/r/zzzzzzz")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Delete(h, code, token)).StatusCode);
        Assert.Equal(HttpStatusCode.Gone, (await h.Client.GetAsync("/r/" + code)).StatusCode);
        await Task.Delay(200);
        Assert.Equal(5L, await WaitForClicks(h, code, 5));
        Assert.Equal(HttpStatusCode.NotFound, (await h.Client.GetAsync("/api/urls/zzzzzzz/analytics")).StatusCode);
    }

    [Fact]
    public async Task Delete_with_correct_token_deactivates()
    {
        await using var h = await ApiHost.StartAsync();
        var (_, j) = await Create(h, new { url = "https://example.com/a" });
        var code = j.GetProperty("code").GetString()!;
        var token = j.GetProperty("ownerToken").GetString()!;
        Assert.Equal(HttpStatusCode.NoContent, (await Delete(h, code, token)).StatusCode);
        Assert.Equal(HttpStatusCode.Gone, (await h.Client.GetAsync("/r/" + code)).StatusCode);
        var m = await ApiHost.ReadJsonAsync(await h.Client.GetAsync("/api/urls/" + code));
        Assert.False(m.GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task Delete_missing_wrong_or_unknown_is_rejected()
    {
        await using var h = await ApiHost.StartAsync();
        var (_, j) = await Create(h, new { url = "https://example.com/a" });
        var code = j.GetProperty("code").GetString()!;
        var none = (await Delete(h, code, null)).StatusCode;
        Assert.True(none == HttpStatusCode.Unauthorized || none == HttpStatusCode.Forbidden, "missing token");
        var wrong = (await Delete(h, code, "wrong-token-value")).StatusCode;
        Assert.True(wrong == HttpStatusCode.Unauthorized || wrong == HttpStatusCode.Forbidden, "wrong token");
        var m = await ApiHost.ReadJsonAsync(await h.Client.GetAsync("/api/urls/" + code));
        Assert.True(m.GetProperty("isActive").GetBoolean());
        Assert.Equal(HttpStatusCode.Found, (await h.Client.GetAsync("/r/" + code)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Delete(h, "zzzzzzz", "any-token")).StatusCode);
    }

    [Fact]
    public async Task RateLimit_11th_429_isolated_resets_and_redirects_unthrottled()
    {
        await using var h = await ApiHost.StartAsync();
        string code = "";
        for (var i = 1; i <= 10; i++)
        {
            var (r, j) = await Create(h, new { url = $"https://example.com/p{i}" }, "ip-a");
            Assert.Equal(HttpStatusCode.Created, r.StatusCode);
            if (i == 1) code = j.GetProperty("code").GetString()!;
        }
        var (limited, _) = await Create(h, new { url = "https://example.com/p11" }, "ip-a");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.Contains("Retry-After"), "Retry-After header expected");

        var (other, _) = await Create(h, new { url = "https://example.com/q1" }, "ip-b");
        Assert.Equal(HttpStatusCode.Created, other.StatusCode);

        for (var i = 0; i < 25; i++)
            Assert.Equal(HttpStatusCode.Found, (await h.Client.GetAsync("/r/" + code)).StatusCode);

        h.Time.Advance(TimeSpan.FromSeconds(61));
        var (again, _) = await Create(h, new { url = "https://example.com/p12" }, "ip-a");
        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
    }
}
