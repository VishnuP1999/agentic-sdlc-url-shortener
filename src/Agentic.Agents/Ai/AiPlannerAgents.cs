using Agentic.Core;
using Agentic.Model;

namespace Agentic.Agents.Ai;

// ====================================================================== requirements

public sealed record AiFunctionalRequirement(string Kind, string Text, List<string> Acceptance);

public sealed record AiRequirementOutput(
    string Goal,
    List<AiFunctionalRequirement> Functional,
    List<string> NonFunctional,
    List<Ambiguity> OpenAmbiguities,
    List<string> Assumptions);

/// <summary>Planner, part 1: interprets the requirement, finds material ambiguity, writes acceptance criteria and assumptions.</summary>
public sealed class AiRequirementsAgent : AiAgent<AiRequirementOutput>
{
    public static readonly string[] Kinds =
        { "create", "redirect", "metadata", "stats", "expiry", "extend", "ratelimit", "alias", "delete", "safety", "audit", "other" };

    public AiRequirementsAgent(IModelClient model) : base(model) { }

    public override string Name => "AiRequirementsAgent";
    protected override string Role => "planner";

    protected override string SystemPrompt => """
        Role: PLANNER (requirements analysis). Turn a stakeholder requirement into a precise engineering problem.
        - Split it into functional requirements, each with a capability kind and 2-5 testable acceptance criteria that
          name concrete HTTP methods, routes and status codes where relevant.
        - Capability kinds: create (POST /api/urls), redirect (GET /r/{code}), metadata (GET /api/urls/{code}),
          stats (GET /api/urls/{code}/analytics), expiry, extend (owner extends expiry), ratelimit, alias, delete
          (owner deactivates), safety (reject unsafe targets: non-http(s), credentials, private/loopback networks), audit, other.
        - Non-functional requirements must be measurable (numbers and units) or be omitted.
        - Ambiguity: list at most 3 questions, and only for wording whose interpretation would materially change the design
          and cannot be covered by a conservative, low-risk assumption. Otherwise record an explicit assumption instead.
        - Questions already asked must never be repeated. An empty answer means the stakeholder declined: record a
          conservative assumption labelled "Assumed (no answer):" instead of asking again.
        - Use stakeholder answers to make requirements measurable.
        """;

    protected override string Schema => SchemaTemplate.Replace("__KINDS__", string.Join(",", Kinds.Select(k => $"\"{k}\"")));

    private const string SchemaTemplate = """
        {"type":"object","additionalProperties":false,"required":["goal","functional","nonFunctional","openAmbiguities","assumptions"],
         "properties":{
          "goal":{"type":"string"},
          "functional":{"type":"array","minItems":1,"items":{"type":"object","additionalProperties":false,"required":["kind","text","acceptance"],
            "properties":{"kind":{"type":"string","enum":[__KINDS__]},"text":{"type":"string"},
              "acceptance":{"type":"array","minItems":2,"items":{"type":"string"}}}}},
          "nonFunctional":{"type":"array","items":{"type":"string"}},
          "openAmbiguities":{"type":"array","maxItems":3,"items":{"type":"object","additionalProperties":false,"required":["term","question"],
            "properties":{"term":{"type":"string"},"question":{"type":"string"}}}},
          "assumptions":{"type":"array","items":{"type":"string"}}}}
        """;

    protected override string BuildPrompt(AgentRequest request)
    {
        var ctx = request.Context;
        var kind = Kind(ctx);
        var answers = ClarificationFact.Read(ctx);
        var notes = ctx.ChangeRequests().Where(c => c.TargetNode == "requirements").Select(c => c.Note).ToList();

        var prompt = Section("Requirement", $"<requirement>\n{ctx.Requirement}\n</requirement>")
                     + Section("Context", kind == "brownfield"
                         ? "Brownfield: the service already exists (create, redirect, analytics, aliases, expiry, deactivation, rate limiting, URL safety). Capture only what is being asked for; existing behaviour must stay backward compatible."
                         : "Greenfield: a new URL shortener. The core always includes create and redirect.");
        if (answers.Count > 0)
            prompt += Section("Questions already asked, with stakeholder answers",
                "<answers>\n" + string.Join("\n", answers.Select(a => $"Q: {a.Question}\nA: {(a.Answer.Trim().Length == 0 ? "(no answer)" : a.Answer.Trim())}")) + "\n</answers>");
        if (notes.Count > 0)
            prompt += Section("Reviewer change requests", "<review-notes>\n" + string.Join("\n", notes.Select(n => "- " + n)) + "\n</review-notes>");
        return prompt;
    }

