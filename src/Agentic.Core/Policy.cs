using System.Text.RegularExpressions;

namespace Agentic.Core;

public enum Severity { Warning, High, Critical }

public sealed record PolicyViolation(string PolicyId, Severity Severity, string Message, string? NodeId = null);

/// <param name="Run">Shared run state.</param>
/// <param name="NodeId">Node whose output is being evaluated.</param>
/// <param name="Artifacts">Artifacts to inspect (new output for post-node checks, everything for a full review).</param>
public sealed record PolicyContext(RunContext Run, string NodeId, IReadOnlyList<Artifact> Artifacts);

public interface IPolicy
{
    string Id { get; }
    string Description { get; }
    IEnumerable<PolicyViolation> Evaluate(PolicyContext context);
}

public interface IPolicyEvaluator
{
    /// <summary>Evaluates all policies against every latest artifact (used by review/security stages).</summary>
    IReadOnlyList<PolicyViolation> EvaluateAll(RunContext run, string nodeId);
}

/// <summary>Runs the registered guardrail policies.</summary>
public sealed class PolicyEngine : IPolicyEvaluator
{
    private readonly IReadOnlyList<IPolicy> _policies;

    public PolicyEngine(IEnumerable<IPolicy> policies) => _policies = policies.ToList();

    public IReadOnlyList<IPolicy> Policies => _policies;

    public IReadOnlyList<PolicyViolation> Evaluate(PolicyContext context) =>
        _policies.SelectMany(p => p.Evaluate(context)
            .Select(v => v with { NodeId = v.NodeId ?? context.NodeId })).ToList();

    public IReadOnlyList<PolicyViolation> EvaluateAll(RunContext run, string nodeId) =>
        Evaluate(new PolicyContext(run, nodeId, run.Latest()));
}

/// <summary>Compliance/security guardrail: no hard-coded secrets in any generated artifact.</summary>
public sealed partial class NoSecretsPolicy : IPolicy
{
    public string Id => "SEC-001-no-secrets";
    public string Description => "Generated artifacts must not contain hard-coded credentials, keys or tokens.";

    [GeneratedRegex(
        @"(?i)(password|passwd|pwd|secret|api[_-]?key|access[_-]?token)\s*[:=]\s*[""']?[^\s""']{4,}|AKIA[0-9A-Z]{16}|-----BEGIN (RSA |EC )?PRIVATE KEY-----")]
    private static partial Regex SecretPattern();

    public IEnumerable<PolicyViolation> Evaluate(PolicyContext context)
    {
        foreach (var artifact in context.Artifacts)
            if (SecretPattern().IsMatch(artifact.Content))
                yield return new PolicyViolation(Id, Severity.Critical,
                    $"Possible hard-coded secret in artifact '{artifact.Ref}'.");
    }
}
