using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using UrlShortener.Api;

namespace Agentic.Acceptance;

/// <summary>
/// End-to-end smoke test of the product: starts the real API on a free local port and exercises the user journey
/// (shorten a link, follow it, read analytics) plus the main safety behaviours, over real HTTP.
/// </summary>
public static class ProductSmoke
{
    private const string Suite = "product.url-shortener";

    public static async Task<IReadOnlyList<CheckResult>> RunAsync()
    {
        var results = new List<CheckResult>();
        var app = AppHost.Build(new[] { "--urls", "http://127.0.0.1:0", "--Logging:LogLevel:Default=Warning", "--Logging:LogLevel:Microsoft=Warning" });
        await app.StartAsync();
        try
        {
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(app.Urls.First()) };
            string? code = null;
            const string target = "https://example.com/products/2026/very/long/path?campaign=sit";

            async Task Step(string name, Func<Task<string>> body)
            {
                var clock = Stopwatch.StartNew();
                try
                {
                    results.Add(new CheckResult(Suite, name, true, await body(), clock.Elapsed.TotalSeconds));
                }
                catch (Exception ex)
                {
                    results.Add(new CheckResult(Suite, name, false, ex.Message, clock.Elapsed.TotalSeconds));
                }
            }

            static void Expect(bool condition, string message)
            {
                if (!condition) throw new InvalidOperationException(message);
            }

            await Step("health endpoint is up", async () =>
            {
                var res = await http.GetAsync("/health");
                Expect(res.StatusCode == HttpStatusCode.OK, $"GET /health -> {(int)res.StatusCode}");
                return "GET /health -> 200";
            });

            await Step("shorten a link", async () =>
            {
                var res = await http.PostAsJsonAsync("/api/urls", new { url = target });
                Expect(res.StatusCode == HttpStatusCode.Created, $"POST /api/urls -> {(int)res.StatusCode}");
                var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
                code = body.GetProperty("code").GetString();
                var shortUrl = body.GetProperty("shortUrl").GetString() ?? "";
                Expect(!string.IsNullOrEmpty(code) && shortUrl.Contains($"/r/{code}"), $"unexpected body {body}");
                Expect(shortUrl.Length < target.Length, "short URL is not shorter than the target");
                return $"201, shortUrl {shortUrl}";
            });

            await Step("short link redirects to the original URL", async () =>
            {
                Expect(code is not null, "no code from the previous step");
                var res = await http.GetAsync($"/r/{code}");
                Expect(res.StatusCode == HttpStatusCode.Redirect && res.Headers.Location?.ToString() == target,
                    $"GET /r/{code} -> {(int)res.StatusCode} Location={res.Headers.Location}");
                return $"302 -> {target}";
            });

            await Step("click analytics are recorded", async () =>
            {
                Expect(code is not null, "no code from the previous step");
                long clicks = 0;
                for (var i = 0; i < 50 && clicks < 1; i++)
                {
                    var stats = JsonDocument.Parse(await http.GetStringAsync($"/api/urls/{code}/analytics")).RootElement;
                    clicks = stats.GetProperty("totalClicks").GetInt64();
                    if (clicks < 1) await Task.Delay(50);
                }
                Expect(clicks == 1, $"totalClicks={clicks}");
                return "totalClicks=1";
            });

            await Step("link metadata is available", async () =>
            {
                var res = await http.GetAsync($"/api/urls/{code}");
                Expect(res.StatusCode == HttpStatusCode.OK, $"GET /api/urls/{code} -> {(int)res.StatusCode}");
                return "200";
            });

            await Step("custom alias works and duplicates are rejected", async () =>
            {
                var alias = $"sit-{Guid.NewGuid():N}"[..12];
                var first = await http.PostAsJsonAsync("/api/urls", new { url = "https://example.com/a", alias });
                var second = await http.PostAsJsonAsync("/api/urls", new { url = "https://example.com/b", alias });
                Expect(first.StatusCode == HttpStatusCode.Created && second.StatusCode == HttpStatusCode.Conflict,
                    $"first {(int)first.StatusCode}, second {(int)second.StatusCode}");
                return "201 then 409";
            });

            await Step("unsafe target URLs are rejected", async () =>
            {
                foreach (var bad in new[] { "http://127.0.0.1/admin", "http://169.254.169.254/latest", "javascript:alert(1)", "not a url" })
                {
                    var res = await http.PostAsJsonAsync("/api/urls", new { url = bad });
                    Expect(res.StatusCode == HttpStatusCode.BadRequest, $"{bad} -> {(int)res.StatusCode}");
                }
                return "loopback, metadata, javascript: and malformed URLs -> 400";
            });

            await Step("unknown codes return 404", async () =>
            {
                var res = await http.GetAsync("/r/doesnotexist");
                Expect(res.StatusCode == HttpStatusCode.NotFound, $"-> {(int)res.StatusCode}");
                return "404";
            });
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
        return results;
    }
}
