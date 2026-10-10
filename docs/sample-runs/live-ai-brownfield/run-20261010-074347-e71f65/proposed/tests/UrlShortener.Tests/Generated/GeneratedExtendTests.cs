using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Xunit;

namespace UrlShortener.Tests.Generated;

public class GeneratedExtendTests
{
    private const long MaxSeconds = 60L * 24 * 365 * 60;

    private static string NewClient() => "c" + Guid.NewGuid().ToString("N").Substring(0, 12);

    private static async Task<string> Link(ApiHost host, string client, int? minutes)
    {
        object body = minutes is null
            ? new { url = "https://example.com/p/" + Guid.NewGuid().ToString("N") }
            : new { url = "https://example.com/p/" + Guid.NewGuid().ToString("N"), expiresInMinutes = minutes.Value };
        var resp = await host.CreateAsync(body, client);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var json = await ApiHost.ReadJsonAsync(resp);
        return json.GetProperty("code").GetString()!;
    }

    private static async Task<HttpResponseMessage> Send(ApiHost host, HttpMethod method, string path, string? client, string? body = null)
    {
        var req = new HttpRequestMessage(method, path);
        if (client is not null) req.Headers.Add("X-Client-Id", client);
        if (body is not null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await host.Client.SendAsync(req);
    }

    private static Task<HttpResponseMessage> Extend(ApiHost host, string code, string? client, string? body) =>
        Send(host, HttpMethod.Post, $"/api/urls/{code}/extend", client, body);

    private static async Task<DateTimeOffset?> Expiry(ApiHost host, string code)
    {
        var json = await ApiHost.ReadJsonAsync(await host.Client.GetAsync($"/api/urls/{code}"));
        var p = json.GetProperty("expiresAt");
        return p.ValueKind == JsonValueKind.Null ? null : p.GetDateTimeOffset();
    }

    [Fact]
    public async Task Extend_Success_AddsSecondsToExpiry()
    {
        await using var host = await ApiHost.StartAsync();
        var client = NewClient();
        var code = await Link(host, client, 60);
        var before = await Expiry(host, code);
        var resp = await Extend(host, code, client, "{\"additionalSeconds\":120}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await ApiHost.ReadJsonAsync(resp);
        Assert.Equal(before!.Value.AddSeconds(120), json.GetProperty("expiresAt").GetDateTimeOffset());
        Assert.Equal(before.Value.AddSeconds(120), (await Expiry(host, code))!.Value);
    }

    [Fact]
    public async Task Extend_UnknownCode404_WrongOwner403_Unchanged()
    {
        await using var host = await ApiHost.StartAsync();
        var client = NewClient();
        var code = await Link(host, client, 60);
        var before = await Expiry(host, code);
        var nf = await Extend(host, "nosuchcode1", client, "{\"additionalSeconds\":10}");
        Assert.Equal(HttpStatusCode.NotFound, nf.StatusCode);
        var wrong = await Extend(host, code, NewClient(), "{\"additionalSeconds\":10}");
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        var none = await Extend(host, code, null, "{\"additionalSeconds\":10}");
        Assert.Equal(HttpStatusCode.Forbidden, none.StatusCode);
        Assert.Equal(before, await Expiry(host, code));
    }

    [Fact]
    public async Task Extend_InvalidBodies_Return400_AndLeaveExpiryUnchanged()
    {
        await using var host = await ApiHost.StartAsync();
        var client = NewClient();
        var code = await Link(host, client, 60);
        var before = await Expiry(host, code);
        var bodies = new[] { "{}", "", "{\"additionalSeconds\":0}", "{\"additionalSeconds\":-5}",
            "{\"additionalSeconds\":\"abc\"}", "{\"additionalSeconds\":null}", "{\"additionalSeconds\":1.5}", "not json", "[1]" };
        foreach (var b in bodies)
        {
            var resp = await Extend(host, code, client, b);
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        }
        Assert.Equal(before, await Expiry(host, code));
    }

    [Fact]
    public async Task Extend_CapBoundary_ExactlyMaxOk_OneMoreRejected()
    {
        await using var host = await ApiHost.StartAsync();
        var client = NewClient();
        var code = await Link(host, client, 1);
        var ok = await Extend(host, code, client, $"{{\"additionalSeconds\":{MaxSeconds - 60}}}");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var atCap = await Expiry(host, code);
        Assert.Equal(host.Time.GetUtcNow().AddSeconds(MaxSeconds), atCap!.Value);
        var over = await Extend(host, code, client, "{\"additionalSeconds\":1}");
        Assert.Equal(HttpStatusCode.BadRequest, over.StatusCode);
        Assert.Equal(atCap, await Expiry(host, code));
    }

    [Fact]
    public async Task Extend_SpecialStates_ExpiredRevives_DeactivatedAndNoExpiryConflict()
    {
        await using var host = await ApiHost.StartAsync();
        var client = NewClient();

        var expired = await Link(host, client, 1);
        host.Time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(HttpStatusCode.Gone, (await host.Client.GetAsync($"/r/{expired}")).StatusCode);
        var resp = await Extend(host, expired, client, "{\"additionalSeconds\":300}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(host.Time.GetUtcNow().AddSeconds(300), (await Expiry(host, expired))!.Value);
        Assert.Equal(HttpStatusCode.Found, (await host.Client.GetAsync($"/r/{expired}")).StatusCode);

        var dead = await Link(host, client, 60);
        var before = await Expiry(host, dead);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(host, HttpMethod.Delete, $"/api/urls/{dead}", client)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Extend(host, dead, client, "{\"additionalSeconds\":60}")).StatusCode);
        var meta = await ApiHost.ReadJsonAsync(await host.Client.GetAsync($"/api/urls/{dead}"));
        Assert.False(meta.GetProperty("isActive").GetBoolean());
        Assert.Equal(before, await Expiry(host, dead));

        var forever = await Link(host, client, null);
        Assert.Equal(HttpStatusCode.Conflict, (await Extend(host, forever, client, "{\"additionalSeconds\":60}")).StatusCode);
        Assert.Null(await Expiry(host, forever));
    }

    [Fact]
    public async Task Analytics_RemainingSeconds_FlooredForFuture_AndExistingFieldsPresent()
    {
        await using var host = await ApiHost.StartAsync();
        var client = NewClient();
        var code = await Link(host, client, 10);
        host.Time.Advance(TimeSpan.FromMilliseconds(1500));
        foreach (var route in new[] { "analytics", "stats" })
        {
            var resp = await host.Client.GetAsync($"/api/urls/{code}/{route}");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var json = await ApiHost.ReadJsonAsync(resp);
            Assert.Equal(598L, json.GetProperty("remainingSeconds").GetInt64());
            Assert.Equal(code, json.GetProperty("code").GetString());
            Assert.Equal(JsonValueKind.String, json.GetProperty("targetUrl").ValueKind);
            Assert.Equal(JsonValueKind.String, json.GetProperty("createdAt").ValueKind);
            Assert.Equal(JsonValueKind.String, json.GetProperty("expiresAt").ValueKind);
            Assert.True(json.GetProperty("isActive").GetBoolean());
            Assert.Equal(JsonValueKind.Number, json.GetProperty("totalClicks").ValueKind);
            Assert.True(json.TryGetProperty("lastAccessedAt", out _));
            Assert.Equal(JsonValueKind.Object, json.GetProperty("dailyClicks").ValueKind);
        }
    }

    [Fact]
    public async Task Analytics_RemainingSeconds_ZeroWhenExpired_NullWhenNoExpiry()
    {
        await using var host = await ApiHost.StartAsync();
        var client = NewClient();
        var expiring = await Link(host, client, 1);
        var forever = await Link(host, client, null);
        host.Time.Advance(TimeSpan.FromMinutes(5));
        var a = await ApiHost.ReadJsonAsync(await host.Client.GetAsync($"/api/urls/{expiring}/analytics"));
        Assert.Equal(0L, a.GetProperty("remainingSeconds").GetInt64());
        var b = await ApiHost.ReadJsonAsync(await host.Client.GetAsync($"/api/urls/{forever}/analytics"));
        Assert.Equal(JsonValueKind.Null, b.GetProperty("remainingSeconds").ValueKind);
    }

    [Fact]
    public async Task Redirect_StillReturns302_AndCountsClick()
    {
        await using var host = await ApiHost.StartAsync();
        var client = NewClient();
        var code = await Link(host, client, 60);
        Assert.Equal(HttpStatusCode.Found, (await host.Client.GetAsync($"/r/{code}")).StatusCode);
        long clicks = 0;
        for (var i = 0; i < 100 && clicks < 1; i++)
        {
            var json = await ApiHost.ReadJsonAsync(await host.Client.GetAsync($"/api/urls/{code}/analytics"));
            clicks = json.GetProperty("totalClicks").GetInt64();
            if (clicks < 1) await Task.Delay(50);
        }
        Assert.Equal(1L, clicks);
    }

    [Fact]
    public async Task CreateAndDeactivate_ContractsUnchanged()
    {
        await using var host = await ApiHost.StartAsync();
        var client = NewClient();
        var resp = await host.CreateAsync(new { url = "https://example.com/c/" + Guid.NewGuid().ToString("N"), expiresInMinutes = 5 }, client);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var json = await ApiHost.ReadJsonAsync(resp);
        var code = json.GetProperty("code").GetString()!;
        Assert.Contains($"/r/{code}", json.GetProperty("shortUrl").GetString());
        Assert.Equal(JsonValueKind.String, json.GetProperty("targetUrl").ValueKind);
        Assert.Equal(JsonValueKind.String, json.GetProperty("expiresAt").ValueKind);
        Assert.True(json.GetProperty("created").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(host, HttpMethod.Delete, $"/api/urls/{code}", NewClient())).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Send(host, HttpMethod.Delete, $"/api/urls/{code}", client)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(host, HttpMethod.Delete, "/api/urls/nosuchcode1", client)).StatusCode);
    }

    [Fact]
    public async Task Extend_PutAlias_Works()
    {
        await using var host = await ApiHost.StartAsync();
        var client = NewClient();
        var code = await Link(host, client, 30);
        var before = await Expiry(host, code);
        var resp = await Send(host, HttpMethod.Put, $"/api/urls/{code}/expiry", client, "{\"additionalSeconds\":45}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(before!.Value.AddSeconds(45), (await Expiry(host, code))!.Value);
        var bad = await Send(host, HttpMethod.Put, $"/api/urls/{code}/expiry", client, "{\"additionalSeconds\":0}");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Extend_Concurrent_LosesNoUpdates_AndCapEnforced()
    {
        await using var host = await ApiHost.StartAsync(o => o.RateLimitPerWindow = 10000);
        var client = NewClient();
        var code = await Link(host, client, 60);
        var before = await Expiry(host, code);
        var results = await Task.WhenAll(Enumerable.Range(0, 50)
            .Select(_ => Extend(host, code, client, "{\"additionalSeconds\":10}")));
        Assert.True(results.All(r => r.StatusCode == HttpStatusCode.OK), "all extends should succeed");
        Assert.Equal(before!.Value.AddSeconds(500), (await Expiry(host, code))!.Value);

        var capped = await Link(host, client, 1);
        var setup = await Extend(host, capped, client, $"{{\"additionalSeconds\":{MaxSeconds - 60 - 250}}}");
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        var res2 = await Task.WhenAll(Enumerable.Range(0, 50)
            .Select(_ => Extend(host, capped, client, "{\"additionalSeconds\":10}")));
        Assert.Equal(25, res2.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(25, res2.Count(r => r.StatusCode == HttpStatusCode.BadRequest));
        Assert.Equal(host.Time.GetUtcNow().AddSeconds(MaxSeconds), (await Expiry(host, capped))!.Value);
    }
}
