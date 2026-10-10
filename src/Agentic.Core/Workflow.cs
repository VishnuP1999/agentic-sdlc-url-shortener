using System.Text;

namespace Agentic.Core;

/// <param name="PreviousFailure">
/// Why the previous attempt of this node was rejected (exception, policy, gate or validation output). Agents use it
/// for a controlled repair attempt; it is null on the first attempt.
/// </param>
public sealed record AgentRequest(RunContext Context, string NodeId, int Attempt, IPolicyEvaluator Policies, string? PreviousFailure = null);

public sealed record AgentResult(IReadOnlyList<Artifact> Artifacts, string Summary);

/// <summary>An autonomous worker. Implementations may be deterministic (mocks) or LLM-backed.</summary>
public interface IAgent
{
    string Name { get; }
    Task<AgentResult> ExecuteAsync(AgentRequest request, CancellationToken ct);
}

/// <summary>
/// A failure that retrying the same agent cannot fix (missing credentials, unknown model, misconfiguration).
/// The engine skips the remaining retries and goes straight to the fallback agent, if any.
/// </summary>
public class NonRetryableAgentException : Exception
{
    public NonRetryableAgentException(string message, Exception? inner = null) : base(message, inner) { }
}

public enum GateOutcome { Pass, Fail, NeedsClarification }

/// <param name="Evidence">Artifacts produced by orchestrator-owned checks (e.g. a real test report). They are stored even when the check fails.</param>
public sealed record GateResult(GateOutcome Outcome, string Reason, IReadOnlyList<string>? Questions = null, IReadOnlyList<Artifact>? Evidence = null)
{
    public static GateResult Pass(string reason = "ok", params Artifact[] evidence) => new(GateOutcome.Pass, reason, null, evidence);
    public static GateResult Fail(string reason, params Artifact[] evidence) => new(GateOutcome.Fail, reason, null, evidence);
    public static GateResult NeedsHuman(string reason, IEnumerable<string> questions) =>
        new(GateOutcome.NeedsClarification, reason, questions.ToList());
}

public sealed record RetryPolicy(int MaxAttempts = 3, int BaseDelayMs = 50, double Multiplier = 2.0)
{
    public TimeSpan DelayFor(int failedAttempt) =>
        TimeSpan.FromMilliseconds(BaseDelayMs * Math.Pow(Multiplier, Math.Max(0, failedAttempt - 1)));
}

public enum ApprovalTiming { BeforeRun, AfterRun }

/// <summary>When the fallback agent may take over.</summary>
public enum FallbackWhen
{
    /// <summary>After retries are exhausted or on a non-retryable failure (output is fully re-checked by gates).</summary>
    AfterRetries,

    /// <summary>
    /// Only when the primary agent is unavailable (non-retryable failure). Used where a weaker agent could make a
    /// genuine failure look like success, e.g. replacing failing proposed code with "no change".
    /// </summary>
    OnlyWhenUnavailable
}

/// <summary>Declares when a node needs a human decision (change control for high-impact actions).</summary>
public sealed record ApprovalSpec(
    ApprovalTiming Timing,
    Func<RunContext, bool> Required,
    string Reason,
    Func<RunContext, string> Summary,
    Func<RunContext, IReadOnlyList<string>>? Risks = null);

public sealed class WorkflowNode
{
    public WorkflowNode(string id, IAgent agent)
    {
        Id = id;
        Agent = agent;
    }

    public string Id { get; }
    public IAgent Agent { get; }
    public string Description { get; init; } = "";

    /// <summary>The role the agent plays (planner, developer, tester, documentation, ...), recorded in the audit trail.</summary>
    public string Role { get; init; } = "agent";

    /// <summary>
    /// Permission: the artifact names this node may write. Anything else is rejected as a permission violation
    /// (null = unrestricted, used by tests). Agents never get file-system, shell or network access; only
    /// orchestrator-owned code (gates, validation) touches the workspace.
    /// </summary>
    public IReadOnlyList<string>? Produces { get; init; }

