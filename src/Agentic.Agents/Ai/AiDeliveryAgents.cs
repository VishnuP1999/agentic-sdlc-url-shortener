using System.Text.RegularExpressions;
using Agentic.Core;
using Agentic.Model;

namespace Agentic.Agents.Ai;

public sealed record AiFile(string Path, string Action, string Summary, string Content);

// ====================================================================== developer

public sealed record AiDeveloperOutput(List<AiFile> Files, List<string> CompatibilityRisks, List<string> AlreadySatisfied, string Summary);

/// <summary>
/// Developer: proposes full-file code changes for the approved plan. It cannot apply them; the orchestrator checks
/// their scope, then builds and tests them in a throw-away sandbox. Failures come back as repair feedback.
/// </summary>
public sealed class AiDeveloperAgent : AiAgent<AiDeveloperOutput>
{
    public AiDeveloperAgent(IModelClient model) : base(model) { }

    public override string Name => "AiDeveloperAgent";
    protected override string Role => "developer";

    protected override string SystemPrompt => $$"""
        Role: DEVELOPER. Propose the code changes that implement the approved design and developer tasks.
        - The repository already contains a working implementation. Change only what the requirement needs; if a requirement
          is already satisfied by existing code, list it in alreadySatisfied instead of rewriting code.
        - Each changed file is returned in full (complete new content, not a diff), action "modify" for existing files or
          "create" for new files. Allowed folders: {{string.Join(", ", ChangeGuard.AllowedRoots)}}; only .cs files.
          Never change project files, tests/UrlShortener.Tests/Program.cs, or delete files.
        - Keep every existing public route, contract and behaviour backward compatible: the existing test suite must still pass.
          You may add tests for your change under tests/UrlShortener.Tests/ using the same test framework.
        - The code must compile on .NET 8 with nullable reference types and implicit usings enabled. Use only the
          framework and the types visible in the provided sources; no new NuGet packages.
        - List compatibility risks explicitly (API contract, data, behaviour, performance).
        """;

    protected override string Schema => """
        {"type":"object","additionalProperties":false,"required":["files","compatibilityRisks","alreadySatisfied","summary"],
         "properties":{
          "files":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["path","action","summary","content"],
            "properties":{"path":{"type":"string"},"action":{"type":"string","enum":["create","modify"]},"summary":{"type":"string"},"content":{"type":"string"}}}},
          "compatibilityRisks":{"type":"array","items":{"type":"string"}},
          "alreadySatisfied":{"type":"array","items":{"type":"string"}},
          "summary":{"type":"string"}}}
        """;

    protected override string BuildPrompt(AgentRequest request)
    {
        var ctx = request.Context;
        var req = ctx.GetJson<NormalizedRequirement>(Artifacts.Requirement);
        var design = ctx.GetJson<DesignSpec>(Artifacts.Design);
        var plan = ctx.GetJson<TaskPlan>(Artifacts.Plan);
        var repo = ctx.GetFact(Facts.RepoPath);

        var prompt = Section("Requirement and acceptance criteria", AsJson(req))
                     + Section("Approved design", AsJson(design))
                     + Section("Developer tasks", AsJson(plan.Tasks.Where(t => t.Role == "developer")));
        if (!string.IsNullOrEmpty(repo))
        {
            prompt += Section("Current product source", Sources(RepoContext.ProductSources(repo)));
            prompt += Section("Existing test files (names only)", string.Join("\n", RepoContext.TestFiles(repo)));
            prompt += Section("Test framework", RepoContext.AssertApi);
        }
        return prompt;
    }

