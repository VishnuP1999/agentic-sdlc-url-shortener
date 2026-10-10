using System.Text.RegularExpressions;
using Agentic.Core;

namespace Agentic.Agents;

/// <summary>Security guardrail: a design that exposes link creation must declare the abuse-prevention controls.</summary>
public sealed class RequiredControlsPolicy : IPolicy
{
    private static readonly string[] Required = { "url-scheme-allowlist", "ssrf-block", "input-limits", "rate-limit" };

    public string Id => "SEC-002-required-controls";
    public string Description => "Designs exposing POST /api/urls must include scheme allow-list, SSRF block, input limits and rate limiting.";

    public IEnumerable<PolicyViolation> Evaluate(PolicyContext context)
    {
        var artifact = context.Artifacts.FirstOrDefault(a => a.Name == Artifacts.Design);
        if (artifact is null || !context.Run.TryGetJson<DesignSpec>(Artifacts.Design, out var design) || design is null) yield break;
        if (!design.Endpoints.Any(e => e.Method == "POST" && e.Path == "/api/urls")) yield break;

        foreach (var control in Required.Where(r => !design.Controls.Contains(r)))
            yield return new PolicyViolation(Id, Severity.High, $"Design is missing required control '{control}'.");
    }
}

/// <summary>Change-control guardrail: schema changes need a recorded human approval; releases need passing tests.</summary>
public sealed class ChangeControlPolicy : IPolicy
{
    public string Id => "CHG-001-change-control";
    public string Description => "Schema changes require a human approval after the latest design; tests must pass before security sign-off.";

    public IEnumerable<PolicyViolation> Evaluate(PolicyContext context)
    {
        if (context.NodeId != "security") yield break;
        var run = context.Run;

        if (run.TryGetJson<DesignSpec>(Artifacts.Design, out var design) && design is { SchemaChange: true })
        {
            var designArtifact = run.Get(Artifacts.Design)!;
            var approved = run.Decisions().Any(d =>
                d.NodeId == "design" && d.Actor.StartsWith("human:") && d.Decision == "approved" && d.At >= designArtifact.CreatedAt);
            if (!approved)
                yield return new PolicyViolation(Id, Severity.High, "Schema change has no human approval for the latest design.");
        }

        if (!run.TryGetJson<TestReport>(Artifacts.TestReport, out var tests) || tests is not { Passed: true })
            yield return new PolicyViolation(Id, Severity.High, "Tests have not passed; security sign-off is blocked.");
    }
}

/// <summary>Compliance guardrail (warning only): personal data such as email addresses should not appear in artifacts.</summary>
public sealed partial class NoPiiPolicy : IPolicy
{
    public string Id => "CMP-001-no-pii";
    public string Description => "Generated artifacts should not embed personal data (email addresses).";

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
    private static partial Regex Email();

    public IEnumerable<PolicyViolation> Evaluate(PolicyContext context)
    {
        foreach (var a in context.Artifacts)
            if (Email().IsMatch(a.Content))
                yield return new PolicyViolation(Id, Severity.Warning, $"Artifact '{a.Ref}' contains an email address.");
    }
}

/// <summary>
/// Execution-permission guardrail: proposed code may only touch product sources and tests (no build files, tooling,
/// deletes, absolute paths or path traversal). Violations fail the node, so the agent gets a repair attempt.
/// </summary>
public sealed class ProposedChangesPolicy : IPolicy
{
    public string Id => "SEC-003-change-scope";
    public string Description => "Proposed files must stay inside src/UrlShortener.Api and tests/UrlShortener.Tests, as create/modify of .cs files.";

    public IEnumerable<PolicyViolation> Evaluate(PolicyContext context)
    {
        var repo = context.Run.GetFact(Facts.RepoPath);
        Func<string, bool>? exists = string.IsNullOrEmpty(repo) ? null : p => File.Exists(Path.Combine(repo, p));

        foreach (var artifact in context.Artifacts.Where(a => a.Name is Artifacts.ProposedChanges or Artifacts.ProposedTests))
        {
            ProposedChanges? proposal;
            try { proposal = System.Text.Json.JsonSerializer.Deserialize<ProposedChanges>(artifact.Content, Json.Options); }
            catch (System.Text.Json.JsonException) { proposal = null; }
            if (proposal is null)
            {
                yield return new PolicyViolation(Id, Severity.High, $"'{artifact.Ref}' is not a valid proposal.");
                continue;
            }
            if (proposal.Applied)
                yield return new PolicyViolation(Id, Severity.High, $"'{artifact.Ref}' claims changes were applied; agents may only propose.");
            foreach (var error in ChangeGuard.CheckAll(proposal.Files, exists))
                yield return new PolicyViolation(Id, Severity.High, $"{artifact.Ref}: {error}");
        }
    }
}

public static class DefaultPolicies
{
    public static PolicyEngine Create() =>
        new(new IPolicy[] { new NoSecretsPolicy(), new RequiredControlsPolicy(), new ChangeControlPolicy(), new NoPiiPolicy(), new ProposedChangesPolicy() });
}
