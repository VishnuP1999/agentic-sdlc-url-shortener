using Agentic.Agents;
using Agentic.Core;
using Xunit;

namespace Agentic.Tests;

internal static class Repo
{
    public static readonly string Root = Find();

    private static string Find()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AgenticSdlc.sln"))) dir = dir.Parent;
            if (dir is not null) return dir.FullName;
        }
        throw new InvalidOperationException("Repository root (AgenticSdlc.sln) not found.");
    }

    public static ScenarioSpec Scenario(string name) => ScenarioSpec.Load(Path.Combine(Root, "scenarios", $"{name}.json"));

    public static Task<ScenarioRunResult> RunAsync(string name, IHumanInteraction? human = null) =>
        ScenarioRunner.RunAsync(Scenario(name), new ScenarioRunOptions
        {
            RepoPath = Root,
            RetryBaseDelayMs = 0,
            Delay = (_, _) => Task.CompletedTask,
            Human = human
        });
}

public class RequirementsAgentTests
{
    private static async Task<NormalizedRequirement> AnalyseAsync(string text, string? kind = null, params (string q, string a)[] answers)
    {
        var ctx = new RunContext(text);
        if (kind is not null) ctx.SetFact(Facts.Kind, kind);
        if (answers.Length > 0)
            ctx.SetFact(ClarificationFact.Key, Json.Serialize(answers.Select(x => new ClarificationAnswer(x.q, x.a)).ToList()));
        var result = await new RequirementsAgent().ExecuteAsync(new AgentRequest(ctx, "requirements", 1, DefaultPolicies.Create()), default);
        return System.Text.Json.JsonSerializer.Deserialize<NormalizedRequirement>(result.Artifacts[0].Content, Json.Options)!;
    }

    [Fact]
    public async Task Vague_wording_is_flagged_as_ambiguity()
    {
        var r = await AnalyseAsync("Make our links safer and faster.");
        Assert.Equal(2, r.OpenAmbiguities.Count);
        Assert.Contains("safer", r.OpenAmbiguities.Select(a => a.Term));
        Assert.Contains("faster", r.OpenAmbiguities.Select(a => a.Term));
    }

    [Fact]
    public async Task Answers_resolve_ambiguity_and_become_assumptions_and_nfrs()
    {
        var q1 = Catalog.VagueTerms["safer"];
        var q2 = Catalog.VagueTerms["faster"];
        var r = await AnalyseAsync("Make our links safer and faster.", "greenfield",
            (q1, "Block private-network targets and rate limit abusers"), (q2, "p95 redirect latency under 50 ms at 500 req/s"));

        Assert.Empty(r.OpenAmbiguities);
        Assert.Contains("p95 redirect latency <= 50 ms", r.NonFunctional);
        Assert.Contains("sustain 500 requests/second", r.NonFunctional);
        Assert.True(r.Functional.Any(f => f.Kind == "safety"));
        Assert.True(r.Functional.Any(f => f.Kind == "ratelimit"));
        Assert.True(r.Functional.Any(f => f.Kind == "create"), "greenfield always includes the shortener core");
        Assert.True(r.Assumptions.Any(a => a.Contains("p95 redirect latency")));
    }

    [Fact]
    public async Task Brownfield_requirement_only_includes_what_was_asked_for()
    {
        var r = await AnalyseAsync("Enhance the existing service: let link owners extend the expiry of an existing short link and show the remaining time in the stats response.");
        Assert.Equal("brownfield", r.Kind);
        var kinds = r.Functional.Select(f => f.Kind).ToList();
        Assert.Contains("extend", kinds);
        Assert.Contains("stats", kinds);
        Assert.DoesNotContain("create", kinds);
        Assert.DoesNotContain("redirect", kinds);
    }
}

public class AnalysisTests
{
    [Fact]
    public void Codebase_map_finds_types_and_routes_with_group_prefixes()
    {
        var map = CodebaseMap.Scan(Repo.Root);
        Assert.True(map.FileExists("src/UrlShortener.Api/Services/ShortenerService.cs"));

        var endpoints = map.Files.SelectMany(f => f.Endpoints).ToList();
        Assert.Contains(("POST", "/api/urls"), endpoints);
        Assert.Contains(("GET", "/api/urls/{code}/stats"), endpoints);
        Assert.Contains(("GET", "/{code}"), endpoints);
        Assert.True(map.Files.Any(f => f.Types.Contains("ShortenerService")));
    }

    [Fact]
    public void Impact_ranking_prefers_the_files_that_match_the_change()
    {
        var map = CodebaseMap.Scan(Repo.Root);
        var impact = map.Rank(new[] { "expir", "owner", "stats" });
        Assert.NotEmpty(impact);
        Assert.Contains("src/UrlShortener.Api/Services/ShortenerService.cs", impact.Select(i => i.File));
        Assert.True(impact.Select(i => i.Score).SequenceEqual(impact.Select(i => i.Score).OrderByDescending(s => s)));
        Assert.False(impact.Any(i => i.File.EndsWith("Base62.cs")), "unrelated files must not be flagged");
    }

