using System.Diagnostics;

namespace Agentic.Core;

public sealed record EngineOptions
{
    /// <summary>How many times human-requested changes may re-plan the workflow before the run is safe-stopped.</summary>
    public int MaxReplans { get; init; } = 2;

    /// <summary>How many times a node may ask the human for clarification before the run is safe-stopped.</summary>
    public int MaxClarificationRounds { get; init; } = 2;

    /// <summary>Upper bound for one agent attempt (LLM-backed agents need minutes, deterministic ones milliseconds).</summary>
    public TimeSpan NodeTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Back-off delay between retries (replaceable so tests do not sleep).</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = (d, ct) => Task.Delay(d, ct);

    /// <summary>If set, the hash-chained audit log is also appended to this file as JSON lines.</summary>
    public string? AuditFilePath { get; init; }

    /// <summary>If set, the live workflow state (node states, attempts, approvals, plan version) is persisted here on every change.</summary>
    public string? StateFilePath { get; init; }
}

public sealed record NodeReport(string Id, NodeState State, int Attempts, long DurationMs, string? LastError);

/// <param name="StopReason">Why the run did not complete: the safe-stop reason, or the failure that blocked downstream work.</param>
public sealed record RunResult(
    string RunId,
    RunStatus Status,
    string? StopReason,
    IReadOnlyList<NodeReport> Nodes,
    MetricsSummary Metrics,
    IReadOnlyList<Artifact> Artifacts,
    IReadOnlyList<Artifact> ArtifactHistory,
    IReadOnlyList<DecisionRecord> Decisions,
    IReadOnlyList<ChangeRequest> ChangeRequests,
    IReadOnlyList<AuditEntry> Audit,
    bool AuditChainValid,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    int PlanVersion,
    IReadOnlyList<ModelInvocation> ModelInvocations);

/// <summary>
/// Stateful, non-linear workflow executor. It runs every node whose dependencies have succeeded (in parallel
/// where the graph allows), enforces entry/exit gates, policies, artifact permissions and validation, retries with
/// bounded back-off (feeding the failure back for a repair attempt), falls back to an alternative agent, rolls back
/// failed output, blocks the dependents of failed nodes, pauses for human approval/clarification, re-plans when
/// upstream output changes, and safe-stops whenever autonomy limits are hit. Only the engine changes node states.
/// </summary>
public sealed class WorkflowEngine
{
    private sealed class NodeRuntime
    {
        public NodeState State = NodeState.Pending;
        public int Attempts;
        public long DurationMs;
        public string? LastError;
    }

    private sealed record ReplanRequest(string Target, string Note, string RequestedBy, string FromNode);

    private readonly WorkflowGraph _graph;
    private readonly IHumanInteraction _human;
    private readonly PolicyEngine _policies;
    private readonly EngineOptions _options;
    private readonly Dictionary<string, NodeRuntime> _runtime = new(StringComparer.Ordinal);
    private readonly MetricsCollector _metrics = new();
    private readonly SemaphoreSlim _humanLock = new(1, 1);
    private readonly object _sync = new();
    private readonly object _persistLock = new();

    private RunContext _ctx = null!;
    private AuditLog _audit = null!;
    private CancellationTokenSource _cts = new();
    private string? _stopReason;
    private string? _failureReason;
    private int _replans;
    private int _planVersion = 1;
    private RunStatus _status = RunStatus.Running;
    private DateTimeOffset _startedAt;

    public WorkflowEngine(WorkflowGraph graph, IHumanInteraction human, PolicyEngine policies, EngineOptions? options = null)
    {
        _graph = graph;
        _human = human;
        _policies = policies;
        _options = options ?? new EngineOptions();
        foreach (var n in graph.Nodes) _runtime[n.Id] = new NodeRuntime();
    }

    /// <summary>Kill switch: halts the run as soon as in-flight work can be cancelled.</summary>
    public void RequestSafeStop(string reason) => SafeStop($"Kill switch: {reason}");

