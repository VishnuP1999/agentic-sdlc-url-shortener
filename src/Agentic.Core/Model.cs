using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agentic.Core;

/// <summary>Lifecycle of a workflow node (task). Only the engine changes states, and only along <see cref="NodeStateMachine"/>.</summary>
public enum NodeState
{
    Pending,            // waiting for dependencies
    Ready,              // dependencies satisfied, scheduled
    Running,            // agent executing
    Validating,         // policies, exit gate and validation checks on the output
    WaitingForApproval, // paused for a human decision
    RolledBack,         // failed output discarded, last known-good context restored
    Retrying,           // backing off before the next attempt
    Succeeded,
    Failed,             // retries and fallback exhausted (not a safety event)
    Blocked,            // a dependency failed; never runs without a recorded decision
    Cancelled,          // in flight when the run was safe-stopped
    Skipped,            // never started because the run ended first
    Stale,              // invalidated by re-planning (immediately re-queued)
    SafeStopped         // the node that triggered a safe-stop
}

public enum RunStatus { Running, Completed, Failed, SafeStopped }

/// <summary>Legal node state transitions. The engine rejects anything else, so illegal flows surface as bugs instead of silent corruption.</summary>
public static class NodeStateMachine
{
    private static readonly IReadOnlyDictionary<NodeState, NodeState[]> Allowed = new Dictionary<NodeState, NodeState[]>
    {
        [NodeState.Pending] = new[] { NodeState.Ready, NodeState.Blocked, NodeState.Skipped, NodeState.Cancelled },
        [NodeState.Ready] = new[] { NodeState.Running, NodeState.WaitingForApproval, NodeState.SafeStopped, NodeState.Cancelled, NodeState.Skipped },
        [NodeState.Running] = new[] { NodeState.Validating, NodeState.RolledBack, NodeState.SafeStopped, NodeState.Cancelled },
        [NodeState.Validating] = new[] { NodeState.Succeeded, NodeState.WaitingForApproval, NodeState.RolledBack, NodeState.Running, NodeState.SafeStopped, NodeState.Cancelled },
        [NodeState.WaitingForApproval] = new[] { NodeState.Running, NodeState.Succeeded, NodeState.Stale, NodeState.SafeStopped, NodeState.Cancelled },
        [NodeState.RolledBack] = new[] { NodeState.Retrying, NodeState.Running, NodeState.Failed, NodeState.SafeStopped, NodeState.Cancelled },
        [NodeState.Retrying] = new[] { NodeState.Running, NodeState.Cancelled },
        [NodeState.Succeeded] = new[] { NodeState.Stale },
        [NodeState.Stale] = new[] { NodeState.Pending },
        [NodeState.Failed] = Array.Empty<NodeState>(),
        [NodeState.Blocked] = Array.Empty<NodeState>(),
        [NodeState.Cancelled] = Array.Empty<NodeState>(),
        [NodeState.Skipped] = Array.Empty<NodeState>(),
        [NodeState.SafeStopped] = Array.Empty<NodeState>()
    };

    public static bool CanTransition(NodeState from, NodeState to) => Allowed[from].Contains(to);

    public static bool IsTerminal(NodeState state) => Allowed[state].Length == 0;
}

/// <summary>One call to a language model, recorded for audit and metrics (never contains prompts or tokens).</summary>
public sealed record ModelInvocation(
    DateTimeOffset At, string NodeId, string Role, string Model, string Outcome, long DurationMs,
    long InputTokens, long OutputTokens, double CostUsd, string? Error = null);

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    public static readonly JsonSerializerOptions Indented = new(Options) { WriteIndented = true };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}

public static class Hashing
{
    public static string Sha256(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}

/// <summary>A versioned output of an agent (requirements, design, code change-set, report, ...).</summary>
public sealed record Artifact(
    string Name, int Version, string Kind, string Content, string ProducedBy, string Hash, DateTimeOffset CreatedAt)
{
    public string Ref => $"{Name}@v{Version}";

    /// <summary>Creates an unversioned artifact; the run context assigns the version when it is stored.</summary>
    public static Artifact Create(string name, string kind, string content, string producedBy) =>
        new(name, 0, kind, content, producedBy, Hashing.Sha256(content), DateTimeOffset.UtcNow);

    public static Artifact FromJson<T>(string name, T value, string producedBy) =>
        Create(name, "json", Json.Serialize(value), producedBy);
}

/// <summary>One entry in the decision lineage: who decided what, why, from which inputs, producing which outputs.</summary>
public sealed record DecisionRecord(
    DateTimeOffset At, string NodeId, string Actor, string Decision, string Rationale,
    IReadOnlyList<string> Inputs, IReadOnlyList<string> Outputs);

/// <summary>A human-requested change that triggers re-planning.</summary>
public sealed record ChangeRequest(string TargetNode, string Note, string RequestedBy, DateTimeOffset At);

public sealed class ContextSnapshot
{
    internal ContextSnapshot(Dictionary<string, Artifact> latest) => Latest = latest;
    internal Dictionary<string, Artifact> Latest { get; }
}

/// <summary>
/// Shared, thread-safe state of one run: versioned artifacts, facts (human inputs), change requests and the
/// decision lineage. Every stage reads and writes through this object, so context is preserved across stages.
/// </summary>
public sealed class RunContext
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Artifact> _latest = new();
    private readonly Dictionary<string, int> _versionCounters = new();
    private readonly List<Artifact> _history = new();
    private readonly List<DecisionRecord> _decisions = new();
    private readonly List<ChangeRequest> _changeRequests = new();
    private readonly Dictionary<string, string> _facts = new();
    private readonly List<ModelInvocation> _invocations = new();

