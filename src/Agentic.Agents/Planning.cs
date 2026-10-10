namespace Agentic.Agents;

/// <summary>
/// Orchestrator-side checks on a task plan. A plan is accepted only if it is a well-formed DAG and every functional
/// requirement is covered by implementation and verification work, whichever agent (model or rules) produced it.
/// </summary>
public static class PlanValidator
{
    public static readonly IReadOnlyList<string> Roles = new[] { "developer", "tester", "documentation" };

    public static IReadOnlyList<string> Validate(TaskPlan plan, NormalizedRequirement requirement)
    {
        var errors = new List<string>();
        if (plan.Tasks.Count == 0) return new[] { "plan has no tasks" };

        var ids = plan.Tasks.Select(t => t.Id).ToList();
        foreach (var dup in ids.GroupBy(i => i, StringComparer.Ordinal).Where(g => g.Count() > 1))
            errors.Add($"duplicate task id '{dup.Key}'");

        var known = ids.ToHashSet(StringComparer.Ordinal);
        foreach (var t in plan.Tasks)
        {
            if (string.IsNullOrWhiteSpace(t.Id) || string.IsNullOrWhiteSpace(t.Title)) errors.Add("every task needs an id and a title");
            if (!Roles.Contains(t.Role)) errors.Add($"task '{t.Id}' has unknown role '{t.Role}' (allowed: {string.Join(", ", Roles)})");
            if (string.IsNullOrWhiteSpace(t.Validation)) errors.Add($"task '{t.Id}' has no validation criterion");
            if (string.IsNullOrWhiteSpace(t.ExpectedOutput)) errors.Add($"task '{t.Id}' has no expected output");
            foreach (var d in t.DependsOn.Where(d => !known.Contains(d))) errors.Add($"task '{t.Id}' depends on unknown task '{d}'");
            if (t.DependsOn.Contains(t.Id)) errors.Add($"task '{t.Id}' depends on itself");
        }

        if (errors.Count == 0 && HasCycle(plan)) errors.Add("task dependencies contain a cycle");

        foreach (var fr in requirement.Functional)
        {
            if (!plan.Tasks.Any(t => t.Role == "developer" && t.Requirements.Contains(fr.Id)))
                errors.Add($"requirement {fr.Id} has no developer task");
            if (!plan.Tasks.Any(t => t.Role == "tester" && t.Requirements.Contains(fr.Id)))
                errors.Add($"requirement {fr.Id} has no tester task");
        }
        if (!plan.Tasks.Any(t => t.Role == "documentation")) errors.Add("plan has no documentation task");
        return errors;
    }

    private static bool HasCycle(TaskPlan plan)
    {
        var indegree = plan.Tasks.ToDictionary(t => t.Id, t => t.DependsOn.Count, StringComparer.Ordinal);
        var queue = new Queue<string>(indegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        var visited = 0;
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            visited++;
            foreach (var child in plan.Tasks.Where(t => t.DependsOn.Contains(id)))
                if (--indegree[child.Id] == 0) queue.Enqueue(child.Id);
        }
        return visited != plan.Tasks.Count;
    }
}

/// <summary>Deterministic task decomposition used by the rule-based architect (and as the fallback).</summary>
public static class PlanBuilder
{
    public static TaskPlan Build(NormalizedRequirement requirement, DesignSpec design)
    {
        var tasks = new List<PlannedTask>();
        foreach (var fr in requirement.Functional)
        {
            var dev = $"T-{fr.Id}-DEV";
            tasks.Add(new PlannedTask(dev, $"Implement: {fr.Text}", "developer",
                $"Change the components that realise {fr.Id} ({fr.Kind}).", Array.Empty<string>(), new[] { fr.Id },
                "Proposed full-file changes and change-set entries", design.Risks.Take(1).ToList(),
                "Solution builds and the existing test suite still passes"));
            tasks.Add(new PlannedTask($"T-{fr.Id}-TEST", $"Verify: {fr.Text}", "tester",
                $"Positive, negative and edge-case tests for the acceptance criteria of {fr.Id}.", new[] { dev }, new[] { fr.Id },
                "Test cases mapped to acceptance criteria", Array.Empty<string>(),
                "Every acceptance criterion has a passing executed test"));
        }
        tasks.Add(new PlannedTask("T-DOCS", "Document the API and decisions", "documentation",
            "API reference, assumptions, risks and limitations.", Array.Empty<string>(),
            requirement.Functional.Select(f => f.Id).ToList(), "Markdown API documentation", Array.Empty<string>(),
            "Every designed endpoint and every review note is documented"));
        return new TaskPlan(tasks);
    }
}
