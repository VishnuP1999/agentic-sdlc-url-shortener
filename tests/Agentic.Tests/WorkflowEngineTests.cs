using Agentic.Core;
using Xunit;

namespace Agentic.Tests;

internal sealed class FuncAgent : IAgent
{
    private readonly Func<AgentRequest, CancellationToken, Task<AgentResult>> _run;

    public FuncAgent(string name, Func<AgentRequest, CancellationToken, Task<AgentResult>> run)
    {
        Name = name;
        _run = run;
    }

    public FuncAgent(string name, Func<AgentRequest, AgentResult> run) : this(name, (r, _) => Task.FromResult(run(r))) { }

    public string Name { get; }

    public Task<AgentResult> ExecuteAsync(AgentRequest request, CancellationToken ct) => _run(request, ct);
}

internal static class T
{
    public static AgentResult Out(string artifact, string content = "ok") =>
        new(new[] { Artifact.Create(artifact, "text", content, "test-agent") }, $"produced {artifact}");

    public static IAgent Ok(string artifact, string content = "ok") => new FuncAgent($"ok-{artifact}", _ => Out(artifact, content));

    public static EngineOptions Fast(int maxReplans = 2, int maxClarifications = 2, TimeSpan? timeout = null) => new()
    {
        MaxReplans = maxReplans,
        MaxClarificationRounds = maxClarifications,
        NodeTimeout = timeout ?? TimeSpan.FromSeconds(10),
        Delay = (_, _) => Task.CompletedTask
    };

    public static PolicyEngine Policies() => new(new IPolicy[] { new NoSecretsPolicy() });

    public static WorkflowNode Node(string id, IAgent agent, params string[] deps) =>
        new(id, agent) { DependsOn = deps, Retry = new RetryPolicy(3, 0) };

    public static NodeReport Report(RunResult r, string id) => r.Nodes.Single(n => n.Id == id);

    public static ApprovalSpec Approve(ApprovalTiming timing = ApprovalTiming.BeforeRun) =>
        new(timing, _ => true, "needs a human", _ => "summary");
}

public class SchedulingTests
{
    [Fact]
    public async Task Independent_branches_run_in_parallel_and_the_join_waits_for_both()
    {
        var running = 0;
        var maxRunning = 0;
        var finished = new List<string>();
        var startedAtJoin = new List<string>();
        var gate = new object();

        IAgent Branch(string name) => new FuncAgent(name, async (_, _) =>
        {
            var now = Interlocked.Increment(ref running);
            lock (gate) maxRunning = Math.Max(maxRunning, now);
            await Task.Delay(150);
            lock (gate) finished.Add(name);
            Interlocked.Decrement(ref running);
            return T.Out(name);
        });

        var join = new FuncAgent("join", _ =>
        {
            lock (gate) startedAtJoin.AddRange(finished);
            return T.Out("join");
        });

        var graph = new WorkflowGraph()
            .Add(T.Node("root", T.Ok("root")))
            .Add(T.Node("b", Branch("b"), "root"))
            .Add(T.Node("c", Branch("c"), "root"))
            .Add(T.Node("join", join, "b", "c"));

        var result = await new WorkflowEngine(graph, new ScriptedHuman(), T.Policies(), T.Fast()).RunAsync(new RunContext("r"));

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(2, maxRunning);
        Assert.Contains("b", startedAtJoin);
        Assert.Contains("c", startedAtJoin);
    }

