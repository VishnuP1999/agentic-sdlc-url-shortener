using Agentic.Agents.Ai;
using Agentic.Core;
using Agentic.Model;

namespace Agentic.Agents;

public sealed record SdlcOptions
{
    public IReadOnlyList<FaultSpec> Faults { get; init; } = Array.Empty<FaultSpec>();
    public int RetryBaseDelayMs { get; init; } = 50;
    public int MaxAttempts { get; init; } = 3;

    /// <summary>When set, LLM-backed agents are primary and the rule-based agents become their fallbacks.</summary>
    public IModelClient? Model { get; init; }

    /// <summary>Runs proposals through the real build and test suite in a sandbox (replaceable in tests).</summary>
    public SandboxValidator? Sandbox { get; init; }
}

/// <summary>
/// The SDLC dependency graph:
/// requirements -> design -> { implementation -> tests | docs } (parallel) -> security (join) -> release (approval).
/// Roles: planner (requirements, design + task plan), developer (implementation), tester (tests), documentation (docs).
/// </summary>
public static class SdlcWorkflow
{
    public static WorkflowGraph Build(SdlcOptions? options = null)
    {
        options ??= new SdlcOptions();
        var retry = new RetryPolicy(options.MaxAttempts, options.RetryBaseDelayMs);
        var model = options.Model;
        var sandbox = options.Sandbox ?? new SandboxValidator();

        IAgent Wrap(string node, IAgent agent)
        {
            var fault = options.Faults.FirstOrDefault(f => string.Equals(f.Node, node, StringComparison.OrdinalIgnoreCase));
            return fault is null ? agent : new FaultInjectingAgent(agent, fault);
        }

        IAgent Primary(string node, IAgent rules, Func<IModelClient, IAgent> ai) => Wrap(node, model is null ? rules : ai(model));

        var graph = new WorkflowGraph();

        graph.Add(new WorkflowNode("requirements", Primary("requirements", new RequirementsAgent(), m => new AiRequirementsAgent(m)))
        {
            Description = "Interpret intent, find ambiguity, normalise into an engineering problem",
            Role = "planner",
            Produces = new[] { Artifacts.Requirement },
            Fallback = model is null ? null : new RequirementsAgent(),
            Retry = retry,
            ExitGate = RequirementsGate
        });

        graph.Add(new WorkflowNode("design", Primary("design", new ArchitectAgent(), m => new AiArchitectAgent(m)))
        {
            Description = "Architecture, API surface, data model, controls, impact analysis and task decomposition",
            Role = "planner",
            Produces = new[] { Artifacts.Design, Artifacts.Plan },
            Fallback = model is null ? null : new ArchitectAgent(),
            DependsOn = new[] { "requirements" },
            Retry = retry,
            EntryGate = RequirementsResolved,
            ExitGate = DesignGate,
            Approval = new ApprovalSpec(
                ApprovalTiming.AfterRun,
                ctx => ctx.TryGetJson<DesignSpec>(Artifacts.Design, out var d) && d is { SchemaChange: true },
                "Schema change requires human approval (change control)",
                ctx =>
                {
                    var d = ctx.GetJson<DesignSpec>(Artifacts.Design);
                    return $"Design {ctx.Get(Artifacts.Design)!.Ref}: {d.Endpoints.Count} endpoint(s); impacted files: {string.Join(", ", d.Impact.Select(i => i.File))}";
                },
                ctx => ctx.GetJson<DesignSpec>(Artifacts.Design).Risks)
        });

        graph.Add(new WorkflowNode("implementation", Primary("implementation", new ImplementationAgent(), m => new AiDeveloperAgent(m)))
        {
            Description = "Proposed code changes, change-set, OpenAPI contract and schema; validated by a sandbox build and test run",
            Role = "developer",
            Produces = new[] { Artifacts.ChangeSet, Artifacts.ProposedChanges, Artifacts.OpenApi, Artifacts.Schema },
            Fallback = model is null ? null : new ImplementationAgent(),
            FallbackWhen = FallbackWhen.OnlyWhenUnavailable,
            DependsOn = new[] { "design" },
            Retry = retry,
            EntryGate = RequirementsResolved,
            ExitGate = ImplementationGate,
            Validation = (ctx, ct) => ValidateImplementationAsync(ctx, sandbox, ct)
        });

        graph.Add(new WorkflowNode("tests", Primary("tests", new TestAgent(), m => new AiTesterAgent(m)))
        {
            Description = "Test plan and proposed tests; the real suite (with proposed code and tests) decides pass/fail",
            Role = "tester",
            Produces = new[] { Artifacts.TestPlan, Artifacts.ProposedTests },
            Fallback = model is null ? null : new TestAgent(),
            FallbackWhen = FallbackWhen.OnlyWhenUnavailable,
            DependsOn = new[] { "implementation" },
            Retry = retry,
            ExitGate = TestsGate,
            Validation = (ctx, ct) => ValidateTestsAsync(ctx, sandbox, ct)
        });

        graph.Add(new WorkflowNode("docs", Primary("docs", new DocsAgent(), m => new AiDocsAgent(m)))
        {
            Description = "API documentation from the design",
            Role = "documentation",
            Produces = new[] { Artifacts.ApiDocs },
            DependsOn = new[] { "design" },
            Retry = retry,
            Fallback = model is null ? new DocsAgent(minimal: true) : new DocsAgent(),
            ExitGate = DocsGate
        });

        graph.Add(new WorkflowNode("security", Wrap("security", new SecurityAgent()))
        {
            Description = "Policy guardrails: security, compliance, change control",
            Role = "security",
            Produces = new[] { Artifacts.SecurityReport },
            DependsOn = new[] { "tests", "docs" },
            Retry = new RetryPolicy(1, options.RetryBaseDelayMs),
            ExitGate = ctx => ctx.TryGetJson<SecurityReport>(Artifacts.SecurityReport, out var r) && r is { Passed: true }
                ? GateResult.Pass("security review passed")
                : GateResult.Fail("security review has blocking findings")
        });

        graph.Add(new WorkflowNode("release", Wrap("release", new ReleaseAgent()))
        {
            Description = "Release readiness checklist and final engineering summary",
            Role = "release",
            Produces = new[] { Artifacts.ReleaseChecklist, Artifacts.EngineeringSummary },
            DependsOn = new[] { "security" },
            Retry = retry,
            EntryGate = ctx =>
                ctx.TryGetJson<TestReport>(Artifacts.TestReport, out var t) && t is { Passed: true }
                && ctx.TryGetJson<SecurityReport>(Artifacts.SecurityReport, out var s) && s is { Passed: true }
                    ? GateResult.Pass("tests and security passed")
                    : GateResult.Fail("release requires passing tests and security review"),
            ExitGate = ctx =>
            {
                var items = ctx.GetJson<List<ChecklistItem>>(Artifacts.ReleaseChecklist);
                var failing = items.Where(i => !i.Ok).Select(i => i.Item).ToList();
                return failing.Count == 0 ? GateResult.Pass("all checklist items satisfied")
                    : GateResult.Fail($"checklist not satisfied: {string.Join("; ", failing)}");
            },
            Approval = new ApprovalSpec(
                ApprovalTiming.BeforeRun,
                _ => true,
                "Release is a high-impact action and needs explicit human approval",
                ctx => $"Tests: {ctx.GetJson<TestReport>(Artifacts.TestReport).Detail} | Docs: {ctx.Get(Artifacts.ApiDocs)?.Ref} | Security: {ctx.Get(Artifacts.SecurityReport)?.Ref}",
                ctx => ctx.GetJson<DesignSpec>(Artifacts.Design).Risks)
        });

        graph.Validate();
        return graph;
    }

