using System.Text.RegularExpressions;

namespace Agentic.Agents;

public sealed record SourceFile(string RelativePath, IReadOnlyList<string> Types, IReadOnlyList<(string Method, string Path)> Endpoints, string Content);

/// <summary>Builds a lightweight map of a C# codebase (files, declared types, minimal-API routes) for impact analysis.</summary>
public sealed partial class CodebaseMap
{
    public CodebaseMap(string root, IReadOnlyList<SourceFile> files)
    {
        Root = root;
        Files = files;
    }

    public string Root { get; }
    public IReadOnlyList<SourceFile> Files { get; }

    [GeneratedRegex(@"\b(?:class|record|interface|struct|enum)\s+(?<name>[A-Z][A-Za-z0-9_]*)")]
    private static partial Regex TypePattern();

    [GeneratedRegex(@"(?<recv>\w+)\.Map(?<verb>Get|Post|Put|Delete|Patch)\(\s*""(?<path>[^""]*)""")]
    private static partial Regex RoutePattern();

    [GeneratedRegex(@"var\s+(?<var>\w+)\s*=\s*\w+\.MapGroup\(\s*""(?<prefix>[^""]*)""")]
    private static partial Regex GroupPattern();

    public static CodebaseMap Scan(string repoRoot, string sourceFolder = "src")
    {
        var files = new List<SourceFile>();
        var srcRoot = Path.Combine(repoRoot, sourceFolder);
        if (Directory.Exists(srcRoot))
        {
            foreach (var path in Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
            {
                var normalized = path.Replace('\\', '/');
                if (normalized.Contains("/obj/") || normalized.Contains("/bin/")) continue;
                if (!normalized.Contains("/UrlShortener.Api/")) continue; // the product code, not the tooling

                var content = File.ReadAllText(path);
                var types = TypePattern().Matches(content).Select(m => m.Groups["name"].Value).Distinct().ToList();
                var prefixes = GroupPattern().Matches(content)
                    .ToDictionary(m => m.Groups["var"].Value, m => m.Groups["prefix"].Value);
                var endpoints = RoutePattern().Matches(content)
                    .Select(m => (m.Groups["verb"].Value.ToUpperInvariant(),
                        prefixes.GetValueOrDefault(m.Groups["recv"].Value, "") + m.Groups["path"].Value)).ToList();
                files.Add(new SourceFile(Path.GetRelativePath(repoRoot, path).Replace('\\', '/'), types, endpoints, content));
            }
        }
        return new CodebaseMap(repoRoot, files.OrderBy(f => f.RelativePath, StringComparer.Ordinal).ToList());
    }

    public bool FileExists(string relativePath) =>
        Files.Any(f => string.Equals(f.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase))
        || File.Exists(Path.Combine(Root, relativePath));

    /// <summary>Ranks files by how strongly they relate to the given keywords (path, type names, content).</summary>
    public IReadOnlyList<ImpactItem> Rank(IEnumerable<string> keywords, int top = 8, double minScore = 1.5)
    {
        var keys = keywords.Select(k => k.ToLowerInvariant()).Distinct().ToList();
        var ranked = new List<ImpactItem>();
        foreach (var f in Files)
        {
            double score = 0;
            var matched = new List<string>();
            var path = f.RelativePath.ToLowerInvariant();
            var types = string.Join(' ', f.Types).ToLowerInvariant();
            var content = f.Content.ToLowerInvariant();
            foreach (var k in keys)
            {
                var s = 0.0;
                if (path.Contains(k)) s += 3;
                if (types.Contains(k)) s += 2;
                s += Math.Min(CountOccurrences(content, k), 5) * 0.2;
                if (s > 0) matched.Add(k);
                score += s;
            }
            if (score >= minScore)
                ranked.Add(new ImpactItem(f.RelativePath, $"matches: {string.Join(", ", matched)}", Math.Round(score, 1)));
        }
        return ranked.OrderByDescending(i => i.Score).ThenBy(i => i.File, StringComparer.Ordinal).Take(top).ToList();
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }
}
