using System.Text;
using Agentic.Core;

namespace Agentic.Agents;

/// <summary>Generates API documentation from the design. <c>minimal</c> mode is the low-risk fallback template.</summary>
public sealed class DocsAgent : IAgent
{
    private readonly bool _minimal;

    public DocsAgent(bool minimal = false) => _minimal = minimal;

    public string Name => _minimal ? "MinimalDocsAgent" : "DocsAgent";

    public Task<AgentResult> ExecuteAsync(AgentRequest request, CancellationToken ct)
    {
        var ctx = request.Context;
        var req = ctx.GetJson<NormalizedRequirement>(Artifacts.Requirement);
        var design = ctx.GetJson<DesignSpec>(Artifacts.Design);
        var docNotes = ctx.ChangeRequests().Where(c => c.TargetNode == "docs").Select(c => c.Note).ToList();

        var sb = new StringBuilder();
        sb.AppendLine("# URL Shortener API");
        sb.AppendLine();
        sb.AppendLine($"> {req.Goal}");
        sb.AppendLine();
        sb.AppendLine("## Endpoints");
        sb.AppendLine();
        sb.AppendLine("| Method | Path | Purpose | Status codes |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var e in design.Endpoints)
            sb.AppendLine($"| {e.Method} | `{e.Path}` | {e.Summary} | {string.Join(", ", e.Statuses)} |");

        if (!_minimal)
        {
            sb.AppendLine();
            sb.AppendLine("## Requirements and acceptance criteria");
            foreach (var fr in req.Functional)
            {
                sb.AppendLine();
                sb.AppendLine($"**{fr.Id} - {fr.Text}**");
                foreach (var a in fr.Acceptance) sb.AppendLine($"- {a}");
            }

            sb.AppendLine();
            sb.AppendLine("## Security controls");
            foreach (var c in design.Controls) sb.AppendLine($"- `{c}`");

            sb.AppendLine();
            sb.AppendLine("## Assumptions");
            foreach (var a in req.Assumptions) sb.AppendLine($"- {a}");
        }

        if (docNotes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Additional documentation (requested in review)");
            foreach (var note in docNotes)
            {
                sb.AppendLine();
                sb.AppendLine($"_Review note: {note}_");
                if (note.Contains("rate", StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine();
                    sb.AppendLine("When a client exceeds its quota the API answers `429 Too Many Requests` and sets a");
                    sb.AppendLine("`Retry-After` header with the number of seconds to wait before retrying.");
                }
            }
        }

        var markdown = sb.ToString();
        return Task.FromResult(new AgentResult(new[] { Artifact.Create(Artifacts.ApiDocs, "markdown", markdown, Name) },
            $"{design.Endpoints.Count} endpoint(s) documented{(docNotes.Count > 0 ? $", {docNotes.Count} review note(s) addressed" : "")}"));
    }
}