    protected override AgentResult ToResult(AiDeveloperOutput o, AgentRequest request)
    {
        var ctx = request.Context;
        var design = ctx.GetJson<DesignSpec>(Artifacts.Design);
        var repo = ctx.GetFact(Facts.RepoPath);
        Func<string, bool>? exists = string.IsNullOrEmpty(repo) ? null : p => File.Exists(Path.Combine(repo, p));

        var files = o.Files.Select(f => new ProposedFile(f.Path.Replace('\\', '/').Trim(), f.Action, f.Summary, f.Content)).ToList();
        var errors = ChangeGuard.CheckAll(files, exists);
        Require(errors.Count == 0, string.Join("; ", errors));
        Require(files.Count > 0 || o.AlreadySatisfied.Count > 0, "propose at least one change, or explain in alreadySatisfied why none is needed");

        var proposal = new ProposedChanges(files, o.CompatibilityRisks, o.AlreadySatisfied, false,
            "Proposed by the developer agent. NOT applied to the repository; validated in a sandbox and exported for human review.");
        var items = files.Select(f => new ChangeItem(f.Path, f.Action, f.Summary, exists?.Invoke(f.Path))).ToList();
        foreach (var i in design.Impact.Where(i => items.All(x => x.Path != i.File)))
            items.Add(new ChangeItem(i.File, "existing", $"Impacted but unchanged ({i.Reason})", exists?.Invoke(i.File)));

        return new AgentResult(new[]
        {
            Artifact.FromJson(Artifacts.ChangeSet, new ChangeSet(items), Name),
            Artifact.FromJson(Artifacts.ProposedChanges, proposal, Name),
            Artifact.Create(Artifacts.OpenApi, "json", ImplementationAgent.BuildOpenApi(design), Name),
            Artifact.Create(Artifacts.Schema, "sql", ImplementationAgent.BuildSchema(design), Name)
        }, $"{files.Count} file(s) proposed ({files.Count(f => f.Action == "create")} new), {o.AlreadySatisfied.Count} already satisfied, {o.CompatibilityRisks.Count} compatibility risk(s)");
    }
}

// ====================================================================== tester

public sealed record AiTesterOutput(List<TestCase> TestCases, List<AiFile> TestFiles, List<string> EdgeCases, string Notes);

/// <summary>
/// Tester: designs test cases mapped to acceptance criteria and writes executable tests. Whether they pass is decided
/// only by the real test runner in the sandbox; proposed and executed tests are reported separately.
/// </summary>
public sealed partial class AiTesterAgent : AiAgent<AiTesterOutput>
{
    public const string GeneratedFolder = "tests/UrlShortener.Tests/Generated/";
    public const int MaxTestFiles = 2;
    public const int MaxFacts = 12;

    public AiTesterAgent(IModelClient model) : base(model) { }

    public override string Name => "AiTesterAgent";
    protected override string Role => "tester";

    protected override string SystemPrompt => $$"""
        Role: TESTER. Design and write the tests that prove the acceptance criteria.
        - testCases is the test DESIGN and is checked mechanically: for EVERY requirement id in the coverage checklist,
          include at least one case of type "positive" AND at least one of type "negative" or "edge", with the exact id in
          "requirement". Write testCases first and walk the checklist id by id. Keep descriptions to one line.
          Type is positive, negative, edge or security; requirement "controls" may be used for security-control cases.
        - testFiles is executable CODE and must stay small and focused: at most {{MaxTestFiles}} files and {{MaxFacts}} [Fact] methods in
          total. Prioritise behaviour the developer changed and acceptance criteria the existing test files do not cover.
          New files go under {{GeneratedFolder}} (action "create"), namespace UrlShortener.Tests.Generated,
          exercising the API end to end through UrlShortener.Tests.ApiHost (in-process server) and the service where useful.
          Use distinct class names prefixed with "Generated". Keep tests deterministic: use ApiHost.Time (FakeTimeProvider) instead of
          sleeping, and poll for asynchronously recorded clicks as the existing tests do.
        - Test the code as it will be after the developer's proposed changes (shown below), not as you wish it were.
        - If you are repairing after a failed run: fix defects in your tests (compile errors, wrong expectations about existing
          behaviour). If the failure shows the product violates an acceptance criterion, keep the assertion and explain it in notes.
        """;

