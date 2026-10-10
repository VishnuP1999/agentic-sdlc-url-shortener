using System.Text.Json;
using Agentic.Core;
using Xunit;

namespace Agentic.Tests;

public class StateMachineTests
{
    [Fact]
    public void Legal_transitions_follow_the_task_lifecycle()
    {
        Assert.True(NodeStateMachine.CanTransition(NodeState.Pending, NodeState.Ready));
        Assert.True(NodeStateMachine.CanTransition(NodeState.Ready, NodeState.Running));
        Assert.True(NodeStateMachine.CanTransition(NodeState.Running, NodeState.Validating));
        Assert.True(NodeStateMachine.CanTransition(NodeState.Validating, NodeState.WaitingForApproval));
        Assert.True(NodeStateMachine.CanTransition(NodeState.Validating, NodeState.RolledBack));
        Assert.True(NodeStateMachine.CanTransition(NodeState.RolledBack, NodeState.Retrying));
        Assert.True(NodeStateMachine.CanTransition(NodeState.RolledBack, NodeState.Failed));
        Assert.True(NodeStateMachine.CanTransition(NodeState.Succeeded, NodeState.Stale));
        Assert.True(NodeStateMachine.CanTransition(NodeState.Pending, NodeState.Blocked));
    }

    [Fact]
    public void Illegal_transitions_are_rejected_and_terminal_states_are_final()
    {
        Assert.False(NodeStateMachine.CanTransition(NodeState.Pending, NodeState.Succeeded)); // cannot skip execution and validation
        Assert.False(NodeStateMachine.CanTransition(NodeState.Running, NodeState.Succeeded)); // output must be validated first
        Assert.False(NodeStateMachine.CanTransition(NodeState.Blocked, NodeState.Running));   // blocked work never runs silently
        Assert.False(NodeStateMachine.CanTransition(NodeState.Failed, NodeState.Retrying));
        foreach (var terminal in new[] { NodeState.Failed, NodeState.Blocked, NodeState.Cancelled, NodeState.Skipped, NodeState.SafeStopped })
        {
            Assert.True(NodeStateMachine.IsTerminal(terminal), $"{terminal} should be terminal");
            foreach (var to in Enum.GetValues<NodeState>()) Assert.False(NodeStateMachine.CanTransition(terminal, to));
        }
    }

    [Fact]
    public async Task Every_transition_in_a_complex_run_is_legal_and_the_engine_ends_in_a_terminal_or_success_state()
    {
        // retries, validation, approval with a re-plan and a parallel branch exercise most transitions;
        // the engine throws on any illegal transition, so completing proves the flow stays inside the state machine
        var calls = 0;
        var graph = new WorkflowGraph()
            .Add(T.Node("design", T.Ok("design")))
            .Add(new WorkflowNode("impl", new FuncAgent("impl", _ => ++calls == 1 ? throw new InvalidOperationException("flaky") : T.Out("code")))
            {
                DependsOn = new[] { "design" }, Retry = new RetryPolicy(3, 0),
                Validation = (_, _) => Task.FromResult(GateResult.Pass("built"))
            })
            .Add(T.Node("docs", T.Ok("docs"), "design"))
            .Add(new WorkflowNode("release", T.Ok("release")) { DependsOn = new[] { "impl", "docs" }, Approval = T.Approve() });
        var human = new ScriptedHuman(new[] { "request_changes:docs:mention limits", "approve" });

        var result = await new WorkflowEngine(graph, human, T.Policies(), T.Fast()).RunAsync(new RunContext("r"));

        Assert.Equal(RunStatus.Completed, result.Status);
        Assert.True(result.Nodes.All(n => n.State == NodeState.Succeeded));
        Assert.Equal(2, result.PlanVersion);
        Assert.True(result.Audit.Any(e => e.Event == "node.ready"));
        Assert.True(result.Audit.Any(e => e.Event == "replan" && e.Detail.Contains("plan v2")));
    }
}