    [Fact]
    public async Task Graph_validation_rejects_cycles_missing_dependencies_and_duplicates()
    {
        var cyclic = new WorkflowGraph().Add(T.Node("a", T.Ok("a"), "b")).Add(T.Node("b", T.Ok("b"), "a"));
        Assert.Throws<InvalidOperationException>(() => cyclic.Validate());

        var missing = new WorkflowGraph().Add(T.Node("a", T.Ok("a"), "ghost"));
        Assert.Throws<InvalidOperationException>(() => missing.Validate());

        var graph = new WorkflowGraph().Add(T.Node("a", T.Ok("a")));
        Assert.Throws<InvalidOperationException>(() => graph.Add(T.Node("a", T.Ok("a"))));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new WorkflowEngine(cyclic, new ScriptedHuman(), T.Policies(), T.Fast()).RunAsync(new RunContext("r")));
    }

    [Fact]
    public void Dependents_are_computed_transitively()
    {
        var graph = new WorkflowGraph()
            .Add(T.Node("a", T.Ok("a")))
            .Add(T.Node("b", T.Ok("b"), "a"))
            .Add(T.Node("c", T.Ok("c"), "b"))
            .Add(T.Node("x", T.Ok("x")));
        var affected = graph.SelfAndDependents("b");
        Assert.True(affected.SetEquals(new[] { "b", "c" }));
        Assert.Contains("flowchart TD", graph.ToMermaid());
    }
}

public class ReliabilityTests
{
    [Fact]
    public async Task Transient_failure_is_retried_rolled_back_and_recovery_time_is_measured()
    {
        var calls = 0;
        var flaky = new FuncAgent("flaky", _ => ++calls < 3 ? throw new InvalidOperationException("boom") : T.Out("a"));
        var graph = new WorkflowGraph().Add(T.Node("a", flaky));

        var result = await new WorkflowEngine(graph, new ScriptedHuman(), T.Policies(), T.Fast()).RunAsync(new RunContext("r"));

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(3, T.Report(result, "a").Attempts);
        Assert.Equal(2, result.Metrics.Retries);
        Assert.Equal(2, result.Metrics.Rollbacks);
        Assert.Equal(1, result.Metrics.Recoveries);
        Assert.NotNull(result.Metrics.MttrMs);
        Assert.Equal(1.0, result.Metrics.NodeSuccessRate);
        Assert.Equal(1.0 / 3.0, result.Metrics.AttemptSuccessRate, 2);
    }

    [Fact]
    public async Task Retry_budget_is_bounded_the_node_fails_and_its_dependents_are_blocked()
    {
        var calls = 0;
        var broken = new FuncAgent("broken", _ => { calls++; throw new InvalidOperationException("always"); });
        var ctx = new RunContext("r");
        var graph = new WorkflowGraph()
            .Add(new WorkflowNode("a", broken) { Retry = new RetryPolicy(2, 0) })
            .Add(T.Node("b", T.Ok("b"), "a"))
            .Add(T.Node("c", T.Ok("c"), "b"))
            .Add(T.Node("independent", T.Ok("i")));

        var result = await new WorkflowEngine(graph, new ScriptedHuman(), T.Policies(), T.Fast()).RunAsync(ctx);

        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Equal(2, calls);
        Assert.Equal(NodeState.Failed, T.Report(result, "a").State);
        Assert.Equal(NodeState.Blocked, T.Report(result, "b").State);       // direct dependent
        Assert.Equal(NodeState.Blocked, T.Report(result, "c").State);       // transitive dependent
        Assert.Equal(NodeState.Succeeded, T.Report(result, "independent").State); // unaffected branch still completes
        Assert.Contains("failed after bounded retries", result.StopReason);
        Assert.Equal(2, ctx.Decisions().Count(d => d.Decision == "blocked" && d.Actor == "engine")); // explicit, recorded
        Assert.True(result.Audit.Any(e => e.Event == "node.blocked" && e.NodeId == "c"));
    }

