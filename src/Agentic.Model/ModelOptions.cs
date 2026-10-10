using System.Globalization;

namespace Agentic.Model;

/// <summary>
/// Model configuration, read from environment variables so nothing model-specific is hard-coded or committed.
/// <list type="bullet">
/// <item><c>CLAUDE_MODEL</c>: model alias or full id passed to Claude Code (default <c>sonnet</c>).</item>
/// <item><c>CLAUDE_CODE_PATH</c>: path to the <c>claude</c> executable (default: PATH, ~/.local/bin, then the VS Code extension).</item>
/// <item><c>CLAUDE_TIMEOUT_SECONDS</c>: per-call timeout (default 300).</item>
/// <item><c>CLAUDE_MAX_BUDGET_USD</c>: per-call spend cap enforced by Claude Code (default 1.00).</item>
/// <item><c>CLAUDE_EFFORT</c>: optional effort level (low, medium, high, xhigh, max).</item>
/// </list>
/// Authentication is whatever the local Claude Code installation is logged in with (or ANTHROPIC_API_KEY in its
/// environment); this application never reads, stores or logs credentials.
/// </summary>
public sealed record ModelOptions
{
    public string Model { get; init; } = "sonnet";
    public string? ExecutablePath { get; init; }
    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromSeconds(300);
    public decimal MaxBudgetUsdPerCall { get; init; } = 1.00m;
    public string? Effort { get; init; }

    public static ModelOptions FromEnvironment(Func<string, string?>? getEnv = null)
    {
        getEnv ??= Environment.GetEnvironmentVariable;
        var options = new ModelOptions();

        if (getEnv("CLAUDE_MODEL") is { Length: > 0 } model) options = options with { Model = model.Trim() };
        if (getEnv("CLAUDE_CODE_PATH") is { Length: > 0 } path) options = options with { ExecutablePath = path.Trim() };
        if (int.TryParse(getEnv("CLAUDE_TIMEOUT_SECONDS"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
            options = options with { CallTimeout = TimeSpan.FromSeconds(seconds) };
        if (decimal.TryParse(getEnv("CLAUDE_MAX_BUDGET_USD"), NumberStyles.Number, CultureInfo.InvariantCulture, out var budget) && budget > 0)
            options = options with { MaxBudgetUsdPerCall = budget };
        if (getEnv("CLAUDE_EFFORT") is { Length: > 0 } effort) options = options with { Effort = effort.Trim().ToLowerInvariant() };

        return options;
    }
}

/// <summary>Finds the Claude Code executable without requiring it to be on PATH.</summary>
public static class ClaudeCodeLocator
{
    public static string? Find(string? configuredPath = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath)) return File.Exists(configuredPath) ? configuredPath : null;

        var exe = OperatingSystem.IsWindows() ? "claude.exe" : "claude";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir.Trim(), exe);
            if (File.Exists(candidate)) return candidate;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Path.Combine(home, ".local", "bin", exe);
        if (File.Exists(local)) return local;

        // The VS Code extension ships a native binary; prefer the newest installed version.
        foreach (var root in new[] { Path.Combine(home, ".vscode", "extensions"), Path.Combine(home, ".vscode-insiders", "extensions") })
        {
            if (!Directory.Exists(root)) continue;
            var bundled = Directory.EnumerateDirectories(root, "anthropic.claude-code-*")
                .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                .Select(d => Path.Combine(d, "resources", "native-binary", exe))
                .FirstOrDefault(File.Exists);
            if (bundled is not null) return bundled;
        }
        return null;
    }
}