    // ------------------------------------------------------------ gates

    private static GateResult RequirementsGate(RunContext ctx)
    {
        var req = ctx.GetJson<NormalizedRequirement>(Artifacts.Requirement);
        if (req.OpenAmbiguities.Count > 0)
            return GateResult.NeedsHuman($"{req.OpenAmbiguities.Count} ambiguous term(s) need a measurable definition",
                req.OpenAmbiguities.Select(a => a.Question));
        return req.Functional.Count > 0 ? GateResult.Pass("requirement normalised") : GateResult.Fail("no functional requirements derived");
    }

    private static GateResult RequirementsResolved(RunContext ctx) =>
        ctx.TryGetJson<NormalizedRequirement>(Artifacts.Requirement, out var r) && r is { OpenAmbiguities.Count: 0 }
            ? GateResult.Pass("requirements resolved")
            : GateResult.Fail("requirements still contain open ambiguities");

    private static GateResult DesignGate(RunContext ctx)
    {
        var d = ctx.GetJson<DesignSpec>(Artifacts.Design);
        var req = ctx.GetJson<NormalizedRequirement>(Artifacts.Requirement);
        if (d.Endpoints.Count == 0) return GateResult.Fail("design has no endpoints");
        foreach (var kind in req.Functional.Select(f => f.Kind).Distinct())
        {
            // Each rule says what is expected, so a failed check is actionable repair feedback for the model.
            var (covered, expected) = kind switch
            {
                "create" => (d.Endpoints.Any(e => e.Method == "POST" && e.Path == "/api/urls"), "POST /api/urls"),
                "redirect" => (d.Endpoints.Any(e => e.Method == "GET" && e.Path is "/{code}" or "/r/{code}"), "GET /r/{code}"),
                "stats" => (d.Endpoints.Any(e => e.Method == "GET" && (e.Path.EndsWith("/stats") || e.Path.EndsWith("/analytics"))),
                    "GET /api/urls/{code}/analytics"),
                "delete" => (d.Endpoints.Any(e => e.Method == "DELETE"), "DELETE /api/urls/{code}"),
                "extend" => (d.Endpoints.Any(e => e.Method is "PUT" or "PATCH" or "POST" && e.Path.Contains("{code}")
                                                  && (e.Path.Contains("expir") || e.Path.Contains("extend"))),
                    "a PUT, PATCH or POST endpoint on /api/urls/{code}/... whose path contains 'expiry' or 'extend' (e.g. PUT /api/urls/{code}/expiry)"),
                _ => (true, "")
            };
            if (!covered)
                return GateResult.Fail($"design does not cover requirement kind '{kind}': add {expected}. " +
                                       $"Designed endpoints: {string.Join(", ", d.Endpoints.Select(e => $"{e.Method} {e.Path}"))}");
        }
        var pending = ctx.ChangeRequests().Where(c => c.TargetNode == "design").Select(c => c.Note)
            .Where(n => !d.AppliedChangeRequests.Contains(n)).ToList();
        if (pending.Count > 0) return GateResult.Fail($"unapplied change request(s): {string.Join("; ", pending)}");

        if (!ctx.TryGetJson<TaskPlan>(Artifacts.Plan, out var plan) || plan is null) return GateResult.Fail("no task plan produced");
        var planErrors = PlanValidator.Validate(plan, req);
        return planErrors.Count == 0
            ? GateResult.Pass($"design covers all requirements; task plan valid ({plan.Tasks.Count} tasks, acyclic)")
            : GateResult.Fail($"task plan invalid: {string.Join("; ", planErrors)}");
    }