    [Fact]
    public async Task Non_retryable_failure_skips_retries_and_goes_straight_to_the_fallback()
    {
        var calls = 0;
        var unavailable = new FuncAgent("ai", _ => { calls++; throw new NonRetryableAgentException("model not configured"); });
        var graph = new WorkflowGraph().Add(new WorkflowNode("a", unavailable)
        {
            Retry = new RetryPolicy(3, 0),
            Fallback = T.Ok("a", "from rules"),
            FallbackWhen = FallbackWhen.OnlyWhenUnavailable
        });

        var result = await new WorkflowEngine(graph, new ScriptedHuman(), T.Policies(), T.Fast()).RunAsync(new RunContext("r"));

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(1, calls);
        Assert.Equal(0, result.Metrics.Retries);
        Assert.Equal(1, result.Metrics.Fallbacks);
    }

    [Fact]
    public async Task Fallback_restricted_to_unavailability_does_not_mask_repeated_bad_output()
    {
        var graph = new WorkflowGraph().Add(new WorkflowNode("impl", T.Ok("code", "broken"))
        {
            Retry = new RetryPolicy(2, 0),
            ExitGate = _ => GateResult.Fail("does not build"),
            Fallback = T.Ok("code", "no change"),
            FallbackWhen = FallbackWhen.OnlyWhenUnavailable
        });

        var result = await new WorkflowEngine(graph, new ScriptedHuman(), T.Policies(), T.Fast()).RunAsync(new RunContext("r"));

        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Equal(0, result.Metrics.Fallbacks);
        Assert.Equal(NodeState.Failed, T.Report(result, "impl").State);
    }

    [Fact]
    public async Task Repair_attempt_receives_the_previous_failure_as_feedback()
    {
        var feedback = new List<string?>();
        var agent = new FuncAgent("dev", r =>
        {
            feedback.Add(r.PreviousFailure);
            return T.Out("code", r.PreviousFailure is null ? "v1" : "v2-fixed");
        });
        var node = new WorkflowNode("impl", agent)
        {
            Retry = new RetryPolicy(3, 0),
            Validation = (c, _) => Task.FromResult(c.Get("code")!.Content == "v1"
                ? GateResult.Fail("CS0103: name 'Foo' does not exist", Artifact.Create("validation-report", "text", "build failed", "sandbox"))
                : GateResult.Pass("built", Artifact.Create("validation-report", "text", "build ok", "sandbox")))
        };
        var ctx = new RunContext("r");

        var result = await new WorkflowEngine(new WorkflowGraph().Add(node), new ScriptedHuman(), T.Policies(), T.Fast()).RunAsync(ctx);

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Null(feedback[0]);
        Assert.Contains("CS0103", feedback[1]);
        Assert.Equal("v2-fixed", ctx.Get("code")!.Content);
        Assert.Equal("build ok", ctx.Get("validation-report")!.Content);                       // evidence of the passing run
        Assert.True(ctx.History().Any(a => a.Name == "validation-report" && a.Content == "build failed")); // failing evidence kept in lineage
        Assert.True(result.Audit.Any(e => e.Event == "validation.passed"));
    }

    [Fact]
    public async Task Fallback_agent_takes_over_after_retries_are_exhausted()
    {
        var primary = new FuncAgent("primary", _ => throw new InvalidOperationException("down"));
        var fallback = new FuncAgent("fallback", _ => T.Out("doc", "from fallback"));
        var graph = new WorkflowGraph().Add(new WorkflowNode("docs", primary) { Retry = new RetryPolicy(2, 0), Fallback = fallback });
        var ctx = new RunContext("r");

        var result = await new WorkflowEngine(graph, new ScriptedHuman(), T.Policies(), T.Fast()).RunAsync(ctx);

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(1, result.Metrics.Fallbacks);
        Assert.Equal("from fallback", ctx.Get("doc")!.Content);
        Assert.True(ctx.Decisions().Any(d => d.Decision.Contains("via fallback")));
    }

