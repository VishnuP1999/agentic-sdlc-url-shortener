using System.Text.Json;
using System.Text.RegularExpressions;
using Agentic.Agents;
using Agentic.Core;

namespace Agentic.Acceptance;

public sealed record CheckResult(string Suite, string Name, bool Passed, string Detail, double Seconds = 0);

/// <summary>
/// Acceptance criteria for each scenario, evaluated on the evidence a run leaves behind (artifacts, audit trail,
/// decision lineage, metrics), not on the run's own claim of success. The checks hold for both AI and rule-based
/// agents, so the same suite gates a live SIT run and an offline CI run.
/// </summary>
public static class ScenarioChecks
{
    public static IReadOnlyList<CheckResult> Evaluate(ScenarioRunResult run, bool aiMode, bool executed, string repoRoot)
    {
        var suite = $"scenario.{run.Scenario.Name}";
        var r = run.Result;
        var checks = new List<CheckResult>();
        void Check(string name, bool ok, string detail) => checks.Add(new CheckResult(suite, name, ok, detail));

        Check("status matches expectation", run.MatchesExpectation, $"expected {run.Scenario.ExpectedStatus}, got {r.Status}{(r.StopReason is null ? "" : $" ({r.StopReason})")}");
        Check("audit trail hash chain is valid", r.AuditChainValid, $"{r.Audit.Count} audit entries");
        var succeeded = r.Nodes.Count(n => n.State == NodeState.Succeeded);
        var agentDecisions = r.Decisions.Count(d => d.Actor.StartsWith("agent:"));
        Check("decision lineage recorded for every completed stage", succeeded > 0 && agentDecisions >= succeeded,
            $"{agentDecisions} agent decision(s) for {succeeded} completed stage(s), {r.Decisions.Count} decision(s) in total");
        Check("metrics recorded", r.Metrics.EndToEndMs > 0 && r.Metrics.AttemptsTotal > 0,
            $"latency {r.Metrics.EndToEndMs} ms, attempts {r.Metrics.AttemptsTotal}, retries {r.Metrics.Retries}, rollbacks {r.Metrics.Rollbacks}");
        Check(aiMode ? "AI model was used" : "no model calls in rule-based mode",
            aiMode ? r.Metrics.ModelCalls > 0 : r.Metrics.ModelCalls == 0,
            $"{r.Metrics.ModelCalls} model call(s), cost ${r.Metrics.ModelCostUsd:0.0000}");

        if (run.Scenario.ExpectedStatus == nameof(RunStatus.SafeStopped))
        {
            SafeStop(r, Check);
            return checks;
        }

        Completed(r, aiMode, executed, Check);
        switch (run.Scenario.Name)
        {
            case "greenfield": Greenfield(r, Check); break;
            case "brownfield": Brownfield(r, aiMode, repoRoot, Check); break;
            case "ambiguous": Ambiguous(r, Check); break;
        }
        return checks;
    }

    // ------------------------------------------------------------------ shared expectations of a completed run