    [Fact]
    public void Policies_detect_secrets_missing_controls_and_pii()
    {
        var ctx = new RunContext("r");
        var secret = ctx.Put(Artifact.Create("code", "text", "var apiKey = \"abcd1234efgh\";", "t"));
        Assert.Single(new NoSecretsPolicy().Evaluate(new PolicyContext(ctx, "n", new[] { secret })));
        var clean = ctx.Put(Artifact.Create("code2", "text", "var x = 1;", "t"));
        Assert.Empty(new NoSecretsPolicy().Evaluate(new PolicyContext(ctx, "n", new[] { clean })));

        var weak = new DesignSpec("greenfield", Array.Empty<string>(),
            new[] { new EndpointSpec("POST", "/api/urls", "Create", new[] { 201 }) }, Array.Empty<string>(), false,
            new[] { "input-limits" }, Array.Empty<string>(), Array.Empty<DesignDecision>(), Array.Empty<ImpactItem>(),
            Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
        var design = ctx.Put(Artifact.FromJson(Artifacts.Design, weak, "t"));
        var violations = new RequiredControlsPolicy().Evaluate(new PolicyContext(ctx, "design", new[] { design })).ToList();
        Assert.Equal(3, violations.Count); // scheme allow-list, ssrf block, rate limit missing
        Assert.True(violations.All(v => v.Severity == Severity.High));

        var pii = ctx.Put(Artifact.Create("docs", "markdown", "contact jane@example.com", "t"));
        Assert.Equal(Severity.Warning, new NoPiiPolicy().Evaluate(new PolicyContext(ctx, "docs", new[] { pii })).Single().Severity);
    }

    [Fact]
    public void Sdlc_graph_has_the_expected_shape()
    {
        var graph = SdlcWorkflow.Build();
        Assert.Equal(7, graph.Nodes.Count);
        Assert.True(graph["implementation"].DependsOn.SequenceEqual(new[] { "design" }));
        Assert.True(graph["tests"].DependsOn.SequenceEqual(new[] { "implementation" }));
        Assert.True(graph["docs"].DependsOn.SequenceEqual(new[] { "design" }));
        Assert.True(graph["security"].DependsOn.OrderBy(x => x).SequenceEqual(new[] { "docs", "tests" }));
        Assert.NotNull(graph["release"].Approval);
        Assert.NotNull(graph["docs"].Fallback);
        Assert.NotNull(graph["implementation"].Validation);
        Assert.NotNull(graph["tests"].Validation);
        Assert.Equal("planner", graph["design"].Role);
        Assert.Equal("developer", graph["implementation"].Role);
        Assert.Equal("tester", graph["tests"].Role);
        Assert.Equal("documentation", graph["docs"].Role);
        Assert.DoesNotContain(Artifacts.TestReport, graph["tests"].Produces!); // only the orchestrator's real run writes the test report
    }
}

public class ScenarioTests
{
    [Fact]
    public async Task Greenfield_recovers_from_a_transient_failure_and_replans_only_the_docs_branch()
    {
        var run = await Repo.RunAsync("greenfield");
        var r = run.Result;

        Assert.True(run.MatchesExpectation, r.StopReason);
        Assert.Equal(2, T.Report(r, "implementation").Attempts);   // injected failure + successful retry
        Assert.Equal(1, T.Report(r, "tests").Attempts);            // unaffected branch preserved
        Assert.Equal(2, T.Report(r, "docs").Attempts);             // re-run after the review comment
        Assert.Equal(2, T.Report(r, "security").Attempts);         // dependent of docs re-evaluated
        Assert.Equal(1, r.Metrics.Replans);
        Assert.Equal(1, r.Metrics.Retries);
        Assert.NotNull(r.Metrics.MttrMs);
        Assert.Equal(2, r.Artifacts.Single(a => a.Name == Artifacts.ApiDocs).Version);
        Assert.Contains("Retry-After", r.Artifacts.Single(a => a.Name == Artifacts.ApiDocs).Content);
        Assert.True(r.AuditChainValid);
        Assert.True(r.Artifacts.Any(a => a.Name == Artifacts.EngineeringSummary));
    }