    protected override string Schema => """
        {"type":"object","additionalProperties":false,"required":["testCases","testFiles","edgeCases","notes"],
         "properties":{
          "testCases":{"type":"array","minItems":1,"items":{"type":"object","additionalProperties":false,"required":["id","requirement","type","description"],
            "properties":{"id":{"type":"string"},"requirement":{"type":"string"},"type":{"type":"string","enum":["positive","negative","edge","security"]},"description":{"type":"string"}}}},
          "testFiles":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["path","action","summary","content"],
            "properties":{"path":{"type":"string"},"action":{"type":"string","enum":["create"]},"summary":{"type":"string"},"content":{"type":"string"}}}},
          "edgeCases":{"type":"array","items":{"type":"string"}},
          "notes":{"type":"string"}}}
        """;

    protected override string BuildPrompt(AgentRequest request)
    {
        var ctx = request.Context;
        var req = ctx.GetJson<NormalizedRequirement>(Artifacts.Requirement);
        var design = ctx.GetJson<DesignSpec>(Artifacts.Design);
        var plan = ctx.GetJson<TaskPlan>(Artifacts.Plan);
        var proposal = ctx.GetJson<ProposedChanges>(Artifacts.ProposedChanges);
        var repo = ctx.GetFact(Facts.RepoPath);

        var checklist = string.Join("\n", req.Functional.Select(f =>
            $"- [ ] {f.Id} ({f.Kind}): needs >= 1 positive AND >= 1 negative/edge case. Criteria: {string.Join(" | ", f.Acceptance)}"));
        var prompt = Section("Coverage checklist (testCases must satisfy every line)", checklist)
                     + Section("Requirement and acceptance criteria", AsJson(req))
                     + Section("Designed endpoints and controls", AsJson(new { design.Endpoints, design.Controls }))
                     + Section("Tester tasks", AsJson(plan.Tasks.Where(t => t.Role == "tester")))
                     + Section("Developer's proposed changes (will be applied before your tests run)",
                         proposal.Files.Count == 0 ? "(none: existing code is expected to satisfy the requirement)" : Sources(proposal.Files.Select(f => (f.Path, f.Content))));
        if (!string.IsNullOrEmpty(repo))
        {
            prompt += Section("API surface and test helpers", Sources(RepoContext.TestAuthoringContext(repo)));
            prompt += Section("Existing test files (do not duplicate names)", string.Join("\n", RepoContext.TestFiles(repo)));
        }
        prompt += Section("Test framework", RepoContext.AssertApi);
        return prompt;
    }

    [GeneratedRegex(@"\[Fact\]")]
    private static partial Regex FactAttribute();

    protected override AgentResult ToResult(AiTesterOutput o, AgentRequest request)
    {
        var ctx = request.Context;
        var req = ctx.GetJson<NormalizedRequirement>(Artifacts.Requirement);
        var repo = ctx.GetFact(Facts.RepoPath);
        Func<string, bool>? exists = string.IsNullOrEmpty(repo) ? null : p => File.Exists(Path.Combine(repo, p));

        var missing = TestCoverage.Missing(req, o.TestCases);
        Require(missing.Count == 0,
            $"requirements without a positive and a negative/edge case: {string.Join(", ", missing)}. " +
            $"Every one of {string.Join(", ", req.Functional.Select(f => f.Id))} needs both; keep test files small so the design stays complete.");

        var files = o.TestFiles.Select(f => new ProposedFile(f.Path.Replace('\\', '/').Trim(), f.Action, f.Summary, f.Content)).ToList();
        Require(files.All(f => f.Path.StartsWith(GeneratedFolder, StringComparison.Ordinal)), $"test files must be created under {GeneratedFolder}");
        Require(files.All(f => FactAttribute().IsMatch(f.Content)), "every test file must contain at least one [Fact]");
        var facts = files.Sum(f => FactAttribute().Matches(f.Content).Count);
        Require(files.Count <= MaxTestFiles && facts <= MaxFacts,
            $"too much test code ({files.Count} file(s), {facts} [Fact] methods; limit {MaxTestFiles} files and {MaxFacts} facts)");
        var errors = ChangeGuard.CheckAll(files, exists);
        Require(errors.Count == 0, string.Join("; ", errors));

        var proposal = new ProposedChanges(files, Array.Empty<string>(), Array.Empty<string>(), false,
            $"Proposed tests ({ProposedTestNames(files).Count} [Fact] method(s)). Executed only by the orchestrator's sandbox run. {o.Notes}".Trim());

        return new AgentResult(new[]
        {
            Artifact.FromJson(Artifacts.TestPlan, o.TestCases, Name),
            Artifact.FromJson(Artifacts.ProposedTests, proposal, Name)
        }, $"{o.TestCases.Count} test case(s) designed, {files.Count} test file(s) proposed, {o.EdgeCases.Count} edge case(s) identified");
    }