    private static GateResult ImplementationGate(RunContext ctx)
    {
        var design = ctx.GetJson<DesignSpec>(Artifacts.Design);
        var changeSet = ctx.GetJson<ChangeSet>(Artifacts.ChangeSet);
        if (changeSet.Items.Count == 0) return GateResult.Fail("change-set is empty");
        if (ctx.Get(Artifacts.OpenApi) is not { } openApi || !design.Endpoints.All(e => openApi.Content.Contains(e.Path)))
            return GateResult.Fail("OpenAPI contract does not cover every designed endpoint");
        if (ctx.GetJson<ProposedChanges>(Artifacts.ProposedChanges) is { Applied: true })
            return GateResult.Fail("proposal claims to be applied; agents may only propose");

        if (ctx.GetFact(Facts.RepoPath) is { Length: > 0 })
        {
            var missing = changeSet.Items.Where(i => i.Action is "modify" or "existing" && i.ExistsInRepo != true).Select(i => i.Path).ToList();
            if (missing.Count > 0) return GateResult.Fail($"planned paths not found in the repository: {string.Join(", ", missing)}");
            var clobber = changeSet.Items.Where(i => i.Action == "create" && i.ExistsInRepo == true).Select(i => i.Path).ToList();
            if (clobber.Count > 0) return GateResult.Fail($"'create' would overwrite existing file(s): {string.Join(", ", clobber)}");
        }
        return GateResult.Pass($"{changeSet.Items.Count} change(s) planned and verified against the repository");
    }

