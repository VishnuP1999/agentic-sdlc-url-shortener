using System.Text;
using System.Text.RegularExpressions;
using Agentic.Core;

namespace Agentic.Agents;

/// <summary>
/// Decides which proposed file paths may be applied. Applies to every proposal before it touches any disk,
/// and is also enforced as a policy on the proposal artifacts.
/// </summary>
public static class ChangeGuard
{
    /// <summary>Only product code and product tests may change; build files, tooling and the test runner may not.</summary>
    public static readonly IReadOnlyList<string> AllowedRoots = new[] { "src/UrlShortener.Api/", "tests/UrlShortener.Tests/" };

    private static readonly string[] ProtectedFiles = { "tests/UrlShortener.Tests/Program.cs" };

    public const int MaxFileBytes = 200_000;
    public const int MaxFiles = 25;

    public static string? Check(ProposedFile file, Func<string, bool>? exists = null)
    {
        var path = file.Path.Replace('\\', '/').Trim();
        if (path.Length == 0) return "empty path";
        if (Path.IsPathRooted(path) || path.StartsWith('/') || path.Contains(':')) return $"'{file.Path}' is not a relative path";
        if (path.Split('/').Any(s => s is ".." or "." or "")) return $"'{file.Path}' contains '.', '..' or empty segments";
        if (!AllowedRoots.Any(r => path.StartsWith(r, StringComparison.Ordinal))) return $"'{file.Path}' is outside the allowed folders ({string.Join(", ", AllowedRoots)})";
        if (!path.EndsWith(".cs", StringComparison.Ordinal)) return $"'{file.Path}' is not a C# source file (project and config changes need a human)";
        if (path.Contains("/bin/") || path.Contains("/obj/")) return $"'{file.Path}' is a build output";
        if (ProtectedFiles.Contains(path)) return $"'{file.Path}' is protected (test runner entry point)";
        if (file.Action is not ("create" or "modify")) return $"'{file.Path}' has unsupported action '{file.Action}' (deletes are not allowed)";
        if (Encoding.UTF8.GetByteCount(file.Content) > MaxFileBytes) return $"'{file.Path}' is larger than {MaxFileBytes} bytes";
        if (file.Content.Trim().Length == 0) return $"'{file.Path}' has no content";
        if (exists is not null)
        {
            var present = exists(path);
            if (file.Action == "modify" && !present) return $"'{file.Path}' is marked modify but does not exist";
            if (file.Action == "create" && present) return $"'{file.Path}' is marked create but already exists";
        }
        return null;
    }

