using System.Text;
using System.Text.Json;

namespace Agentic.Core;

/// <summary>Persists a run (artifacts, report, lineage, audit trail, model calls) and renders a console summary.</summary>
public static class RunOutput
{
    public static string Write(RunResult result, WorkflowGraph graph, string outputDirectory)
    {
        var dir = Path.Combine(outputDirectory, result.RunId);
        var artifactDir = Path.Combine(dir, "artifacts");
        Directory.CreateDirectory(artifactDir);

        foreach (var a in result.Artifacts)
        {
            var ext = a.Kind switch { "json" => "json", "markdown" => "md", "sql" => "sql", _ => "txt" };
            File.WriteAllText(Path.Combine(artifactDir, $"{a.Name}.v{a.Version}.{ext}"), a.Content);
        }

        var report = new
        {
            result.RunId,
            Status = result.Status.ToString(),
            result.StopReason,
            result.StartedAt,
            result.FinishedAt,
            result.PlanVersion,
            result.AuditChainValid,
            result.Metrics,
            result.Nodes,
            Artifacts = result.Artifacts.Select(a => new { a.Name, a.Version, a.Hash, a.ProducedBy }),
            ArtifactVersionsProduced = result.ArtifactHistory.Select(a => a.Ref),
            result.ChangeRequests
        };
        File.WriteAllText(Path.Combine(dir, "run-report.json"), JsonSerializer.Serialize(report, Json.Indented));
        File.WriteAllText(Path.Combine(dir, "decision-lineage.json"), JsonSerializer.Serialize(result.Decisions, Json.Indented));
        File.WriteAllText(Path.Combine(dir, "model-invocations.json"), JsonSerializer.Serialize(result.ModelInvocations, Json.Indented));
        File.WriteAllText(Path.Combine(dir, "workflow.mmd"), graph.ToMermaid());
        File.WriteAllLines(Path.Combine(dir, "audit.jsonl"), result.Audit.Select(e => Json.Serialize(e)));
        return dir;
    }

    public static string Summarize(RunResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Run {r.RunId}: {r.Status}{(r.StopReason is null ? "" : $" ({r.StopReason})")}");
        sb.AppendLine($"  {"node",-16}{"state",-20}{"attempts",-10}{"ms",-9}last error");
        foreach (var n in r.Nodes)
            sb.AppendLine($"  {n.Id,-16}{n.State,-20}{n.Attempts,-10}{n.DurationMs,-9}{Truncate(n.LastError, 140)}");

        var m = r.Metrics;
        sb.AppendLine("  Metrics:");
        sb.AppendLine($"    node success rate     {m.NodeSuccessRate:P0} ({m.NodesSucceeded}/{m.NodesTotal})");
        sb.AppendLine($"    attempt success rate  {m.AttemptSuccessRate:P0} ({m.AttemptsSucceeded}/{m.AttemptsTotal})");
        sb.AppendLine($"    retries / rollbacks / fallbacks   {m.Retries} / {m.Rollbacks} / {m.Fallbacks}");
        sb.AppendLine($"    re-plans / approvals / clarifications   {m.Replans} / {m.Approvals} / {m.Clarifications}  (plan v{r.PlanVersion})");
        sb.AppendLine($"    MTTR                  {(m.MttrMs is null ? "n/a (no failures)" : $"{m.MttrMs:0.#} ms over {m.Recoveries} recovery(ies)")}");
        sb.AppendLine($"    end-to-end latency    {m.EndToEndMs} ms");
        sb.AppendLine(m.ModelCalls == 0
            ? "    model calls           0 (deterministic agents)"
            : $"    model calls           {m.ModelCalls} ({m.ModelCallFailures} failed), tokens in/out {m.ModelInputTokens}/{m.ModelOutputTokens}, cost ${m.ModelCostUsd:0.0000}");
        sb.AppendLine($"  Audit entries: {r.Audit.Count}, hash chain {(r.AuditChainValid ? "VALID" : "BROKEN")}");
        return sb.ToString();
    }

    private static string? Truncate(string? text, int max) =>
        text is null || text.Length <= max ? text : text[..max].ReplaceLineEndings(" ") + "...";
}