    private static GateResult TestsGate(RunContext ctx)
    {
        var req = ctx.GetJson<NormalizedRequirement>(Artifacts.Requirement);
        var cases = ctx.GetJson<List<TestCase>>(Artifacts.TestPlan);
        var missing = TestCoverage.Missing(req, cases);
        return missing.Count == 0
            ? GateResult.Pass($"{cases.Count} test case(s) cover every requirement with positive and negative/edge cases")
            : GateResult.Fail($"test plan is missing negative or edge coverage for: {string.Join(", ", missing)}");
    }

    private static GateResult DocsGate(RunContext ctx)
    {
        var design = ctx.GetJson<DesignSpec>(Artifacts.Design);
        var docs = ctx.Get(Artifacts.ApiDocs);
        if (docs is null || docs.Content.Length == 0) return GateResult.Fail("documentation is empty");
        var missing = design.Endpoints.Where(e => !docs.Content.Contains(e.Path)).Select(e => e.Path).ToList();
        if (missing.Count > 0) return GateResult.Fail($"documentation misses endpoint(s): {string.Join(", ", missing)}");
        var unaddressed = ctx.ChangeRequests().Where(c => c.TargetNode == "docs").Where(c => !docs.Content.Contains(c.Note)).ToList();
        return unaddressed.Count == 0 ? GateResult.Pass("documentation complete")
            : GateResult.Fail($"documentation does not quote the review note(s): {string.Join("; ", unaddressed.Select(c => c.Note))}");
    }

    // ------------------------------------------------------------ orchestrator-owned validation (real execution)

    private static bool ExecutionEnabled(RunContext ctx, out string repo)
    {
        repo = ctx.GetFact(Facts.RepoPath) ?? "";
        return ctx.GetFact(Facts.ExecuteTests) == "true" && repo.Length > 0;
    }

    /// <summary>Source of the failing tests, appended to the failure so the repair attempt can see what is expected.</summary>
    private static string RepairHint(string repo, IReadOnlyList<ProposedFile> files, SandboxReport report, string guidance)
    {
        var sources = SandboxValidator.FailingTestSources(repo, files, report.FailedTests);
        return sources.Length == 0 ? "" : $"\n\nSource of the failing test(s). {guidance}\n{sources}";
    }