    private static void Completed(RunResult r, bool aiMode, bool executed, Action<string, bool, string> Check)
    {
        var notDone = r.Nodes.Where(n => n.State != NodeState.Succeeded).Select(n => $"{n.Id}={n.State}").ToList();
        Check("every stage succeeded", notDone.Count == 0, notDone.Count == 0 ? $"{r.Nodes.Count} stages" : string.Join(", ", notDone));

        var req = Get<NormalizedRequirement>(r, Artifacts.Requirement);
        Check("requirement normalised (no open ambiguity, testable criteria)",
            req is not null && req.OpenAmbiguities.Count == 0 && req.Functional.Count > 0 && req.Functional.All(f => f.Acceptance.Count >= 2),
            req is null ? "missing" : $"{req.Functional.Count} functional, {req.NonFunctional.Count} non-functional, {req.Assumptions.Count} assumption(s)");

        var plan = Get<TaskPlan>(r, Artifacts.Plan);
        var planErrors = plan is null || req is null ? new List<string> { "missing plan" } : PlanValidator.Validate(plan, req).ToList();
        Check("task decomposition is a valid dependency graph", planErrors.Count == 0,
            planErrors.Count == 0 ? $"{plan!.Tasks.Count} tasks, roles {string.Join("/", plan.Tasks.Select(t => t.Role).Distinct())}" : string.Join("; ", planErrors));

        var design = Get<DesignSpec>(r, Artifacts.Design);
        Check("design declares the required security controls",
            design is not null && ArchitectAgent.RequiredControls.All(design.Controls.Contains),
            design is null ? "missing" : string.Join(", ", design.Controls));
        Check("API contract (OpenAPI) and schema produced",
            r.Artifacts.Any(a => a.Name == Artifacts.OpenApi && a.Content.Contains("\"openapi\"")) && r.Artifacts.Any(a => a.Name == Artifacts.Schema && a.Content.Contains("CREATE TABLE")),
            "openapi + schema artifacts");

        var proposal = Get<ProposedChanges>(r, Artifacts.ProposedChanges);
        Check("proposals are not applied by agents", proposal is { Applied: false }, proposal?.Note ?? "missing");

        var tests = Get<TestReport>(r, Artifacts.TestReport);
        if (executed)
        {
            Check("tests executed by the real runner and passed",
                tests is { Executed: true, Passed: true, ExitCode: 0, FailedCount: 0 },
                tests is null ? "missing" : $"mode={tests.Mode}, {tests.PassedCount}/{tests.Total} passed, exit code {tests.ExitCode}");
            Check("every proposed test was actually executed",
                tests is { ProposedTestsNotExecuted.Count: 0 } || tests?.ProposedTestsNotExecuted is null,
                tests is null ? "missing" : $"{tests.ProposedTestsExecuted?.Count ?? 0} executed, not executed: [{string.Join(", ", tests.ProposedTestsNotExecuted ?? Array.Empty<string>())}]");
            var validation = Get<SandboxReport>(r, Artifacts.ValidationReport);
            Check("proposed code built in the sandbox", validation is { Executed: true, BuildSucceeded: true },
                validation is null ? "missing" : SandboxValidator.Describe(validation));
        }
        else
        {
            Check("offline run is labelled unverified", tests is { Mode: "not-executed" }, tests?.Detail ?? "missing");
        }

        var checklist = Get<List<ChecklistItem>>(r, Artifacts.ReleaseChecklist);
        var failing = checklist?.Where(i => !i.Ok).Select(i => i.Item).ToList() ?? new List<string> { "missing checklist" };
        Check("release readiness checklist satisfied", failing.Count == 0, failing.Count == 0 ? $"{checklist!.Count} items" : string.Join("; ", failing));

        var summary = r.Artifacts.FirstOrDefault(a => a.Name == Artifacts.EngineeringSummary)?.Content ?? "";
        var sections = new[] { "Plan and rationale", "Artifacts", "Risks", "Assumptions", "Limitations" };
        var missingSections = sections.Where(s => !summary.Contains(s, StringComparison.OrdinalIgnoreCase)).ToList();
        Check("engineering summary has all required sections", missingSections.Count == 0,
            missingSections.Count == 0 ? string.Join(", ", sections) : $"missing: {string.Join(", ", missingSections)}");

        Check("human approved the release",
            r.Decisions.Any(d => d.NodeId == "release" && d.Actor.StartsWith("human:") && d.Decision == "approved"), "release approval decision");

        // implementation and docs both depend only on design: docs must start before implementation finishes
        var docsStart = r.Audit.FirstOrDefault(e => e.NodeId == "docs" && e.Event == "node.started")?.Seq;
        var implDone = r.Audit.FirstOrDefault(e => e.NodeId == "implementation" && e.Event == "node.succeeded")?.Seq;
        Check("independent stages ran in parallel", docsStart is not null && implDone is not null && docsStart < implDone,
            $"docs started at audit #{docsStart}, implementation finished at #{implDone}");
        Check("security stage waited for both branches (synchronisation)",
            SeqOf(r, "security", "node.started") > Math.Max(SeqOf(r, "tests", "node.succeeded"), SeqOf(r, "docs", "node.succeeded")),
            "security started after tests and docs succeeded");

        if (aiMode)
        {
            var aiArtifacts = r.Artifacts.Where(a => a.ProducedBy.StartsWith("Ai", StringComparison.Ordinal)).Select(a => a.Name).ToList();
            Check("AI agents produced engineering artifacts", aiArtifacts.Count > 0, string.Join(", ", aiArtifacts));
        }
    }

    // ------------------------------------------------------------------ scenario specifics

    private static void Greenfield(RunResult r, Action<string, bool, string> Check)
    {
        var impl = r.Nodes.Single(n => n.Id == "implementation");
        Check("injected failure was retried and rolled back", impl.Attempts >= 2 && r.Metrics.Rollbacks >= 1,
            $"implementation attempts {impl.Attempts}, rollbacks {r.Metrics.Rollbacks}");
        Check("recovery time (MTTR) measured", r.Metrics.MttrMs is not null && r.Metrics.Recoveries >= 1,
            $"MTTR {r.Metrics.MttrMs} ms over {r.Metrics.Recoveries} recovery(ies)");
        var replan = r.Audit.FirstOrDefault(e => e.Event == "replan");
        Check("reviewer comment re-planned only the affected branch",
            replan is not null && replan.Detail.Contains("target=docs") && replan.Detail.Contains("preserved=[") && replan.Detail.Contains("implementation"),
            replan?.Detail ?? "no re-plan");
        var docs = r.Artifacts.FirstOrDefault(a => a.Name == Artifacts.ApiDocs);
        Check("review note addressed in the re-generated docs",
            docs is not null && docs.Version >= 2 && r.ChangeRequests.All(c => c.TargetNode != "docs" || docs.Content.Contains(c.Note)),
            docs is null ? "missing" : $"{docs.Ref}");
    }