    [Fact]
    public async Task Brownfield_analyses_impact_gates_the_schema_change_and_replans_from_design()
    {
        var run = await Repo.RunAsync("brownfield");
        var r = run.Result;

        Assert.True(run.MatchesExpectation, r.StopReason);
        var design = System.Text.Json.JsonSerializer.Deserialize<DesignSpec>(
            r.Artifacts.Single(a => a.Name == Artifacts.Design).Content, Json.Options)!;
        Assert.True(design.SchemaChange);
        Assert.Contains("src/UrlShortener.Api/Services/ShortenerService.cs", design.Impact.Select(i => i.File));
        Assert.Contains("audit-logging", design.Controls);
        Assert.Contains("Add audit logging for expiry changes", design.AppliedChangeRequests);
        Assert.Equal(2, r.Artifacts.Single(a => a.Name == Artifacts.Design).Version);
        Assert.Equal(1, T.Report(r, "requirements").Attempts);      // upstream of the change request: preserved
        Assert.True(r.Metrics.Fallbacks >= 1);                       // docs agent failed; template agent took over
        Assert.Equal("MinimalDocsAgent", r.Artifacts.Single(a => a.Name == Artifacts.ApiDocs).ProducedBy);
        Assert.Equal(4, r.Metrics.Approvals);                        // schema x2 (after re-plan) + release x2
        Assert.True(r.AuditChainValid);
    }

    [Fact]
    public async Task Ambiguous_requirement_pauses_for_clarification_then_completes()
    {
        var run = await Repo.RunAsync("ambiguous");
        var r = run.Result;

        Assert.True(run.MatchesExpectation, r.StopReason);
        Assert.Equal(1, r.Metrics.Clarifications);
        Assert.Equal(2, r.Artifacts.Single(a => a.Name == Artifacts.Requirement).Version);
        var requirement = System.Text.Json.JsonSerializer.Deserialize<NormalizedRequirement>(
            r.Artifacts.Single(a => a.Name == Artifacts.Requirement).Content, Json.Options)!;
        Assert.Empty(requirement.OpenAmbiguities);
        Assert.Contains("p95 redirect latency <= 50 ms", requirement.NonFunctional);
        Assert.True(requirement.Assumptions.Any(a => a.StartsWith("Stakeholder answer")));
        Assert.True(r.Decisions.Any(d => d.Actor == "human" && d.Decision == "clarification provided"));
    }

    [Fact]
    public async Task Leaked_credential_triggers_safe_stop_before_release()
    {
        var run = await Repo.RunAsync("safe-stop");
        var r = run.Result;

        Assert.True(run.MatchesExpectation);
        Assert.Equal(RunStatus.SafeStopped, r.Status);
        Assert.Contains("SEC-001", r.StopReason);
        Assert.Equal(NodeState.SafeStopped, T.Report(r, "implementation").State);
        Assert.Equal(NodeState.Skipped, T.Report(r, "release").State);
        Assert.False(r.Artifacts.Any(a => a.Name == Artifacts.ChangeSet), "unsafe output must be rolled back");
        Assert.False(r.Artifacts.Any(a => a.Name == Artifacts.EngineeringSummary));
        Assert.True(r.AuditChainValid);
    }

    [Fact]
    public async Task Without_a_human_decision_the_release_is_never_approved()
    {
        // answers the clarification questions but never records an approval decision
        var run = await Repo.RunAsync("ambiguous", new ScriptedHuman(clarificationAnswers: Repo.Scenario("ambiguous").Clarifications));
        Assert.Equal(RunStatus.SafeStopped, run.Result.Status);
        Assert.Equal(NodeState.SafeStopped, T.Report(run.Result, "release").State);
    }

    [Fact]
    public async Task Policy_blocks_a_design_without_required_security_controls()
    {
        // an architect that "forgets" the abuse-prevention controls must be rejected by the guardrail
        var graph = SdlcWorkflow.Build(new SdlcOptions { RetryBaseDelayMs = 0 });
        var forgetful = new FuncAgent("forgetful", async (req, ct) =>
        {
            var real = await new ArchitectAgent().ExecuteAsync(req, ct);
            var design = System.Text.Json.JsonSerializer.Deserialize<DesignSpec>(real.Artifacts[0].Content, Json.Options)!;
            return new AgentResult(new[] { Artifact.FromJson(Artifacts.Design, design with { Controls = new[] { "input-limits" } }, "forgetful") }, "design");
        });
        var broken = new WorkflowGraph()
            .Add(graph["requirements"])
            .Add(new WorkflowNode("design", forgetful) { DependsOn = new[] { "requirements" }, Retry = new RetryPolicy(2, 0) });

        var ctx = new RunContext("Build a URL shortener service.");
        ctx.SetFact(Facts.Kind, "greenfield");
        var result = await new WorkflowEngine(broken, new ScriptedHuman(), DefaultPolicies.Create(), T.Fast()).RunAsync(ctx);

        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Equal(NodeState.Failed, T.Report(result, "design").State);
        Assert.Equal(2, T.Report(result, "design").Attempts);
        Assert.Contains("required control", T.Report(result, "design").LastError);
    }
}