    public IAgent? Fallback { get; init; }
    public FallbackWhen FallbackWhen { get; init; } = FallbackWhen.AfterRetries;
    public IReadOnlyList<string> DependsOn { get; init; } = Array.Empty<string>();
    public Func<RunContext, GateResult>? EntryGate { get; init; }
    public Func<RunContext, GateResult>? ExitGate { get; init; }

    /// <summary>
    /// Orchestrator-owned validation that runs after the exit gate (e.g. build and run the real test suite in a sandbox).
    /// A failure is fed back to the agent as <see cref="AgentRequest.PreviousFailure"/> for a bounded repair attempt.
    /// </summary>
    public Func<RunContext, CancellationToken, Task<GateResult>>? Validation { get; init; }

    public ApprovalSpec? Approval { get; init; }
    public RetryPolicy Retry { get; init; } = new();
}

/// <summary>Explicit dependency graph (DAG) of workflow nodes.</summary>
public sealed class WorkflowGraph
{
    private readonly Dictionary<string, WorkflowNode> _nodes = new(StringComparer.Ordinal);
    private readonly List<string> _order = new();

    public IReadOnlyList<WorkflowNode> Nodes => _order.Select(id => _nodes[id]).ToList();

    public WorkflowNode this[string id] =>
        _nodes.TryGetValue(id, out var n) ? n : throw new KeyNotFoundException($"Unknown node '{id}'.");

    public bool Contains(string id) => _nodes.ContainsKey(id);

    public WorkflowGraph Add(WorkflowNode node)
    {
        if (!_nodes.TryAdd(node.Id, node)) throw new InvalidOperationException($"Duplicate node '{node.Id}'.");
        _order.Add(node.Id);
        return this;
    }

    /// <summary>Checks that every dependency exists and the graph has no cycles.</summary>
    public void Validate()
    {
        foreach (var n in _nodes.Values)
            foreach (var d in n.DependsOn)
                if (!_nodes.ContainsKey(d))
                    throw new InvalidOperationException($"Node '{n.Id}' depends on unknown node '{d}'.");

        // Kahn's algorithm
        var indegree = _nodes.Values.ToDictionary(n => n.Id, n => n.DependsOn.Count);
        var queue = new Queue<string>(indegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        var visited = 0;
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            visited++;
            foreach (var child in _nodes.Values.Where(n => n.DependsOn.Contains(id)))
                if (--indegree[child.Id] == 0) queue.Enqueue(child.Id);
        }
        if (visited != _nodes.Count) throw new InvalidOperationException("Workflow graph contains a cycle.");
    }

    /// <summary>The node itself plus everything that (transitively) depends on it.</summary>
    public IReadOnlySet<string> SelfAndDependents(string id)
    {
        var result = new HashSet<string>(StringComparer.Ordinal) { id };
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var n in _nodes.Values)
                if (!result.Contains(n.Id) && n.DependsOn.Any(result.Contains))
                {
                    result.Add(n.Id);
                    changed = true;
                }
        }
        return result;
    }

    public string ToMermaid()
    {
        var sb = new StringBuilder("flowchart TD\n");
        foreach (var n in _nodes.Values)
        {
            var flags = new List<string>();
            if (n.Role != "agent") flags.Add(n.Role);
            if (n.Approval is not null) flags.Add("approval");
            if (n.Fallback is not null) flags.Add("fallback");
            if (n.Validation is not null) flags.Add("validation");
            var label = flags.Count == 0 ? n.Id : $"{n.Id}<br/>[{string.Join(", ", flags)}]";
            sb.Append($"    {n.Id}[\"{label}\"]\n");
        }
        foreach (var n in _nodes.Values)
            foreach (var d in n.DependsOn)
                sb.Append($"    {d} --> {n.Id}\n");
        return sb.ToString();
    }
}
