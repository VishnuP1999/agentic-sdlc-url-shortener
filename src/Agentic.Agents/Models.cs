using Agentic.Core;

namespace Agentic.Agents;

/// <summary>Artifact names used across the SDLC workflow.</summary>
public static class Artifacts
{
    public const string Requirement = "requirement";
    public const string Design = "design";
    public const string Plan = "plan";
    public const string ChangeSet = "changeset";
    public const string ProposedChanges = "proposed-changes";
    public const string ProposedTests = "proposed-tests";
    public const string ValidationReport = "validation-report";
    public const string OpenApi = "openapi";
    public const string Schema = "schema";
    public const string TestPlan = "test-plan";
    public const string TestReport = "test-report";
    public const string ApiDocs = "api-docs";
    public const string SecurityReport = "security-report";
    public const string ReleaseChecklist = "release-checklist";
    public const string EngineeringSummary = "engineering-summary";
}

/// <summary>Facts the host sets on the run context before it starts.</summary>
public static class Facts
{
    public const string Kind = "kind";              // greenfield | brownfield
    public const string RepoPath = "repoPath";          // root of the codebase (for impact analysis / verification)
    public const string ExecuteTests = "executeTests";  // "true" => proposed changes are built and the real suite is run in a sandbox
}

public sealed record FunctionalRequirement(string Id, string Kind, string Text, IReadOnlyList<string> Acceptance);

public sealed record Ambiguity(string Term, string Question);

public sealed record NormalizedRequirement(
    string Kind,
    string Goal,
    IReadOnlyList<FunctionalRequirement> Functional,
    IReadOnlyList<string> NonFunctional,
    IReadOnlyList<Ambiguity> OpenAmbiguities,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<ClarificationAnswer> ClarificationsApplied);

public sealed record EndpointSpec(string Method, string Path, string Summary, IReadOnlyList<int> Statuses);

public sealed record DesignDecision(string Decision, string Rationale, string Alternative);

public sealed record ImpactItem(string File, string Reason, double Score);

public sealed record DesignSpec(
    string Kind,
    IReadOnlyList<string> Components,
    IReadOnlyList<EndpointSpec> Endpoints,
    IReadOnlyList<string> DataModel,
    bool SchemaChange,
    IReadOnlyList<string> Controls,
    IReadOnlyList<string> Risks,
    IReadOnlyList<DesignDecision> Decisions,
    IReadOnlyList<ImpactItem> Impact,
    IReadOnlyList<string> ImpactedEndpoints,
    IReadOnlyList<string> DataFlows,
    IReadOnlyList<string> AppliedChangeRequests);

/// <summary>One task in the decomposed plan (the planner's output, validated by <see cref="PlanValidator"/>).</summary>
/// <param name="Role">developer | tester | documentation</param>
public sealed record PlannedTask(
    string Id,
    string Title,
    string Role,
    string Description,
    IReadOnlyList<string> DependsOn,
    IReadOnlyList<string> Requirements,
    string ExpectedOutput,
    IReadOnlyList<string> Risks,
    string Validation);

public sealed record TaskPlan(IReadOnlyList<PlannedTask> Tasks);

/// <param name="Action">create (new file) | modify (existing file) | existing (verified, no change proposed)</param>
public sealed record ChangeItem(string Path, string Action, string Summary, bool? ExistsInRepo);

public sealed record ChangeSet(IReadOnlyList<ChangeItem> Items);

/// <summary>A full-file proposal. Agents never write files; only the sandbox validator applies these, to a throw-away copy.</summary>
public sealed record ProposedFile(string Path, string Action, string Summary, string Content);

/// <param name="Applied">Always false: proposals are validated in a sandbox and exported for human review, never applied to the repository.</param>
public sealed record ProposedChanges(
    IReadOnlyList<ProposedFile> Files,
    IReadOnlyList<string> CompatibilityRisks,
    IReadOnlyList<string> AlreadySatisfied,
    bool Applied,
    string Note);

public sealed record TestCase(string Id, string Requirement, string Type, string Description);

/// <summary>Result of building and running the real test suite in a sandbox with proposed files applied.</summary>
public sealed record SandboxReport(
    string Stage,
    string ChangeHash,
    IReadOnlyList<string> AppliedFiles,
    bool Executed,
    bool BuildSucceeded,
    int? ExitCode,
    int? Total,
    int? PassedCount,
    int? FailedCount,
    IReadOnlyList<string> ExecutedTests,
    IReadOnlyList<string> FailedTests,
    IReadOnlyList<string> BuildErrors,
    string OutputTail,
    long DurationMs)
{
    public bool Passed => Executed && BuildSucceeded && ExitCode == 0 && FailedCount == 0;
}

/// <param name="Mode">executed (real test runner output is the source of truth) | not-executed (offline mode, unverified)</param>
public sealed record TestReport(
    string Mode,
    int Planned,
    bool CoverageComplete,
    bool Passed,
    string Detail,
    int? Total = null,
    int? PassedCount = null,
    int? FailedCount = null,
    int? ExitCode = null,
    IReadOnlyList<string>? FailedTests = null,
    IReadOnlyList<string>? ProposedTestsExecuted = null,
    IReadOnlyList<string>? ProposedTestsNotExecuted = null)
{
    public bool Executed => Mode == "executed";
}

public sealed record SecurityReport(bool Passed, IReadOnlyList<string> ControlsVerified, IReadOnlyList<PolicyViolation> Findings);

public sealed record ChecklistItem(string Item, bool Ok, string Evidence);