    [GeneratedRegex(@"\bclass\s+(?<cls>\w+)|\[Fact\]\s*public\s+(?:async\s+)?(?:Task|void)\s+(?<m>\w+)")]
    private static partial Regex ClassOrFact();

    /// <summary>"Class.Method" names of the proposed tests, as the test runner prints them.</summary>
    public static IReadOnlyList<string> ProposedTestNames(IEnumerable<ProposedFile> files)
    {
        var names = new List<string>();
        foreach (var f in files)
        {
            string? cls = null;
            foreach (Match m in ClassOrFact().Matches(f.Content))
            {
                if (m.Groups["cls"].Success) cls = m.Groups["cls"].Value;
                else if (cls is not null) names.Add($"{cls}.{m.Groups["m"].Value}");
            }
        }
        return names;
    }
}

// ====================================================================== documentation

public sealed record AiDocsOutput(string Markdown);

/// <summary>Documentation: architecture, setup, API reference, assumptions, risks, trade-offs and limitations.</summary>
public sealed class AiDocsAgent : AiAgent<AiDocsOutput>
{
    public AiDocsAgent(IModelClient model) : base(model) { }

    public override string Name => "AiDocsAgent";
    protected override string Role => "documentation";

    protected override string SystemPrompt => """
        Role: DOCUMENTATION. Write the API documentation for this change as one Markdown document with sections:
        Overview, Architecture, Setup (build: `dotnet build AgenticSdlc.sln`; run: `dotnet run --project src/UrlShortener.Api`;
        tests: `dotnet run --project tests/UrlShortener.Tests`), API reference (a table row for every designed endpoint with the
        exact path in backticks, plus request/response examples for the main endpoints), Security controls, Assumptions,
        Risks and trade-offs, Limitations, and "Review notes addressed" (quote every review note verbatim and explain how it is
        addressed). Document only what the design says; mark anything unverified as such.
        """;

    protected override string Schema => """
        {"type":"object","additionalProperties":false,"required":["markdown"],"properties":{"markdown":{"type":"string"}}}
        """;

    protected override string BuildPrompt(AgentRequest request)
    {
        var ctx = request.Context;
        var notes = ctx.ChangeRequests().Where(c => c.TargetNode == "docs").Select(c => c.Note).ToList();
        var prompt = Section("Requirement", AsJson(ctx.GetJson<NormalizedRequirement>(Artifacts.Requirement)))
                     + Section("Design", AsJson(ctx.GetJson<DesignSpec>(Artifacts.Design)))
                     + Section("Task plan", AsJson(ctx.GetJson<TaskPlan>(Artifacts.Plan)));
        if (notes.Count > 0)
            prompt += Section("Review notes (quote each verbatim)", "<review-notes>\n" + string.Join("\n", notes.Select(n => "- " + n)) + "\n</review-notes>");
        return prompt;
    }

    protected override AgentResult ToResult(AiDocsOutput o, AgentRequest request)
    {
        var design = request.Context.GetJson<DesignSpec>(Artifacts.Design);
        Require(o.Markdown.Trim().Length > 200, "documentation is too short");
        var missing = design.Endpoints.Where(e => !o.Markdown.Contains(e.Path, StringComparison.Ordinal)).Select(e => e.Path).ToList();
        Require(missing.Count == 0, $"documentation misses endpoint(s): {string.Join(", ", missing)}");
        return new AgentResult(new[] { Artifact.Create(Artifacts.ApiDocs, "markdown", o.Markdown, Name) },
            $"{design.Endpoints.Count} endpoint(s) documented, {o.Markdown.Length} characters");
    }
}
