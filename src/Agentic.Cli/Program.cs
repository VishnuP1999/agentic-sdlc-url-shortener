using System.Text.Json;
using Agentic.Agents;
using Agentic.Core;
using Agentic.Model;

// Usage:
//   dotnet run --project src/Agentic.Cli -- --scenario greenfield|brownfield|ambiguous|safe-stop|all
//   dotnet run --project src/Agentic.Cli -- --requirement "Build a URL shortener ..." --interactive
//   dotnet run --project src/Agentic.Cli -- --check-model
//   dotnet run --project src/Agentic.Cli -- --metrics [--since 2026-10-01]
// Options: --agents ai|rules --no-exec --out <dir> --interactive --scenarios <dir> --repo <dir>

var options = CliOptions.Parse(args);
if (options.ShowHelp)
{
    CliOptions.PrintHelp();
    return 0;
}

var repoRoot = options.Repo ?? RepoLocator.FindRoot();
var scenarioDir = options.ScenariosDir ?? Path.Combine(repoRoot, "scenarios");
var outputDir = options.Out ?? Path.Combine(repoRoot, "out");
var historyIndex = Path.Combine(outputDir, "runs-index.jsonl");

if (options.Metrics)
{
    DateTimeOffset? since = DateTimeOffset.TryParse(options.Since, out var s) ? s : null;
    var metrics = RunHistory.Summarize(RunHistory.Load(historyIndex), since);
    Console.WriteLine($"Reliability metrics from {historyIndex}");
    Console.WriteLine($"  measurement window: {metrics.WindowStart:u} .. {metrics.WindowEnd:u} ({metrics.Runs} run(s))");
    Console.WriteLine(JsonSerializer.Serialize(metrics, Json.Indented));
    return 0;
}

var modelOptions = ModelOptions.FromEnvironment();
var claude = new ClaudeCodeModelClient(modelOptions);

if (options.CheckModel) return await ModelSmokeTest.RunAsync(claude);

AgentModeDecision decision;
try
{
    decision = await AgentModeResolver.ResolveAsync(options.Agents ?? Environment.GetEnvironmentVariable("AGENTIC_AGENTS"), claude);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}
var model = decision.Model;
var agentMode = decision.Mode == AgentMode.Ai ? "ai" : "rules";
Console.WriteLine(decision.Mode == AgentMode.Ai
    ? $"Agents: AI - {decision.Reason}; rule-based agents are the fallback."
    : $"Agents: rule-based - {decision.Reason}.");
Console.WriteLine(options.NoExec
    ? "Validation: OFFLINE (--no-exec). Proposals are NOT built or tested; results are marked unverified."
    : "Validation: proposals are built and the real test suite is run in a throw-away sandbox.");

var scenarios = new List<ScenarioSpec>();
if (options.Requirement is not null)
{
    scenarios.Add(new ScenarioSpec("custom", "", "Custom requirement", options.Requirement, "Completed",
        Array.Empty<string>(), Array.Empty<string>(), Array.Empty<FaultSpec>()));
}
else
{
    var names = options.Scenario is null or "all"
        ? new[] { "greenfield", "brownfield", "ambiguous", "safe-stop" }
        : new[] { options.Scenario };
    foreach (var name in names)
    {
        var path = Path.Combine(scenarioDir, $"{name}.json");
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"Scenario file not found: {path}");
            return 2;
        }
        scenarios.Add(ScenarioSpec.Load(path));
    }
}

var failures = 0;
foreach (var scenario in scenarios)
{
    Console.WriteLine(new string('=', 78));
    Console.WriteLine($"SCENARIO: {scenario.Name}  -  {scenario.Description}");
    Console.WriteLine($"Requirement: {scenario.Requirement}");
    Console.WriteLine(new string('-', 78));

    var run = await ScenarioRunner.RunAsync(scenario, new ScenarioRunOptions
    {
        RepoPath = repoRoot,
        ExecuteTests = !options.NoExec,
        AuditDirectory = outputDir,
        Human = options.Interactive ? new ConsoleHuman() : null,
        Model = model
    });

    var dir = RunOutput.Write(run.Result, run.Graph, outputDir);
    var exported = ProposalExport.Write(run.Result, dir);
    RunHistory.Append(historyIndex, RunHistoryEntry.From(run.Result, scenario.Name, agentMode));
    Console.WriteLine(RunOutput.Summarize(run.Result));
    Console.WriteLine($"  Output written to: {dir}");
    if (exported > 0) Console.WriteLine($"  {exported} proposed file(s) exported for review to: {Path.Combine(dir, "proposed")} (not applied)");
    Console.WriteLine($"  Expected status {scenario.ExpectedStatus}: {(run.MatchesExpectation ? "MATCH" : "MISMATCH")}");
    if (!run.MatchesExpectation) failures++;
}

Console.WriteLine(new string('=', 78));
Console.WriteLine(failures == 0 ? "All scenarios behaved as expected." : $"{failures} scenario(s) did not match the expected outcome.");
return failures == 0 ? 0 : 1;

