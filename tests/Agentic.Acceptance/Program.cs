using System.Diagnostics;
using Agentic.Acceptance;
using Agentic.Agents;
using Agentic.Core;
using Agentic.Model;

// Automated acceptance (SIT) suite: runs the SDLC scenarios end to end and the product smoke test, checks the
// evidence each run leaves behind, and writes JUnit XML + Markdown + JSON reports. No manual steps.
//
//   dotnet run --project tests/Agentic.Acceptance                       # auto: Claude if available, else rule-based
//   dotnet run --project tests/Agentic.Acceptance -- --agents rules     # deterministic, free (CI)
//   dotnet run --project tests/Agentic.Acceptance -- --require-ai       # fail if Claude is not usable (SIT with AI)
//   options: --scenarios greenfield,brownfield,ambiguous,safe-stop  --no-exec  --retries <n>  --out <dir>  --repo <dir>
//
// Exit code: 0 = all checks passed, 1 = at least one check failed, 2 = bad arguments.

string? Value(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

var repo = Value("--repo") ?? FindRepoRoot();
var outDir = Path.GetFullPath(Value("--out") ?? Path.Combine(repo, "TestResults", "acceptance"));
var runsDir = Path.Combine(outDir, "runs");
var execute = !args.Contains("--no-exec");
var requireAi = args.Contains("--require-ai");
var scenarioNames = (Value("--scenarios") ?? "greenfield,brownfield,ambiguous,safe-stop")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

var started = DateTimeOffset.UtcNow;
var checks = new List<CheckResult>();
var outcomes = new List<ScenarioOutcome>();

Console.WriteLine("== Agentic SDLC acceptance suite ==");
Console.WriteLine($"repo: {repo}");
Console.WriteLine($"reports: {outDir}");

// ---- 1. choose agents (AI if Claude Code is installed and healthy, unless told otherwise)
var claude = new ClaudeCodeModelClient(ModelOptions.FromEnvironment());
AgentModeDecision decision;
try
{
    decision = await AgentModeResolver.ResolveAsync(Value("--agents") ?? Environment.GetEnvironmentVariable("AGENTIC_AGENTS") ?? "auto", claude);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}
var aiMode = decision.Mode == AgentMode.Ai;
Console.WriteLine($"agents: {(aiMode ? "AI" : "rule-based")} - {decision.Reason}");
Console.WriteLine($"real build + test execution: {(execute ? "yes" : "NO (offline)")}");
if (decision.Health is { } health)
    checks.Add(new CheckResult("environment", "model health check", health.Healthy, $"{health.Model}: {health.Detail}", health.LatencyMs / 1000.0));
if (requireAi)
    checks.Add(new CheckResult("environment", "AI agents required and available", aiMode, decision.Reason));

// AI output varies between runs; one bounded re-run of a failed scenario separates flakiness from real defects.
var retries = int.TryParse(Value("--retries"), out var n) ? Math.Max(0, n) : aiMode ? 1 : 0;

// ---- 2. the repository must be untouched by the whole suite (agents only propose)
var fingerprintBefore = RepoFingerprint(repo);

// ---- 3. scenarios
foreach (var name in scenarioNames)
{
    var path = Path.Combine(repo, "scenarios", $"{name}.json");
    if (!File.Exists(path))
    {
        checks.Add(new CheckResult($"scenario.{name}", "scenario file exists", false, path));
        continue;
    }
    var spec = ScenarioSpec.Load(path);

    for (var attempt = 1; attempt <= retries + 1; attempt++)
    {
        Console.WriteLine();
        Console.WriteLine($"-- scenario {name} (attempt {attempt}/{retries + 1}) --");
        var clock = Stopwatch.StartNew();
        var run = await ScenarioRunner.RunAsync(spec, new ScenarioRunOptions
        {
            RepoPath = repo,
            ExecuteTests = execute,
            AuditDirectory = runsDir,
            Model = decision.Model
        });
        var runDir = RunOutput.Write(run.Result, run.Graph, runsDir);
        RunHistory.Append(Path.Combine(outDir, "runs-index.jsonl"), RunHistoryEntry.From(run.Result, name, aiMode ? "ai" : "rules"));

        // the run's wall-clock time is reported on its first check; the checks themselves are instant
        var result = ScenarioChecks.Evaluate(run, aiMode, execute, repo)
            .Select((c, i) => i == 0 ? c with { Seconds = clock.Elapsed.TotalSeconds } : c).ToList();
        var failed = result.Where(c => !c.Passed).ToList();
        Console.WriteLine($"   {run.Result.Status} in {clock.Elapsed.TotalSeconds:0}s; checks {result.Count - failed.Count}/{result.Count} passed; {run.Result.Metrics.ModelCalls} model call(s), ${run.Result.Metrics.ModelCostUsd:0.0000}");
        foreach (var f in failed) Console.WriteLine($"   FAIL {f.Name}: {f.Detail}");

        if (failed.Count == 0 || attempt == retries + 1)
        {
            if (attempt > 1)
                result.Add(new CheckResult($"scenario.{name}", "FLAKY: needed a re-run", failed.Count == 0,
                    $"first attempt failed, attempt {attempt} {(failed.Count == 0 ? "passed" : "also failed")}; see earlier run folders in {runsDir}"));
            checks.AddRange(result);
            outcomes.Add(new ScenarioOutcome(name, run.Result.Status.ToString(), attempt, run.Result.Metrics.EndToEndMs,
                run.Result.Metrics.ModelCalls, run.Result.Metrics.ModelCostUsd, Path.GetRelativePath(repo, runDir)));
            break;
        }
        Console.WriteLine("   re-running once to distinguish a flaky model answer from a real defect...");
    }
}

// ---- 4. repository untouched
var fingerprintAfter = RepoFingerprint(repo);
checks.Add(new CheckResult("governance", "repository source unchanged by agents", fingerprintBefore == fingerprintAfter,
    "SHA-256 over src/UrlShortener.Api and tests/UrlShortener.Tests before and after the suite"));

// ---- 5. product smoke test (the user journey: shorten a link and follow it)
Console.WriteLine();
Console.WriteLine("-- product smoke test --");
var smoke = await ProductSmoke.RunAsync();
foreach (var s in smoke) Console.WriteLine($"   {(s.Passed ? "PASS" : "FAIL")} {s.Name}: {s.Detail}");
checks.AddRange(smoke);

// ---- 6. reports
var report = new AcceptanceReport(started, DateTimeOffset.UtcNow, aiMode ? "ai" : "rules", decision.Reason, execute,
    Environment.MachineName, outcomes, checks);
Reports.Write(report, outDir);

Console.WriteLine();
Console.WriteLine($"== {(report.Passed ? "PASSED" : "FAILED")}: {checks.Count - report.Failed}/{checks.Count} checks, {outcomes.Count} scenario(s), {(DateTimeOffset.UtcNow - started).TotalMinutes:0.0} min ==");
foreach (var f in checks.Where(c => !c.Passed)) Console.WriteLine($"   FAIL [{f.Suite}] {f.Name}: {f.Detail}");
Console.WriteLine($"reports: {Path.Combine(outDir, "acceptance-report.md")}, acceptance.junit.xml, acceptance-report.json");
return report.Passed ? 0 : 1;

static string FindRepoRoot()
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

static string RepoFingerprint(string repo)
{
    var files = new[] { "src/UrlShortener.Api", "tests/UrlShortener.Tests" }
        .Select(d => Path.Combine(repo, d)).Where(Directory.Exists)
        .SelectMany(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories))
        .Where(f => !f.Replace('\\', '/').Contains("/bin/") && !f.Replace('\\', '/').Contains("/obj/"))
        .OrderBy(f => f, StringComparer.Ordinal);
    return Hashing.Sha256(string.Join("\n", files.Select(f => $"{Path.GetRelativePath(repo, f)}:{Hashing.Sha256(File.ReadAllText(f))}")));
}