    [Fact]
    public async Task Failed_output_is_rolled_back_to_the_last_known_good_context()
    {
        var ctx = new RunContext("r");
        var first = T.Node("first", T.Ok("doc", "v1-good"));
        var second = new WorkflowNode("second", new FuncAgent("bad", _ => T.Out("doc", "bad")))
        {
            DependsOn = new[] { "first" },
            Retry = new RetryPolicy(2, 0),
            ExitGate = c => c.Get("doc")!.Content == "bad" ? GateResult.Fail("content invalid") : GateResult.Pass()
        };
        var graph = new WorkflowGraph().Add(first).Add(second);

        var result = await new WorkflowEngine(graph, new ScriptedHuman(), T.Policies(), T.Fast()).RunAsync(ctx);

        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Equal("v1-good", ctx.Get("doc")!.Content);        // latest view restored
        Assert.True(ctx.History().Any(a => a.Content == "bad")); // lineage still shows what was attempted
        Assert.Equal(2, result.Metrics.Rollbacks);
    }

    [Fact]
    public async Task Rollback_in_one_branch_never_discards_a_parallel_branch_output()
    {
        // Regression (found by the AI acceptance run): implementation failed after docs had finished in parallel,
        // and the whole-context rollback erased the docs artifact, so release correctly refused to ship.
        var docsDone = new TaskCompletionSource();
        var calls = 0;
        var docs = new FuncAgent("docs", _ =>
        {
            var result = T.Out("api-docs", "documented");
            return result;
        });
        var impl = new FuncAgent("impl", async (_, _) =>
        {
            if (++calls == 1)
            {
                await docsDone.Task;                       // fail only after the sibling branch has produced output
                throw new InvalidOperationException("flaky build");
            }
            return T.Out("code");
        });
        var ctx = new RunContext("r");
        var graph = new WorkflowGraph()
            .Add(T.Node("design", T.Ok("design")))
            .Add(T.Node("impl", impl, "design"))
            .Add(new WorkflowNode("docs", docs)
            {
                DependsOn = new[] { "design" },
                Retry = new RetryPolicy(3, 0),
                ExitGate = _ => { docsDone.TrySetResult(); return GateResult.Pass(); }
            })
            .Add(T.Node("release", new FuncAgent("release", r =>
                r.Context.Get("api-docs") is null ? throw new InvalidOperationException("docs missing") : T.Out("release")), "impl", "docs"));

        var result = await new WorkflowEngine(graph, new ScriptedHuman(), T.Policies(), T.Fast()).RunAsync(ctx);

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(2, T.Report(result, "impl").Attempts);   // the failure and rollback really happened
        Assert.Equal("documented", ctx.Get("api-docs")!.Content);
        Assert.Equal(1, T.Report(result, "release").Attempts);
    }

    [Fact]
    public async Task Timeout_counts_as_a_failure()
    {
        var hang = new FuncAgent("hang", async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return T.Out("a");
        });
        var graph = new WorkflowGraph().Add(new WorkflowNode("a", hang) { Retry = new RetryPolicy(1, 0) });

