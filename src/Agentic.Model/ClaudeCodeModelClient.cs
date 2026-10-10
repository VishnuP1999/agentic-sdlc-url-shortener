using System.Globalization;
using System.Text.Json;
using Agentic.Core;

namespace Agentic.Model;

/// <summary>
/// Calls Claude through the Claude Code CLI in headless mode (<c>claude -p --output-format json</c>).
/// <para>
/// Safety: the model runs with <b>no tools</b> (<c>--tools ""</c>, no MCP servers, no slash commands, no session
/// persistence, user customizations disabled with <c>--safe-mode</c>) in an empty working directory, and anything
/// that would prompt for permission is denied. It can only return text, which must validate against the JSON schema.
/// The prompt goes through stdin and arguments are passed as a list, so no shell is involved.
/// </para>
/// </summary>
public sealed class ClaudeCodeModelClient : IModelClient
{
    private readonly ModelOptions _options;
    private readonly IProcessRunner _runner;
    private readonly string? _executable;
    private readonly string _workingDirectory;

    public ClaudeCodeModelClient(ModelOptions options, IProcessRunner? runner = null, string? executable = null)
    {
        _options = options;
        _runner = runner ?? new ProcessRunner();
        _executable = executable ?? ClaudeCodeLocator.Find(options.ExecutablePath);
        _workingDirectory = Path.Combine(Path.GetTempPath(), "agentic-sdlc-model-sandbox");
    }

    public string Describe => $"claude-code:{_options.Model}";

    public bool IsAvailable => _executable is not null;

    public string? ExecutablePath => _executable;

    /// <summary>The exact argument list sent to Claude Code (exposed for tests and the audit documentation).</summary>
    public IReadOnlyList<string> BuildArguments(ModelRequest request)
    {
        var args = new List<string>
        {
            "-p",
            "--output-format", "json",
            "--model", _options.Model,
            "--tools", "",
            "--strict-mcp-config",
            "--disable-slash-commands",
            "--safe-mode",
            "--no-session-persistence",
            "--permission-prompts", "none",
            "--max-budget-usd", _options.MaxBudgetUsdPerCall.ToString(CultureInfo.InvariantCulture),
            "--system-prompt", request.SystemPrompt,
            "--json-schema", request.JsonSchema
        };
        if (_options.Effort is { Length: > 0 } effort) args.AddRange(new[] { "--effort", effort });
        return args;
    }

    public async Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct)
    {
        if (_executable is null)
            throw new ModelException(ModelErrorKind.NotConfigured,
                "Claude Code executable not found. Install Claude Code or set CLAUDE_CODE_PATH (or run with --agents rules).");

        Directory.CreateDirectory(_workingDirectory);
        ProcessResult result;
        try
        {
            result = await _runner.RunAsync(_executable, BuildArguments(request), request.UserPrompt, _workingDirectory,
                _options.CallTimeout, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new ModelException(ModelErrorKind.ProcessFailed, $"Could not run Claude Code: {ex.Message}", ex);
        }

        return ClaudeCodeOutputParser.Parse(result, _options.Model, _options.CallTimeout);
    }
}

/// <summary>Turns the CLI's JSON result envelope into a <see cref="ModelResponse"/> or a classified <see cref="ModelException"/>.</summary>
public static class ClaudeCodeOutputParser
{
    public static ModelResponse Parse(ProcessResult process, string requestedModel, TimeSpan timeout)
    {
        if (process.TimedOut)
            throw new ModelException(ModelErrorKind.Timeout, $"Claude Code did not answer within {timeout.TotalSeconds:0}s.");

        var stdout = process.StandardOutput.Trim();
        if (stdout.Length == 0)
            throw Classify(process.StandardError, null, process.ExitCode == 0
                ? new ModelException(ModelErrorKind.EmptyResponse, "Claude Code returned no output.")
                : new ModelException(ModelErrorKind.ProcessFailed, $"Claude Code exited with code {process.ExitCode}: {FirstLine(process.StandardError)}"));

        JsonElement envelope;
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            envelope = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw Classify(stdout + "\n" + process.StandardError, null,
                new ModelException(ModelErrorKind.ProcessFailed, $"Claude Code returned non-JSON output (exit {process.ExitCode}): {FirstLine(stdout)}"));
        }