    private static void Brownfield(RunResult r, bool aiMode, string repoRoot, Action<string, bool, string> Check)
    {
        var design = Get<DesignSpec>(r, Artifacts.Design);
        var missingFiles = design?.Impact.Where(i => !File.Exists(Path.Combine(repoRoot, i.File))).Select(i => i.File).ToList() ?? new List<string>();
        Check("impact analysis names real files of the existing codebase",
            design is { Impact.Count: > 0 } && missingFiles.Count == 0,
            design is null ? "missing" : $"{string.Join(", ", design.Impact.Select(i => i.File))}{(missingFiles.Count > 0 ? $"; NOT FOUND: {string.Join(", ", missingFiles)}" : "")}");
        Check("schema change detected and gated by a human",
            design is { SchemaChange: true } && r.Decisions.Any(d => d.NodeId == "design" && d.Actor.StartsWith("human:") && d.Decision == "approved"),
            $"schemaChange={design?.SchemaChange}");
        var cr = r.ChangeRequests.FirstOrDefault(c => c.TargetNode == "design");
        Check("change request re-planned from design and was applied",
            cr is not null && design is not null && design.AppliedChangeRequests.Contains(cr.Note) && r.PlanVersion >= 2,
            cr is null ? "no change request" : $"\"{cr.Note}\" -> plan v{r.PlanVersion}");
        var docs = r.Artifacts.FirstOrDefault(a => a.Name == Artifacts.ApiDocs);
        Check("failing docs agent was replaced by its fallback", r.Metrics.Fallbacks >= 1 && docs is not null && !docs.ProducedBy.StartsWith("Ai"),
            $"fallbacks {r.Metrics.Fallbacks}, docs by {docs?.ProducedBy}");
        if (aiMode)
        {
            var proposal = Get<ProposedChanges>(r, Artifacts.ProposedChanges);
            Check("developer proposed real code for the enhancement", proposal is { Files.Count: > 0 },
                proposal is null ? "missing" : string.Join(", ", proposal.Files.Select(f => f.Path)));
        }
    }

    private static readonly Regex SpeedWords = new("fast|latenc|perform|speed|throughput|p95", RegexOptions.IgnoreCase);
    private static readonly Regex SafetyWords = new("saf|threat|malic|abuse|secur|phish|malware|block", RegexOptions.IgnoreCase);

    private static void Ambiguous(RunResult r, Action<string, bool, string> Check)
    {
        Check("ambiguity paused the run for human clarification", r.Metrics.Clarifications >= 1, $"{r.Metrics.Clarifications} clarification round(s)");
        var req = Get<NormalizedRequirement>(r, Artifacts.Requirement);
        Check("answers became explicit assumptions",
            req is not null && req.ClarificationsApplied.Count > 0 && req.Assumptions.Any(a => a.StartsWith("Stakeholder answer")),
            req is null ? "missing" : $"{req.ClarificationsApplied.Count} answer(s) applied, {req.Assumptions.Count} assumption(s)");
        Check("vague wording became measurable non-functional requirements",
            req is not null && req.NonFunctional.Any(n => Regex.IsMatch(n, @"\d")),
            req is null ? "missing" : string.Join(" | ", req.NonFunctional));

        // every answer must have gone to the question it answers
        var pairs = r.Decisions.Where(d => d.Actor == "human" && d.Decision == "clarification provided")
            .SelectMany(d => d.Rationale.Split(" | ")).Select(p => p.Split(" => ", 2)).Where(p => p.Length == 2 && p[1].Trim().Length > 0).ToList();
        var mismatched = pairs.Where(p => (p[1].Contains("p95") && !SpeedWords.IsMatch(p[0])) || (p[1].Contains("malicious") && !SafetyWords.IsMatch(p[0])))
            .Select(p => p[0]).ToList();
        Check("each clarification answer matched its question", pairs.Count > 0 && mismatched.Count == 0,
            mismatched.Count == 0 ? $"{pairs.Count} answered question(s)" : $"mismatched: {string.Join(" / ", mismatched)}");
    }

    private static void SafeStop(RunResult r, Action<string, bool, string> Check)
    {
        Check("critical policy stopped the run", r.Status == RunStatus.SafeStopped && (r.StopReason ?? "").Contains("SEC-001"), r.StopReason ?? "");
        Check("unsafe output was rolled back", !r.Artifacts.Any(a => a.Name == Artifacts.ChangeSet) && r.Audit.Any(e => e.Event == "node.rolledback"),
            "no change-set in the final context");
        Check("nothing was released", r.Nodes.Single(n => n.Id == "release").State != NodeState.Succeeded
                                      && !r.Artifacts.Any(a => a.Name == Artifacts.EngineeringSummary),
            $"release={r.Nodes.Single(n => n.Id == "release").State}");
        Check("violation was not retried", r.Nodes.Single(n => n.Id == "implementation").Attempts == 1, "critical violations are never retried");
    }

    // ------------------------------------------------------------------ helpers

    private static T? Get<T>(RunResult r, string name) where T : class
    {
        var artifact = r.Artifacts.FirstOrDefault(a => a.Name == name);
        if (artifact is null) return null;
        try { return JsonSerializer.Deserialize<T>(artifact.Content, Json.Options); }
        catch (JsonException) { return null; }
    }

    private static long SeqOf(RunResult r, string node, string evt) =>
        r.Audit.LastOrDefault(e => e.NodeId == node && e.Event == evt)?.Seq ?? long.MaxValue / 2;
}