    protected override AgentResult ToResult(AiRequirementOutput o, AgentRequest request)
    {
        var ctx = request.Context;
        var kind = Kind(ctx);
        Require(o.Functional.Count > 0, "no functional requirements");
        Require(o.Functional.All(f => f.Acceptance.Count >= 2), "every functional requirement needs at least two acceptance criteria");
        Require(o.Functional.All(f => Kinds.Contains(f.Kind)), $"unknown capability kind (allowed: {string.Join(", ", Kinds)})");
        if (kind == "greenfield")
            Require(o.Functional.Any(f => f.Kind == "create") && o.Functional.Any(f => f.Kind == "redirect"),
                "a greenfield URL shortener must include 'create' and 'redirect' requirements");

        var answers = ClarificationFact.Read(ctx);
        var answered = answers.Where(a => a.Answer.Trim().Length > 0).ToList();
        var asked = answers.Select(a => a.Question.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var open = o.OpenAmbiguities.Where(a => !asked.Contains(a.Question.Trim())).Take(3).ToList();

        var functional = o.Functional.Select((f, i) => new FunctionalRequirement($"FR-{i + 1}", f.Kind, f.Text, f.Acceptance)).ToList();
        var assumptions = o.Assumptions.Concat(answered.Select(a => $"Stakeholder answer: \"{a.Answer.Trim()}\"")).Distinct().ToList();
        var requirement = new NormalizedRequirement(kind, o.Goal.Trim().Length > 0 ? o.Goal.Trim() : ctx.Requirement.Trim(),
            functional, o.NonFunctional, open, assumptions, answered);

        var summary = open.Count == 0
            ? $"{functional.Count} functional requirement(s), {o.NonFunctional.Count} non-functional, {assumptions.Count} assumption(s)"
            : $"{open.Count} open ambiguity(ies): {string.Join(", ", open.Select(a => a.Term))}";
        return new AgentResult(new[] { Artifact.FromJson(Artifacts.Requirement, requirement, Name) }, summary);
    }

    private static string Kind(RunContext ctx) =>
        ctx.GetFact(Facts.Kind) is { Length: > 0 } k ? k : RequirementsAgent.InferKind(ctx.Requirement);
}

// ====================================================================== design + task decomposition

public sealed record AiImpact(string File, string Reason);

public sealed record AiDesignOutput(
    List<string> Components,
    List<EndpointSpec> Endpoints,
    List<string> DataModel,
    bool SchemaChange,
    List<string> Controls,
    List<string> Risks,
    List<DesignDecision> Decisions,
    List<AiImpact> Impact,
    List<string> DataFlows,
    List<string> AppliedChangeRequests,
    List<PlannedTask> Tasks);

/// <summary>Planner, part 2: architecture, API surface, impact analysis on the real codebase, and the task dependency graph.</summary>
public sealed class AiArchitectAgent : AiAgent<AiDesignOutput>
{
    public AiArchitectAgent(IModelClient model) : base(model) { }

    public override string Name => "AiArchitectAgent";
    protected override string Role => "planner";

    protected override string SystemPrompt => $$"""
        Role: PLANNER (design and task decomposition).
        - Design the components, HTTP endpoints (method, path, summary, status codes), data model and risks with trade-offs.
        - Canonical routes: POST /api/urls, GET /r/{code}, GET /api/urls/{code}, GET /api/urls/{code}/analytics, GET /health;
          owner extends expiry: PUT /api/urls/{code}/expiry; owner deactivates: DELETE /api/urls/{code}.
          Every functional requirement must map to at least one endpoint.
          Existing routes (GET /{code}, GET /api/urls/{code}/stats, DELETE /api/urls/{code}) must keep working.
        - Security controls must always include: {{string.Join(", ", ArchitectAgent.RequiredControls)}}; add "audit-logging" when audit is required.
        - Brownfield: base the impact analysis on the source files provided; list only files that exist, with the reason.
          Set schemaChange=true if persisted data changes shape.
        - Echo every reviewer change request verbatim in appliedChangeRequests and reflect it in the design and a decision.
        - Decompose the work into tasks (ids like T1, T2, ...) with role developer, tester or documentation, dependsOn
          (task ids), requirements (FR ids they cover), expectedOutput, risks and a concrete validation criterion.
          Every functional requirement needs at least one developer task and one tester task; testers depend on the
          developer tasks they verify; include one documentation task. Dependencies must form a DAG.
        """;

    protected override string Schema => """
        {"type":"object","additionalProperties":false,
         "required":["components","endpoints","dataModel","schemaChange","controls","risks","decisions","impact","dataFlows","appliedChangeRequests","tasks"],
         "properties":{
          "components":{"type":"array","items":{"type":"string"}},
          "endpoints":{"type":"array","minItems":1,"items":{"type":"object","additionalProperties":false,"required":["method","path","summary","statuses"],
            "properties":{"method":{"type":"string","enum":["GET","POST","PUT","PATCH","DELETE"]},"path":{"type":"string"},"summary":{"type":"string"},
              "statuses":{"type":"array","items":{"type":"integer"}}}}},
          "dataModel":{"type":"array","items":{"type":"string"}},
          "schemaChange":{"type":"boolean"},
          "controls":{"type":"array","items":{"type":"string"}},
          "risks":{"type":"array","items":{"type":"string"}},
          "decisions":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["decision","rationale","alternative"],
            "properties":{"decision":{"type":"string"},"rationale":{"type":"string"},"alternative":{"type":"string"}}}},
          "impact":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["file","reason"],
            "properties":{"file":{"type":"string"},"reason":{"type":"string"}}}},
          "dataFlows":{"type":"array","items":{"type":"string"}},
          "appliedChangeRequests":{"type":"array","items":{"type":"string"}},
          "tasks":{"type":"array","minItems":1,"items":{"type":"object","additionalProperties":false,
            "required":["id","title","role","description","dependsOn","requirements","expectedOutput","risks","validation"],
            "properties":{"id":{"type":"string"},"title":{"type":"string"},"role":{"type":"string","enum":["developer","tester","documentation"]},
              "description":{"type":"string"},"dependsOn":{"type":"array","items":{"type":"string"}},
              "requirements":{"type":"array","items":{"type":"string"}},"expectedOutput":{"type":"string"},
              "risks":{"type":"array","items":{"type":"string"}},"validation":{"type":"string"}}}}}}
        """;

