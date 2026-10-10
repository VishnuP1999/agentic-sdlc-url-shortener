namespace Agentic.Model;

public sealed record HealthCheckResult(bool Healthy, string Model, long LatencyMs, double CostUsd, string Detail, ModelErrorKind? Error = null);

/// <summary>A live, cheap end-to-end check of the model adapter: authentication, model selection and structured output.</summary>
public static class ModelHealthCheck
{
    public static async Task<HealthCheckResult> RunAsync(IModelClient client, CancellationToken ct = default)
    {
        try
        {
            var response = await client.CompleteAsync(new ModelRequest("health-check",
                "You are a health check. Answer only with JSON matching the schema.",
                "Return status \"ok\" and the sum of 2 and 3.",
                """{"type":"object","additionalProperties":false,"required":["status","sum"],"properties":{"status":{"type":"string"},"sum":{"type":"integer"}}}"""),
                ct);
            var ok = response.Output.TryGetProperty("status", out var s) && s.GetString() == "ok"
                     && response.Output.TryGetProperty("sum", out var n) && n.TryGetInt32(out var sum) && sum == 5;
            return new HealthCheckResult(ok, response.Model, response.DurationMs, response.Usage.CostUsd,
                ok ? $"structured output {response.Output.GetRawText()}" : $"unexpected answer {response.Output.GetRawText()}");
        }
        catch (ModelException ex)
        {
            return new HealthCheckResult(false, client.Describe, 0, 0, ex.Message, ex.Kind);
        }
    }
}

public enum AgentMode { Ai, Rules }

public sealed record AgentModeDecision(AgentMode Mode, IModelClient? Model, string Reason, HealthCheckResult? Health);

/// <summary>
/// Chooses between LLM-backed and rule-based agents.
/// <list type="bullet">
/// <item><c>ai</c>: always use the model (calls that fail fall back per node).</item>
/// <item><c>rules</c>: deterministic agents, no model calls.</item>
/// <item><c>auto</c> (default): use the model only if Claude Code is installed AND a live health check passes;
/// otherwise run rule-based agents and say why. This is what lets the tool run on machines without Claude Code.</item>
/// </list>
/// </summary>
public static class AgentModeResolver
{
    public static async Task<AgentModeDecision> ResolveAsync(string? requested, ClaudeCodeModelClient claude, CancellationToken ct = default)
    {
        var mode = (requested ?? "auto").Trim().ToLowerInvariant();
        switch (mode)
        {
            case "rules":
                return new AgentModeDecision(AgentMode.Rules, null, "rule-based agents requested", null);
            case "ai":
                return new AgentModeDecision(AgentMode.Ai, claude,
                    claude.IsAvailable ? $"AI requested ({claude.Describe})" : "AI requested but Claude Code was not found; nodes will fall back to rule-based agents", null);
            case "auto":
                if (!claude.IsAvailable)
                    return new AgentModeDecision(AgentMode.Rules, null,
                        "auto: Claude Code not found on this machine (install it or set CLAUDE_CODE_PATH); using rule-based agents", null);
                var health = await ModelHealthCheck.RunAsync(claude, ct);
                return health.Healthy
                    ? new AgentModeDecision(AgentMode.Ai, claude, $"auto: Claude Code healthy ({health.Model}, {health.LatencyMs} ms)", health)
                    : new AgentModeDecision(AgentMode.Rules, null, $"auto: Claude Code health check failed ({health.Error}: {health.Detail}); using rule-based agents", health);
            default:
                throw new ArgumentException($"Unknown agent mode '{requested}' (use auto, ai or rules).");
        }
    }
}
