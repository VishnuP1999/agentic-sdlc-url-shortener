using Agentic.Core;

namespace Agentic.Agents;

/// <summary>
/// Derives a test plan from requirements and design controls: a case per acceptance criterion plus an edge case per
/// requirement and a failing-path case per security control. It proposes no test code; execution of the real suite
/// is orchestrator-owned (see <see cref="SandboxValidator"/>), so this agent never claims tests ran.
/// </summary>
public sealed class TestAgent : IAgent
{
    public string Name => "TestAgent";

    public Task<AgentResult> ExecuteAsync(AgentRequest request, CancellationToken ct)
    {
        var ctx = request.Context;
        var req = ctx.GetJson<NormalizedRequirement>(Artifacts.Requirement);
        var design = ctx.GetJson<DesignSpec>(Artifacts.Design);

        var cases = new List<TestCase>();
        foreach (var fr in req.Functional)
        {
            var n = 0;
            foreach (var criterion in fr.Acceptance)
                cases.Add(new TestCase($"{fr.Id}-T{++n}", fr.Id, criterion.Contains("reject", StringComparison.OrdinalIgnoreCase) || criterion.Contains("4") ? "negative" : "positive", criterion));
            cases.Add(new TestCase($"{fr.Id}-T{++n}", fr.Id, "edge", $"Boundary and malformed input for: {fr.Text}"));
        }
        foreach (var control in design.Controls)
            cases.Add(new TestCase($"CTRL-{control}", "controls", "security", $"Control '{control}' is enforced and has a failing-path test"));

        return Task.FromResult(new AgentResult(new[] { Artifact.FromJson(Artifacts.TestPlan, cases, Name) },
            $"{cases.Count} test case(s) planned; coverage complete={TestCoverage.IsComplete(req, cases)}"));
    }
}

public static class TestCoverage
{
    /// <summary>Every requirement needs at least two cases, at least one of them negative, edge or security.</summary>
    public static bool IsComplete(NormalizedRequirement req, IReadOnlyList<TestCase> cases) =>
        req.Functional.All(fr =>
            cases.Count(c => c.Requirement == fr.Id) >= 2 && cases.Any(c => c.Requirement == fr.Id && c.Type != "positive"));

    public static IReadOnlyList<string> Missing(NormalizedRequirement req, IReadOnlyList<TestCase> cases) =>
        req.Functional.Where(fr =>
                cases.Count(c => c.Requirement == fr.Id) < 2 || !cases.Any(c => c.Requirement == fr.Id && c.Type != "positive"))
            .Select(fr => fr.Id).ToList();
}