public class ScriptedHumanTests
{
    [Fact]
    public async Task Keyed_answers_follow_the_question_not_the_position()
    {
        var human = new ScriptedHuman(clarificationAnswers: new[]
        {
            "safe,malicious=>block private networks",
            "fast,latency=>p95 under 50 ms",
            "plain answer"
        });
        // the model asked about speed first, then safety, then something unkeyed
        var answers = await human.ClarifyAsync(new ClarificationRequest("r", "requirements", new[]
        {
            "Does 'faster' mean redirect latency?",
            "Should 'safer' include a malware blocklist?",
            "Which retention period applies?"
        }), default);

        Assert.Equal("p95 under 50 ms", answers[0]);
        Assert.Equal("block private networks", answers[1]);
        Assert.Equal("plain answer", answers[2]);
    }

    [Fact]
    public async Task Unmatched_questions_get_no_answer_and_unused_answers_wait_for_the_next_round()
    {
        var human = new ScriptedHuman(clarificationAnswers: new[] { "latency=>p95 under 50 ms" });
        var first = await human.ClarifyAsync(new ClarificationRequest("r", "n", new[] { "Which storage?" }), default);
        Assert.Equal("", first[0]);
        var second = await human.ClarifyAsync(new ClarificationRequest("r", "n", new[] { "What latency target?" }), default);
        Assert.Equal("p95 under 50 ms", second[0]);
    }
}

public class PermissionTests
{
    [Fact]
    public async Task A_node_cannot_write_artifacts_outside_its_permissions()
    {
        var sneaky = new FuncAgent("sneaky", _ => new AgentResult(new[]
        {
            Artifact.Create("docs", "markdown", "fine", "sneaky"),
            Artifact.Create("security-report", "json", "{\"passed\":true}", "sneaky")
        }, "wrote too much"));
        var ctx = new RunContext("r");
        var graph = new WorkflowGraph().Add(new WorkflowNode("docs", sneaky) { Produces = new[] { "docs" }, Retry = new RetryPolicy(1, 0) });

        var result = await new WorkflowEngine(graph, new ScriptedHuman(), T.Policies(), T.Fast()).RunAsync(ctx);

        Assert.Equal(RunStatus.Failed, result.Status);
        Assert.Contains("permission denied", T.Report(result, "docs").LastError);
        Assert.Null(ctx.Get("security-report"));
        Assert.Null(ctx.Get("docs")); // nothing from a rejected attempt is kept
        Assert.True(result.Audit.Any(e => e.Event == "permission.denied"));
    }
}

