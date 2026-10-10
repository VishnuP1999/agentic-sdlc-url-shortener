using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Agentic.Core;

public sealed record AuditEntry(
    long Seq, DateTimeOffset At, string RunId, string? NodeId, string Event, string Actor, string Detail,
    string PrevHash, string Hash);

/// <summary>
/// Append-only, hash-chained audit trail (each entry commits to the previous one, so tampering is detectable).
/// Email addresses are redacted before anything is written (no PII in logs).
/// </summary>
public sealed partial class AuditLog
{
    private readonly object _gate = new();
    private readonly List<AuditEntry> _entries = new();
    private readonly string _runId;
    private readonly string? _filePath;

    public AuditLog(string runId, string? filePath = null)
    {
        _runId = runId;
        _filePath = filePath;
        if (filePath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
            File.WriteAllText(filePath, string.Empty);
        }
    }

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
    private static partial Regex EmailPattern();

    public static string Redact(string text) => EmailPattern().Replace(text, "[redacted-email]");

    public AuditEntry Append(string? nodeId, string evt, string actor, string detail)
    {
        lock (_gate)
        {
            var prev = _entries.Count == 0 ? new string('0', 64) : _entries[^1].Hash;
            var seq = _entries.Count + 1;
            var at = DateTimeOffset.UtcNow;
            var safeDetail = Redact(detail);
            var hash = ComputeHash(prev, seq, at, _runId, nodeId, evt, actor, safeDetail);
            var entry = new AuditEntry(seq, at, _runId, nodeId, evt, actor, safeDetail, prev, hash);
            _entries.Add(entry);
            if (_filePath is not null) File.AppendAllText(_filePath, Json.Serialize(entry) + Environment.NewLine);
            return entry;
        }
    }

    public IReadOnlyList<AuditEntry> Entries()
    {
        lock (_gate) return _entries.ToList();
    }

    public bool Verify() => Verify(Entries());

    public static bool Verify(IReadOnlyList<AuditEntry> entries)
    {
        var prev = new string('0', 64);
        foreach (var e in entries)
        {
            if (e.PrevHash != prev) return false;
            if (e.Hash != ComputeHash(e.PrevHash, e.Seq, e.At, e.RunId, e.NodeId, e.Event, e.Actor, e.Detail)) return false;
            prev = e.Hash;
        }
        return true;
    }

    private static string ComputeHash(string prev, long seq, DateTimeOffset at, string runId, string? nodeId,
        string evt, string actor, string detail) =>
        Hashing.Sha256($"{prev}|{seq}|{at:O}|{runId}|{nodeId}|{evt}|{actor}|{detail}");
}

public sealed record NodeMetrics(string NodeId, int Attempts, int Failures, int Retries, int Rollbacks, long TotalMs);

public sealed record MetricsSummary(
    int AttemptsTotal,
    int AttemptsSucceeded,
    double AttemptSuccessRate,
    int NodesTotal,
    int NodesSucceeded,
    double NodeSuccessRate,
    int Retries,
    int Rollbacks,
    int Fallbacks,
    int Replans,
    int Approvals,
    int Clarifications,
    int Recoveries,
    double? MttrMs,
    long EndToEndMs,
    IReadOnlyList<NodeMetrics> PerNode,
    int ModelCalls,
    int ModelCallFailures,
    long ModelInputTokens,
    long ModelOutputTokens,
    double ModelCostUsd);

/// <summary>Collects reliability metrics: success rate, retries, rollbacks, MTTR and latency.</summary>
public sealed class MetricsCollector
{
    private sealed class Counters
    {
        public int Attempts, Failures, Retries, Rollbacks;
        public long Ms;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Counters> _nodes = new();
    private readonly List<double> _recoveryMs = new();
    private int _fallbacks, _replans, _approvals, _clarifications;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private Counters For(string id)
    {
        if (!_nodes.TryGetValue(id, out var c)) _nodes[id] = c = new Counters();
        return c;
    }

    public void AttemptFinished(string nodeId, bool success, TimeSpan elapsed)
    {
        lock (_gate)
        {
            var c = For(nodeId);
            c.Attempts++;
            c.Ms += (long)elapsed.TotalMilliseconds;
            if (!success) c.Failures++;
        }
    }

    public void Retry(string nodeId) { lock (_gate) For(nodeId).Retries++; }
    public void Rollback(string nodeId) { lock (_gate) For(nodeId).Rollbacks++; }
    public void Fallback() { lock (_gate) _fallbacks++; }
    public void Replan() { lock (_gate) _replans++; }
    public void Approval() { lock (_gate) _approvals++; }
    public void Clarification() { lock (_gate) _clarifications++; }
    public void Recovery(TimeSpan timeToRecover) { lock (_gate) _recoveryMs.Add(timeToRecover.TotalMilliseconds); }

    public MetricsSummary Summarize(int nodesTotal, int nodesSucceeded, IReadOnlyList<ModelInvocation>? invocations = null)
    {
        invocations ??= Array.Empty<ModelInvocation>();
        lock (_gate)
        {
            var attempts = _nodes.Values.Sum(c => c.Attempts);
            var failures = _nodes.Values.Sum(c => c.Failures);
            return new MetricsSummary(
                attempts, attempts - failures,
                attempts == 0 ? 0 : Math.Round((double)(attempts - failures) / attempts, 3),
                nodesTotal, nodesSucceeded,
                nodesTotal == 0 ? 0 : Math.Round((double)nodesSucceeded / nodesTotal, 3),
                _nodes.Values.Sum(c => c.Retries), _nodes.Values.Sum(c => c.Rollbacks),
                _fallbacks, _replans, _approvals, _clarifications,
                _recoveryMs.Count,
                _recoveryMs.Count == 0 ? null : Math.Round(_recoveryMs.Average(), 1),
                _clock.ElapsedMilliseconds,
                _nodes.Select(kv => new NodeMetrics(kv.Key, kv.Value.Attempts, kv.Value.Failures, kv.Value.Retries,
                    kv.Value.Rollbacks, kv.Value.Ms)).OrderBy(n => n.NodeId, StringComparer.Ordinal).ToList(),
                invocations.Count,
                invocations.Count(i => i.Outcome != "ok"),
                invocations.Sum(i => i.InputTokens),
                invocations.Sum(i => i.OutputTokens),
                Math.Round(invocations.Sum(i => i.CostUsd), 4));
        }
    }
}