        var result = await new WorkflowEngine(graph, new ScriptedHuman(), T.Policies(), T.Fast(timeout: TimeSpan.FromMilliseconds(150)))
            .RunAsync(new RunContext("r"));

        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Contains("timed out", T.Report(result, "a").LastError);
    }

    [Fact]
    public async Task Kill_switch_stops_the_run_and_blocks_downstream_work()
    {
        WorkflowEngine? engine = null;
        var downstreamRan = false;
        var a = new FuncAgent("a", _ =>
        {
            engine!.RequestSafeStop("operator pressed stop");
            return T.Out("a");
        });
        var b = new FuncAgent("b", _ => { downstreamRan = true; return T.Out("b"); });
        var graph = new WorkflowGraph().Add(T.Node("a", a)).Add(T.Node("b", b, "a"));
        engine = new WorkflowEngine(graph, new ScriptedHuman(), T.Policies(), T.Fast());

        var result = await engine.RunAsync(new RunContext("r"));

        Assert.Equal(RunStatus.SafeStopped, result.Status);
        Assert.False(downstreamRan);
        Assert.Contains("operator pressed stop", result.StopReason);
        Assert.Equal(NodeState.Skipped, T.Report(result, "b").State);
    }

    [Fact]
    public async Task Safe_stop_cancels_in_flight_parallel_work()
    {
        WorkflowEngine? engine = null;
        var slow = new FuncAgent("slow", async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return T.Out("slow");
        });
        var stopper = new FuncAgent("stopper", async (_, _) =>
        {
            await Task.Delay(50);
            engine!.RequestSafeStop("stop");
            return T.Out("stopper");
        });
        var graph = new WorkflowGraph().Add(T.Node("slow", slow)).Add(T.Node("stopper", stopper));
        engine = new WorkflowEngine(graph, new ScriptedHuman(), T.Policies(), T.Fast());

        var result = await engine.RunAsync(new RunContext("r"));

        Assert.Equal(RunStatus.SafeStopped, result.Status);
        Assert.Equal(NodeState.Cancelled, T.Report(result, "slow").State);
    }

    [Fact]
    public async Task Entry_gate_failure_safe_stops_before_the_agent_runs()
    {
        var ran = false;
        var node = new WorkflowNode("a", new FuncAgent("a", _ => { ran = true; return T.Out("a"); }))
        {
            EntryGate = _ => GateResult.Fail("precondition missing")
        };
        var result = await new WorkflowEngine(new WorkflowGraph().Add(node), new ScriptedHuman(), T.Policies(), T.Fast())
            .RunAsync(new RunContext("r"));

        Assert.False(ran);
        Assert.Equal(RunStatus.SafeStopped, result.Status);
        Assert.Contains("precondition missing", result.StopReason);
    }
}

public class GovernanceTests
{
    [Fact]
    public async Task Critical_policy_violation_is_not_retried_and_the_output_is_discarded()
    {
        var ctx = new RunContext("r");
        var leaky = new FuncAgent("leaky", _ => T.Out("code", "var password = \"hunter2-prod\";"));
        var graph = new WorkflowGraph().Add(T.Node("impl", leaky)).Add(T.Node("release", T.Ok("rel"), "impl"));

        var result = await new WorkflowEngine(graph, new ScriptedHuman(), T.Policies(), T.Fast()).RunAsync(ctx);

        Assert.Equal(RunStatus.SafeStopped, result.Status);
        Assert.Equal(1, T.Report(result, "impl").Attempts);
        Assert.Null(ctx.Get("code"));
        Assert.Contains("SEC-001", result.StopReason);
        Assert.Equal(NodeState.SafeStopped, T.Report(result, "impl").State);
        Assert.Equal(NodeState.Skipped, T.Report(result, "release").State);
    }

    [Fact]
    public async Task Approval_gate_pauses_for_the_human_and_proceeds_when_approved()
    {
        var graph = new WorkflowGraph()
            .Add(T.Node("build", T.Ok("build")))
            .Add(new WorkflowNode("release", T.Ok("rel")) { DependsOn = new[] { "build" }, Approval = T.Approve() });

        var result = await new WorkflowEngine(graph, new ScriptedHuman(new[] { "approve" }), T.Policies(), T.Fast())
            .RunAsync(new RunContext("r"));

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(1, result.Metrics.Approvals);
        Assert.True(result.Decisions.Any(d => d.NodeId == "release" && d.Actor.StartsWith("human:") && d.Decision == "approved"));
    }

    [Fact]
    public async Task Rejection_and_silence_both_stop_the_run()
    {
        WorkflowGraph Graph() => new WorkflowGraph()
            .Add(T.Node("build", T.Ok("build")))
            .Add(new WorkflowNode("release", T.Ok("rel")) { DependsOn = new[] { "build" }, Approval = T.Approve() });

        var rejected = await new WorkflowEngine(Graph(), new ScriptedHuman(new[] { "reject:not ready" }), T.Policies(), T.Fast())
            .RunAsync(new RunContext("r"));
        Assert.Equal(RunStatus.SafeStopped, rejected.Status);
        Assert.Contains("not ready", rejected.StopReason);

        var silent = await new WorkflowEngine(Graph(), new ScriptedHuman(), T.Policies(), T.Fast()).RunAsync(new RunContext("r"));
        Assert.Equal(RunStatus.SafeStopped, silent.Status); // no decision available => safe default is reject
        Assert.Equal(NodeState.SafeStopped, T.Report(silent, "release").State);
    }