public class PersistenceAndObservabilityTests
{
    [Fact]
    public async Task Workflow_state_is_persisted_while_the_run_progresses()
    {
        var dir = Path.Combine(Path.GetTempPath(), "agentic-tests", Guid.NewGuid().ToString("N"));
        var statePath = Path.Combine(dir, "state.json");
        string? midRun = null;
        var graph = new WorkflowGraph()
            .Add(T.Node("a", T.Ok("a")))
            .Add(T.Node("b", new FuncAgent("b", _ => { midRun = File.ReadAllText(statePath); return T.Out("b"); }), "a"));

        try
        {
            var result = await new WorkflowEngine(graph, new ScriptedHuman(), T.Policies(), T.Fast() with { StateFilePath = statePath })
                .RunAsync(new RunContext("r"));

            Assert.Equal(RunStatus.Completed, result.Status);
            using var during = JsonDocument.Parse(midRun!);
            Assert.Equal("Running", during.RootElement.GetProperty("status").GetString());
            var nodes = during.RootElement.GetProperty("nodes").EnumerateArray().ToDictionary(n => n.GetProperty("id").GetString()!, n => n.GetProperty("state").GetString());
            Assert.Equal("Succeeded", nodes["a"]);
            Assert.Equal("Running", nodes["b"]);

            using var after = JsonDocument.Parse(File.ReadAllText(statePath));
            Assert.Equal("Completed", after.RootElement.GetProperty("status").GetString());
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* temp */ }
        }
    }

    [Fact]
    public async Task Model_invocations_are_audited_and_summarised_without_prompt_content()
    {
        var agent = new FuncAgent("ai", r =>
        {
            r.Context.RecordInvocation(new ModelInvocation(DateTimeOffset.UtcNow, r.NodeId, "planner", "claude-test", "ok", 1200, 1000, 200, 0.012));
            r.Context.RecordInvocation(new ModelInvocation(DateTimeOffset.UtcNow, r.NodeId, "planner", "claude-test", "error:Timeout", 300000, 0, 0, 0, "timeout"));
            return T.Out("plan");
        });
        var result = await new WorkflowEngine(new WorkflowGraph().Add(T.Node("plan", agent)), new ScriptedHuman(), T.Policies(), T.Fast())
            .RunAsync(new RunContext("r"));

        Assert.Equal(2, result.Metrics.ModelCalls);
        Assert.Equal(1, result.Metrics.ModelCallFailures);
        Assert.Equal(1000L, result.Metrics.ModelInputTokens);
        Assert.Equal(0.012, result.Metrics.ModelCostUsd, 3);
        var events = result.Audit.Where(e => e.Event == "model.invoked").ToList();
        Assert.Equal(2, events.Count);
        Assert.Equal("model:planner", events[0].Actor);
        Assert.True(result.AuditChainValid);
    }

    private static RunHistoryEntry Entry(string id, string status, DateTimeOffset at, long ms, int attempts, int retries, int rollbacks, int recoveries, double? mttr) =>
        new(id, "test", "rules", status, at, at.AddMilliseconds(ms), ms, 7, status == "Completed" ? 7 : 3, attempts, attempts - retries,
            retries, rollbacks, 0, recoveries, mttr, 0, 0, null);

    [Fact]
    public void Cross_run_metrics_are_calculated_from_recorded_history_within_a_window()
    {
        var t0 = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var history = new[]
        {
            Entry("r1", "Completed", t0, 1000, 8, 1, 1, 1, 100),
            Entry("r2", "Completed", t0.AddHours(1), 2000, 10, 3, 3, 1, 300),
            Entry("r3", "SafeStopped", t0.AddHours(2), 500, 4, 0, 1, 0, null),
            Entry("r4", "Failed", t0.AddDays(10), 9000, 12, 4, 4, 0, null) // outside the window below
        };

        var m = RunHistory.Summarize(history, t0, t0.AddDays(1));

        Assert.Equal(3, m.Runs);
        Assert.Equal(0.667, m.RunSuccessRate);
        Assert.Equal(4.0 / 22.0, m.RetryFrequency, 3);
        Assert.Equal(5.0 / 22.0, m.RollbackFrequency, 3);
        Assert.Equal(200.0, m.MttrMs!.Value, 1);  // (100*1 + 300*1) / 2 recoveries
        Assert.Equal(2000L, m.P95LatencyMs);
        Assert.Equal(t0, m.WindowStart);
    }

    [Fact]
    public void Run_history_round_trips_through_the_index_file_and_ignores_torn_lines()
    {
        var path = Path.Combine(Path.GetTempPath(), "agentic-tests", Guid.NewGuid().ToString("N"), "runs-index.jsonl");
        try
        {
            RunHistory.Append(path, Entry("r1", "Completed", DateTimeOffset.UtcNow, 10, 1, 0, 0, 0, null));
            File.AppendAllText(path, "{ this is not json\n");
            RunHistory.Append(path, Entry("r2", "Failed", DateTimeOffset.UtcNow, 20, 2, 1, 1, 0, null));

            var loaded = RunHistory.Load(path);
            Assert.Equal(2, loaded.Count);
            Assert.Equal(0.5, RunHistory.Summarize(loaded).RunSuccessRate);
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch { /* temp */ }
        }
    }
}
