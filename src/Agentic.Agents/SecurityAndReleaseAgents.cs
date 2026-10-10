using System.Text;
using Agentic.Core;

namespace Agentic.Agents;

/// <summary>Runs all policy guardrails over the assembled artifacts and records a security report.</summary>
public sealed class SecurityAgent : IAgent
{
    public string Name => "SecurityAgent";

    public Task<AgentResult> ExecuteAsync(AgentRequest request, CancellationToken ct)
    {
        var ctx = request.Context;
        var design = ctx.GetJson<DesignSpec>(Artifacts.Design);
        var findings = request.Policies.EvaluateAll(ctx, "security");
        var passed = findings.All(f => f.Severity == Severity.Warning);
        var report = new SecurityReport(passed, design.Controls, findings);
        return Task.FromResult(new AgentResult(new[] { Artifact.FromJson(Artifacts.SecurityReport, report, Name) },
            passed
                ? $"{design.Controls.Count} control(s) verified, {findings.Count} warning(s)"
                : $"{findings.Count(f => f.Severity != Severity.Warning)} blocking finding(s)"));
    }
}

/// <summary>Builds the release checklist and the final engineering summary (plan, artifacts, risks, assumptions, limits).</summary>
public sealed class ReleaseAgent : IAgent
{
    public string Name => "ReleaseAgent";

    public Task<AgentResult> ExecuteAsync(AgentRequest request, CancellationToken ct)
    {
        var ctx = request.Context;
        var req = ctx.GetJson<NormalizedRequirement>(Artifacts.Requirement);
        var design = ctx.GetJson<DesignSpec>(Artifacts.Design);
        var changeSet = ctx.GetJson<ChangeSet>(Artifacts.ChangeSet);
        var tests = ctx.GetJson<TestReport>(Artifacts.TestReport);
        var security = ctx.GetJson<SecurityReport>(Artifacts.SecurityReport);
        var docs = ctx.Get(Artifacts.ApiDocs);

        var plan = ctx.GetJson<TaskPlan>(Artifacts.Plan);
        var proposal = ctx.GetJson<ProposedChanges>(Artifacts.ProposedChanges);
        var validation = ctx.TryGetJson<SandboxReport>(Artifacts.ValidationReport, out var v) ? v : null;
        var planErrors = PlanValidator.Validate(plan, req);

        var humanApprovals = ctx.Decisions().Count(d => d.Actor.StartsWith("human:") && d.Decision == "approved");
        var pathsOk = changeSet.Items.All(i => i.Action == "create" ? i.ExistsInRepo != true : i.ExistsInRepo != false);
        var checklist = new List<ChecklistItem>
        {
            new("Requirements resolved (no open ambiguity)", req.OpenAmbiguities.Count == 0, $"{req.Functional.Count} functional requirement(s)"),
            new("Task plan is a valid DAG covering every requirement", planErrors.Count == 0,
                planErrors.Count == 0 ? $"{plan.Tasks.Count} task(s)" : string.Join("; ", planErrors)),
            new("Design declares required security controls", ArchitectAgent.RequiredControls.All(design.Controls.Contains), string.Join(", ", design.Controls)),
            new("Change-set paths verified against the codebase", pathsOk,
                $"{changeSet.Items.Count} path(s); {proposal.Files.Count} file(s) with proposed code"),
            new("Proposed code built and existing suite passed in the sandbox", validation is { Passed: true } || !tests.Executed,
                validation is null ? "NOT EXECUTED (offline mode): unverified" : SandboxValidator.Describe(validation)),
            new("Tests executed by the real test runner and passed", tests.Passed,
                tests.Executed ? tests.Detail : $"UNVERIFIED: {tests.Detail}"),
            new("Documentation generated", docs is not null && docs.Content.Length > 0, docs?.Ref ?? "missing"),
            new("Security and policy review passed", security.Passed, $"{security.Findings.Count} finding(s)"),
            new("Human approval recorded", humanApprovals > 0, $"{humanApprovals} human approval(s)"),
            new("Proposed changes not applied to the repository (exported for review)", !proposal.Applied, proposal.Note)
        };

        var summary = BuildSummary(ctx, req, design, changeSet, tests, security, checklist);
        return Task.FromResult(new AgentResult(new[]
        {
            Artifact.FromJson(Artifacts.ReleaseChecklist, checklist, Name),
            Artifact.Create(Artifacts.EngineeringSummary, "markdown", summary, Name)
        }, $"{checklist.Count(c => c.Ok)}/{checklist.Count} checklist item(s) satisfied"));
    }