    [Fact]
    public async Task Rejecting_an_after_run_approval_discards_the_unapproved_output()
    {
        var ctx = new RunContext("r");
        var graph = new WorkflowGraph().Add(new WorkflowNode("design", T.Ok("design", "schema v1")) { Approval = T.Approve(ApprovalTiming.AfterRun) });

        var result = await new WorkflowEngine(graph, new ScriptedHuman(new[] { "reject" }), T.Policies(), T.Fast()).RunAsync(ctx);

        Assert.Equal(RunStatus.SafeStopped, result.Status);
        Assert.Null(ctx.Get("design"));
    }

    [Fact]
    public async Task Audit_trail_is_hash_chained_and_tampering_is_detected()
    {
        var graph = new WorkflowGraph().Add(T.Node("a", T.Ok("a"))).Add(T.Node("b", T.Ok("b"), "a"));
        var result = await new WorkflowEngine(graph, new ScriptedHuman(), T.Policies(), T.Fast()).RunAsync(new RunContext("r"));

        Assert.True(result.AuditChainValid);
        Assert.True(result.Audit.Count >= 6);

        var tampered = result.Audit.ToList();
        tampered[2] = tampered[2] with { Detail = "something else happened" };
        Assert.False(AuditLog.Verify(tampered));

        var removed = result.Audit.Where((_, i) => i != 3).ToList();
        Assert.False(AuditLog.Verify(removed));
    }

    [Fact]
    public void Audit_log_redacts_personal_data()
    {
        var log = new AuditLog("run");
        var entry = log.Append("n", "evt", "actor", "reviewer jane.doe@example.com approved");
        Assert.False(entry.Detail.Contains("@"));
        Assert.Contains("[redacted-email]", entry.Detail);
    }

    [Fact]
    public async Task Decision_lineage_links_inputs_to_outputs()
    {
        var graph = new WorkflowGraph().Add(T.Node("a", T.Ok("alpha"))).Add(T.Node("b", T.Ok("beta"), "a"));
        var result = await new WorkflowEngine(graph, new ScriptedHuman(), T.Policies(), T.Fast()).RunAsync(new RunContext("r"));

        var decision = result.Decisions.Single(d => d.NodeId == "b");
        Assert.Contains("alpha@v1", decision.Inputs);
        Assert.Contains("beta@v1", decision.Outputs);
    }
}

public class ReplanTests
{
    private static (WorkflowGraph Graph, Dictionary<string, int> Runs) Diamond()
    {
        var runs = new Dictionary<string, int>();
        IAgent Counting(string id) => new FuncAgent(id, _ =>
        {
            lock (runs) runs[id] = runs.GetValueOrDefault(id) + 1;
            return T.Out(id);
        });

        var graph = new WorkflowGraph()
            .Add(T.Node("root", Counting("root")))
            .Add(T.Node("a", Counting("a"), "root"))
            .Add(T.Node("b", Counting("b"), "root"))
            .Add(T.Node("join", Counting("join"), "a", "b"))
            .Add(new WorkflowNode("release", Counting("release")) { DependsOn = new[] { "join" }, Approval = T.Approve() });
        return (graph, runs);
    }

