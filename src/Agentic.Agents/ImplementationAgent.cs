using System.Text;
using System.Text.Json.Nodes;
using Agentic.Core;

namespace Agentic.Agents;

/// <summary>
/// Turns the design into a reviewable change-set (which files to create or modify), an OpenAPI contract and a
/// database schema. When a repository path is known, every planned path is checked against the real codebase.
/// </summary>
public sealed class ImplementationAgent : IAgent
{
    public string Name => "ImplementationAgent";

    private const string Api = "src/UrlShortener.Api";

    private static readonly IReadOnlyDictionary<string, string[]> ComponentFiles = new Dictionary<string, string[]>
    {
        ["Endpoints"] = new[] { $"{Api}/Endpoints/ShortLinkEndpoints.cs", $"{Api}/Endpoints/Contracts.cs" },
        ["ShortenerService"] = new[] { $"{Api}/Services/ShortenerService.cs", $"{Api}/Services/Results.cs" },
        ["UrlRepository"] = new[] { $"{Api}/Persistence/IUrlRepository.cs", $"{Api}/Persistence/InMemoryUrlRepository.cs" },
        ["UrlValidator"] = new[] { $"{Api}/Validation/UrlValidator.cs", $"{Api}/Validation/AliasValidator.cs" },
        ["ClickRecorder"] = new[] { $"{Api}/Analytics/ClickRecorder.cs" },
        ["AnalyticsStore"] = new[] { $"{Api}/Analytics/AnalyticsStore.cs" },
        ["FixedWindowRateLimiter"] = new[] { $"{Api}/Reliability/FixedWindowRateLimiter.cs" },
        ["ExpiryPolicy"] = new[] { $"{Api}/Domain/ShortLink.cs", $"{Api}/Services/ShortenerService.cs" },
        ["AuditTrail"] = new[] { $"{Api}/Services/AuditTrail.cs" }
    };

    public Task<AgentResult> ExecuteAsync(AgentRequest request, CancellationToken ct)
    {
        var ctx = request.Context;
        var design = ctx.GetJson<DesignSpec>(Artifacts.Design);
        var repoPath = ctx.GetFact(Facts.RepoPath);
        var map = string.IsNullOrEmpty(repoPath) ? null : CodebaseMap.Scan(repoPath);

        var items = new List<ChangeItem>();
        void Add(string path, string summary, string? forcedAction = null)
        {
            if (items.Any(i => i.Path == path)) return;
            bool? exists = map?.FileExists(path);
            // This agent plans paths only; files that already exist are verified rather than re-created.
            var action = forcedAction ?? (exists == true ? (design.Kind == "brownfield" ? "modify" : "existing") : "create");
            items.Add(new ChangeItem(path, action, summary, exists));
        }

        if (design.Kind == "brownfield" && design.Impact.Count > 0)
        {
            foreach (var impact in design.Impact)
                Add(impact.File, $"Adapt for the requested change ({impact.Reason})", "modify");
        }
        foreach (var component in design.Components)
        {
            var files = ComponentFiles.TryGetValue(component, out var f) ? f : new[] { $"{Api}/Services/{component}.cs" };
            foreach (var path in files) Add(path, $"Implement {component}");
        }
        Add("src/UrlShortener.Api/Program.cs", "Compose services and map endpoints");

        var testPath = "tests/UrlShortener.Tests";
        Add($"{testPath}/ServiceTests.cs", "Unit tests for the service logic");
        Add($"{testPath}/ApiIntegrationTests.cs", "End-to-end API tests (in-process host)");

        var changeSet = new ChangeSet(items);
        var openApi = BuildOpenApi(design);
        var schema = BuildSchema(design);

        var proposed = new ProposedChanges(Array.Empty<ProposedFile>(), Array.Empty<string>(), Array.Empty<string>(), false,
            "Rule-based planner: the change-set lists affected paths only; no code is proposed.");

        var created = items.Count(i => i.Action == "create");
        return Task.FromResult(new AgentResult(new[]
        {
            Artifact.FromJson(Artifacts.ChangeSet, changeSet, Name),
            Artifact.FromJson(Artifacts.ProposedChanges, proposed, Name),
            Artifact.Create(Artifacts.OpenApi, "json", openApi, Name),
            Artifact.Create(Artifacts.Schema, "sql", schema, Name)
        }, $"{items.Count} planned path(s) ({created} new, {items.Count - created} existing/modified), OpenAPI with {design.Endpoints.Count} operation(s)"));
    }

    internal static string BuildOpenApi(DesignSpec design)
    {
        var paths = new JsonObject();
        foreach (var e in design.Endpoints)
        {
            var responses = new JsonObject();
            foreach (var status in e.Statuses) responses[status.ToString()] = new JsonObject { ["description"] = StatusText(status) };

            var op = new JsonObject { ["summary"] = e.Summary, ["responses"] = responses };
            if (paths[e.Path] is not JsonObject pathItem) paths[e.Path] = pathItem = new JsonObject();
            pathItem[e.Method.ToLowerInvariant()] = op;
        }

        var doc = new JsonObject
        {
            ["openapi"] = "3.0.3",
            ["info"] = new JsonObject { ["title"] = "URL Shortener API", ["version"] = "1.0.0" },
            ["paths"] = paths,
            ["x-security-controls"] = new JsonArray(design.Controls.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray())
        };
        return doc.ToJsonString(Json.Indented);
    }

    internal static string BuildSchema(DesignSpec design)
    {
        var sb = new StringBuilder("-- Logical data model derived from the design (SQL dialect: ANSI-ish)\n");
        foreach (var entity in design.DataModel) sb.AppendLine($"-- {entity}");
        sb.AppendLine("CREATE TABLE short_links (");
        sb.AppendLine("  code            VARCHAR(32)  NOT NULL PRIMARY KEY,");
        sb.AppendLine("  target_url      VARCHAR(2048) NOT NULL,");
        sb.AppendLine("  created_at      TIMESTAMP    NOT NULL,");
        sb.AppendLine("  expires_at      TIMESTAMP    NULL,");
        sb.AppendLine("  is_active       BOOLEAN      NOT NULL DEFAULT TRUE,");
        sb.AppendLine("  owner_client_id VARCHAR(64)  NOT NULL");
        sb.AppendLine(");");
        sb.AppendLine("CREATE INDEX ix_short_links_owner_target ON short_links (owner_client_id, target_url);");
        if (design.DataModel.Any(d => d.StartsWith("click_events")))
        {
            sb.AppendLine("CREATE TABLE click_events (code VARCHAR(32) NOT NULL, clicked_at TIMESTAMP NOT NULL);");
            sb.AppendLine("CREATE INDEX ix_click_events_code_time ON click_events (code, clicked_at);");
        }
        return sb.ToString();
    }

    private static string StatusText(int status) => status switch
    {
        200 => "OK", 201 => "Created", 204 => "No content", 302 => "Redirect", 400 => "Validation error",
        403 => "Forbidden", 404 => "Not found", 409 => "Conflict", 410 => "Gone", 429 => "Too many requests",
        _ => "Response"
    };
}
