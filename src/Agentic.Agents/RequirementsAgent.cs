using Agentic.Core;

namespace Agentic.Agents;

/// <summary>
/// Interprets the raw requirement: detects vague wording, applies human clarifications, derives functional and
/// non-functional requirements and records explicit assumptions.
/// </summary>
public sealed class RequirementsAgent : IAgent
{
    public string Name => "RequirementsAgent";

    public Task<AgentResult> ExecuteAsync(AgentRequest request, CancellationToken ct)
    {
        var ctx = request.Context;
        var clarifications = ClarificationFact.Read(ctx);
        var answered = clarifications.Where(c => c.Answer.Trim().Length > 0).ToList();

        // 1. Ambiguity detection on the requirement text; an ambiguity is resolved once its question has an answer.
        var ambiguities = Catalog.FindAmbiguities(ctx.Requirement);
        var open = ambiguities
            .Where(a => !answered.Any(c => c.Question == a.Question))
            .ToList();

        // 2. Normalise into functional requirements using the requirement text plus stakeholder answers.
        var text = ctx.Requirement + " " + string.Join(" ", answered.Select(a => a.Answer));
        var kind = ctx.GetFact(Facts.Kind) is { Length: > 0 } k ? k : InferKind(ctx.Requirement);
        var detected = Catalog.Detect(text).ToList();
        if (kind == "greenfield")
        {
            // A new URL shortener always needs its core: create and redirect.
            foreach (var core in Catalog.Capabilities.Where(c => c.Kind is "create" or "redirect").Reverse())
                if (detected.All(c => c.Kind != core.Kind)) detected.Insert(0, core);
        }

        var functional = detected
            .Select((c, i) => new FunctionalRequirement($"FR-{i + 1}", c.Kind, c.Text, c.Acceptance))
            .ToList();

        var nonFunctional = Catalog.ExtractNonFunctional(text).Distinct().ToList();

        // 3. Make assumptions explicit so a human can challenge them.
        var assumptions = new List<string>
        {
            "No end-user authentication in this iteration: clients are identified by the X-Client-Id header or IP (spoofable).",
            "Single-instance deployment with in-memory storage behind a repository abstraction."
        };
        if (kind == "brownfield") assumptions.Add("Existing public API contracts stay backward compatible.");
        assumptions.AddRange(answered.Select(a => $"Stakeholder answer: \"{a.Answer.Trim()}\""));

        var requirement = new NormalizedRequirement(kind, ctx.Requirement.Trim(), functional, nonFunctional, open,
            assumptions, answered);

        var summary = open.Count == 0
            ? $"{functional.Count} functional requirement(s), {nonFunctional.Count} non-functional, {assumptions.Count} assumption(s)"
            : $"{open.Count} open ambiguity(ies): {string.Join(", ", open.Select(a => a.Term))}";

        return Task.FromResult(new AgentResult(new[] { Artifact.FromJson(Artifacts.Requirement, requirement, Name) }, summary));
    }

    internal static string InferKind(string requirement)
    {
        var lower = requirement.ToLowerInvariant();
        return new[] { "existing", "enhance", "extend", "refactor", "fix", "modify", "update the" }.Any(lower.Contains)
            ? "brownfield"
            : "greenfield";
    }
}
