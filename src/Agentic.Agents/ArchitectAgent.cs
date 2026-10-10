using Agentic.Core;

namespace Agentic.Agents;

/// <summary>
/// Produces the design: components, API surface, data model, security controls, risks and decisions.
/// For brownfield work it first reasons about the existing codebase (impact analysis) and honours human change requests.
/// </summary>
public sealed class ArchitectAgent : IAgent
{
    public string Name => "ArchitectAgent";

    public static readonly string[] RequiredControls = { "url-scheme-allowlist", "ssrf-block", "input-limits", "rate-limit" };

    /// <summary>Capabilities that change persisted data in an existing system (schema change => human approval).</summary>
    internal static readonly string[] SchemaAffectingKinds = { "expiry", "extend", "stats", "alias", "audit" };

    public Task<AgentResult> ExecuteAsync(AgentRequest request, CancellationToken ct)
    {
        var ctx = request.Context;
        var req = ctx.GetJson<NormalizedRequirement>(Artifacts.Requirement);
        var kinds = req.Functional.Select(f => f.Kind).ToHashSet();

        // Human change requests aimed at the design (or at the whole plan) become additional requirements.
        var changeNotes = ctx.ChangeRequests()
            .Where(c => c.TargetNode is "design" or "requirements")
            .Select(c => c.Note).ToList();
        var extraCaps = changeNotes.SelectMany(n => Catalog.Detect(n)).Select(c => c.Kind).ToList();
        foreach (var k in extraCaps) kinds.Add(k);

        var endpoints = new List<EndpointSpec>();
        if (kinds.Contains("create")) endpoints.Add(new("POST", "/api/urls", "Create a short link", new[] { 201, 200, 400, 409, 429 }));
        if (kinds.Contains("redirect")) endpoints.Add(new("GET", "/{code}", "Redirect to the target URL", new[] { 302, 404, 410 }));
        if (kinds.Contains("stats")) endpoints.Add(new("GET", "/api/urls/{code}/stats", "Click analytics for a link", new[] { 200, 404 }));
        if (kinds.Contains("delete")) endpoints.Add(new("DELETE", "/api/urls/{code}", "Deactivate a link", new[] { 204, 403, 404 }));
        if (kinds.Contains("extend")) endpoints.Add(new("PUT", "/api/urls/{code}/expiry", "Extend the expiry of a link", new[] { 200, 400, 403, 404, 410 }));
        endpoints.Add(new("GET", "/health", "Liveness probe", new[] { 200 }));

        var components = new List<string> { "Endpoints", "ShortenerService", "UrlRepository", "UrlValidator" };
        if (kinds.Contains("stats")) components.AddRange(new[] { "ClickRecorder", "AnalyticsStore" });
        if (kinds.Contains("ratelimit") || kinds.Contains("create")) components.Add("FixedWindowRateLimiter");
        if (kinds.Contains("expiry") || kinds.Contains("extend")) components.Add("ExpiryPolicy");
        if (kinds.Contains("audit")) components.Add("AuditTrail");

        var dataModel = new List<string> { "short_links(code PK, target_url, created_at, expires_at NULL, is_active, owner_client_id)" };
        if (kinds.Contains("stats")) dataModel.Add("click_events(code, clicked_at)  -- aggregated per day in AnalyticsStore");
        if (kinds.Contains("audit")) dataModel.Add("audit_records(id PK, at, actor, action, code)");

        var controls = new List<string>(RequiredControls);
        if (kinds.Contains("audit")) controls.Add("audit-logging");
        foreach (var note in changeNotes)
            if (note.Contains("encrypt", StringComparison.OrdinalIgnoreCase)) controls.Add("encryption-at-rest");

        var risks = new List<string>
        {
            "Open redirect / phishing abuse: mitigated by scheme allow-list, credential and private-network blocking, rate limiting.",
            "Code enumeration: sequential ids are scrambled by a bijection before Base62 encoding."
        };
        if (kinds.Contains("stats")) risks.Add("Analytics write amplification: clicks are queued and dropped (counted) when the queue is full.");
        if (kinds.Contains("expiry") || kinds.Contains("extend")) risks.Add("Clock skew on multi-instance deployments affects expiry decisions (single UTC time source).");
        if (kinds.Contains("extend")) risks.Add("Ownership is based on a spoofable client id until real authentication exists.");

        var decisions = new List<DesignDecision>
        {
            new("Base62 of a scrambled sequential id", "Collision-free by construction and compact; scrambling avoids trivial enumeration.", "Random codes with collision retries"),
            new("Click events through a bounded queue", "Keeps redirect latency independent of analytics storage.", "Synchronous counter update on the request path"),
            new("Repository abstraction over in-memory storage", "Prototype runs anywhere; SQL can replace it without touching the service.", "EF Core + SQLite from day one")
        };
        decisions.AddRange(changeNotes.Select(n => new DesignDecision($"Applied change request: {n}", "Requested by a human reviewer during approval.", "Defer to a later iteration")));

        // Brownfield: reason about the existing code before proposing changes.
        var impact = new List<ImpactItem>();
        var impactedEndpoints = new List<string>();
        var dataFlows = new List<string>();
        var repoPath = ctx.GetFact(Facts.RepoPath);
        var isBrownfield = req.Kind == "brownfield";
        if (isBrownfield && !string.IsNullOrEmpty(repoPath))
        {
            var map = CodebaseMap.Scan(repoPath);
            var keywords = Catalog.Capabilities.Where(c => kinds.Contains(c.Kind)).SelectMany(c => c.ImpactKeywords);
            impact.AddRange(map.Rank(keywords));

            var impactedFiles = impact.Select(i => i.File).ToHashSet();
            impactedEndpoints.AddRange(map.Files.Where(f => impactedFiles.Contains(f.RelativePath))
                .SelectMany(f => f.Endpoints).Where(e => e.Path != "/health").Select(e => $"{e.Method} {e.Path}").Distinct());

            if (impactedEndpoints.Count > 0 && impactedFiles.Any(f => f.Contains("ShortenerService")))
                dataFlows.AddRange(impactedEndpoints.Select(e => $"{e} -> ShortenerService -> IUrlRepository"));
        }

        var schemaChange = isBrownfield && kinds.Overlaps(SchemaAffectingKinds);

        var design = new DesignSpec(req.Kind, components, endpoints, dataModel, schemaChange, controls, risks, decisions,
            impact, impactedEndpoints, dataFlows, changeNotes);

        var summary = $"{endpoints.Count} endpoint(s), {components.Count} component(s), {controls.Count} control(s)"
                      + (impact.Count > 0 ? $", {impact.Count} impacted file(s)" : "")
                      + (schemaChange ? ", schema change" : "")
                      + (changeNotes.Count > 0 ? $", {changeNotes.Count} change request(s) applied" : "");
        var plan = PlanBuilder.Build(req, design);
        summary += $", {plan.Tasks.Count} planned task(s)";
        return Task.FromResult(new AgentResult(new[]
        {
            Artifact.FromJson(Artifacts.Design, design, Name),
            Artifact.FromJson(Artifacts.Plan, plan, Name)
        }, summary));
    }
}