        var resultText = envelope.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() ?? "" : "";
        var isError = envelope.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
        var subtype = envelope.TryGetProperty("subtype", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        int? status = envelope.TryGetProperty("api_error_status", out var st) && st.ValueKind == JsonValueKind.Number ? st.GetInt32() : null;

        if (subtype is "error_max_budget_usd")
            throw new ModelException(ModelErrorKind.BudgetExceeded, "Per-call budget (CLAUDE_MAX_BUDGET_USD) was exhausted before the model finished.");
        if (isError || (subtype is not null && subtype.StartsWith("error", StringComparison.Ordinal)))
        {
            var fallback = status switch
            {
                401 or 403 => new ModelException(ModelErrorKind.Authentication, $"Claude Code is not authenticated: {resultText}"),
                404 => new ModelException(ModelErrorKind.ModelUnavailable, $"Model '{requestedModel}' is not available: {resultText}"),
                429 => new ModelException(ModelErrorKind.RateLimited, $"Rate limited: {resultText}"),
                >= 500 => new ModelException(ModelErrorKind.ServiceUnavailable, $"Model service error {status}: {resultText}"),
                _ => new ModelException(ModelErrorKind.ProcessFailed, $"Claude Code reported an error ({subtype ?? "unknown"}): {resultText}")
            };
            throw status is null ? Classify(resultText + "\n" + process.StandardError, status, fallback) : fallback;
        }

        JsonElement output;
        if (envelope.TryGetProperty("structured_output", out var so) && so.ValueKind == JsonValueKind.Object)
        {
            output = so.Clone();
        }
        else if (resultText.Trim().Length == 0)
        {
            throw new ModelException(ModelErrorKind.EmptyResponse, "The model returned an empty response.");
        }
        else if (!TryParseObject(resultText, out output))
        {
            throw new ModelException(ModelErrorKind.InvalidOutput, $"The model did not return a JSON object: {FirstLine(resultText)}");
        }

        // Claude Code also uses a small model for housekeeping; the answering model is the one that cost the most.
        var model = envelope.TryGetProperty("modelUsage", out var mu) && mu.ValueKind == JsonValueKind.Object
            ? mu.EnumerateObject()
                .OrderByDescending(p => p.Value.TryGetProperty("costUSD", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : 0)
                .Select(p => p.Name).FirstOrDefault() ?? requestedModel
            : requestedModel;

        long input = 0, outputTokens = 0;
        if (envelope.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
        {
            input = Long(u, "input_tokens") + Long(u, "cache_creation_input_tokens") + Long(u, "cache_read_input_tokens");
            outputTokens = Long(u, "output_tokens");
        }
        var cost = envelope.TryGetProperty("total_cost_usd", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : 0;
        var duration = envelope.TryGetProperty("duration_ms", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt64() : process.DurationMs;

        return new ModelResponse(output, model, new ModelUsage(input, outputTokens, cost), duration);
    }

    /// <summary>Recognises authentication and model problems from free text when no HTTP status is available.</summary>
    private static ModelException Classify(string text, int? status, ModelException fallback)
    {
        var lower = text.ToLowerInvariant();
        if (lower.Contains("/login") || lower.Contains("not logged in") || lower.Contains("invalid api key") || lower.Contains("authentication"))
            return new ModelException(ModelErrorKind.Authentication, $"Claude Code is not authenticated (run 'claude' once and log in): {FirstLine(text)}");
        if (lower.Contains("unrecognized_model") || lower.Contains("issue with the selected model"))
            return new ModelException(ModelErrorKind.ModelUnavailable, $"The configured model is not available: {FirstLine(text)}");
        return fallback;
    }

    private static bool TryParseObject(string text, out JsonElement value)
    {
        value = default;
        var trimmed = text.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewLine = trimmed.IndexOf('\n');
            var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewLine > 0 && lastFence > firstNewLine) trimmed = trimmed[(firstNewLine + 1)..lastFence].Trim();
        }
        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            value = doc.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static long Long(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

    private static string FirstLine(string text)
    {
        var line = text.Trim().Split('\n').FirstOrDefault()?.Trim() ?? "";
        return line.Length > 300 ? line[..300] + "..." : line;
    }
}
