namespace Agentic.Core;

public enum ApprovalOutcome { Approved, Rejected, ChangesRequested }

public sealed record ApprovalRequest(
    string RunId, string NodeId, string Reason, string Summary, IReadOnlyList<string> Risks);

/// <param name="TargetNode">For ChangesRequested: the node whose output must be redone (default chosen by the engine).</param>
public sealed record ApprovalDecision(ApprovalOutcome Outcome, string Reviewer, string? Note = null, string? TargetNode = null);

public sealed record ClarificationRequest(string RunId, string NodeId, IReadOnlyList<string> Questions);

/// <summary>The human oversight boundary: approvals for high-impact actions and answers to open questions.</summary>
public interface IHumanInteraction
{
    Task<ApprovalDecision> RequestApprovalAsync(ApprovalRequest request, CancellationToken ct);
    Task<IReadOnlyList<string>> ClarifyAsync(ClarificationRequest request, CancellationToken ct);
}

/// <summary>
/// Plays back pre-recorded human decisions (used by scenarios and tests).
/// Safe default: when the script has no decision for a request, the request is rejected.
/// Script syntax: "[node=&gt;]approve" | "[node=&gt;]reject[:note]" | "[node=&gt;]request_changes:&lt;targetNode&gt;:&lt;note&gt;".
/// A decision prefixed with a node id ("release=&gt;approve") is only used for that node's approval, so the script
/// does not depend on which approvals happen to be requested (e.g. an AI design may or may not change the schema).
/// Unprefixed decisions are used in order for any node.
/// </summary>
public sealed class ScriptedHuman : IHumanInteraction
{
    private readonly List<string> _approvals;
    private readonly Queue<string> _answers;
    private readonly object _gate = new();

    public ScriptedHuman(IEnumerable<string>? approvals = null, IEnumerable<string>? clarificationAnswers = null)
    {
        _approvals = new List<string>(approvals ?? Array.Empty<string>());
        _answers = new Queue<string>(clarificationAnswers ?? Array.Empty<string>());
    }

    public Task<ApprovalDecision> RequestApprovalAsync(ApprovalRequest request, CancellationToken ct)
    {
        string? script;
        lock (_gate)
        {
            var prefix = request.NodeId + "=>";
            var index = _approvals.FindIndex(a => a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            if (index < 0) index = _approvals.FindIndex(a => !a.Contains("=>"));
            script = index < 0 ? null : _approvals[index];
            if (index >= 0) _approvals.RemoveAt(index);
            if (script is not null && script.Contains("=>")) script = script[(script.IndexOf("=>", StringComparison.Ordinal) + 2)..];
        }

        if (script is null)
            return Task.FromResult(new ApprovalDecision(ApprovalOutcome.Rejected, "scripted-reviewer",
                "No scripted decision available; rejecting by default."));

        var parts = script.Split(':', 3);
        var decision = parts[0].Trim().ToLowerInvariant() switch
        {
            "approve" => new ApprovalDecision(ApprovalOutcome.Approved, "scripted-reviewer"),
            "request_changes" => new ApprovalDecision(ApprovalOutcome.ChangesRequested, "scripted-reviewer",
                parts.Length > 2 ? parts[2] : (parts.Length > 1 ? parts[1] : null),
                parts.Length > 2 ? parts[1] : null),
            _ => new ApprovalDecision(ApprovalOutcome.Rejected, "scripted-reviewer", parts.Length > 1 ? parts[1] : null)
        };
        return Task.FromResult(decision);
    }

    /// <summary>
    /// Answers are matched to questions, not handed out by position: an answer scripted as
    /// "keyword1,keyword2=&gt;answer" goes to the first question that contains one of its keywords (an LLM may ask
    /// questions in any order). Answers without keywords fill the remaining questions in order; leftovers stay
    /// queued for later rounds, and questions with no answer get an empty string (the stakeholder declined).
    /// </summary>
    public Task<IReadOnlyList<string>> ClarifyAsync(ClarificationRequest request, CancellationToken ct)
    {
        lock (_gate)
        {
            var pending = _answers.ToList();
            _answers.Clear();
            var result = new string?[request.Questions.Count];

            foreach (var script in pending.Where(s => s.Contains("=>")).ToList())
            {
                var parts = script.Split("=>", 2);
                var keywords = parts[0].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var index = Enumerable.Range(0, result.Length).FirstOrDefault(i => result[i] is null
                    && keywords.Any(k => request.Questions[i].Contains(k, StringComparison.OrdinalIgnoreCase)), -1);
                if (index < 0) continue;
                result[index] = parts[1].Trim();
                pending.Remove(script);
            }
            for (var i = 0; i < result.Length; i++)
            {
                if (result[i] is not null) continue;
                var next = pending.FirstOrDefault(s => !s.Contains("=>"));
                if (next is null) break;
                result[i] = next;
                pending.Remove(next);
            }
            foreach (var left in pending) _answers.Enqueue(left);
            return Task.FromResult<IReadOnlyList<string>>(result.Select(a => a ?? string.Empty).ToList());
        }
    }
}
