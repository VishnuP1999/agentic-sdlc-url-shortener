using System.Text.Json;

namespace Agentic.Core;

/// <summary>One finished run, as recorded in the cross-run history index.</summary>
public sealed record RunHistoryEntry(
    string RunId,
    string Label,
    string AgentMode,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    long EndToEndMs,
    int NodesTotal,
    int NodesSucceeded,
    int Attempts,
    int AttemptsSucceeded,
    int Retries,
    int Rollbacks,
    int Fallbacks,
    int Recoveries,
    double? MttrMs,
    int ModelCalls,
    double ModelCostUsd,
    string? StopReason)
{
    public static RunHistoryEntry From(RunResult r, string label, string agentMode) => new(
        r.RunId, label, agentMode, r.Status.ToString(), r.StartedAt, r.FinishedAt, r.Metrics.EndToEndMs,
        r.Metrics.NodesTotal, r.Metrics.NodesSucceeded, r.Metrics.AttemptsTotal, r.Metrics.AttemptsSucceeded,
        r.Metrics.Retries, r.Metrics.Rollbacks, r.Metrics.Fallbacks, r.Metrics.Recoveries, r.Metrics.MttrMs,
        r.Metrics.ModelCalls, r.Metrics.ModelCostUsd, r.StopReason);
}

/// <summary>Reliability metrics over a measurement window, calculated only from recorded run history.</summary>
public sealed record CrossRunMetrics(
    DateTimeOffset? WindowStart,
    DateTimeOffset? WindowEnd,
    int Runs,
    int Completed,
    int Failed,
    int SafeStopped,
    double RunSuccessRate,
    double RetriesPerRun,
    double RetryFrequency,
    double RollbacksPerRun,
    double RollbackFrequency,
    double? MttrMs,
    double? MeanLatencyMs,
    long? P95LatencyMs,
    int ModelCalls,
    double ModelCostUsd);

/// <summary>
/// Append-only JSON-lines index of finished runs (one line per run). Metrics are derived from it rather than kept
/// in memory, so they survive restarts and can be recomputed for any time window.
/// </summary>
public static class RunHistory
{
    public static void Append(string indexPath, RunHistoryEntry entry)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(indexPath))!);
        File.AppendAllText(indexPath, Json.Serialize(entry) + Environment.NewLine);
    }

    public static IReadOnlyList<RunHistoryEntry> Load(string indexPath)
    {
        if (!File.Exists(indexPath)) return Array.Empty<RunHistoryEntry>();
        var entries = new List<RunHistoryEntry>();
        foreach (var line in File.ReadLines(indexPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                if (JsonSerializer.Deserialize<RunHistoryEntry>(line, Json.Options) is { } e) entries.Add(e);
            }
            catch (JsonException)
            {
                // a torn or hand-edited line must not hide the rest of the history
            }
        }
        return entries;
    }

    /// <summary>
    /// Run success rate = completed / all runs (a deliberate safe-stop counts as not successful).
    /// Retry and rollback frequency = per attempt. MTTR = mean recovery time weighted by recoveries.
    /// </summary>
    public static CrossRunMetrics Summarize(IEnumerable<RunHistoryEntry> history, DateTimeOffset? from = null, DateTimeOffset? to = null)
    {
        var runs = history.Where(e => (from is null || e.StartedAt >= from) && (to is null || e.StartedAt <= to))
            .OrderBy(e => e.StartedAt).ToList();
        if (runs.Count == 0)
            return new CrossRunMetrics(from, to, 0, 0, 0, 0, 0, 0, 0, 0, 0, null, null, null, 0, 0);

        var attempts = runs.Sum(r => r.Attempts);
        var recoveries = runs.Sum(r => r.Recoveries);
        var mttr = recoveries == 0 ? (double?)null
            : Math.Round(runs.Where(r => r.MttrMs is not null).Sum(r => r.MttrMs!.Value * r.Recoveries) / recoveries, 1);
        var latencies = runs.Select(r => r.EndToEndMs).OrderBy(x => x).ToList();
        var p95 = latencies[(int)Math.Ceiling(0.95 * latencies.Count) - 1];
        var completed = runs.Count(r => r.Status == nameof(RunStatus.Completed));

        return new CrossRunMetrics(
            from ?? runs[0].StartedAt, to ?? runs[^1].FinishedAt, runs.Count,
            completed,
            runs.Count(r => r.Status == nameof(RunStatus.Failed)),
            runs.Count(r => r.Status == nameof(RunStatus.SafeStopped)),
            Math.Round((double)completed / runs.Count, 3),
            Math.Round((double)runs.Sum(r => r.Retries) / runs.Count, 2),
            attempts == 0 ? 0 : Math.Round((double)runs.Sum(r => r.Retries) / attempts, 3),
            Math.Round((double)runs.Sum(r => r.Rollbacks) / runs.Count, 2),
            attempts == 0 ? 0 : Math.Round((double)runs.Sum(r => r.Rollbacks) / attempts, 3),
            mttr,
            Math.Round(latencies.Average(), 1),
            p95,
            runs.Sum(r => r.ModelCalls),
            Math.Round(runs.Sum(r => r.ModelCostUsd), 4));
    }
}