    public static IReadOnlyList<string> CheckAll(IReadOnlyList<ProposedFile> files, Func<string, bool>? exists = null)
    {
        var errors = files.Select(f => Check(f, exists)).OfType<string>().ToList();
        if (files.Count > MaxFiles) errors.Add($"too many files proposed ({files.Count} > {MaxFiles})");
        foreach (var dup in files.GroupBy(f => f.Path.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            errors.Add($"'{dup.Key}' is proposed more than once");
        return errors;
    }
}

/// <summary>
/// Validates proposals the only trustworthy way: copy the product and its tests to a throw-away workspace, apply the
/// proposed files there (never to the repository), build, run the real test suite, and record the exit code and
/// output. The model's own claims are never used as evidence.
/// </summary>
public sealed partial class SandboxValidator
{
    private static readonly string[] CopyFolders = { "src/UrlShortener.Api", "tests/UrlShortener.Tests", "tests/Shared" };
    private static readonly string[] CopyFiles = { "nuget.config", "global.json", "Directory.Build.props", "Directory.Build.targets" };
    private const string TestProject = "tests/UrlShortener.Tests/UrlShortener.Tests.csproj";

    private readonly IProcessRunner _runner;
    private readonly TimeSpan _timeout;

    public SandboxValidator(IProcessRunner? runner = null, TimeSpan? timeout = null)
    {
        _runner = runner ?? new ProcessRunner();
        _timeout = timeout ?? TimeSpan.FromMinutes(6);
    }

    [GeneratedRegex(@"Total:\s*(?<t>\d+)\s+Passed:\s*(?<p>\d+)\s+Failed:\s*(?<f>\d+)")]
    private static partial Regex Totals();

    [GeneratedRegex(@"^\s*(?<r>PASS|FAIL)\s+(?<name>\S+)", RegexOptions.Multiline)]
    private static partial Regex TestLine();

    [GeneratedRegex(@"error\s+CS\d+:[^\r\n\[]*")]
    private static partial Regex CompilerError();

    public static string HashOf(IEnumerable<ProposedFile> files) =>
        Hashing.Sha256(string.Join("\n", files.OrderBy(f => f.Path, StringComparer.Ordinal).Select(f => $"{f.Path}\n{Hashing.Sha256(f.Content)}")));

    public async Task<SandboxReport> RunAsync(string repoRoot, string stage, IReadOnlyList<ProposedFile> files, CancellationToken ct)
    {
        var hash = HashOf(files);
        var guardErrors = ChangeGuard.CheckAll(files, p => File.Exists(Path.Combine(repoRoot, p)));
        if (guardErrors.Count > 0)
            return new SandboxReport(stage, hash, Array.Empty<string>(), false, false, null, null, null, null,
                Array.Empty<string>(), Array.Empty<string>(), guardErrors, "proposal rejected before execution", 0);

        var workspace = Path.Combine(Path.GetTempPath(), "agentic-sdlc-sandbox", $"{stage}-{Guid.NewGuid():N}"[..20]);
        try
        {
            Prepare(repoRoot, workspace);
            var applied = new List<string>();
            var root = Path.GetFullPath(workspace) + Path.DirectorySeparatorChar;
            foreach (var f in files)
            {
                var target = Path.GetFullPath(Path.Combine(workspace, f.Path.Replace('\\', '/')));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"refusing to write outside the sandbox: {f.Path}");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await File.WriteAllTextAsync(target, f.Content, ct);
                applied.Add(f.Path);
            }

            var env = new Dictionary<string, string>
            {
                ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
                ["DOTNET_NOLOGO"] = "1",
                ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1"
            };
            var result = await _runner.RunAsync("dotnet", new[] { "run", "--project", TestProject }, null, workspace, _timeout, ct, env);
            return Parse(stage, hash, applied, result);
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); } catch { /* best effort; temp folder */ }
        }
    }

    public static SandboxReport Parse(string stage, string hash, IReadOnlyList<string> applied, ProcessResult result)
    {
        var output = result.StandardOutput + "\n" + result.StandardError;
        var totals = Totals().Match(output);
        var tests = TestLine().Matches(output).Select(m => (Result: m.Groups["r"].Value, Name: m.Groups["name"].Value)).ToList();
        var buildErrors = CompilerError().Matches(output).Select(m => m.Value.Trim()).Distinct().Take(25).ToList();
        var buildSucceeded = totals.Success || tests.Count > 0;
        if (result.TimedOut) buildErrors.Add("timed out");

        var failedNames = tests.Where(t => t.Result == "FAIL").Select(t => t.Name).ToList();
        var tail = FailureDetails(output, failedNames);

        return new SandboxReport(stage, hash, applied, true, buildSucceeded, result.ExitCode,
            totals.Success ? int.Parse(totals.Groups["t"].Value) : null,
            totals.Success ? int.Parse(totals.Groups["p"].Value) : null,
            totals.Success ? int.Parse(totals.Groups["f"].Value) : null,
            tests.Select(t => t.Name).ToList(), failedNames, buildErrors, tail, result.DurationMs);
    }

    /// <summary>The lines a repairing agent needs: failing tests with their assertion messages, or the end of the output.</summary>
    private static string FailureDetails(string output, IReadOnlyList<string> failed)
    {
        var lines = output.Replace("\r", "").Split('\n');
        var sb = new StringBuilder();
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].TrimStart().StartsWith("FAIL ", StringComparison.Ordinal)) continue;
            sb.AppendLine(lines[i].Trim());
            for (var j = i + 1; j < lines.Length && j <= i + 8 && lines[j].StartsWith("        ", StringComparison.Ordinal); j++)
                sb.AppendLine(lines[j].Trim());
        }
        var text = sb.Length > 0 ? sb.ToString() : string.Join('\n', lines.TakeLast(25));
        return text.Length > 4000 ? text[..4000] + "..." : text;
    }

    private static void Prepare(string repoRoot, string workspace)
    {
        foreach (var folder in CopyFolders)
        {
            var source = Path.Combine(repoRoot, folder);
            if (Directory.Exists(source)) CopyDirectory(source, Path.Combine(workspace, folder));
        }
        foreach (var file in CopyFiles)
        {
            var source = Path.Combine(repoRoot, file);
            if (File.Exists(source)) File.Copy(source, Path.Combine(workspace, file));
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            var name = Path.GetFileName(dir);
            if (name is "bin" or "obj" or ".vs") continue;
            CopyDirectory(dir, Path.Combine(target, name));
        }
    }

    /// <summary>
    /// The source of each failing test, so a repairing agent can see what is actually expected (it only knows test
    /// names otherwise). Proposed files override repository files of the same path. At most <paramref name="max"/> tests.
    /// </summary>
    public static string FailingTestSources(string repoRoot, IReadOnlyList<ProposedFile> proposed, IReadOnlyList<string> failedTests, int max = 3)
    {
        if (failedTests.Count == 0) return "";
        var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var testDir = Path.Combine(repoRoot, "tests", "UrlShortener.Tests");
        if (Directory.Exists(testDir))
            foreach (var file in Directory.EnumerateFiles(testDir, "*.cs", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
                if (!rel.Contains("/bin/") && !rel.Contains("/obj/")) sources[rel] = File.ReadAllText(file);
            }
        foreach (var f in proposed.Where(f => f.Path.StartsWith("tests/", StringComparison.Ordinal))) sources[f.Path] = f.Content;

        var sb = new StringBuilder();
        foreach (var test in failedTests.Take(max))
        {
            var dot = test.LastIndexOf('.');
            if (dot <= 0) continue;
            var (cls, method) = (test[..dot], test[(dot + 1)..]);
            foreach (var (path, content) in sources)
            {
                if (!Regex.IsMatch(content, $@"\bclass\s+{Regex.Escape(cls)}\b")) continue;
                var body = ExtractMethod(content, method);
                if (body is null) continue;
                sb.AppendLine($"<source path=\"{path}\" test=\"{test}\">");
                sb.AppendLine(body.Length > 3000 ? body[..3000] + "\n...(truncated)" : body);
                sb.AppendLine("</source>");
                break;
            }
        }
        return sb.ToString();
    }

    private static string? ExtractMethod(string content, string method)
    {
        var m = Regex.Match(content, $@"(\[Fact\]\s*)?public[^\n;{{]*\b{Regex.Escape(method)}\s*\(");
        if (!m.Success) return null;
        var open = content.IndexOf('{', m.Index);
        var arrow = content.IndexOf("=>", m.Index, StringComparison.Ordinal);
        if (arrow >= 0 && (open < 0 || arrow < open))
        {
            var end = content.IndexOf(';', arrow);
            return end < 0 ? null : content[m.Index..(end + 1)];
        }
        if (open < 0) return null;
        var depth = 0;
        for (var i = open; i < content.Length; i++)
        {
            if (content[i] == '{') depth++;
            else if (content[i] == '}' && --depth == 0) return content[m.Index..(i + 1)];
        }
        return null;
    }

    /// <summary>A concise, actionable reason for a failed sandbox run (fed back to the agent for repair).</summary>
    public static string Describe(SandboxReport r)
    {
        if (!r.Executed) return $"proposal rejected: {string.Join("; ", r.BuildErrors)}";
        if (!r.BuildSucceeded)
            return $"build failed with the proposed changes applied ({string.Join(", ", r.AppliedFiles)}):\n{string.Join("\n", r.BuildErrors.DefaultIfEmpty(r.OutputTail))}";
        if (r.Passed) return $"build succeeded and {r.PassedCount}/{r.Total} tests passed (exit code 0)";
        return $"{r.FailedCount} of {r.Total} tests failed (exit code {r.ExitCode}):\n{r.OutputTail}";
    }
}