internal sealed record CliOptions(
    string? Scenario, string? Requirement, string? Out, string? Repo, string? ScenariosDir, string? Agents, string? Since,
    bool NoExec, bool Interactive, bool CheckModel, bool Metrics, bool ShowHelp)
{
    public static CliOptions Parse(string[] args)
    {
        string? Value(string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        return new CliOptions(Value("--scenario"), Value("--requirement"), Value("--out"), Value("--repo"),
            Value("--scenarios"), Value("--agents"), Value("--since"), args.Contains("--no-exec"), args.Contains("--interactive"),
            args.Contains("--check-model"), args.Contains("--metrics"), args.Contains("--help") || args.Contains("-h"));
    }

    public static void PrintHelp() => Console.WriteLine("""
        Agentic SDLC orchestrator for the URL shortener.

          --scenario <name|all>   greenfield | brownfield | ambiguous | safe-stop | all (default: all)
          --requirement "<text>"  run your own requirement instead of a scenario
          --interactive           answer approvals/clarifications on the console instead of the script
          --agents auto|ai|rules  auto (default): Claude if Claude Code is installed and passes a live health check,
                                  otherwise rule-based; ai: always Claude (rule-based fallback per node);
                                  rules: deterministic only (also AGENTIC_AGENTS)
          --no-exec               offline: do not build or run tests in the sandbox (results marked unverified)
          --check-model           live smoke test of the model adapter, then exit
          --metrics [--since <date>]  reliability metrics over recorded runs (out/runs-index.jsonl), then exit
          --out <dir>             output folder (default: <repo>/out)
          --repo <dir>            repository root (default: auto-detected)
          --scenarios <dir>       scenario folder (default: <repo>/scenarios)

        Model configuration (environment): CLAUDE_MODEL (default sonnet), CLAUDE_CODE_PATH, CLAUDE_TIMEOUT_SECONDS,
        CLAUDE_MAX_BUDGET_USD (per call), CLAUDE_EFFORT. Authentication is your Claude Code login; no keys are read or logged.
        """);
}

/// <summary>Writes proposed files under out/&lt;run&gt;/proposed/ so a human can review (and apply) them. Never touches the repository.</summary>
internal static class ProposalExport
{
    public static int Write(RunResult result, string runDirectory)
    {
        var count = 0;
        var root = Path.GetFullPath(Path.Combine(runDirectory, "proposed")) + Path.DirectorySeparatorChar;
        foreach (var artifact in result.Artifacts.Where(a => a.Name is Artifacts.ProposedChanges or Artifacts.ProposedTests))
        {
            var proposal = JsonSerializer.Deserialize<ProposedChanges>(artifact.Content, Json.Options);
            foreach (var file in proposal?.Files ?? Array.Empty<ProposedFile>())
            {
                if (ChangeGuard.Check(file) is not null) continue;
                var target = Path.GetFullPath(Path.Combine(root, file.Path));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllText(target, file.Content);
                count++;
            }
        }
        return count;
    }
}

/// <summary>Live check that the model adapter works end to end: authentication, model selection, structured output.</summary>
internal static class ModelSmokeTest
{
    public static async Task<int> RunAsync(ClaudeCodeModelClient client)
    {
        Console.WriteLine($"Model adapter: {client.Describe}; executable: {client.ExecutablePath ?? "NOT FOUND"}");
        var health = await ModelHealthCheck.RunAsync(client);
        Console.WriteLine($"  model={health.Model} latency={health.LatencyMs} ms cost=${health.CostUsd:0.0000}");
        Console.WriteLine($"  {health.Detail}");
        Console.WriteLine(health.Healthy ? "  RESULT: OK" : $"  RESULT: FAILED{(health.Error is { } e ? $" ({e})" : "")}");
        return health.Healthy ? 0 : 1;
    }
}

internal static class RepoLocator
{
    public static string FindRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "AgenticSdlc.sln"))) return dir.FullName;
                dir = dir.Parent;
            }
        }
        return Directory.GetCurrentDirectory();
    }
}

/// <summary>Console-based human: the operator answers approvals and clarifications interactively.</summary>
internal sealed class ConsoleHuman : IHumanInteraction
{
    public Task<ApprovalDecision> RequestApprovalAsync(ApprovalRequest request, CancellationToken ct)
    {
        Console.WriteLine();
        Console.WriteLine($"  >> APPROVAL NEEDED at '{request.NodeId}': {request.Reason}");
        Console.WriteLine($"     {request.Summary}");
        foreach (var r in request.Risks) Console.WriteLine($"     risk: {r}");
        Console.Write("     [a]pprove, [r]eject, or [c]hanges <targetNode>:<note> > ");
        var input = (Console.ReadLine() ?? "").Trim();

        if (input.StartsWith("c", StringComparison.OrdinalIgnoreCase))
        {
            var rest = input.Length > 1 ? input[1..].Trim() : "";
            var parts = rest.Split(':', 2);
            return Task.FromResult(new ApprovalDecision(ApprovalOutcome.ChangesRequested, "console-user",
                parts.Length > 1 ? parts[1].Trim() : rest, parts.Length > 1 ? parts[0].Trim() : null));
        }
        return Task.FromResult(input.StartsWith("a", StringComparison.OrdinalIgnoreCase)
            ? new ApprovalDecision(ApprovalOutcome.Approved, "console-user")
            : new ApprovalDecision(ApprovalOutcome.Rejected, "console-user", "rejected on console"));
    }

    public Task<IReadOnlyList<string>> ClarifyAsync(ClarificationRequest request, CancellationToken ct)
    {
        Console.WriteLine();
        Console.WriteLine($"  >> CLARIFICATION NEEDED at '{request.NodeId}'");
        var answers = new List<string>();
        foreach (var q in request.Questions)
        {
            Console.WriteLine($"     {q}");
            Console.Write("     > ");
            answers.Add(Console.ReadLine() ?? "");
        }
        return Task.FromResult<IReadOnlyList<string>>(answers);
    }
}