    public async Task<RunResult> RunAsync(RunContext context, CancellationToken ct = default)
    {
        _graph.Validate();
        _ctx = context;
        _audit = new AuditLog(context.RunId, _options.AuditFilePath);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _startedAt = DateTimeOffset.UtcNow;
        context.InvocationRecorded += OnModelInvocation;
        Log(null, "run.started", "engine", $"requirement: {context.Requirement}");
        Log(null, "plan.created", "engine", $"plan v{_planVersion}: [{string.Join(", ", _graph.Nodes.Select(n => n.Id))}]");
        PersistState();

        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var ready = ReadyNodes();
                if (ready.Count == 0) break;

                foreach (var n in ready)
                {
                    SetState(n, NodeState.Ready);
                    Log(n.Id, "node.ready", "engine", n.DependsOn.Count == 0 ? "no dependencies" : $"dependencies succeeded: {string.Join(", ", n.DependsOn)}");
                }
                var replans = await Task.WhenAll(ready.Select(n => RunNodeAsync(n, _cts.Token)));
                foreach (var request in replans.Where(r => r is not null)) ApplyReplan(request!);
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            if (_stopReason is null) SafeStop("Run cancelled by caller.");
        }
        finally
        {
            context.InvocationRecorded -= OnModelInvocation;
        }

        var allDone = _graph.Nodes.All(n => _runtime[n.Id].State == NodeState.Succeeded);
        var anyFailed = _graph.Nodes.Any(n => _runtime[n.Id].State == NodeState.Failed);
        if (!allDone && !anyFailed && _stopReason is null) SafeStop("Workflow stalled: no runnable nodes remain.");

        // Nodes that never started are recorded as skipped, so the final state of every task is explicit.
        foreach (var n in _graph.Nodes.Where(n => _runtime[n.Id].State is NodeState.Pending or NodeState.Ready))
        {
            SetState(n, NodeState.Skipped);
            Log(n.Id, "node.skipped", "engine", _stopReason ?? _failureReason ?? "run ended");
        }

        _status = _stopReason is not null ? RunStatus.SafeStopped : anyFailed ? RunStatus.Failed : RunStatus.Completed;
        Log(null, "run.finished", "engine", _status switch
        {
            RunStatus.Completed => "completed",
            RunStatus.Failed => $"failed: {_failureReason}",
            _ => $"safe-stopped: {_stopReason}"
        });
        PersistState();

        var nodes = _graph.Nodes
            .Select(n => new NodeReport(n.Id, _runtime[n.Id].State, _runtime[n.Id].Attempts, _runtime[n.Id].DurationMs, _runtime[n.Id].LastError))
            .ToList();
        var invocations = context.Invocations();
        var metrics = _metrics.Summarize(nodes.Count, nodes.Count(n => n.State == NodeState.Succeeded), invocations);
        var audit = _audit.Entries();