    protected override string BuildPrompt(AgentRequest request)
    {
        var ctx = request.Context;
        var req = ctx.GetJson<NormalizedRequirement>(Artifacts.Requirement);
        var notes = ChangeNotes(ctx);

        var prompt = Section("Normalised requirement", AsJson(req));
        if (notes.Count > 0)
            prompt += Section("Reviewer change requests (apply all, echo verbatim)", "<review-notes>\n" + string.Join("\n", notes.Select(n => "- " + n)) + "\n</review-notes>");

        if (ctx.GetFact(Facts.RepoPath) is { Length: > 0 } repo)
        {
            var map = CodebaseMap.Scan(repo);
            prompt += Section("Existing codebase map (files, types, routes)", string.Join("\n", map.Files.Select(f =>
                $"- {f.RelativePath}: types [{string.Join(", ", f.Types)}]{(f.Endpoints.Count > 0 ? $"; routes [{string.Join(", ", f.Endpoints.Select(e => $"{e.Method} {e.Path}"))}]" : "")}")));
            if (req.Kind == "brownfield")
                prompt += Section("Source of the existing service", Sources(map.Files.Select(f => (f.RelativePath, f.Content))));
        }
        return prompt;
    }

    protected override AgentResult ToResult(AiDesignOutput o, AgentRequest request)
    {
        var ctx = request.Context;
        var req = ctx.GetJson<NormalizedRequirement>(Artifacts.Requirement);
        var notes = ChangeNotes(ctx);
        foreach (var note in notes)
            Require(o.AppliedChangeRequests.Contains(note), $"change request not acknowledged verbatim in appliedChangeRequests: \"{note}\"");

        // Impact analysis is grounded in the real repository: unknown files are rejected, scores come from the code map.
        var impact = new List<ImpactItem>();
        var impactedEndpoints = new List<string>();
        if (ctx.GetFact(Facts.RepoPath) is { Length: > 0 } repo)
        {
            var map = CodebaseMap.Scan(repo);
            var unknown = o.Impact.Where(i => !map.FileExists(i.File)).Select(i => i.File).ToList();
            Require(unknown.Count == 0, $"impact analysis names files that do not exist: {string.Join(", ", unknown)}");
            var ranked = map.Rank(o.Impact.SelectMany(i => i.Reason.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Where(w => w.Length > 3), top: 50, minScore: 0)
                .ToDictionary(r => r.File, r => r.Score);
            impact.AddRange(o.Impact.Select(i => new ImpactItem(i.File, i.Reason, ranked.GetValueOrDefault(i.File, 1.0))));
            var files = impact.Select(i => i.File).ToHashSet();
            impactedEndpoints.AddRange(map.Files.Where(f => files.Contains(f.RelativePath))
                .SelectMany(f => f.Endpoints).Where(e => e.Path != "/health").Select(e => $"{e.Method} {e.Path}").Distinct());
            if (req.Kind == "brownfield") Require(impact.Count > 0, "brownfield design needs an impact analysis of existing files");
        }

        // A model cannot opt out of change control: the deterministic rule also flags schema changes.
        var ruleSchemaChange = req.Kind == "brownfield" && req.Functional.Any(f => ArchitectAgent.SchemaAffectingKinds.Contains(f.Kind));
        var design = new DesignSpec(req.Kind, o.Components, o.Endpoints, o.DataModel, o.SchemaChange || ruleSchemaChange, o.Controls,
            o.Risks, o.Decisions, impact, impactedEndpoints, o.DataFlows, notes);
        var plan = new TaskPlan(o.Tasks);

        return new AgentResult(new[]
        {
            Artifact.FromJson(Artifacts.Design, design, Name),
            Artifact.FromJson(Artifacts.Plan, plan, Name)
        }, $"{o.Endpoints.Count} endpoint(s), {o.Components.Count} component(s), {o.Controls.Count} control(s), {plan.Tasks.Count} task(s)"
           + (impact.Count > 0 ? $", {impact.Count} impacted file(s)" : "") + (design.SchemaChange ? ", schema change" : "")
           + (notes.Count > 0 ? $", {notes.Count} change request(s) applied" : ""));
    }

    private static List<string> ChangeNotes(RunContext ctx) =>
        ctx.ChangeRequests().Where(c => c.TargetNode is "design" or "requirements").Select(c => c.Note).ToList();
}
