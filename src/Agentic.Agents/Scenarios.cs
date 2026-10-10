using System.Text.Json;
using Agentic.Core;
using Agentic.Model;

namespace Agentic.Agents;

/// <summary>A runnable scenario: requirement plus the scripted human decisions and injected faults.</summary>
public sealed record ScenarioSpec(
    string Name,
    string Kind,
    string Description,
    string Requirement,
    string ExpectedStatus,
    IReadOnlyList<string> Approvals,
    IReadOnlyList<string> Clarifications,
    IReadOnlyList<FaultSpec> Faults)
{
    public static ScenarioSpec Load(string path) =>
        JsonSerializer.Deserialize<ScenarioSpec>(File.ReadAllText(path), Json.Options)
        ?? throw new InvalidDataException($"Scenario file '{path}' is empty.");
}

public sealed record ScenarioRunOptions
{
    public string? RepoPath { get; init; }

    /// <summary>Build proposals and run the real test suite in a sandbox (the source of truth for pass/fail).</summary>
    public bool ExecuteTests { get; init; }

    /// <summary>Folder for per-run live files (audit.live.jsonl, state.json).</summary>
    public string? AuditDirectory { get; init; }

    public int RetryBaseDelayMs { get; init; } = 50;
    public IHumanInteraction? Human { get; init; }
    public Func<TimeSpan, CancellationToken, Task>? Delay { get; init; }

    /// <summary>LLM client for AI agents; null runs the deterministic rule-based agents.</summary>
    public IModelClient? Model { get; init; }

    public SandboxValidator? Sandbox { get; init; }

    /// <summary>Per-attempt agent timeout; defaults to 60 s for rule-based agents and 7 min for LLM-backed agents.</summary>
    public TimeSpan? NodeTimeout { get; init; }
}

public sealed record ScenarioRunResult(ScenarioSpec Scenario, RunResult Result, WorkflowGraph Graph)
{
    public bool MatchesExpectation => string.Equals(Result.Status.ToString(), Scenario.ExpectedStatus, StringComparison.OrdinalIgnoreCase);
}

public static class ScenarioRunner
{
    public static async Task<ScenarioRunResult> RunAsync(ScenarioSpec scenario, ScenarioRunOptions? options = null, CancellationToken ct = default)
    {
        options ??= new ScenarioRunOptions();

        var graph = SdlcWorkflow.Build(new SdlcOptions
        {
            Faults = scenario.Faults,
            RetryBaseDelayMs = options.RetryBaseDelayMs,
            // model output may need more than one repair round (compile error, then a failing test); still bounded
            MaxAttempts = options.Model is null ? 3 : 4,
            Model = options.Model,
            Sandbox = options.Sandbox
        });
        var human = options.Human ?? new ScriptedHuman(scenario.Approvals, scenario.Clarifications);

        var ctx = new RunContext(scenario.Requirement);
        if (!string.IsNullOrEmpty(scenario.Kind)) ctx.SetFact(Facts.Kind, scenario.Kind);
        if (!string.IsNullOrEmpty(options.RepoPath)) ctx.SetFact(Facts.RepoPath, options.RepoPath);
        if (options.ExecuteTests) ctx.SetFact(Facts.ExecuteTests, "true");

        var runDir = options.AuditDirectory is null ? null : Path.Combine(options.AuditDirectory, ctx.RunId);
        var engineOptions = new EngineOptions
        {
            AuditFilePath = runDir is null ? null : Path.Combine(runDir, "audit.live.jsonl"),
            StateFilePath = runDir is null ? null : Path.Combine(runDir, "state.json"),
            NodeTimeout = options.NodeTimeout ?? (options.Model is null ? TimeSpan.FromSeconds(60) : TimeSpan.FromMinutes(7)),
            Delay = options.Delay ?? ((d, token) => Task.Delay(d, token))
        };

        var engine = new WorkflowEngine(graph, human, DefaultPolicies.Create(), engineOptions);
        var result = await engine.RunAsync(ctx, ct);
        return new ScenarioRunResult(scenario, result, graph);
    }
}