        return new RunResult(context.RunId, _status, _stopReason ?? _failureReason, nodes, metrics, context.Latest(), context.History(),
            context.Decisions(), context.ChangeRequests(), audit, AuditLog.Verify(audit), _startedAt, DateTimeOffset.UtcNow,
            _planVersion, invocations);
    }

    // ---------------------------------------------------------------- scheduling and state

    private List<WorkflowNode> ReadyNodes()
    {
        lock (_sync)
        {
            return _graph.Nodes
                .Where(n => _runtime[n.Id].State == NodeState.Pending
                            && n.DependsOn.All(d => _runtime[d].State == NodeState.Succeeded))
                .ToList();
        }
    }

    private NodeState StateOf(WorkflowNode node)
    {
        lock (_sync) return _runtime[node.Id].State;
    }

    /// <summary>The only place node states change. Illegal transitions are engine bugs and fail loudly.</summary>
    private void SetState(WorkflowNode node, NodeState to) => SetState(node.Id, to);

    private void SetState(string nodeId, NodeState to)
    {
        lock (_sync)
        {
            var rt = _runtime[nodeId];
            if (rt.State == to) return;
            if (!NodeStateMachine.CanTransition(rt.State, to))
                throw new InvalidOperationException($"Illegal state transition for '{nodeId}': {rt.State} -> {to}.");
            rt.State = to;
        }
        PersistState();
    }

    private void SafeStop(string reason)
    {
        lock (_sync)
        {
            if (_stopReason is not null) return;
            _stopReason = reason;
        }
        Log(null, "run.safe-stop", "engine", reason);
        try { _cts.Cancel(); } catch (ObjectDisposedException) { /* already finished */ }
    }

    private void Log(string? nodeId, string evt, string actor, string detail) => _audit.Append(nodeId, evt, actor, detail);

    private void OnModelInvocation(ModelInvocation i)
    {
        Log(i.NodeId, "model.invoked", $"model:{i.Role}",
            $"model={i.Model}; outcome={i.Outcome}; {i.DurationMs} ms; tokens in/out={i.InputTokens}/{i.OutputTokens}; cost=${i.CostUsd:0.0000}"
            + (i.Error is null ? "" : $"; error={i.Error}"));
    }

    /// <summary>Writes the live workflow state atomically (temp file + rename) so a crash never leaves a torn file.</summary>
    private void PersistState()
    {
        if (_options.StateFilePath is null || _ctx is null) return;
        lock (_persistLock)
        {
            List<object> nodes;
            lock (_sync)
            {
                nodes = _graph.Nodes.Select(n => (object)new
                {
                    n.Id, n.Role, State = _runtime[n.Id].State.ToString(), _runtime[n.Id].Attempts, _runtime[n.Id].LastError,
                    DependsOn = n.DependsOn
                }).ToList();
            }
            var state = new
            {
                _ctx.RunId,
                Status = _status.ToString(),
                StopReason = _stopReason ?? _failureReason,
                PlanVersion = _planVersion,
                StartedAt = _startedAt,
                UpdatedAt = DateTimeOffset.UtcNow,
                Nodes = nodes,
                Approvals = _ctx.Decisions().Where(d => d.Actor.StartsWith("human:", StringComparison.Ordinal)).ToList(),
                ChangeRequests = _ctx.ChangeRequests(),
                ArtifactVersions = _ctx.Latest().Select(a => a.Ref).ToList()
            };
            var path = Path.GetFullPath(_options.StateFilePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            var json = System.Text.Json.JsonSerializer.Serialize(state, Json.Indented);

            // Virus scanners and indexers can briefly lock the file on Windows: retry, and never let a
            // persistence hiccup crash the run (the audit log still records everything).
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.WriteAllText(tmp, json);
                    File.Move(tmp, path, overwrite: true);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (attempt < 5)
                    {
                        Thread.Sleep(20 * attempt);
                        continue;
                    }
                    _audit.Append(null, "state.persist.failed", "engine", ex.Message);
                    return;
                }
            }
        }
    }

    // ---------------------------------------------------------------- node execution

    private async Task<ReplanRequest?> RunNodeAsync(WorkflowNode node, CancellationToken ct)
    {
        var rt = _runtime[node.Id];
        var nodeClock = Stopwatch.StartNew();
        try
        {
            return await ExecuteNodeAsync(node, rt, ct);
        }
        catch (OperationCanceledException)
        {
            if (StateOf(node) is NodeState.Ready or NodeState.Running or NodeState.Validating or NodeState.Retrying
                or NodeState.RolledBack or NodeState.WaitingForApproval)
            {
                SetState(node, NodeState.Cancelled);
                Log(node.Id, "node.cancelled", "engine", "cancelled by safe-stop");
            }
            return null;
        }
        finally
        {
            lock (_sync) rt.DurationMs += nodeClock.ElapsedMilliseconds;
        }
    }

    private async Task<ReplanRequest?> ExecuteNodeAsync(WorkflowNode node, NodeRuntime rt, CancellationToken ct)
    {
        // ---- entry gate
        if (node.EntryGate is not null)
        {
            var entry = SafeGate(node.EntryGate);
            if (entry.Outcome != GateOutcome.Pass)
            {
                rt.LastError = entry.Reason;
                Log(node.Id, "gate.entry.failed", "engine", entry.Reason);
                SetState(node, NodeState.SafeStopped);
                SafeStop($"Entry gate failed for '{node.Id}': {entry.Reason}");
                return null;
            }
            Log(node.Id, "gate.entry.passed", "engine", entry.Reason);
        }

        // ---- approval before running (e.g. release)
        if (node.Approval is { Timing: ApprovalTiming.BeforeRun } before && before.Required(_ctx))
        {
            var (proceed, replan) = await ApproveAsync(node, before, ct);
            if (!proceed) return replan;
        }

        // ---- execute with bounded retries, repair feedback, fallback, rollback
        var attempt = 0;
        var clarificationRounds = 0;
        var usingFallback = false;
        string? previousFailure = null;
        DateTimeOffset? firstFailure = null;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var agent = usingFallback ? node.Fallback! : node.Agent;
            attempt++;
            lock (_sync) rt.Attempts++;
            var snapshot = _ctx.Snapshot();

            SetState(node, NodeState.Running);
            Log(node.Id, "node.started", $"agent:{agent.Name}",
                $"role={node.Role}; attempt {attempt}{(usingFallback ? " (fallback)" : "")}{(previousFailure is null ? "" : "; repair attempt with failure feedback")}");

            var attemptClock = Stopwatch.StartNew();
            string? failure = null;
            var retryable = true;
            IReadOnlyList<Artifact> produced = Array.Empty<Artifact>();
            string summary = "";

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(_options.NodeTimeout);
                var result = await agent.ExecuteAsync(new AgentRequest(_ctx, node.Id, attempt, _policies, previousFailure), timeout.Token)
                             ?? throw new InvalidOperationException("Agent returned no result.");

                var forbidden = node.Produces is { } allowed
                    ? result.Artifacts.Select(a => a.Name).Where(name => !allowed.Contains(name)).Distinct().ToList()
                    : new List<string>();
                if (forbidden.Count > 0)
                {
                    failure = $"permission denied: '{node.Id}' may not write artifact(s) {string.Join(", ", forbidden)}";
                    Log(node.Id, "permission.denied", "engine", failure);
                }
                else
                {
                    produced = result.Artifacts.Select(_ctx.Put).ToList();
                    summary = result.Summary;
                    foreach (var a in produced) Log(node.Id, "artifact.produced", $"agent:{agent.Name}", $"{a.Ref} sha256={a.Hash[..12]}");
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                failure = $"timed out after {_options.NodeTimeout.TotalSeconds:0.#}s";
            }
            catch (NonRetryableAgentException ex)
            {
                failure = $"non-retryable agent error: {ex.Message}";
                retryable = false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failure = $"agent error: {ex.Message}";
            }

            if (failure is null) SetState(node, NodeState.Validating);

            // ---- policy guardrails on the new output
            if (failure is null)
            {
                var violations = _policies.Evaluate(new PolicyContext(_ctx, node.Id, produced));
                foreach (var v in violations)
                    Log(node.Id, "policy.violation", $"policy:{v.PolicyId}", $"{v.Severity}: {v.Message}");

                var critical = violations.FirstOrDefault(v => v.Severity == Severity.Critical);
                if (critical is not null)
                {
                    // Not retryable: unsafe output must never be passed downstream.
                    _ctx.Restore(snapshot, produced.Select(a => a.Name)); // only this node's output: parallel branches keep theirs
                    _metrics.Rollback(node.Id);
                    _metrics.AttemptFinished(node.Id, false, attemptClock.Elapsed);
                    rt.LastError = critical.Message;
                    SetState(node, NodeState.RolledBack);
                    Log(node.Id, "node.rolledback", "engine", "critical policy violation");
                    SetState(node, NodeState.SafeStopped);
                    SafeStop($"Critical policy violation at '{node.Id}': [{critical.PolicyId}] {critical.Message}");
                    return null;
                }
                var high = violations.FirstOrDefault(v => v.Severity == Severity.High);
                if (high is not null) failure = $"policy {high.PolicyId}: {high.Message}";
            }

            // ---- exit gate
            if (failure is null && node.ExitGate is not null)
            {
                var gate = SafeGate(node.ExitGate);
                if (gate.Outcome == GateOutcome.Fail)
                {
                    failure = $"exit gate: {gate.Reason}";
                }
                else if (gate.Outcome == GateOutcome.NeedsClarification)
                {
                    _metrics.AttemptFinished(node.Id, true, attemptClock.Elapsed);
                    if (++clarificationRounds > _options.MaxClarificationRounds)
                    {
                        rt.LastError = gate.Reason;
                        SetState(node, NodeState.SafeStopped);
                        SafeStop($"'{node.Id}' still needs clarification after {_options.MaxClarificationRounds} rounds: {gate.Reason}");
                        return null;
                    }
                    await ClarifyAsync(node, gate, ct);
                    attempt--; // a human round-trip is not a failed attempt
                    continue;
                }
                else
                {
                    Log(node.Id, "gate.exit.passed", "engine", gate.Reason);
                }
            }

            // ---- orchestrator-owned validation (e.g. real build + tests in a sandbox)
            if (failure is null && node.Validation is not null)
            {
                Log(node.Id, "validation.started", "engine", "orchestrator validation");
                var validation = await SafeValidationAsync(node.Validation, ct);
                foreach (var evidence in validation.Evidence ?? Array.Empty<Artifact>())
                {
                    var stored = _ctx.Put(evidence);
                    produced = produced.Append(stored).ToList();
                    Log(node.Id, "artifact.produced", "engine:validation", $"{stored.Ref} sha256={stored.Hash[..12]}");
                }
                if (validation.Outcome == GateOutcome.Pass) Log(node.Id, "validation.passed", "engine", validation.Reason);
                else failure = $"validation: {validation.Reason}";
            }

            if (failure is null)
            {
                _metrics.AttemptFinished(node.Id, true, attemptClock.Elapsed);
                RecordNodeDecision(node, agent, snapshot, produced, summary, usingFallback);

                if (firstFailure is { } started)
                {
                    var mttr = DateTimeOffset.UtcNow - started;
                    _metrics.Recovery(mttr);
                    Log(node.Id, "node.recovered", "engine", $"recovered after {mttr.TotalMilliseconds:0} ms");
                }

                // ---- approval after running (e.g. schema change)
                if (node.Approval is { Timing: ApprovalTiming.AfterRun } after && after.Required(_ctx))
                {
                    var (proceed, replan) = await ApproveAsync(node, after, ct);
                    if (!proceed)
                    {
                        if (replan is null) _ctx.Restore(snapshot, produced.Select(a => a.Name)); // rejected: discard this node's unapproved output
                        return replan;
                    }
                }

                SetState(node, NodeState.Succeeded);
                rt.LastError = null;
                Log(node.Id, "node.succeeded", $"agent:{agent.Name}", summary);
                return null;
            }

            // ---- failure handling
            firstFailure ??= DateTimeOffset.UtcNow;
            rt.LastError = failure;
            previousFailure = failure;
            _metrics.AttemptFinished(node.Id, false, attemptClock.Elapsed);
            Log(node.Id, "node.failed", $"agent:{agent.Name}", failure);

            _ctx.Restore(snapshot, produced.Select(a => a.Name)); // only this node's output: parallel branches keep theirs
            _metrics.Rollback(node.Id);
            SetState(node, NodeState.RolledBack);
            Log(node.Id, "node.rolledback", "engine", "restored last known-good context");

            if (!usingFallback && retryable && attempt < node.Retry.MaxAttempts)
            {
                _metrics.Retry(node.Id);
                SetState(node, NodeState.Retrying);
                var delay = node.Retry.DelayFor(attempt);
                Log(node.Id, "node.retry", "engine", $"retry {attempt}/{node.Retry.MaxAttempts - 1} after {delay.TotalMilliseconds:0} ms");
                await _options.Delay(delay, ct);
                continue;
            }

            if (!usingFallback && node.Fallback is not null && (!retryable || node.FallbackWhen == FallbackWhen.AfterRetries))
            {
                usingFallback = true;
                _metrics.Fallback();
                Log(node.Id, "node.fallback", "engine",
                    $"{(retryable ? "retries exhausted" : "non-retryable failure")}, switching to fallback agent '{node.Fallback.Name}'");
                continue;
            }

            FailNode(node, failure);
            return null;
        }
    }

    /// <summary>
    /// Terminal failure: the node is Failed and every (transitive) dependent is Blocked with a recorded decision.
    /// Independent branches keep running; the run ends as Failed rather than safe-stopped (nothing unsafe happened).
    /// </summary>
    private void FailNode(WorkflowNode node, string failure)
    {
        SetState(node, NodeState.Failed);
        lock (_sync) _failureReason ??= $"Node '{node.Id}' failed after bounded retries: {failure}";
        Log(node.Id, "node.failed.final", "engine", failure);

        foreach (var id in _graph.SelfAndDependents(node.Id).Where(id => id != node.Id))
        {
            if (StateOf(_graph[id]) != NodeState.Pending) continue;
            SetState(id, NodeState.Blocked);
            var reason = $"dependency '{node.Id}' failed; blocked (no override decision recorded)";
            Log(id, "node.blocked", "engine", reason);
            _ctx.RecordDecision(new DecisionRecord(DateTimeOffset.UtcNow, id, "engine", "blocked", reason,
                Array.Empty<string>(), Array.Empty<string>()));
        }
    }

    private GateResult SafeGate(Func<RunContext, GateResult> gate)
    {
        try { return gate(_ctx); }
        catch (Exception ex) { return GateResult.Fail($"gate threw {ex.GetType().Name}: {ex.Message}"); }
    }

    private async Task<GateResult> SafeValidationAsync(Func<RunContext, CancellationToken, Task<GateResult>> validation, CancellationToken ct)
    {
        try { return await validation(_ctx, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return GateResult.Fail($"validation threw {ex.GetType().Name}: {ex.Message}"); }
    }

    private void RecordNodeDecision(WorkflowNode node, IAgent agent, ContextSnapshot snapshot,
        IReadOnlyList<Artifact> produced, string summary, bool fallback)
    {
        _ctx.RecordDecision(new DecisionRecord(
            DateTimeOffset.UtcNow, node.Id, $"agent:{agent.Name}",
            fallback ? $"{summary} (via fallback)" : summary,
            node.Description,
            snapshot.Latest.Values.Select(a => a.Ref).OrderBy(r => r, StringComparer.Ordinal).ToList(),
            produced.Select(a => a.Ref).ToList()));
    }

    // ---------------------------------------------------------------- human interaction

    private async Task ClarifyAsync(WorkflowNode node, GateResult gate, CancellationToken ct)
    {
        await _humanLock.WaitAsync(ct);
        try
        {
            var questions = gate.Questions ?? Array.Empty<string>();
            Log(node.Id, "clarification.requested", "engine", $"{gate.Reason} ({questions.Count} question(s))");
            var answers = await _human.ClarifyAsync(new ClarificationRequest(_ctx.RunId, node.Id, questions), ct);
            _metrics.Clarification();

            var qa = questions.Select((q, i) => new ClarificationAnswer(q, i < answers.Count ? answers[i] : string.Empty)).ToList();
            var existing = _ctx.GetFact(ClarificationFact.Key);
            var all = string.IsNullOrEmpty(existing)
                ? new List<ClarificationAnswer>()
                : System.Text.Json.JsonSerializer.Deserialize<List<ClarificationAnswer>>(existing, Json.Options)!;
            all.AddRange(qa);
            _ctx.SetFact(ClarificationFact.Key, Json.Serialize(all));

            _ctx.RecordDecision(new DecisionRecord(DateTimeOffset.UtcNow, node.Id, "human", "clarification provided",
                string.Join(" | ", qa.Select(x => $"{x.Question} => {x.Answer}")), Array.Empty<string>(), Array.Empty<string>()));
            Log(node.Id, "clarification.provided", "human", $"{qa.Count(x => x.Answer.Length > 0)} answer(s)");
        }
        finally
        {
            _humanLock.Release();
        }
    }

    private async Task<(bool Proceed, ReplanRequest? Replan)> ApproveAsync(WorkflowNode node, ApprovalSpec spec, CancellationToken ct)
    {
        await _humanLock.WaitAsync(ct);
        try
        {
            SetState(node, NodeState.WaitingForApproval);
            var request = new ApprovalRequest(_ctx.RunId, node.Id, spec.Reason, spec.Summary(_ctx),
                spec.Risks?.Invoke(_ctx) ?? Array.Empty<string>());
            Log(node.Id, "approval.requested", "engine", spec.Reason);

            var decision = await _human.RequestApprovalAsync(request, ct);
            _metrics.Approval();

            var actor = $"human:{decision.Reviewer}";
            var label = decision.Outcome switch
            {
                ApprovalOutcome.Approved => "approved",
                ApprovalOutcome.Rejected => "rejected",
                _ => "changes-requested"
            };
            _ctx.RecordDecision(new DecisionRecord(DateTimeOffset.UtcNow, node.Id, actor, label,
                decision.Note ?? spec.Reason, _ctx.Latest().Select(a => a.Ref).ToList(), Array.Empty<string>()));
            Log(node.Id, $"approval.{label}", actor, decision.Note ?? "");
            PersistState();

            switch (decision.Outcome)
            {
                case ApprovalOutcome.Approved:
                    return (true, null);

                case ApprovalOutcome.Rejected:
                    SetState(node, NodeState.SafeStopped);
                    SafeStop($"Approval rejected at '{node.Id}' by {decision.Reviewer}: {decision.Note ?? "no reason given"}");
                    return (false, null);

                default:
                    var target = decision.TargetNode
                                 ?? (spec.Timing == ApprovalTiming.AfterRun ? node.Id : node.DependsOn.FirstOrDefault() ?? node.Id);
                    if (!_graph.Contains(target))
                    {
                        SetState(node, NodeState.SafeStopped);
                        SafeStop($"Change request at '{node.Id}' targets unknown node '{target}'.");
                        return (false, null);
                    }
                    if (_replans >= _options.MaxReplans)
                    {
                        SetState(node, NodeState.SafeStopped);
                        SafeStop($"Re-plan budget ({_options.MaxReplans}) exhausted at '{node.Id}'.");
                        return (false, null);
                    }
                    return (false, new ReplanRequest(target, decision.Note ?? "Changes requested", actor, node.Id));
            }
        }
        finally
        {
            _humanLock.Release();
        }
    }

    // ---------------------------------------------------------------- re-planning

    /// <summary>
    /// Invalidates the target node and everything downstream of it (they become Stale, then Pending) while
    /// keeping already-valid, unaffected branches. The change request is stored so agents can honour it, and the
    /// plan version is incremented so every artifact can be traced to the plan it was produced under.
    /// </summary>
    private void ApplyReplan(ReplanRequest request)
    {
        _replans++;
        _planVersion++;
        _metrics.Replan();
        _ctx.AddChangeRequest(new ChangeRequest(request.Target, request.Note, request.RequestedBy, DateTimeOffset.UtcNow));

        var affected = _graph.SelfAndDependents(request.Target).ToHashSet(StringComparer.Ordinal);
        affected.Add(request.FromNode); // the node that asked must run its approval again
        var preserved = _graph.Nodes.Select(n => n.Id).Where(id => !affected.Contains(id)).ToList();

        foreach (var id in affected.OrderBy(x => x, StringComparer.Ordinal))
        {
            var state = StateOf(_graph[id]);
            if (state == NodeState.Pending) continue;
            if (!NodeStateMachine.CanTransition(state, NodeState.Stale))
            {
                Log(id, "node.replan.skipped", "engine", $"cannot re-plan a node in state {state}");
                continue;
            }
            SetState(id, NodeState.Stale);
            Log(id, "node.stale", "engine", $"invalidated by change request on '{request.Target}'");
            SetState(id, NodeState.Pending);
        }

        var detail = $"plan v{_planVersion}; target={request.Target}; note={request.Note}; re-run=[{string.Join(", ", affected.OrderBy(x => x, StringComparer.Ordinal))}]; preserved=[{string.Join(", ", preserved)}]";
        Log(null, "replan", request.RequestedBy, detail);
        _ctx.RecordDecision(new DecisionRecord(DateTimeOffset.UtcNow, request.FromNode, "engine", "re-plan", detail,
            _ctx.Latest().Select(a => a.Ref).ToList(), Array.Empty<string>()));
        PersistState();
    }
}

/// <summary>Stored human answers to clarification questions (shared with agents through a run fact).</summary>
public sealed record ClarificationAnswer(string Question, string Answer);

public static class ClarificationFact
{
    public const string Key = "clarifications";

    public static IReadOnlyList<ClarificationAnswer> Read(RunContext ctx)
    {
        var raw = ctx.GetFact(Key);
        return string.IsNullOrEmpty(raw)
            ? Array.Empty<ClarificationAnswer>()
            : System.Text.Json.JsonSerializer.Deserialize<List<ClarificationAnswer>>(raw, Json.Options)!;
    }
}
