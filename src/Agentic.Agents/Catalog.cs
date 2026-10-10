using System.Text.RegularExpressions;

namespace Agentic.Agents;

/// <summary>A capability the URL-shortener domain knows how to reason about.</summary>
public sealed record Capability(
    string Kind,
    string[] Keywords,
    string Text,
    string[] Acceptance,
    string[] ImpactKeywords);

/// <summary>Domain knowledge shared by the deterministic agents (what a URL shortener consists of).</summary>
public static class Catalog
{
    public static readonly IReadOnlyList<Capability> Capabilities = new[]
    {
        new Capability("create",
            new[] { "shorten", "shortener", "create short", "create link", "build a url" },
            "Create a short link for a long URL",
            new[]
            {
                "POST /api/urls returns 201 with a unique short code",
                "Invalid or unsafe target URLs are rejected with 400",
                "Creating the same URL again for the same client returns the existing link"
            },
            new[] { "shortener", "create", "repository", "validator" }),
        new Capability("redirect",
            new[] { "shorten", "shortener", "redirect", "resolve" },
            "Redirect a short code to its target URL",
            new[]
            {
                "GET /{code} answers 302 with the target in Location",
                "Unknown codes answer 404",
                "Expired or deactivated links answer 410"
            },
            new[] { "redirect", "resolve", "endpoints" }),
        new Capability("stats",
            new[] { "stat", "analytic", "click", "track" },
            "Expose click analytics per short link",
            new[]
            {
                "GET /api/urls/{code}/stats returns total clicks, last access and a daily breakdown",
                "Recording clicks never slows down redirects",
                "Unknown codes answer 404"
            },
            new[] { "stats", "click", "analytics" }),
        new Capability("expiry",
            new[] { "expir", "ttl", "time-limited" },
            "Support optional link expiry",
            new[]
            {
                "A link created with expiresInMinutes stops redirecting after that time (410)",
                "Out-of-range expiry values are rejected with 400"
            },
            new[] { "expir" }),
        new Capability("extend",
            new[] { "extend the expir", "extend expir", "extend its expir", "extension" },
            "Allow the link owner to extend the expiry of an existing link",
            new[]
            {
                "PUT /api/urls/{code}/expiry moves the expiry forward for the owner",
                "Non-owners receive 403; unknown codes 404; deactivated links 410",
                "Stats expose the remaining time until expiry"
            },
            new[] { "expir", "owner", "stats" }),
        new Capability("ratelimit",
            new[] { "rate limit", "rate-limit", "throttl", "abuse", "spam" },
            "Rate-limit link creation per client",
            new[]
            {
                "Requests above the per-client quota answer 429 with Retry-After",
                "The quota window resets after it elapses"
            },
            new[] { "ratelimit", "limiter" }),
        new Capability("alias",
            new[] { "alias", "custom code", "vanity" },
            "Allow custom aliases for short links",
            new[]
            {
                "A valid alias becomes the short code",
                "A taken alias answers 409; reserved or malformed aliases answer 400"
            },
            new[] { "alias" }),
        new Capability("delete",
            new[] { "delete", "deactivate", "revoke" },
            "Allow owners to deactivate a link",
            new[]
            {
                "DELETE /api/urls/{code} answers 204 for the owner",
                "Other clients receive 403",
                "Deactivated links answer 410 on redirect"
            },
            new[] { "deactivate", "delete" }),
        new Capability("safety",
            new[] { "block", "private network", "malicious", "ssrf", "unsafe" },
            "Reject unsafe target URLs (non-http schemes, credentials, private/loopback networks)",
            new[]
            {
                "Non-http(s) schemes are rejected",
                "Loopback, private and link-local targets are rejected"
            },
            new[] { "validator", "url" }),
        new Capability("audit",
            new[] { "audit" },
            "Record an audit trail for state-changing operations",
            new[]
            {
                "Create, extend and deactivate operations emit an audit record",
                "Audit records contain no personal data"
            },
            new[] { "audit", "service" })
    };

    /// <summary>Words that make a requirement ambiguous until a human defines measurable criteria.</summary>
    public static readonly IReadOnlyDictionary<string, string> VagueTerms = new Dictionary<string, string>
    {
        ["safer"] = "What does 'safer' mean here: which threats must be mitigated (malicious targets, abuse/spam, link tampering) and against which standard?",
        ["faster"] = "What latency target defines 'faster' (for example p95 redirect latency) and under what load?",
        ["better"] = "Define measurable acceptance criteria for 'better'.",
        ["improve"] = "Define measurable acceptance criteria for 'improve'.",
        ["optimize"] = "Define measurable acceptance criteria for 'optimize'.",
        ["scalable"] = "What load (requests per second, links stored) must the system handle to count as 'scalable'?",
        ["robust"] = "Define measurable acceptance criteria for 'robust' (failure modes and recovery expectations).",
        ["secure"] = "Which security requirements make the system 'secure' (threat model, standard, data classification)?",
        ["user-friendly"] = "Define measurable acceptance criteria for 'user-friendly'."
    };

    public static IReadOnlyList<Capability> Detect(string text)
    {
        var lower = text.ToLowerInvariant();
        return Capabilities.Where(c => c.Keywords.Any(k => lower.Contains(k))).ToList();
    }

    public static IReadOnlyList<Ambiguity> FindAmbiguities(string text) =>
        VagueTerms
            .Where(kv => Regex.IsMatch(text, $@"\b{Regex.Escape(kv.Key)}\b", RegexOptions.IgnoreCase))
            .Select(kv => new Ambiguity(kv.Key, kv.Value))
            .ToList();

    /// <summary>Extracts measurable non-functional targets such as "p95 ... 50 ms" or "500 req/s".</summary>
    public static IEnumerable<string> ExtractNonFunctional(string text)
    {
        foreach (Match m in Regex.Matches(text, @"p(?<p>\d{2})\D{0,40}?(?<ms>\d+)\s*ms", RegexOptions.IgnoreCase))
            yield return $"p{m.Groups["p"].Value} redirect latency <= {m.Groups["ms"].Value} ms";
        foreach (Match m in Regex.Matches(text, @"(?<n>\d+)\s*(req/s|requests per second|rps)", RegexOptions.IgnoreCase))
            yield return $"sustain {m.Groups["n"].Value} requests/second";
    }
}