    /// <summary>Developer proposals must build and keep the existing suite green before anyone writes tests against them.</summary>
    private static async Task<GateResult> ValidateImplementationAsync(RunContext ctx, SandboxValidator sandbox, CancellationToken ct)
    {
        if (!ExecutionEnabled(ctx, out var repo))
            return GateResult.Pass("NOT EXECUTED: sandbox validation disabled (offline mode); proposals are unverified");

        var files = ctx.GetJson<ProposedChanges>(Artifacts.ProposedChanges).Files;
        var report = await sandbox.RunAsync(repo, "implementation", files, ct);
        var evidence = Artifact.FromJson(Artifacts.ValidationReport, report, "orchestrator:sandbox");
        return report.Passed
            ? GateResult.Pass($"sandbox: {SandboxValidator.Describe(report)} with {files.Count} proposed file(s) applied", evidence)
            : GateResult.Fail($"sandbox: {SandboxValidator.Describe(report)}{RepairHint(repo, files, report,
                "These existing tests define required behaviour: change the product code so they pass, do not change these tests.")}", evidence);
    }

    /// <summary>
    /// Runs the real suite with the developer's changes and the tester's proposed tests applied. The test report is
    /// built from the runner's output and exit code only; proposed tests that did not run are listed as such.
    /// </summary>
    private static async Task<GateResult> ValidateTestsAsync(RunContext ctx, SandboxValidator sandbox, CancellationToken ct)
    {
        var req = ctx.GetJson<NormalizedRequirement>(Artifacts.Requirement);
        var cases = ctx.GetJson<List<TestCase>>(Artifacts.TestPlan);
        var coverage = TestCoverage.IsComplete(req, cases);
        var devFiles = ctx.GetJson<ProposedChanges>(Artifacts.ProposedChanges).Files;
        var testFiles = ctx.TryGetJson<ProposedChanges>(Artifacts.ProposedTests, out var t) && t is not null ? t.Files : Array.Empty<ProposedFile>();
        var proposedNames = AiTesterAgent.ProposedTestNames(testFiles);

        if (!ExecutionEnabled(ctx, out var repo))
        {
            var unverified = new TestReport("not-executed", cases.Count, coverage, coverage,
                "NOT EXECUTED: offline mode; coverage of the test plan was checked, no test ran.",
                ProposedTestsNotExecuted: proposedNames);
            return GateResult.Pass(unverified.Detail, Artifact.FromJson(Artifacts.TestReport, unverified, "orchestrator:sandbox"));
        }

        var all = devFiles.Concat(testFiles).ToList();
        SandboxReport report;
        var previous = ctx.TryGetJson<SandboxReport>(Artifacts.ValidationReport, out var v) ? v : null;
        if (testFiles.Count == 0 && previous is not null && previous.ChangeHash == SandboxValidator.HashOf(all) && previous.Executed)
            report = previous with { Stage = "tests (reused implementation run: no new tests proposed)" };
        else
            report = await sandbox.RunAsync(repo, "tests", all, ct);

        var executed = report.ExecutedTests.ToHashSet(StringComparer.Ordinal);
        var testReport = new TestReport("executed", cases.Count, coverage, report.Passed && coverage,
            $"Real suite executed: {SandboxValidator.Describe(report)}",
            report.Total, report.PassedCount, report.FailedCount, report.ExitCode, report.FailedTests,
            proposedNames.Where(executed.Contains).ToList(), proposedNames.Where(n => !executed.Contains(n)).ToList());
        var evidence = new[]
        {
            Artifact.FromJson(Artifacts.TestReport, testReport, "orchestrator:sandbox"),
            Artifact.FromJson(Artifacts.ValidationReport, report, "orchestrator:sandbox")
        };

        if (!report.Passed)
            return GateResult.Fail($"sandbox: {SandboxValidator.Describe(report)}{RepairHint(repo, all, report,
                "Fix your proposed tests if they assume behaviour the product does not have; never weaken an assertion that encodes an acceptance criterion.")}", evidence);
        if (testReport.ProposedTestsNotExecuted!.Count > 0)
            return GateResult.Fail($"proposed tests did not run: {string.Join(", ", testReport.ProposedTestsNotExecuted)}", evidence);
        return GateResult.Pass($"{testReport.Detail}; {testReport.ProposedTestsExecuted!.Count} proposed test(s) executed", evidence);
    }
}