    [Fact]
    public async Task Change_request_re_runs_the_target_and_its_dependents_but_keeps_unaffected_branches()
    {
        var (graph, runs) = Diamond();
        var human = new ScriptedHuman(new[] { "request_changes:a:tighten the contract", "approve" });
        var ctx = new RunContext("r");

        var result = await new WorkflowEngine(graph, human, T.Policies(), T.Fast()).RunAsync(ctx);

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(1, runs["root"]);
        Assert.Equal(2, runs["a"]);
        Assert.Equal(1, runs["b"]);     // unaffected branch preserved
        Assert.Equal(2, runs["join"]);  // dependent of the target re-run
        Assert.Equal(1, runs["release"]);
        Assert.Equal(1, result.Metrics.Replans);
        Assert.Equal("tighten the contract", ctx.ChangeRequests().Single().Note);
        Assert.Equal(2, ctx.Get("a")!.Version);
    }

    [Fact]
    public async Task Default_target_is_the_upstream_node_of_the_approval()
    {
        var (graph, runs) = Diamond();
        var human = new ScriptedHuman(new[] { "request_changes:please rework", "approve" });

        var result = await new WorkflowEngine(graph, human, T.Policies(), T.Fast()).RunAsync(new RunContext("r"));

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(2, runs["join"]);
        Assert.Equal(1, runs["a"]);
    }

    [Fact]
    public async Task Unknown_change_target_safe_stops()
    {
        var (graph, _) = Diamond();
        var result = await new WorkflowEngine(graph, new ScriptedHuman(new[] { "request_changes:ghost:note" }), T.Policies(), T.Fast())
            .RunAsync(new RunContext("r"));
        Assert.Equal(RunStatus.SafeStopped, result.Status);
        Assert.Contains("unknown node", result.StopReason);
    }

    [Fact]
    public async Task Re_planning_is_bounded()
    {
        var (graph, _) = Diamond();
        var endless = new ScriptedHuman(Enumerable.Repeat("request_changes:a:again", 10));

        var result = await new WorkflowEngine(graph, endless, T.Policies(), T.Fast(maxReplans: 2)).RunAsync(new RunContext("r"));

        Assert.Equal(RunStatus.SafeStopped, result.Status);
        Assert.Equal(2, result.Metrics.Replans);
        Assert.Contains("budget", result.StopReason);
    }

    [Fact]
    public async Task Ambiguity_pauses_the_run_for_clarification_and_continues_with_the_answers()
    {
        var ctx = new RunContext("make it faster");
        var requirements = new FuncAgent("req", r => T.Out("requirement", ClarificationFact.Read(r.Context).Count == 0 ? "ambiguous" : "clear"));
        var node = new WorkflowNode("requirements", requirements)
        {
            ExitGate = c => c.Get("requirement")!.Content == "ambiguous"
                ? GateResult.NeedsHuman("vague wording", new[] { "What latency target?" })
                : GateResult.Pass()
        };
        var human = new ScriptedHuman(clarificationAnswers: new[] { "p95 under 50 ms" });

        var result = await new WorkflowEngine(new WorkflowGraph().Add(node), human, T.Policies(), T.Fast()).RunAsync(ctx);

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.Equal(1, result.Metrics.Clarifications);
        Assert.Equal("clear", ctx.Get("requirement")!.Content);
        Assert.Equal("p95 under 50 ms", ClarificationFact.Read(ctx).Single().Answer);
        Assert.Equal(0, result.Metrics.Retries); // a human round-trip is not a failure
    }

    [Fact]
    public async Task Unanswered_clarifications_are_bounded()
    {
        var node = new WorkflowNode("requirements", T.Ok("requirement"))
        {
            ExitGate = _ => GateResult.NeedsHuman("still vague", new[] { "Define 'better'" })
        };
        var result = await new WorkflowEngine(new WorkflowGraph().Add(node), new ScriptedHuman(), T.Policies(), T.Fast(maxClarifications: 2))
            .RunAsync(new RunContext("r"));

        Assert.Equal(RunStatus.SafeStopped, result.Status);
        Assert.Equal(2, result.Metrics.Clarifications);
        Assert.Contains("clarification", result.StopReason);
    }
}