    private static string BuildSummary(RunContext ctx, NormalizedRequirement req, DesignSpec design, ChangeSet changeSet,
        TestReport tests, SecurityReport security, IReadOnlyList<ChecklistItem> checklist)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Engineering Summary");
        sb.AppendLine();
        sb.AppendLine($"**Requirement ({req.Kind}):** {req.Goal}");

        sb.AppendLine().AppendLine("## 1. Plan and rationale");
        foreach (var fr in req.Functional) sb.AppendLine($"- {fr.Id}: {fr.Text}");
        foreach (var nfr in req.NonFunctional) sb.AppendLine($"- NFR: {nfr}");
        sb.AppendLine().AppendLine("Key decisions:");
        foreach (var d in design.Decisions) sb.AppendLine($"- **{d.Decision}** - {d.Rationale} (alternative: {d.Alternative})");
        if (design.Impact.Count > 0)
        {
            sb.AppendLine().AppendLine("Impact analysis (existing code):");
            foreach (var i in design.Impact) sb.AppendLine($"- `{i.File}` (score {i.Score}; {i.Reason})");
            foreach (var flow in design.DataFlows) sb.AppendLine($"- data flow: {flow}");
        }
        var changes = ctx.ChangeRequests();
        if (changes.Count > 0)
        {
            sb.AppendLine().AppendLine("Human change requests applied (re-planning):");
            foreach (var c in changes) sb.AppendLine($"- [{c.TargetNode}] {c.Note}");
        }

        sb.AppendLine().AppendLine("## 2. Artifacts");
        foreach (var a in ctx.Latest().Where(a => a.Name != Artifacts.EngineeringSummary))
            sb.AppendLine($"- `{a.Ref}` by {a.ProducedBy} (sha256 {a.Hash[..12]})");
        sb.AppendLine($"- planned changes: {changeSet.Items.Count} ({changeSet.Items.Count(i => i.Action == "create")} new, {changeSet.Items.Count(i => i.Action == "modify")} modified)");

        sb.AppendLine().AppendLine("## 3. Risks, trade-offs and validation");
        foreach (var r in design.Risks) sb.AppendLine($"- Risk: {r}");
        sb.AppendLine($"- Tests: {tests.Detail}");
        sb.AppendLine($"- Security review: {(security.Passed ? "passed" : "FAILED")}; controls: {string.Join(", ", security.Controls())}");
        sb.AppendLine("- Checklist:");
        foreach (var item in checklist) sb.AppendLine($"  - [{(item.Ok ? "x" : " ")}] {item.Item} - {item.Evidence}");

        sb.AppendLine().AppendLine("## 4. Assumptions");
        foreach (var a in req.Assumptions) sb.AppendLine($"- {a}");

        var calls = ctx.Invocations();
        sb.AppendLine().AppendLine("## 5. Agents and verification");
        sb.AppendLine(calls.Count == 0
            ? "- Agents: deterministic, rule-based (no model calls in this run)."
            : $"- Agents: LLM-backed ({string.Join(", ", calls.Select(c => c.Model).Distinct())}); {calls.Count} model call(s), {calls.Count(c => c.Outcome != "ok")} rejected or failed, cost ${calls.Sum(c => c.CostUsd):0.0000}.");
        sb.AppendLine(tests.Executed
            ? $"- Verification: real test runner output (exit code {tests.ExitCode}); {tests.ProposedTestsExecuted?.Count ?? 0} proposed test(s) executed."
            : "- Verification: NOT EXECUTED (offline mode). Test results above are a coverage check only and are unverified.");
        if (tests.ProposedTestsNotExecuted is { Count: > 0 } notRun)
            sb.AppendLine($"- Proposed tests not executed: {string.Join(", ", notRun)}");
        sb.AppendLine("- Proposed code is never applied to the repository by the system; it is exported under `proposed/` for human review.");

        sb.AppendLine().AppendLine("## 6. Limitations");
        sb.AppendLine("- Model output is non-deterministic; gates, policies, sandbox execution and human approval are the safety net, not the model's own claims.");
        sb.AppendLine("- In-memory storage and in-process rate limiting do not scale horizontally (production path: SQL store, shared rate-limit state).");
        sb.AppendLine("- Human approval is scripted or console based; production would integrate a ticketing or review UI.");
        sb.AppendLine("- Client identity is a spoofable header until real authentication is added.");
        sb.AppendLine().AppendLine($"Decision lineage entries: {ctx.Decisions().Count}; artifact versions produced: {ctx.History().Count}.");
        return sb.ToString();
    }
}

internal static class SecurityReportExtensions
{
    public static IReadOnlyList<string> Controls(this SecurityReport report) => report.ControlsVerified;
}
