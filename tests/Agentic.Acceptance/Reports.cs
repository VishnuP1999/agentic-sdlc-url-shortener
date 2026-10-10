using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Agentic.Core;

namespace Agentic.Acceptance;

public sealed record ScenarioOutcome(string Name, string Status, int Attempts, long LatencyMs, int ModelCalls, double CostUsd, string RunDirectory);

public sealed record AcceptanceReport(
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    string AgentMode,
    string ModeReason,
    bool TestsExecuted,
    string Machine,
    IReadOnlyList<ScenarioOutcome> Scenarios,
    IReadOnlyList<CheckResult> Checks)
{
    public int Failed => Checks.Count(c => !c.Passed);
    public bool Passed => Failed == 0 && Checks.Count > 0;
}

/// <summary>Writes the acceptance results for humans (Markdown), CI servers (JUnit XML) and tools (JSON).</summary>
public static class Reports
{
    public static void Write(AcceptanceReport report, string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "acceptance.junit.xml"), JUnit(report).ToString());
        File.WriteAllText(Path.Combine(directory, "acceptance-report.md"), Markdown(report));
        File.WriteAllText(Path.Combine(directory, "acceptance-report.json"), JsonSerializer.Serialize(report, Json.Indented));
    }

    private static string F(double seconds) => seconds.ToString("0.000", CultureInfo.InvariantCulture);

    public static XElement JUnit(AcceptanceReport report) =>
        new("testsuites",
            new XAttribute("name", "Agentic SDLC acceptance"),
            new XAttribute("tests", report.Checks.Count),
            new XAttribute("failures", report.Failed),
            new XAttribute("time", F((report.FinishedAt - report.StartedAt).TotalSeconds)),
            report.Checks.GroupBy(c => c.Suite).Select(g => new XElement("testsuite",
                new XAttribute("name", g.Key),
                new XAttribute("tests", g.Count()),
                new XAttribute("failures", g.Count(c => !c.Passed)),
                new XAttribute("time", F(g.Sum(c => c.Seconds))),
                new XElement("properties",
                    new XElement("property", new XAttribute("name", "agentMode"), new XAttribute("value", report.AgentMode)),
                    new XElement("property", new XAttribute("name", "testsExecuted"), new XAttribute("value", report.TestsExecuted))),
                g.Select(c => new XElement("testcase",
                    new XAttribute("classname", g.Key),
                    new XAttribute("name", c.Name),
                    new XAttribute("time", F(c.Seconds)),
                    c.Passed
                        ? new XElement("system-out", c.Detail)
                        : new XElement("failure", new XAttribute("message", c.Detail.Split('\n')[0]), c.Detail))))));

    public static string Markdown(AcceptanceReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Acceptance report: {(report.Passed ? "PASSED" : "FAILED")}");
        sb.AppendLine();
        sb.AppendLine($"- When: {report.StartedAt:u} to {report.FinishedAt:u} ({(report.FinishedAt - report.StartedAt).TotalMinutes:0.0} min)");
        sb.AppendLine($"- Machine: {report.Machine}");
        sb.AppendLine($"- Agents: **{report.AgentMode}** ({report.ModeReason})");
        sb.AppendLine($"- Real build and test execution: **{(report.TestsExecuted ? "yes" : "no (offline, results unverified)")}**");
        sb.AppendLine($"- Checks: {report.Checks.Count - report.Failed}/{report.Checks.Count} passed");
        sb.AppendLine();
        sb.AppendLine("## Scenarios");
        sb.AppendLine();
        sb.AppendLine("| Scenario | Status | Attempts | Latency | Model calls | Cost | Output |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var s in report.Scenarios)
            sb.AppendLine($"| {s.Name} | {s.Status} | {s.Attempts} | {s.LatencyMs / 1000.0:0.0} s | {s.ModelCalls} | ${s.CostUsd:0.0000} | `{s.RunDirectory}` |");
        foreach (var group in report.Checks.GroupBy(c => c.Suite))
        {
            sb.AppendLine();
            sb.AppendLine($"## {group.Key}");
            sb.AppendLine();
            sb.AppendLine("| Result | Check | Evidence |");
            sb.AppendLine("|---|---|---|");
            foreach (var c in group)
                sb.AppendLine($"| {(c.Passed ? "PASS" : "**FAIL**")} | {c.Name} | {c.Detail.Replace("|", "\\|").ReplaceLineEndings(" ")} |");
        }
        return sb.ToString();
    }
}