    public RunContext(string requirement, string? runId = null)
    {
        Requirement = requirement;
        RunId = runId ?? $"run-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
    }

    public string RunId { get; }
    public string Requirement { get; }

    /// <summary>Stores an artifact, assigning the next version for its name (versions never repeat, even after rollback).</summary>
    public Artifact Put(Artifact artifact)
    {
        lock (_gate)
        {
            var version = _versionCounters.GetValueOrDefault(artifact.Name) + 1;
            _versionCounters[artifact.Name] = version;
            var stored = artifact with { Version = version };
            _latest[artifact.Name] = stored;
            _history.Add(stored);
            return stored;
        }
    }

    public Artifact? Get(string name)
    {
        lock (_gate) return _latest.GetValueOrDefault(name);
    }

    public T GetJson<T>(string name)
    {
        var artifact = Get(name) ?? throw new InvalidOperationException($"Artifact '{name}' has not been produced.");
        return JsonSerializer.Deserialize<T>(artifact.Content, Json.Options)
               ?? throw new InvalidOperationException($"Artifact '{name}' is empty.");
    }

    public bool TryGetJson<T>(string name, out T? value) where T : class
    {
        value = null;
        var artifact = Get(name);
        if (artifact is null) return false;
        try { value = JsonSerializer.Deserialize<T>(artifact.Content, Json.Options); }
        catch (JsonException) { return false; }
        return value is not null;
    }

    public IReadOnlyList<Artifact> Latest()
    {
        lock (_gate) return _latest.Values.OrderBy(a => a.Name, StringComparer.Ordinal).ToList();
    }

    public IReadOnlyList<Artifact> History()
    {
        lock (_gate) return _history.ToList();
    }

    public void SetFact(string key, string value)
    {
        lock (_gate) _facts[key] = value;
    }

    public string? GetFact(string key)
    {
        lock (_gate) return _facts.GetValueOrDefault(key);
    }

    public void RecordDecision(DecisionRecord decision)
    {
        lock (_gate) _decisions.Add(decision);
    }

    public IReadOnlyList<DecisionRecord> Decisions()
    {
        lock (_gate) return _decisions.ToList();
    }

    public void AddChangeRequest(ChangeRequest request)
    {
        lock (_gate) _changeRequests.Add(request);
    }

    public IReadOnlyList<ChangeRequest> ChangeRequests()
    {
        lock (_gate) return _changeRequests.ToList();
    }

    /// <summary>Raised for every recorded model call (the engine turns these into audit events).</summary>
    public event Action<ModelInvocation>? InvocationRecorded;

    /// <summary>Records a model call. Append-only: rollbacks never erase what was spent or attempted.</summary>
    public void RecordInvocation(ModelInvocation invocation)
    {
        lock (_gate) _invocations.Add(invocation);
        InvocationRecorded?.Invoke(invocation);
    }

    public IReadOnlyList<ModelInvocation> Invocations()
    {
        lock (_gate) return _invocations.ToList();
    }

    public ContextSnapshot Snapshot()
    {
        lock (_gate) return new ContextSnapshot(new Dictionary<string, Artifact>(_latest));
    }

    /// <summary>Restores the whole "latest" view to a snapshot. History is append-only and keeps the rolled-back versions for lineage.</summary>
    public void Restore(ContextSnapshot snapshot)
    {
        lock (_gate)
        {
            _latest.Clear();
            foreach (var (k, v) in snapshot.Latest) _latest[k] = v;
        }
    }

    /// <summary>
    /// Rolls back only the named artifacts to their snapshot versions (removing ones that did not exist then).
    /// Used when a node fails while other branches run in parallel: undoing the failed node's own output must
    /// never discard what a sibling branch produced after the snapshot was taken.
    /// </summary>
    public void Restore(ContextSnapshot snapshot, IEnumerable<string> names)
    {
        lock (_gate)
        {
            foreach (var name in names.Distinct())
            {
                if (snapshot.Latest.TryGetValue(name, out var previous)) _latest[name] = previous;
                else _latest.Remove(name);
            }
        }
    }
}
