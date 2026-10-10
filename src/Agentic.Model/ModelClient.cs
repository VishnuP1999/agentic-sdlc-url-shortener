using System.Text.Json;

namespace Agentic.Model;

/// <summary>
/// One structured request to a language model. The model gets no tools: it reads the prompt and answers with JSON
/// that must match <see cref="JsonSchema"/>. Everything it "does" is a proposal the orchestrator verifies.
/// </summary>
/// <param name="Role">Agent role (planner, developer, tester, documentation), used for audit and telemetry.</param>
public sealed record ModelRequest(string Role, string SystemPrompt, string UserPrompt, string JsonSchema);

public sealed record ModelUsage(long InputTokens, long OutputTokens, double CostUsd);

public sealed record ModelResponse(JsonElement Output, string Model, ModelUsage Usage, long DurationMs);

public enum ModelErrorKind
{
    NotConfigured,     // no model runtime available (CLI not found)
    Authentication,    // not logged in / invalid credentials
    ModelUnavailable,  // unknown model or no access to it
    BudgetExceeded,    // per-call spend limit reached
    RateLimited,
    ServiceUnavailable,
    Timeout,
    EmptyResponse,
    InvalidOutput,     // response did not contain valid JSON for the schema
    ProcessFailed
}

public sealed class ModelException : Exception
{
    public ModelException(ModelErrorKind kind, string message, Exception? inner = null) : base(message, inner) => Kind = kind;

    public ModelErrorKind Kind { get; }

    /// <summary>Configuration and access problems cannot be fixed by asking again; transient and output problems can.</summary>
    public bool IsRetryable => Kind is ModelErrorKind.RateLimited or ModelErrorKind.ServiceUnavailable or ModelErrorKind.Timeout
        or ModelErrorKind.EmptyResponse or ModelErrorKind.InvalidOutput or ModelErrorKind.ProcessFailed;
}

/// <summary>The only way agents reach a language model. Model-specific code lives behind this interface.</summary>
public interface IModelClient
{
    /// <summary>Human-readable model identity for audit records (e.g. "claude-code:sonnet").</summary>
    string Describe { get; }

    Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct);
}
